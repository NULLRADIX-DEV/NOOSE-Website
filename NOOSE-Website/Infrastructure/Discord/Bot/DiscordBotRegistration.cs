using Microsoft.Extensions.DependencyInjection.Extensions;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NOOSE_Website.Services.Discord.Bot;

namespace NOOSE_Website.Infrastructure.Discord.Bot;

public static class DiscordBotRegistration
{
    /// <summary>Command code always; the gateway only with a bot token, so demo and frozen instances stay offline.</summary>
    public static IServiceCollection AddFibDiscordBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DiscordBotStatus>();
        services.AddSingleton<DiscordBotRateLimiter>();
        services.AddSingleton<BotScratch>();
        services.AddScoped<IDiscordBotSettingsService, DiscordBotSettingsService>();
        services.AddScoped<IDiscordActorResolver, DiscordActorResolver>();
        services.AddScoped<BotRecords>();
        services.AddScoped<RecordCommands>();
        services.AddScoped<KnowledgeCommands>();
        services.AddScoped<PersonalCommands>();
        services.AddScoped<LeadershipCommands>();
        services.AddScoped<CaptureCommands>();
        services.AddScoped<DiscordBotRouter>();
        services.AddScoped<DiscordLiveBoards>();
        services.AddHttpClient(CaptureCommands.CdnClient, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();

        var token = configuration["Discord:BotToken"]?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return services;
        }

        // privileged: without the portal switch Discord closes the connection (4014), so it can be turned off here
        var messageContent = !string.Equals(configuration["Discord:MessageContent"], "false", StringComparison.OrdinalIgnoreCase);
        services.AddDiscordGateway(options =>
        {
            options.Token = token;
            // the worker connects after the app is up: a slow or refused gateway must never hold up the start
            options.AutoStartStop = false;
            options.Intents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages
                | (messageContent ? GatewayIntents.MessageContent : 0);
        });
        services.AddGatewayHandler<DiscordInteractionHandler>(ServiceLifetime.Singleton);
        services.AddGatewayHandler<DiscordLinkPreviewHandler>(ServiceLifetime.Singleton);
        services.AddGatewayHandler<DiscordReadyHandler>(ServiceLifetime.Singleton);
        services.AddGatewayHandler<DiscordDisconnectHandler>(ServiceLifetime.Singleton);
        services.AddSingleton<DiscordCommandRegistrar>();
        services.AddHostedService<DiscordBotWorker>();
        return services;
    }
}
