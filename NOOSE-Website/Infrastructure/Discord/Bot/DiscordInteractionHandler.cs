using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Services.Discord.Bot;

namespace NOOSE_Website.Infrastructure.Discord.Bot;

/// <summary>Every interaction from the gateway: answers within Discord's three seconds, then runs the command in its own scope.</summary>
public sealed class DiscordInteractionHandler(
    IServiceScopeFactory scopeFactory,
    DiscordBotRateLimiter rateLimiter,
    ILogger<DiscordInteractionHandler> logger) : IInteractionCreateGatewayHandler
{
    public const string Disabled = "Der FIB-Bot ist gerade abgeschaltet.";
    public const string TooFast = "Langsamer bitte, das waren zu viele Befehle in einer Minute.";

    private static readonly BotOptions Empty = new((IReadOnlyList<ApplicationCommandInteractionDataOption>?)null);

    // never block the gateway loop
    public ValueTask HandleAsync(Interaction interaction)
    {
        _ = Task.Run(() => ProcessAsync(interaction));
        return ValueTask.CompletedTask;
    }

    private async Task ProcessAsync(Interaction interaction)
    {
        var context = new BotContext(interaction.User.Id, interaction.User.GlobalName ?? interaction.User.Username,
            interaction.GuildId, interaction.Channel?.Id);
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = timeout.Token;
        try
        {
            var settings = await services.GetRequiredService<IDiscordBotSettingsService>().GetAsync(ct);
            if (!settings.Enabled)
            {
                await RefuseAsync(interaction, Disabled);
                return;
            }
            // before anything is parked in memory or read from the database
            if (interaction is not AutocompleteInteraction && !rateLimiter.TryEnter(context.UserId))
            {
                await RefuseAsync(interaction, TooFast);
                return;
            }

            // modals open before anything is read: they show nothing and the clock is short
            if (TryOpenEarly(interaction, services) is { } early)
            {
                if (early.Modal is { } modal)
                {
                    await interaction.SendResponseAsync(InteractionCallback.Modal(modal), cancellationToken: ct);
                }
                else
                {
                    await interaction.SendResponseAsync(InteractionCallback.Message(Message(early, ephemeral: true)), cancellationToken: ct);
                }
                return;
            }

            if (interaction is MessageComponentInteraction remove && BotIds.Parse(remove.Data.CustomId) is { Action: "rm" } removal)
            {
                await RemovePreviewAsync(remove, removal.Args, context, ct);
                return;
            }

            // defer first: resolving the agent takes database time the three seconds may not have
            var plan = Plan(interaction, context, settings);
            if (plan is { } deferred)
            {
                await DeferAsync(interaction, deferred.Mode, deferred.Shared, ct);
            }

            var (actor, refusal) = await services.GetRequiredService<IDiscordActorResolver>().ResolveAsync(context, ct);
            if (actor is null)
            {
                if (plan is { } refused)
                {
                    await FinishAsync(interaction, refused.Mode, refused.Shared, BotReply.Error(refusal!.Text), ct);
                }
                else
                {
                    await RefuseAsync(interaction, refusal!.Text);
                }
                return;
            }

            using var ambient = CurrentUserService.ActAs(actor.Principal);
            var router = services.GetRequiredService<DiscordBotRouter>();
            if (interaction is AutocompleteInteraction auto)
            {
                await AutocompleteAsync(auto, router, Call(context, actor, new BotOptions(auto.Data.Options), false, ct), ct);
                return;
            }
            var (mode, shared) = plan!.Value;
            var reply = await RunSafeAsync(() => interaction switch
            {
                SlashCommandInteraction slash => SlashAsync(slash, router, Call(context, actor, new BotOptions(slash.Data.Options), shared, ct)),
                UserCommandInteraction user => router.UserCommandAsync(user.Data.TargetUser.Id, Call(context, actor, Empty, false, ct)),
                MessageComponentInteraction component => ComponentAsync(component, router, Call(context, actor, Empty, false, ct)),
                ModalInteraction modalSubmit => ModalAsync(modalSubmit, router, Call(context, actor, Empty, false, ct)),
                _ => Task.FromResult(BotReply.Error("Unbekannte Aktion.")),
            });
            await FinishAsync(interaction, mode, shared, reply, ct);
        }
        catch (Exception ex)
        {
            // never the token, the options or the content
            logger.LogWarning("Discord interaction {Type} failed ({Error}).", interaction.GetType().Name, ex.GetType().Name);
        }
    }

    private static BotCall Call(BotContext context, DiscordActor actor, BotOptions options, bool shared, CancellationToken ct)
        => new() { Context = context, Actor = actor, Options = options, Shared = shared, Ct = ct };

    /// <summary>How the interaction is answered; null for autocomplete, which answers once with choices.</summary>
    private static (BotAnswerMode Mode, bool Shared)? Plan(Interaction interaction, BotContext context, DiscordBotSettings settings)
        => interaction switch
        {
            AutocompleteInteraction => null,
            SlashCommandInteraction slash => (BotAnswerMode.Reply,
                new BotOptions(slash.Data.Options).Flag("teilen") && DiscordBotRouter.Shareable.Contains(slash.Data.Name)
                && !context.IsDm && settings.MayShareIn(context.ChannelId)),
            MessageComponentInteraction component when BotIds.Parse(component.Data.CustomId) is { } parsed
                => (DiscordBotRouter.ModeForComponent(parsed.Action, parsed.Args), false),
            _ => (BotAnswerMode.Reply, false),
        };

    private static Task DeferAsync(Interaction interaction, BotAnswerMode mode, bool shared, CancellationToken ct)
        => mode == BotAnswerMode.Update
            ? interaction.SendResponseAsync(InteractionCallback.DeferredModifyMessage, cancellationToken: ct)
            : interaction.SendResponseAsync(InteractionCallback.DeferredMessage(shared ? null : MessageFlags.Ephemeral), cancellationToken: ct);

    /// <summary>Modals and the attach start; they read nothing.</summary>
    private static BotReply? TryOpenEarly(Interaction interaction, IServiceProvider services)
    {
        switch (interaction)
        {
            case MessageCommandInteraction message:
                var target = message.Data.TargetMessage;
                var state = new CaptureCommands.AttachState
                {
                    Content = target.Content ?? string.Empty,
                    Author = target.Author.GlobalName ?? target.Author.Username,
                    SentAt = target.CreatedAt.UtcDateTime,
                    OwnerDiscordId = interaction.User.Id,
                    JumpLink = $"https://discord.com/channels/{(interaction.GuildId?.ToString() ?? "@me")}/{target.ChannelId}/{target.Id}",
                    Images = target.Attachments
                        .Where(a => a.ContentType is not null && a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        .Select(a => (a.Url, (string?)a.ContentType, a.Size))
                        .ToList(),
                };
                return services.GetRequiredService<DiscordBotRouter>().MessageCommand(state);
            case MessageComponentInteraction component when BotIds.Parse(component.Data.CustomId) is { } parsed
                                                            && DiscordBotRouter.ModeForComponent(parsed.Action, parsed.Args) == BotAnswerMode.Modal:
                return DiscordBotRouter.ModalFor(parsed.Action, parsed.Args);
            default:
                return null;
        }
    }

    private static async Task RemovePreviewAsync(MessageComponentInteraction component, string[] args, BotContext context, CancellationToken ct)
    {
        // only the poster removes a link preview
        if (args.Arg(0) != context.UserId.ToString())
        {
            await RefuseAsync(component, "Entfernen kann nur, wer den Link gepostet hat.");
            return;
        }
        await component.SendResponseAsync(InteractionCallback.DeferredModifyMessage, cancellationToken: ct);
        await component.Message.DeleteAsync(cancellationToken: ct);
    }

    private static async Task AutocompleteAsync(AutocompleteInteraction interaction, DiscordBotRouter router, BotCall call, CancellationToken ct)
    {
        BotReply reply;
        try
        {
            reply = await router.AutocompleteAsync(interaction.Data.Name, call);
        }
        catch
        {
            reply = BotReply.Autocomplete([]);
        }
        await interaction.SendResponseAsync(InteractionCallback.Autocomplete(reply.Choices ?? []), cancellationToken: ct);
    }

    private static async Task<BotReply> SlashAsync(SlashCommandInteraction slash, DiscordBotRouter router, BotCall call)
    {
        var reply = await router.SlashAsync(slash.Data.Name, call);
        if (call.Options.Flag("teilen") && !call.Shared && !reply.IsError)
        {
            // asked to share where it is not allowed: say so under the private card
            reply = BotReply.Of(reply.Components.Append(DiscordCards.Notice("ℹ️",
                "Hier darf nicht geteilt werden, deshalb siehst nur du die Karte.", DiscordCards.Steel)), reply.Files);
        }
        return reply;
    }

    private static Task<BotReply> ComponentAsync(MessageComponentInteraction component, DiscordBotRouter router, BotCall call)
    {
        if (BotIds.Parse(component.Data.CustomId) is not { } parsed)
        {
            return Task.FromResult(BotReply.Error("Unbekannte Aktion."));
        }
        var selected = component is StringMenuInteraction menu ? menu.Data.SelectedValues : (IReadOnlyList<string>)[];
        return router.ComponentAsync(parsed.Action, parsed.Args, selected, call);
    }

    private static Task<BotReply> ModalAsync(ModalInteraction modal, DiscordBotRouter router, BotCall call)
    {
        if (BotIds.Parse(modal.Data.CustomId) is not { } parsed)
        {
            return Task.FromResult(BotReply.Error("Unbekannte Eingabe."));
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in modal.Data.Components)
        {
            var inner = part is Label label ? label.Component : part as ILabelComponent;
            switch (inner)
            {
                case TextInput input:
                    fields[input.CustomId] = input.Value ?? string.Empty;
                    break;
                case StringMenu stringMenu:
                    fields[stringMenu.CustomId] = stringMenu.SelectedValues.FirstOrDefault() ?? string.Empty;
                    break;
            }
        }
        return router.ModalAsync(parsed.Action, parsed.Args, fields, call);
    }

    /// <summary>Exceptions of the services become a private error card.</summary>
    private static async Task<BotReply> RunSafeAsync(Func<Task<BotReply>> run)
    {
        try
        {
            return await run();
        }
        catch (UnauthorizedAccessException ex)
        {
            return BotReply.Error(string.IsNullOrWhiteSpace(ex.Message) ? "Dafür fehlen dir die Rechte." : ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BotReply.Error(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return BotReply.Error("Das hat zu lange gedauert.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return BotReply.Error("Da ist etwas schiefgegangen. Versuch es auf fibhq.com.");
        }
    }

    /// <summary>Fills the deferred answer; errors stay private, and a rejected edit still gets an answer.</summary>
    private static async Task FinishAsync(Interaction interaction, BotAnswerMode mode, bool shared, BotReply reply, CancellationToken ct)
    {
        if (reply.IsError && (shared || mode == BotAnswerMode.Update))
        {
            await PrivateErrorAsync(interaction, shared, reply, ct);
            return;
        }

        DiscordCards.FitText(reply.Components);
        var streams = reply.Files.Select(f => new MemoryStream(f.Content)).ToList();
        try
        {
            await interaction.ModifyResponseAsync(options =>
            {
                options.Content = string.Empty;
                options.Embeds = [];
                options.Components = reply.Components;
                options.Flags = MessageFlags.IsComponentsV2;
                options.AllowedMentions = AllowedMentionsProperties.None;
                options.Attachments = reply.Files.Select((f, i) => new AttachmentProperties(f.Name, streams[i])).ToList();
            }, cancellationToken: ct);
        }
        catch (RestException)
        {
            // Discord refused the card: never leave the user on "denkt nach"
            await PrivateErrorAsync(interaction, mode == BotAnswerMode.Reply,
                BotReply.Error("Discord hat die Antwort abgelehnt. Öffne den Eintrag bitte auf fibhq.com."), ct);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private static async Task PrivateErrorAsync(Interaction interaction, bool deleteResponse, BotReply reply, CancellationToken ct)
    {
        if (deleteResponse)
        {
            try
            {
                await interaction.DeleteResponseAsync(cancellationToken: ct);
            }
            catch { /* ignore */ }
        }
        await interaction.SendFollowupMessageAsync(Message(reply, ephemeral: true), cancellationToken: ct);
    }

    public static InteractionMessageProperties Message(BotReply reply, bool ephemeral)
        => new()
        {
            Components = reply.Components,
            Flags = MessageFlags.IsComponentsV2 | (ephemeral ? MessageFlags.Ephemeral : 0),
            AllowedMentions = AllowedMentionsProperties.None,
        };

    private static async Task RefuseAsync(Interaction interaction, string text)
    {
        try
        {
            if (interaction is AutocompleteInteraction)
            {
                await interaction.SendResponseAsync(InteractionCallback.Autocomplete([]));
                return;
            }
            await interaction.SendResponseAsync(InteractionCallback.Message(Message(BotReply.Error(text), ephemeral: true)));
        }
        catch { /* ignore */ }
    }
}
