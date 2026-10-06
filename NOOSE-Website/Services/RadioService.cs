using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Radio;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Radio;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IRadioService" />
public class RadioService(IDbContextFactory<AppDbContext> dbFactory) : IRadioService
{
    public async Task<RadioPlan> GetPlanAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireReadAsync(db, actor, cancellationToken);
        var scope = ViewerScope.From(actor);

        var channels = await db.Funkkanaele.AsNoTracking().OnlyVisible(db, scope)
            .OrderBy(c => c.Scope).ThenBy(c => c.Frequency)
            .ToListAsync(cancellationToken);

        var names = await TaskforceNamesAsync(db, channels, cancellationToken);
        var rows = channels
            .Select(c => new RadioChannelRow(
                c.Id, c.Frequency, c.Label, c.Scope, c.Agency, c.TaskforceId,
                c.TaskforceId is not null && names.TryGetValue(c.TaskforceId, out var name) ? name : null,
                c.Note, c.IsClassified))
            .ToList();

        // faction frequencies are read live; the faction record stays the one place they are written
        var factions = await db.Factions.AsNoTracking().OnlyActive().OnlyVisible(scope)
            .Where(f => f.Radio != null && f.Radio != "")
            .OrderBy(f => f.Name)
            .Select(f => new RadioFactionRow(f.Id, f.Name, f.Radio!, f.IsClassified, true))
            .ToListAsync(cancellationToken);
        if (scope.PartnerAgency is { } agency)
        {
            // link only released
            var ids = factions.Select(f => f.FactionId).ToList();
            var released = (await db.Factions.Where(f => ids.Contains(f.Id))
                .OnlyPartnerVisible(db, agency, scope.MeId)
                .Select(f => f.Id)
                .ToListAsync(cancellationToken)).ToHashSet();
            factions = factions.Select(f => f with { Linkable = released.Contains(f.FactionId) }).ToList();
            rows = rows.Select(r => r with { TaskforceId = null, TaskforceName = null }).ToList();
        }

        return new RadioPlan(rows, factions);
    }

    public async Task<RadioChannel?> GetAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireReadAsync(db, actor, cancellationToken);
        var scope = ViewerScope.From(actor);

        return await db.Funkkanaele.AsNoTracking().OnlyVisible(db, scope)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<RadioChannel> CreateAsync(RadioChannelInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireHand(actor, input.IsClassified);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var channel = new RadioChannel();
        Apply(channel, input);
        db.Funkkanaele.Add(channel);
        await db.SaveChangesAsync(cancellationToken);
        return channel;
    }

    public async Task RefreshAsync(string id, RadioChannelInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var channel = await db.Funkkanaele.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Funkkanal '{id}' nicht gefunden.");

        // the old secrecy counts as much as the new one: lowering it is itself a leadership decision
        RequireHand(actor, channel.IsClassified || input.IsClassified);
        Apply(channel, input);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var channel = await db.Funkkanaele.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel is null)
        {
            return;
        }
        RequireHand(actor, channel.IsClassified);
        // soft delete via interceptor
        db.Funkkanaele.Remove(channel);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<RadioChannel>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // no classification filter: whoever reaches the trash page may read classified records anyway
        return await db.Funkkanaele.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.IsDeleted)
            .OrderByDescending(c => c.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var channel = await db.Funkkanaele.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Funkkanal '{id}' nicht gefunden.");

        channel.IsDeleted = false;
        channel.DeletedAt = null;
        channel.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Write guard; mirrors <see cref="RadioVisibility.MayEdit"/> so the UI cannot offer more than this allows.</summary>
    private static void RequireHand(ClaimsPrincipal actor, bool touchesClassified)
    {
        Permission.RequireWriteAccess(actor);
        if (!RadioVisibility.MayEdit(actor, touchesClassified))
        {
            throw new UnauthorizedAccessException("Eingestufte Funkkanäle bearbeitet nur die Führung.");
        }
    }

    private static void Apply(RadioChannel channel, RadioChannelInput input)
    {
        channel.Frequency = RadioFrequency.Normalize(input.Frequency);
        channel.Label = input.Label.Trim();
        channel.Scope = input.Scope;
        // an agency outside the partner block would show up nowhere and confuse the next editor
        channel.Agency = input.Scope == RadioScope.Partner ? input.Agency : null;
        // a taskforce is a NOOSE unit; bound to a partner row the gate would hide a channel nobody can explain
        channel.TaskforceId = input.Scope == RadioScope.Noose && !string.IsNullOrWhiteSpace(input.TaskforceId)
            ? input.TaskforceId
            : null;
        channel.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        channel.IsClassified = input.IsClassified;
    }

    /// <summary>Internal agents read the plan; partners only with the agency's radio function.</summary>
    private static async Task RequireReadAsync(AppDbContext db, ClaimsPrincipal actor, CancellationToken cancellationToken)
        => Permission.RequireInternalOrPartnerFeature(actor,
            await PartnerVisibility.FeaturesAsync(db, actor.GetPartnerAgency(), cancellationToken), PartnerFeature.Radio);

    /// <summary>Names for the bound taskforces of rows that already passed the gate.</summary>
    private static async Task<Dictionary<string, string>> TaskforceNamesAsync(
        AppDbContext db, List<RadioChannel> channels, CancellationToken cancellationToken)
    {
        var ids = channels.Where(c => c.TaskforceId is not null).Select(c => c.TaskforceId!).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, string>();
        }
        return await db.Taskforces.AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
    }
}
