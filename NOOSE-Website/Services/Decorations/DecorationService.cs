using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Decorations;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Announcements;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Decorations;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services.Decorations;

/// <inheritdoc />
public class DecorationService(
    IDbContextFactory<AppDbContext> dbFactory,
    IAnnouncementService announcements,
    INotificationService notifications,
    IDiscordWebhookService discord,
    ILogger<DecorationService> logger) : IDecorationService
{
    private const int MaxNameLength = 80;
    private const int MaxDescriptionLength = 500;

    // ---- catalog ----

    public async Task<IReadOnlyList<DecorationView>> GetCatalogAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.IsInternalAgent())
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Decorations.AsNoTracking()
            .OrderBy(d => d.Order).ThenBy(d => d.Name)
            .Select(d => new DecorationView(d.Id, d.Name, d.Description, d.DefaultCitation, d.Symbol, d.Tier,
                d.IsActive, d.Order, db.DecorationAwards.Count(a => a.DecorationId == d.Id)))
            .ToListAsync(cancellationToken);
    }

    public async Task<string> SaveDecorationAsync(DecorationInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Bitte einen Namen für das Ehrenzeichen angeben.");
        }
        if (name.Length > MaxNameLength)
        {
            throw new InvalidOperationException($"Der Name darf höchstens {MaxNameLength} Zeichen lang sein.");
        }
        if (!DecorationSymbols.IsKnown(input.Symbol))
        {
            throw new InvalidOperationException("Bitte ein Symbol aus der Auswahl wählen.");
        }
        if (!Enum.IsDefined(input.Tier))
        {
            throw new InvalidOperationException("Bitte eine Stufe aus der Auswahl wählen.");
        }
        var description = input.Description.TrimToNull();
        if (description?.Length > MaxDescriptionLength)
        {
            throw new InvalidOperationException($"Die Beschreibung darf höchstens {MaxDescriptionLength} Zeichen lang sein.");
        }
        var citation = DecorationRules.NormalizeText(input.DefaultCitation);
        if (citation.Length > DecorationRules.MaxCitationLength)
        {
            throw new InvalidOperationException(
                $"Der Standardtext darf höchstens {DecorationRules.MaxCitationLength} Zeichen lang sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = string.IsNullOrWhiteSpace(input.Id)
            ? null
            : await db.Decorations.FirstOrDefaultAsync(d => d.Id == input.Id, cancellationToken)
                ?? throw new InvalidOperationException("Ehrenzeichen nicht gefunden.");
        // the unique index folds more than this (accents on MariaDB); the save below catches the rest
        var folded = name.ToLower();
        if (await db.Decorations.AnyAsync(d => d.Id != (row == null ? "" : row.Id) && d.Name.ToLower() == folded,
                cancellationToken))
        {
            throw new InvalidOperationException($"Ein Ehrenzeichen „{name}“ gibt es schon.");
        }

        if (row is null)
        {
            row = new Decoration();
            db.Decorations.Add(row);
        }
        row.Name = name;
        row.Description = description;
        row.DefaultCitation = citation.Length == 0 ? null : citation;
        row.Symbol = input.Symbol;
        row.Tier = input.Tier;
        row.IsActive = input.IsActive;
        row.Order = Math.Clamp(input.Order, 0, 9999);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new InvalidOperationException($"Ein Ehrenzeichen „{name}“ gibt es schon.");
        }
        return row.Id;
    }

    public async Task DeleteDecorationAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Decorations.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (row is null)
        {
            return;
        }
        // revoked awards count too: their personnel files still show the name
        if (await db.DecorationAwards.AnyAsync(a => a.DecorationId == id, cancellationToken))
        {
            throw new InvalidOperationException(
                $"„{row.Name}“ wurde schon verliehen und bleibt deshalb im Katalog. Schalte es stattdessen ab.");
        }
        db.Decorations.Remove(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // an award landed between the check and the delete
            throw new InvalidOperationException(
                $"„{row.Name}“ wurde inzwischen verliehen und bleibt deshalb im Katalog. Schalte es stattdessen ab.");
        }
    }

    // ---- certificate settings ----

    public async Task<CertificateSettings> GetCertificateSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ReadSettingsAsync(db, cancellationToken);
    }

    public async Task<IReadOnlyList<SignatoryOption>> GetSignatoryOptionsAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.IsInternalAgent())
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var options = (await db.Users.AsNoTracking().OnlySelectable()
                .Where(u => u.Rank != null && u.Rank >= DecorationRules.SignatoryFloor)
                .Select(u => new { u.Id, u.Codename, u.Rank })
                .ToListAsync(cancellationToken))
            .OrderByDescending(u => u.Rank).ThenBy(u => u.Codename, StringComparer.CurrentCulture)
            .Select(u => new SignatoryOption(u.Id, u.Codename, u.Rank, Eligible: true))
            .ToList();

        // a stored signatory who no longer qualifies stays visible, or saving would drop them unseen
        var stored = (await ReadSettingsAsync(db, cancellationToken)).SignatoryId;
        if (stored is not null && options.All(o => o.Id != stored)
            && await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == stored, cancellationToken) is { } gone)
        {
            options.Add(new SignatoryOption(gone.Id, gone.Codename, gone.Rank, Eligible: false));
        }
        return options;
    }

    public async Task SaveCertificateSettingsAsync(CertificateSettings input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        var place = (input.Place ?? string.Empty).Trim();
        if (place.Length > DecorationRules.MaxPlaceLength)
        {
            throw new InvalidOperationException($"Der Ort darf höchstens {DecorationRules.MaxPlaceLength} Zeichen lang sein.");
        }
        var signatoryId = input.SignatoryId.TrimToNull();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var current = await ReadSettingsAsync(db, cancellationToken);
        // only a new pick is checked; keeping a stored one that has since dropped out must not lock the form
        if (signatoryId is not null && signatoryId != current.SignatoryId)
        {
            var agent = await db.Users.FirstOrDefaultAsync(u => u.Id == signatoryId, cancellationToken);
            if (agent is null || !DecorationRules.MaySign(agent))
            {
                throw new InvalidOperationException("Unterschreiben kann nur ein aktiver Agent ab Deputy Director.");
            }
        }
        await SetAsync(db, SystemSettingKeys.CertificateSignatoryId, signatoryId, cancellationToken);
        await SetAsync(db, SystemSettingKeys.CertificatePlace,
            place.Length == 0 || place == DecorationRules.DefaultPlace ? null : place, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- awards ----

    public async Task<string> AwardAsync(DecorationAwardInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // write guard first: the read-only supervision and the demo visitor stop here, not at the rank check
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        var actorId = actor.GetAgentId();
        if (string.Equals(actorId, input.AgentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Ein Ehrenzeichen kann man sich nicht selbst verleihen.");
        }
        var day = CeremonyDay(input.AwardedOn);
        var citation = Citation(input.Citation);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // the picker offers selectable agents only; a raw id off the wire must not get past that
        var agent = await db.Users.OnlySelectable().FirstOrDefaultAsync(u => u.Id == input.AgentId, cancellationToken)
            ?? throw new InvalidOperationException("Ehrenzeichen gehen nur an aktive Agenten der NOOSE.");
        // again on the resolved row: MariaDB matches ids case-insensitively
        if (string.Equals(agent.Id, actorId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Ein Ehrenzeichen kann man sich nicht selbst verleihen.");
        }
        var decoration = await db.Decorations
            .FirstOrDefaultAsync(d => d.Id == input.DecorationId && d.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("Dieses Ehrenzeichen kann nicht (mehr) verliehen werden.");
        var signatory = await SignatoryAsync(db, cancellationToken);

        var award = new DecorationAward
        {
            AgentId = agent.Id,
            DecorationId = decoration.Id,
            AwardedOn = day,
            Citation = citation,
            RecipientRank = agent.Rank,
            AwardedById = actorId,
            AwardedByName = actor.GetCodename(),
            SignatoryName = signatory?.Codename,
            SignatoryRank = signatory?.Rank,
        };
        db.DecorationAwards.Add(award);
        await db.SaveChangesAsync(cancellationToken);

        // the award stands; from here a cancelled circuit must not drop half of the announcements
        var codename = string.IsNullOrWhiteSpace(agent.Codename) ? "Unbenannter Agent" : agent.Codename;
        var href = PersonnelHref(agent.Id);
        if (input.PostToBoard)
        {
            try
            {
                // no broadcast: that would ring every bell and post a second time to Discord
                var entry = await announcements.CreateAsync(new AnnouncementInput
                {
                    Title = DecorationRules.AnnouncementTitle(decoration.Name, codename),
                    Content = DecorationRules.AnnouncementHtml(decoration.Name, codename, day, citation, award.AwardedByName),
                    Audience = AnnouncementAudience.AllActive,
                }, actor, CancellationToken.None);
                award.AnnouncementId = entry.Id;
                // a pure link: a tracked save would log an empty change and stamp the award as edited
                await db.DecorationAwards.Where(a => a.Id == award.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.AnnouncementId, entry.Id), CancellationToken.None);
            }
            catch (Exception ex)
            {
                /* best effort */
                logger.LogWarning(ex, "Board entry for decoration award {AwardId} failed.", award.Id);
            }
        }
        if (input.NotifyAgent)
        {
            try
            {
                // bell only: the generic push of a routable type would post a second, bare notice
                await notifications.NotifyManyInAppAsync(new[] { agent.Id }, NotificationType.DecorationAwarded,
                    DecorationRules.BellTitle(decoration.Name), href, actorId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                /* best effort */
                logger.LogWarning(ex, "Bell for decoration award {AwardId} failed.", award.Id);
            }
        }
        if (input.PostToDiscord)
        {
            await discord.PushDecorationAsync(agent.Id, codename, decoration.Name, day, citation,
                award.AwardedByName, href, CancellationToken.None);
        }
        return award.Id;
    }

    public async Task UpdateAsync(string id, DecorationAwardInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        var day = CeremonyDay(input.AwardedOn);
        var citation = Citation(input.Citation);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var award = await db.DecorationAwards.Include(a => a.Decoration)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Ehrung nicht gefunden.");
        RequireOther(award, actor);
        if (award.RevokedAt is not null)
        {
            throw new InvalidOperationException("Eine aberkannte Ehrung lässt sich nicht mehr ändern.");
        }
        var decoration = award.Decoration!;
        if (!string.Equals(award.DecorationId, input.DecorationId, StringComparison.Ordinal))
        {
            // a switched-off decoration may stay on an award that has it, but no award may newly get it
            decoration = await db.Decorations
                .FirstOrDefaultAsync(d => d.Id == input.DecorationId && d.IsActive, cancellationToken)
                ?? throw new InvalidOperationException("Dieses Ehrenzeichen kann nicht (mehr) verliehen werden.");
            // the row keeps only the id, so the protocol needs the names written by hand
            db.AuditLogs.Add(ManualAudit.Row(nameof(DecorationAward), award.Id, AuditAction.Modified, actor,
                ManualAudit.Change("Ehrenzeichen", award.Decoration!.Name, decoration.Name)));
            award.DecorationId = decoration.Id;
            award.Decoration = decoration;
        }
        award.AwardedOn = day;
        award.Citation = citation;
        await db.SaveChangesAsync(cancellationToken);

        // what was announced and released must say what the award says now
        var codename = await db.Users.Where(u => u.Id == award.AgentId).Select(u => u.Codename)
            .FirstOrDefaultAsync(CancellationToken.None);
        codename = string.IsNullOrWhiteSpace(codename) ? "Unbenannter Agent" : codename;
        if (award.AnnouncementId is { } announcementId)
        {
            try
            {
                await announcements.RefreshAsync(announcementId, new AnnouncementInput
                {
                    Title = DecorationRules.AnnouncementTitle(decoration.Name, codename),
                    Content = DecorationRules.AnnouncementHtml(decoration.Name, codename, day, citation, award.AwardedByName),
                    Audience = AnnouncementAudience.AllActive,
                }, actor, CancellationToken.None);
            }
            catch (Exception ex)
            {
                /* best effort */
                logger.LogWarning(ex, "Refreshing the board entry of corrected award {AwardId} failed.", award.Id);
            }
        }
    }

    public async Task RevokeAsync(string id, string reason, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        var why = DecorationRules.NormalizeText(reason);
        if (why.Length == 0)
        {
            throw new InvalidOperationException("Bitte einen Grund für die Aberkennung angeben.");
        }
        if (why.Length > DecorationRules.MaxReasonLength)
        {
            throw new InvalidOperationException(
                $"Der Grund darf höchstens {DecorationRules.MaxReasonLength} Zeichen lang sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var award = await db.DecorationAwards.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Ehrung nicht gefunden.");
        RequireOther(award, actor);
        if (award.RevokedAt is not null)
        {
            return;
        }
        award.RevokedAt = DateTime.UtcNow;
        award.RevokedByName = actor.GetCodename();
        award.RevocationReason = why;
        await db.SaveChangesAsync(cancellationToken);

        // the board must stop celebrating it
        if (award.AnnouncementId is { } announcementId)
        {
            try
            {
                await announcements.DeleteAsync(announcementId, actor, CancellationToken.None);
            }
            catch (Exception ex)
            {
                /* best effort */
                logger.LogWarning(ex, "Trashing the board entry of revoked award {AwardId} failed.", award.Id);
            }
        }
    }

    // ---- reads ----

    public async Task<IReadOnlyList<DecorationAwardView>> GetForAgentAsync(string agentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.IsInternalAgent())
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.DecorationAwards.AsNoTracking()
            .Where(a => a.AgentId == agentId)
            .Select(a => new
            {
                a.Id, a.AgentId, a.DecorationId, a.Decoration!.Name, a.Decoration.Symbol, a.Decoration.Tier,
                a.AwardedOn, a.Citation, a.AwardedByName, a.RevokedAt, a.RevokedByName, a.RevocationReason, a.CreatedAt,
            })
            .ToListAsync(cancellationToken);
        return rows
            .OrderByDescending(a => a.AwardedOn).ThenByDescending(a => a.CreatedAt)
            .Select(a => new DecorationAwardView(a.Id, a.AgentId, a.DecorationId, a.Name, a.Symbol, a.Tier,
                a.AwardedOn, a.Citation, a.AwardedByName, a.RevokedAt, a.RevokedByName, a.RevocationReason))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DecorationPlaqueView>>> GetPlaquesAsync(
        IReadOnlyCollection<string> agentIds, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (!actor.IsInternalAgent() || agentIds.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<DecorationPlaqueView>>();
        }
        var ids = agentIds.Distinct(StringComparer.Ordinal).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.DecorationAwards.AsNoTracking()
            .Where(a => ids.Contains(a.AgentId) && a.RevokedAt == null)
            .Select(a => new
            {
                a.AgentId, a.DecorationId, a.Decoration!.Name, a.Decoration.Symbol, a.Decoration.Tier,
                a.Decoration.Order, a.AwardedOn,
            })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(a => a.AgentId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => DecorationRules.Plaques(g.Select(a => new DecorationRules.PlaqueRow(
                    a.DecorationId, a.Name, a.Symbol, a.Tier, a.Order, a.AwardedOn, Revoked: false))),
                StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<RecentDecorationView>> GetRecentAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.IsInternalAgent())
        {
            return [];
        }
        var since = DateTime.Now.Date.AddDays(-DecorationRules.RecentDays);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // the recipient may have become a team lead or partner since; neither appears in any list
        var roster = db.Users.OnlySelectable();
        var rows = await db.DecorationAwards.AsNoTracking()
            .Where(a => a.RevokedAt == null && a.AwardedOn >= since && roster.Any(u => u.Id == a.AgentId))
            .OrderByDescending(a => a.AwardedOn).ThenByDescending(a => a.CreatedAt)
            .Take(DecorationRules.RecentMax)
            .Select(a => new
            {
                a.Id, a.AgentId, a.Decoration!.Name, a.Decoration.Symbol, a.Decoration.Tier, a.AwardedOn,
                Codename = db.Users.Where(u => u.Id == a.AgentId).Select(u => u.Codename).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        return rows
            .Select(a => new RecentDecorationView(a.Id, a.AgentId,
                string.IsNullOrWhiteSpace(a.Codename) ? "Unbenannter Agent" : a.Codename,
                a.Name, a.Symbol, a.Tier, a.AwardedOn))
            .ToList();
    }

    // ---- certificates ----

    public async Task<CertificateView?> GetCertificateAsync(string awardId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.IsInternalAgent())
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var award = await db.DecorationAwards.AsNoTracking().Include(a => a.Decoration)
            .FirstOrDefaultAsync(a => a.Id == awardId, cancellationToken);
        if (award is null || award.Decoration is null
            || !DecorationRules.MayPrint(actor, award.AgentId, award.RevokedAt is not null))
        {
            return null;
        }
        var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == award.AgentId, cancellationToken);
        if (agent is null)
        {
            return null;
        }
        var settings = await ReadSettingsAsync(db, cancellationToken);
        return new CertificateView(CertificateKind.Decoration, agent.Id, Codename(agent),
            DecorationRules.MayChooseRealName(actor, agent.Id) ? agent.RealName.TrimToNull() : null,
            agent.BadgeNumber.TrimToNull(), award.RecipientRank, award.AwardedOn.Date, award.Decoration.Name,
            award.Decoration.Symbol, award.Decoration.Tier, award.Citation, award.SignatoryName, award.SignatoryRank,
            settings.Place);
    }

    public async Task<CertificateView?> GetRankCertificateAsync(string historyId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.IsInternalAgent())
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.AgentRankHistories.AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        if (row is null || DecorationRules.RankCertificateKind(row.Alt, row.New) is not { } kind
            || !DecorationRules.MayPrint(actor, row.AgentId, revoked: false))
        {
            return null;
        }
        var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == row.AgentId, cancellationToken);
        if (agent is null)
        {
            return null;
        }
        // nothing was stored with old history rows, so these carry today's office head
        var signatory = await SignatoryAsync(db, cancellationToken);
        var settings = await ReadSettingsAsync(db, cancellationToken);
        var day = DateTime.SpecifyKind(row.Timestamp, DateTimeKind.Utc).ToLocalTime().Date;
        return new CertificateView(kind, agent.Id, Codename(agent),
            DecorationRules.MayChooseRealName(actor, agent.Id) ? agent.RealName.TrimToNull() : null,
            agent.BadgeNumber.TrimToNull(), row.New, day, null, null, null, null,
            signatory?.Codename, signatory?.Rank, settings.Place);
    }

    // ---- helpers ----

    private static DateTime CeremonyDay(DateTime? chosen)
        => DecorationRules.CeremonyDay(chosen, DateTime.Now)
            ?? throw new InvalidOperationException("Das Verleihungsdatum darf nicht in der Zukunft liegen.");

    private static string Citation(string? text)
    {
        var citation = DecorationRules.NormalizeText(text);
        if (citation.Length == 0)
        {
            throw new InvalidOperationException("Bitte einen Verleihungstext angeben.");
        }
        if (citation.Length > DecorationRules.MaxCitationLength)
        {
            throw new InvalidOperationException(
                $"Der Verleihungstext darf höchstens {DecorationRules.MaxCitationLength} Zeichen lang sein.");
        }
        return citation;
    }

    // nobody honours, corrects or strips their own decoration
    private static void RequireOther(DecorationAward award, ClaimsPrincipal actor)
    {
        if (string.Equals(award.AgentId, actor.GetAgentId(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Über deine eigenen Ehrungen entscheidet jemand anderes.");
        }
    }

    private static string PersonnelHref(string agentId) => $"/personal/{agentId}?tab=auszeichnungen";

    private static string Codename(Agent agent)
        => string.IsNullOrWhiteSpace(agent.Codename) ? "Unbenannter Agent" : agent.Codename;

    /// <summary>The configured signatory while still eligible; otherwise none.</summary>
    private static async Task<Agent?> SignatoryAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var id = (await ReadSettingsAsync(db, cancellationToken)).SignatoryId;
        if (id is null)
        {
            return null;
        }
        var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        return agent is not null && DecorationRules.MaySign(agent) ? agent : null;
    }

    private static async Task<CertificateSettings> ReadSettingsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var keys = new[] { SystemSettingKeys.CertificateSignatoryId, SystemSettingKeys.CertificatePlace };
        var values = await db.SystemSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        var place = values.GetValueOrDefault(SystemSettingKeys.CertificatePlace).TrimToNull() ?? DecorationRules.DefaultPlace;
        return new CertificateSettings(values.GetValueOrDefault(SystemSettingKeys.CertificateSignatoryId).TrimToNull(), place);
    }

    private static async Task SetAsync(AppDbContext db, string key, string? value, CancellationToken cancellationToken)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null)
        {
            if (value is not null)
            {
                db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
            }
            return;
        }
        row.Value = value;
    }
}
