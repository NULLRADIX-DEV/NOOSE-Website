using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

// Scoped AppDbContext injected directly to share UserManager's context (writes only).
public class AgentManagementService(
    UserManager<Agent> userManager,
    AppDbContext db,
    IDbContextFactory<AppDbContext> dbFactory,
    INotificationService notifications,
    IDiscordWebhookService discord,
    IConfiguration configuration,
    IAgentAvatarStorageService avatars) : IAgentManagementService
{
    private static readonly SemaphoreSlim BadgeNumberGate = new(1, 1);

    public async Task<List<Agent>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var readDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await readDb.Users.AsNoTracking().Where(a => a.Status == AgentStatus.Pending)
            .OrderBy(a => a.RegisteredAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Agent>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var readDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        // applicants and citizens are not agents; they live in recruiting and the public area, never in agent rosters
        return await readDb.Users.AsNoTracking()
            .Where(a => a.Status != AgentStatus.Applicant && a.Status != AgentStatus.Civilian)
            .OrderByDescending(a => a.Status == AgentStatus.Pending)
            .ThenBy(a => a.Codename)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Agent>> GetSelectableAsync(CancellationToken cancellationToken = default)
    {
        await using var readDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await readDb.Users.AsNoTracking().OnlySelectable()
            .OrderBy(a => a.Codename)
            .ToListAsync(cancellationToken);
    }

    public async Task<Agent?> FindAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var readDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await readDb.Users.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
    }

    public async Task<List<string>> GetAvailableBadgeNumbersAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var readDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var assigned = await readDb.Users.AsNoTracking()
            .Where(a => a.Id != agentId && (a.BadgeNumber != null || a.PendingBadgeNumber != null))
            .Select(a => new { a.BadgeNumber, a.PendingBadgeNumber })
            .ToListAsync(cancellationToken);
        var target = await readDb.Users.AsNoTracking()
            .Where(a => a.Id == agentId)
            .Select(a => new { a.BadgeNumber, a.PendingBadgeNumber })
            .SingleOrDefaultAsync(cancellationToken);

        var taken = assigned
            .SelectMany(a => new[] { a.BadgeNumber, a.PendingBadgeNumber })
            .Select(BadgeNumbers.Normalize)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = BadgeNumbers.All.Where(x => !taken.Contains(x)).ToList();

        if (target is not null)
        {
            foreach (var value in new[] { target.BadgeNumber, target.PendingBadgeNumber })
            {
                var normalized = BadgeNumbers.Normalize(value);
                if (normalized is not null && !options.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    options.Add(normalized);
                }
            }
        }

        return options;
    }

    public async Task ReleaseAsync(string agentId, Rank rank, bool isTRU, bool isHRB, ClaimsPrincipal actor)
    {
        // The page was the only gate. Releasing an account hands out a rank - up to Director - and the socket
        // takes whatever it is sent, so the check has to sit here, where every path through it passes.
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        RequireDepartmentsFitRank(rank, isTRU, isHRB);

        var agent = await GetOrThrow(agentId);
        var altRank = agent.Rank;
        agent.Status = AgentStatus.Active;
        agent.Rank = rank;
        agent.IsTRU = isTRU;
        agent.IsHRB = isHRB;
        agent.ReleasedAt = DateTime.UtcNow;
        agent.ReleasedById = actor.GetAgentId();
        agent.BlockedReason = null;

        HistoryEntryAdd(agent.Id, altRank, rank, actor, "Erstmalige Freigabe");
        Audit(agent, AuditAction.Modified, actor,
            $"Freigegeben als {rank}{(isTRU ? " (TRU)" : "")}{(isHRB ? " (HRB)" : "")}");
        await Save(agent, newStamp: true);

        // notify agent
        try { await notifications.NotifyAsync(agent.Id, NotificationType.Account, "Dein Account wurde freigegeben.", "/"); }
        catch { /* best effort */ }
    }

    public async Task ReleaseAsPartnerAsync(string agentId, PartnerAgency agency, PartnerRank partnerRank, ClaimsPrincipal actor)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        var agent = await GetOrThrow(agentId);
        agent.Status = AgentStatus.Active;
        agent.PartnerAgency = agency;
        agent.PartnerRank = partnerRank;
        // partners hold no internal rank/flags
        agent.Rank = null;
        agent.IsTRU = false;
        agent.IsHRB = false;
        agent.IsTeamLead = false;
        agent.IsAdmin = false;
        agent.ReleasedAt = DateTime.UtcNow;
        agent.ReleasedById = actor.GetAgentId();
        agent.BlockedReason = null;

        Audit(agent, AuditAction.Modified, actor, $"Als Partner freigegeben ({PartnerRankDisplay.Full(agency, partnerRank)})");
        await Save(agent, newStamp: true);

        try { await notifications.NotifyAsync(agent.Id, NotificationType.Account, "Dein Partner-Zugang wurde freigegeben.", "/"); }
        catch { /* best effort */ }
    }

    public async Task RejectAsync(string agentId, string reason, ClaimsPrincipal actor)
    {
        // same reason as the release above: this one blocks an account, and it had no check at all
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        var agent = await GetOrThrow(agentId);
        agent.Status = AgentStatus.Blocked;
        agent.BlockedReason = string.IsNullOrWhiteSpace(reason) ? "Registrierung abgelehnt" : reason;

        Audit(agent, AuditAction.Modified, actor, $"Registrierung abgelehnt: {agent.BlockedReason}");
        await Save(agent, newStamp: true);
    }

    public async Task PromoteApplicantToAgentAsync(string applicantUserId, Rank rank, bool isTRU, bool isHRB, ClaimsPrincipal actor)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        RequireDepartmentsFitRank(rank, isTRU, isHRB);

        var agent = await GetOrThrow(applicantUserId);
        // a partner applies without ever leaving status Active, so it arrives here as an Active account with an
        // agency rather than as an Applicant; the partner role ends with the same save that hands out the rank.
        // The application has to exist: status Applicant is the applicant's own proof of having gone through
        // recruiting, and without this the panel would convert any partner at all.
        bool partnerJoining = agent.Status == AgentStatus.Active && agent.PartnerAgency is not null
            && await db.Bewerbungen.AnyAsync(b => b.ApplicantUserId == applicantUserId);
        if (agent.Status != AgentStatus.Applicant && !partnerJoining)
        {
            throw new InvalidOperationException("Nur Bewerber können hochgestuft werden.");
        }
        var altRank = agent.Rank;
        agent.Status = AgentStatus.Active;
        agent.PartnerAgency = null;
        agent.PartnerRank = null;
        agent.Rank = rank;
        agent.IsTRU = isTRU;
        agent.IsHRB = isHRB;
        agent.ReleasedAt = DateTime.UtcNow;
        agent.ReleasedById = actor.GetAgentId();
        agent.BlockedReason = null;

        HistoryEntryAdd(agent.Id, altRank, rank, actor, "Aus Bewerbung hochgestuft");
        Audit(agent, AuditAction.Modified, actor,
            $"Bewerber hochgestuft als {rank}{(isTRU ? " (TRU)" : "")}{(isHRB ? " (HRB)" : "")}");
        await Save(agent, newStamp: true);

        try { await notifications.NotifyAsync(agent.Id, NotificationType.Account, "Dein NOOSE-Zugang wurde freigeschaltet.", "/"); }
        catch { /* best effort */ }
    }

    public async Task<Agent> StartApplicationAsync(string userId, CancellationToken cancellationToken = default)
    {
        // no actor guard: the account holder acts on itself, and the caller is the verified Discord callback
        var agent = await GetOrThrow(userId, cancellationToken);
        if (agent.Status != AgentStatus.Civilian)
        {
            throw new InvalidOperationException("Nur Bürgerkonten können eine Bewerbung starten.");
        }

        agent.Status = AgentStatus.Applicant;

        // only the {field:[old,new]} shape renders in the audit viewer; a free-text hint would show as "—"
        Audit(agent, AuditAction.Modified, agent.Id, agent.DiscordUsername ?? agent.Id,
            JsonSerializer.Serialize(ManualAudit.Change("Status", "Bürger", "Bewerber")));
        // new stamp so a session open elsewhere stops claiming citizen status
        await Save(agent, newStamp: true);
        return agent;
    }

    public async Task MasterDataChangeAsync(string agentId, string? realName, string codename, string? badgeNumber, ClaimsPrincipal actor)
    {
        Permission.RequireWriteAccess(actor);
        codename = codename?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(codename))
        {
            throw new InvalidOperationException("Der Codename darf nicht leer sein.");
        }

        // This writes another agent's codename, real name and badge straight through, without the request the
        // rest of the workforce goes over. Both callers already gate on leadership; the socket did not.
        Permission.RequireLeadership(actor);

        await BadgeNumberGate.WaitAsync();
        try
        {
            var agent = await GetOrThrow(agentId);
            var normalizedBadgeNumber = BadgeNumbers.Normalize(badgeNumber);
            await ValidateBadgeNumberAsync(agent, normalizedBadgeNumber, allowLegacyValue: true);
            agent.RealName = string.IsNullOrWhiteSpace(realName) ? null : realName.Trim();
            agent.Codename = codename;
            agent.BadgeNumber = normalizedBadgeNumber;
            PendingNameChangeEmpty(agent);

            Audit(agent, AuditAction.Modified, actor, $"Stammdaten geändert (Codename: {agent.Codename})");
            await Save(agent, newStamp: true); // refresh claims
        }
        finally
        {
            BadgeNumberGate.Release();
        }
    }

    public async Task NameChangeRequestAsync(string agentId, string? realName, string codename, string? badgeNumber, ClaimsPrincipal actor)
    {
        Permission.RequireWriteAccess(actor);
        codename = codename?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(codename))
        {
            throw new InvalidOperationException("Der Codename darf nicht leer sein.");
        }

        await BadgeNumberGate.WaitAsync();
        try
        {
            var agent = await GetOrThrow(agentId);
            var normalizedBadgeNumber = BadgeNumbers.Normalize(badgeNumber);
            await ValidateBadgeNumberAsync(agent, normalizedBadgeNumber, allowLegacyValue: true);
            agent.PendingCodename = codename;
            agent.PendingRealName = string.IsNullOrWhiteSpace(realName) ? null : realName.Trim();
            agent.PendingBadgeNumber = normalizedBadgeNumber;
            agent.NameChangeRequestedAt = DateTime.UtcNow;

            Audit(agent, AuditAction.Modified, actor, $"Namensänderung beantragt (Codename: {codename})");
            await Save(agent, newStamp: false);
        }
        finally
        {
            BadgeNumberGate.Release();
        }
    }

    public async Task<List<Agent>> GetPendingNameChangesAsync(CancellationToken cancellationToken = default)
    {
        await using var readDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await readDb.Users.AsNoTracking().Where(a => a.NameChangeRequestedAt != null)
            .OrderBy(a => a.NameChangeRequestedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task NameChangeApproveAsync(string agentId, ClaimsPrincipal actor)
    {
        // write guard before the gate: a refused actor must not take the process-wide lock
        Permission.RequireWriteAccess(actor);
        Agent agent;
        await BadgeNumberGate.WaitAsync();
        try
        {
            agent = await GetOrThrow(agentId);
            if (agent.NameChangeRequestedAt is null)
            {
                throw new InvalidOperationException("Für diesen Agent liegt kein Namensänderungs-Antrag vor.");
            }

            await ValidateBadgeNumberAsync(agent, agent.PendingBadgeNumber, allowLegacyValue: true, ownValueIsPending: false);

            agent.Codename = agent.PendingCodename ?? string.Empty;
            agent.RealName = agent.PendingRealName;
            agent.BadgeNumber = agent.PendingBadgeNumber;
            PendingNameChangeEmpty(agent);

            Audit(agent, AuditAction.Modified, actor, $"Namensänderung genehmigt (Codename: {agent.Codename})");
            await Save(agent, newStamp: true);
        }
        finally
        {
            BadgeNumberGate.Release();
        }

        try { await notifications.NotifyAsync(agent.Id, NotificationType.Account, "Deine Namensänderung wurde genehmigt.", "/profil"); }
        catch { /* best effort */ }
    }

    public async Task NameChangeRejectAsync(string agentId, string reason, ClaimsPrincipal actor)
    {
        // write guard before the reads: the barrier would only refuse at the save
        Permission.RequireWriteAccess(actor);
        var agent = await GetOrThrow(agentId);
        if (agent.NameChangeRequestedAt is null)
        {
            throw new InvalidOperationException("Für diesen Agent liegt kein Namensänderungs-Antrag vor.");
        }

        var requestedCodename = agent.PendingCodename;
        PendingNameChangeEmpty(agent);

        var hint = string.IsNullOrWhiteSpace(reason) ? "ohne Angabe" : reason.Trim();
        Audit(agent, AuditAction.Modified, actor,
            $"Namensänderung abgelehnt (beantragter Codename: {requestedCodename}): {hint}");
        await Save(agent, newStamp: false);

        try { await notifications.NotifyAsync(agent.Id, NotificationType.Account, "Deine Namensänderung wurde abgelehnt.", "/profil"); }
        catch { /* best effort */ }
    }

    public async Task AvatarSetAsync(string agentId, Stream content, string contentType, long sizeBytes,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        // own picture only; leadership moderates through AvatarRemoveAsync, it never uploads for someone else
        if (agentId != actor.GetAgentId())
        {
            throw new UnauthorizedAccessException("Ein Profilbild kann nur für das eigene Konto gesetzt werden.");
        }
        if (!avatars.IsAllowedType(contentType ?? string.Empty))
        {
            throw new InvalidOperationException("Nur Bilddateien (JPG, PNG, WebP, GIF) sind als Profilbild erlaubt.");
        }
        if (sizeBytes > avatars.MaxBytes)
        {
            throw new InvalidOperationException($"Das Profilbild ist zu groß (max. {avatars.MaxBytes / (1024 * 1024)} MB).");
        }

        var agent = await GetOrThrow(agentId);
        var fileName = await avatars.SaveAsync(content, contentType!, cancellationToken);

        string? obsoleteActive = null;
        string? obsoleteStaged;
        if (actor.IsLeadership())
        {
            // leadership edits its own master data instantly; the picture follows that precedent
            obsoleteActive = agent.AvatarFileName;
            AvatarPendingEmpty(agent, out obsoleteStaged);
            agent.AvatarFileName = fileName;
            agent.AvatarContentType = contentType;
            Audit(agent, AuditAction.Modified, actor, "Profilbild gesetzt");
        }
        else
        {
            obsoleteStaged = agent.PendingAvatarFileName;
            agent.PendingAvatarFileName = fileName;
            agent.PendingAvatarContentType = contentType;
            agent.AvatarRequestedAt = DateTime.UtcNow;
            Audit(agent, AuditAction.Modified, actor, "Profilbild beantragt");
        }

        await Save(agent, newStamp: false); // no claim carries the picture, so no forced re-login

        // only after the row committed, or a failed save would leave a dangling reference
        AvatarFileDelete(obsoleteActive);
        AvatarFileDelete(obsoleteStaged);
    }

    public async Task AvatarRemoveAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        if (agentId != actor.GetAgentId() && !actor.IsLeadership())
        {
            throw new UnauthorizedAccessException("Fremde Profilbilder darf nur die Führung entfernen.");
        }

        var agent = await GetOrThrow(agentId);
        var active = agent.AvatarFileName;
        agent.AvatarFileName = null;
        agent.AvatarContentType = null;
        AvatarPendingEmpty(agent, out var staged);

        Audit(agent, AuditAction.Modified, actor, "Profilbild entfernt");
        await Save(agent, newStamp: false);

        AvatarFileDelete(active);
        AvatarFileDelete(staged);
    }

    public async Task<List<Agent>> GetPendingAvatarsAsync(CancellationToken cancellationToken = default)
    {
        await using var readDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await readDb.Users.AsNoTracking().Where(a => a.PendingAvatarFileName != null)
            .OrderBy(a => a.AvatarRequestedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task AvatarApproveAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);

        var agent = await GetOrThrow(agentId);
        if (agent.PendingAvatarFileName is null)
        {
            throw new InvalidOperationException("Für diesen Agent liegt kein Profilbild zur Freigabe vor.");
        }

        var obsolete = agent.AvatarFileName;
        agent.AvatarFileName = agent.PendingAvatarFileName;
        agent.AvatarContentType = agent.PendingAvatarContentType;
        AvatarPendingEmpty(agent, out _);

        Audit(agent, AuditAction.Modified, actor, "Profilbild genehmigt");
        await Save(agent, newStamp: false);

        AvatarFileDelete(obsolete);

        try { await notifications.NotifyAsync(agent.Id, NotificationType.Account, "Dein Profilbild wurde genehmigt.", "/profil"); }
        catch { /* best effort */ }
    }

    public async Task AvatarRejectAsync(string agentId, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);

        var agent = await GetOrThrow(agentId);
        if (agent.PendingAvatarFileName is null)
        {
            throw new InvalidOperationException("Für diesen Agent liegt kein Profilbild zur Freigabe vor.");
        }

        AvatarPendingEmpty(agent, out var staged);

        var hint = string.IsNullOrWhiteSpace(reason) ? "ohne Angabe" : reason.Trim();
        Audit(agent, AuditAction.Modified, actor, $"Profilbild abgelehnt: {hint}");
        await Save(agent, newStamp: false);

        AvatarFileDelete(staged);

        try { await notifications.NotifyAsync(agent.Id, NotificationType.Account, "Dein Profilbild wurde abgelehnt.", "/profil"); }
        catch { /* best effort */ }
    }

    public async Task<Agent?> FindByAvatarFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }
        await using var readDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await readDb.Users.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AvatarFileName == fileName || a.PendingAvatarFileName == fileName, cancellationToken);
    }

    public async Task RankChangeAsync(string agentId, Rank rank, ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        var agent = await GetOrThrow(agentId);
        var alt = agent.Rank;
        agent.Rank = rank;
        var lost = DropDepartmentsBelowThreshold(agent);

        if (alt != rank)
        {
            HistoryEntryAdd(agent.Id, alt, rank, actor, "Rangänderung");
        }
        Audit(agent, AuditAction.Modified, actor, $"Dienstgrad {alt?.ToString() ?? "—"} → {rank}{lost}");
        await Save(agent, newStamp: true);
    }

    public async Task SetPartnerRankAsync(string agentId, PartnerRank partnerRank, ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);

        var agent = await GetOrThrow(agentId);
        if (agent.PartnerAgency is not { } agency)
        {
            throw new InvalidOperationException("Dieser Account ist kein Partner-Konto.");
        }
        agent.PartnerRank = partnerRank;

        Audit(agent, AuditAction.Modified, actor, $"Partner-Rang → {PartnerRankDisplay.Full(agency, partnerRank)}");
        await Save(agent, newStamp: true);
    }

    public async Task ConvertToPartnerAsync(string agentId, PartnerAgency agency, PartnerRank partnerRank, ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);

        var agent = await GetOrThrow(agentId);
        if (agent.Status != AgentStatus.Active)
        {
            throw new InvalidOperationException("Die Zugehörigkeit kann nur für aktive Konten geändert werden.");
        }

        // guard admin strip
        if (agent.IsAdmin)
        {
            if (actor.GetAgentId() == agentId)
            {
                throw new InvalidOperationException("Du kannst deine eigene Zugehörigkeit hier nicht ändern.");
            }
            if (await db.Users.CountAsync(u => u.IsAdmin) <= 1)
            {
                throw new InvalidOperationException("Der letzte verbliebene Admin kann nicht zu einem Partner umgewandelt werden.");
            }
        }

        agent.PartnerAgency = agency;
        agent.PartnerRank = partnerRank;
        agent.Rank = null;
        agent.IsTRU = false;
        agent.IsHRB = false;
        agent.IsTeamLead = false;
        agent.IsAdmin = false;

        Audit(agent, AuditAction.Modified, actor, $"Zugehörigkeit → Partner ({PartnerRankDisplay.Full(agency, partnerRank)})");
        await Save(agent, newStamp: true);

        try { await notifications.NotifyAsync(agent.Id, NotificationType.Account, "Deine Zugehörigkeit wurde auf einen Partner-Zugang umgestellt.", "/"); }
        catch { /* best effort */ }
    }

    public async Task ConvertToInternalAsync(string agentId, Rank rank, ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);

        var agent = await GetOrThrow(agentId);
        if (agent.Status != AgentStatus.Active)
        {
            throw new InvalidOperationException("Die Zugehörigkeit kann nur für aktive Konten geändert werden.");
        }
        if (agent.PartnerAgency is null)
        {
            throw new InvalidOperationException("Dieser Account ist bereits ein interner NOOSE-Agent.");
        }

        agent.PartnerAgency = null;
        agent.PartnerRank = null;
        agent.Rank = rank;
        // the inactivity radar starts counting from here
        agent.ActiveSinceAt = DateTime.UtcNow;

        HistoryEntryAdd(agent.Id, null, rank, actor, "Von Partner zu intern");
        Audit(agent, AuditAction.Modified, actor, $"Zugehörigkeit → intern NOOSE ({rank})");
        await Save(agent, newStamp: true);

        try { await notifications.NotifyAsync(agent.Id, NotificationType.Account, "Deine Zugehörigkeit wurde auf einen internen NOOSE-Zugang umgestellt.", "/"); }
        catch { /* best effort */ }
    }

    public async Task PromotionDecideAsync(string requestId, bool approved, string? note, ClaimsPrincipal actor)
    {
        Permission.RequirePromotionDecide(actor);

        var request = await db.AgentPromotionRequests.FirstOrDefaultAsync(a => a.Id == requestId)
            ?? throw new InvalidOperationException($"Beförderungsantrag '{requestId}' nicht gefunden.");
        if (request.Status != PromotionStatus.Requested)
        {
            throw new InvalidOperationException("Über diesen Antrag wurde bereits entschieden.");
        }

        request.Status = approved ? PromotionStatus.Approved : PromotionStatus.Rejected;
        request.DeciderName = actor.GetCodename();
        request.DecidedAt = DateTime.UtcNow;
        request.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        var agent = await GetOrThrow(request.AgentId);
        if (approved)
        {
            var alt = agent.Rank;
            agent.Rank = request.TargetRank;
            if (alt != request.TargetRank)
            {
                HistoryEntryAdd(agent.Id, alt, request.TargetRank, actor, "Beförderung");
            }
            // a request may also lower the rank; the departments cannot survive below their own threshold
            var lost = DropDepartmentsBelowThreshold(agent);
            Audit(agent, AuditAction.Modified, actor,
                $"Beförderung genehmigt: {alt?.ToString() ?? "—"} → {request.TargetRank}{lost}");
            await Save(agent, newStamp: true);
        }
        else
        {
            Audit(agent, AuditAction.Modified, actor,
                $"Beförderungsantrag abgelehnt (Ziel: {request.TargetRank})");
            // request only, no claims refresh
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Reject a release that would hand out a department the rank may not carry.</summary>
    private static void RequireDepartmentsFitRank(Rank rank, bool isTRU, bool isHRB)
    {
        if (isTRU)
        {
            DepartmentRules.RequireMayHold(rank, "Die Tactical Response Unit");
        }
        if (isHRB)
        {
            DepartmentRules.RequireMayHold(rank, "Die Human Resource Branch");
        }
    }

    /// <summary>Strip TRU and HRB once a rank change puts the agent below the threshold; returns an audit suffix.</summary>
    /// <remarks>
    /// Without this a demotion leaves a Junior Agent carrying HRB - a state the guards refuse to create but would
    /// happily keep, and the flag still opens every HRB gate.
    /// </remarks>
    private static string DropDepartmentsBelowThreshold(Agent agent)
    {
        if (DepartmentRules.MayHold(agent.Rank) || !(agent.IsTRU || agent.IsHRB))
        {
            return string.Empty;
        }

        var lost = new List<string>(2);
        if (agent.IsTRU) { agent.IsTRU = false; lost.Add("TRU"); }
        if (agent.IsHRB) { agent.IsHRB = false; lost.Add("HRB"); }
        return $"; {string.Join(" und ", lost)} entfernt (Dienstgrad unter {RankDisplay.DefaultName(DepartmentRules.MinimumRank)})";
    }

    public async Task TruSetAsync(string agentId, bool isTRU, ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        var agent = await GetOrThrow(agentId);
        if (isTRU)
        {
            DepartmentRules.RequireMayHold(agent.Rank, "Die Tactical Response Unit");
        }
        agent.IsTRU = isTRU;

        Audit(agent, AuditAction.Modified, actor, isTRU ? "TRU-Flag gesetzt" : "TRU-Flag entfernt");
        await Save(agent, newStamp: true);
    }

    public async Task HrbSetAsync(string agentId, bool isHRB, ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        var agent = await GetOrThrow(agentId);
        if (isHRB)
        {
            DepartmentRules.RequireMayHold(agent.Rank, "Die Human Resource Branch");
        }
        agent.IsHRB = isHRB;

        Audit(agent, AuditAction.Modified, actor, isHRB ? "HRB-Flag gesetzt" : "HRB-Flag entfernt");
        await Save(agent, newStamp: true);
    }

    public async Task TeamLeadSetAsync(string agentId, bool isTeamLead, ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        var agent = await GetOrThrow(agentId);
        if (agent.IsTeamLead && !isTeamLead)
        {
            // selectable again: the inactivity radar starts counting from here
            agent.ActiveSinceAt = DateTime.UtcNow;
        }
        agent.IsTeamLead = isTeamLead;

        Audit(agent, AuditAction.Modified, actor,
            isTeamLead ? "Als Teamleitung markiert" : "Teamleitung-Markierung entfernt");
        await Save(agent, newStamp: true); // refresh claims
    }

    public async Task AdminSetAsync(string agentId, bool isAdmin, ClaimsPrincipal actor)
    {
        Permission.RequireAdmin(actor);

        var agent = await GetOrThrow(agentId);

        // bootstrap admins re-grant themselves on next login
        if (!isAdmin && IsBootstrapAdmin(agent.DiscordId))
        {
            throw new InvalidOperationException(
                "Bootstrap-Admins behalten ihre Admin-Rechte dauerhaft und können nicht entzogen werden.");
        }

        // guard self-lockout and last admin
        if (!isAdmin && agent.IsAdmin)
        {
            if (actor.GetAgentId() == agentId)
            {
                throw new InvalidOperationException("Du kannst dir nicht selbst die Admin-Rechte entziehen.");
            }
            if (await db.Users.CountAsync(u => u.IsAdmin) <= 1)
            {
                throw new InvalidOperationException("Der letzte verbliebene Admin kann nicht entfernt werden.");
            }
        }

        agent.IsAdmin = isAdmin;

        Audit(agent, AuditAction.Modified, actor, isAdmin ? "Admin-Rechte vergeben" : "Admin-Rechte entzogen");
        await Save(agent, newStamp: true);
    }

    public async Task BlockAsync(string agentId, string reason, ClaimsPrincipal actor)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        if (actor.GetAgentId() == agentId)
        {
            throw new InvalidOperationException("Du kannst dich nicht selbst sperren.");
        }

        var agent = await GetOrThrow(agentId);
        agent.Status = AgentStatus.Blocked;
        agent.BlockedReason = string.IsNullOrWhiteSpace(reason) ? "Notfall-Sperre" : reason;

        Audit(agent, AuditAction.Modified, actor, $"Gesperrt (Kill-Switch): {agent.BlockedReason}");
        await Save(agent, newStamp: true);
    }

    public async Task UnblockAsync(string agentId, ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);

        var agent = await GetOrThrow(agentId);
        if (agent.Status == AgentStatus.Terminated)
        {
            throw new InvalidOperationException(
                "Gekündigte Agenten werden über die Personalakte reaktiviert, nicht über die Entsperrung.");
        }
        agent.Status = AgentStatus.Active;
        agent.BlockedReason = null;
        // a blocked agent could not log in; the inactivity radar starts counting from here
        agent.ActiveSinceAt = DateTime.UtcNow;

        Audit(agent, AuditAction.Modified, actor, "Entsperrt");
        await Save(agent, newStamp: true);
    }

    public async Task TerminateAsync(string agentId, string reason, bool createNote, bool postDiscord,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("Eine Begründung ist für die Kündigung erforderlich.");
        }
        if (actor.GetAgentId() == agentId)
        {
            throw new InvalidOperationException("Du kannst dich nicht selbst kündigen.");
        }

        var agent = await GetOrThrow(agentId);

        if (agent.Status == AgentStatus.Terminated)
        {
            throw new InvalidOperationException("Dieser Agent ist bereits gekündigt.");
        }
        if (agent.Status == AgentStatus.Applicant)
        {
            throw new InvalidOperationException("Bewerber werden über die Bewerbungsverwaltung abgelehnt, nicht gekündigt.");
        }
        // a bootstrap admin re-activates itself on the next login, so the termination would not stick
        if (IsBootstrapAdmin(agent.DiscordId))
        {
            throw new InvalidOperationException(
                "Bootstrap-Admins können nicht gekündigt werden (sie würden sich beim nächsten Login reaktivieren).");
        }
        if (agent.IsAdmin && await db.Users.CountAsync(u => u.IsAdmin, cancellationToken) <= 1)
        {
            throw new InvalidOperationException("Der letzte verbliebene Admin kann nicht gekündigt werden.");
        }

        var trimmed = reason.Trim();
        var actorName = actor.GetCodename();
        var subjectDisplay = string.IsNullOrWhiteSpace(agent.RealName)
            ? agent.Codename
            : $"{agent.RealName} - {agent.Codename}";

        agent.Status = AgentStatus.Terminated;
        agent.TerminatedAt = DateTime.UtcNow;
        agent.TerminatedById = actor.GetAgentId();
        agent.TerminatedByName = actorName;
        agent.TerminationReason = trimmed;
        // a dead account must not keep propping up the last-admin guard
        agent.IsAdmin = false;

        await BlacklistAdd(agent, subjectDisplay, trimmed, actorName, cancellationToken);

        if (createNote)
        {
            db.AgentNotes.Add(new AgentNote
            {
                AgentId = agent.Id,
                Kind = AgentNoteKind.Termination,
                EntryDate = agent.TerminatedAt.Value,
                Text = HtmlCleanup.Clean($"<p>{System.Net.WebUtility.HtmlEncode(trimmed)}</p>"),
                AuthorName = actorName,
            });
        }

        Audit(agent, AuditAction.Modified, actor, $"Gekündigt: {trimmed}");
        // flushes the agent, the blacklist entry, the note and the audit row in one SaveChanges
        await Save(agent, newStamp: true);

        if (postDiscord)
        {
            try
            {
                // the termination channel, not the personnel one: its own embed, and no double post
                await discord.PushTerminationAsync(agent.Id, subjectDisplay, agent.TerminatedById,
                    actorName, agent.TerminatedAt.Value, trimmed, $"/personal/{agent.Id}", cancellationToken);
            }
            catch { /* best effort */ }
        }
    }

    public async Task TerminationRevokeAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        var agent = await GetOrThrow(agentId);
        if (agent.Status != AgentStatus.Terminated)
        {
            throw new InvalidOperationException("Dieser Agent ist nicht gekündigt.");
        }

        agent.Status = AgentStatus.Active;
        agent.TerminatedAt = null;
        agent.TerminatedById = null;
        agent.TerminatedByName = null;
        agent.TerminationReason = null;
        // the inactivity radar starts counting from here
        agent.ActiveSinceAt = DateTime.UtcNow;

        var blacklisted = await db.Bewerbungssperren
            .Where(s => s.AgentId == agentId && s.IsBlacklist)
            .ToListAsync(cancellationToken);
        foreach (var entry in blacklisted)
        {
            db.Bewerbungssperren.Remove(entry); // interceptor soft-deletes
        }

        Audit(agent, AuditAction.Modified, actor, "Kündigung zurückgenommen");
        await Save(agent, newStamp: true);
    }

    /// <summary>Stage a permanent recruitment blacklist entry on the shared context; no-op if one is already active.</summary>
    private async Task BlacklistAdd(Agent agent, string subjectDisplay, string reason, string? actorName,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var active = await db.Bewerbungssperren
            .Where(s => s.AgentId == agent.Id).Where(BewerbungssperreRules.Active(now))
            .ToListAsync(cancellationToken);

        if (active.Any(s => s.IsBlacklist))
        {
            return;
        }
        // the permanent blacklist supersedes any running temporary ban
        foreach (var temp in active)
        {
            db.Bewerbungssperren.Remove(temp);
        }
        db.Bewerbungssperren.Add(new Bewerbungssperre
        {
            AgentId = agent.Id,
            DiscordId = agent.DiscordId,
            ApplicantName = subjectDisplay,
            IsBlacklist = true,
            BannedUntil = null,
            Reason = $"Kündigung: {reason}",
            CreatedByName = actorName,
        });
    }

    public async Task DeleteAccountAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // Bulk SQL bypasses the interceptors, so guard explicitly and first.
        Permission.RequireLeadership(actor);

        if (actor.GetAgentId() == agentId)
        {
            throw new InvalidOperationException("Du kannst deinen eigenen Account nicht löschen.");
        }

        var agent = await GetOrThrow(agentId);

        // bootstrap admins would just re-create themselves on the next login
        if (IsBootstrapAdmin(agent.DiscordId))
        {
            throw new InvalidOperationException(
                "Bootstrap-Admins können nicht gelöscht werden (sie würden sich beim nächsten Login neu anlegen).");
        }

        if (agent.IsAdmin && await db.Users.CountAsync(u => u.IsAdmin, cancellationToken) <= 1)
        {
            throw new InvalidOperationException("Der letzte verbliebene Admin kann nicht gelöscht werden.");
        }

        var codename = agent.Codename;
        var discordId = agent.DiscordId;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Purge the Restrict-FK rows that block the user delete. IgnoreQueryFilters so soft-deleted rows
        // (still physically present, FK still live) are removed too. SetNull/Cascade FKs
        // (Observation, Followup, SavedSearch) are handled by MySQL when the user row drops.
        await db.Watchlists.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.FactionAgents.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.PersonGroupAgents.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.PartyAgents.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.OperationAgents.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.CaseAgents.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.TaskforceAgents.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.JobAssignments.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.AppointmentAssignments.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.AnnouncementAcknowledgments.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.AgentRankHistories.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.AgentNotes.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.AgentPromotionRequests.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.AgentModuleCompletions.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        // internal tests go with the account like the module ticks; answers first, they point at the assignment
        await db.AgentTestAnswers.IgnoreQueryFilters()
            .Where(x => db.AgentTestAssignments.IgnoreQueryFilters().Any(a => a.Id == x.AssignmentId && a.AgentId == agentId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.AgentTestAssignments.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);

        // The account's own working state: nothing here means anything without the account it belonged to, so
        // the rows go rather than the pointer. Badges, quota and KI logs are derived or per-account bookkeeping;
        // an absence, a meeting attendance and a document access revocation only ever describe this one agent.
        await db.Absences.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.Feedbacks.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.AgentBadges.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.DecorationAwards.Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.DocumentAccessExclusions.IgnoreQueryFilters().Where(x => x.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.MeetingAttendances.IgnoreQueryFilters().Where(x => x.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.MeetingSignOffs.IgnoreQueryFilters().Where(x => x.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.LlmQuotaAdjustments.IgnoreQueryFilters().Where(x => x.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.LlmQuotaPeriods.IgnoreQueryFilters().Where(x => x.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.LlmRequests.IgnoreQueryFilters().Where(x => x.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);
        // messages first: the cascade off the thread is a MySQL one, and SQLite runs with foreign keys off
        var conversationIds = await db.NooseiConversations.IgnoreQueryFilters()
            .Where(x => x.AgentId == agentId).Select(x => x.Id).ToListAsync(cancellationToken);
        if (conversationIds.Count > 0)
        {
            await db.NooseiMessages.IgnoreQueryFilters().Where(x => conversationIds.Contains(x.ConversationId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.NooseiConversations.IgnoreQueryFilters().Where(x => x.AgentId == agentId)
                .ExecuteDeleteAsync(cancellationToken);
        }

        // Records that outlive the account and only carry it as a pointer. The citizen profile is the sharpest
        // case: a tip and an objection hold a Restrict FK to the profile and a tip carries an anonymity promise,
        // so the profile stays and loses its account instead. Same shape for a seized item's handler, an
        // informant's handler, an abduction victim and the money history - the row is the record, not the agent.
        await db.BuergerProfile.IgnoreQueryFilters().Where(x => x.UserId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, (string?)null), cancellationToken);
        await db.EvidenceEntries.IgnoreQueryFilters().Where(x => x.HandlerAgentId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.HandlerAgentId, (string?)null), cancellationToken);
        await db.Informants.IgnoreQueryFilters().Where(x => x.HandlerId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.HandlerId, (string?)null), cancellationToken);
        await db.AgentAbductions.IgnoreQueryFilters().Where(x => x.VictimAgentId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.VictimAgentId, (string?)null), cancellationToken);
        await db.FinancingRequests.IgnoreQueryFilters().Where(x => x.AgentId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentId, (string?)null), cancellationToken);
        await db.FinancingBudgetPeriods.IgnoreQueryFilters().Where(x => x.AgentId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentId, (string?)null), cancellationToken);

        // Public area: every one of these is a "who did this" pointer on a row that is history - a publication, a
        // payment, a decision, a message to a citizen - so the row survives the account and only the pointer is
        // dropped. All are nullable and all hold a Restrict FK, which would otherwise refuse the user delete
        // outright: an agent who ever published a notice could not be deleted at all.
        await db.BuergerProfile.IgnoreQueryFilters().Where(x => x.BlockedById == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BlockedById, (string?)null), cancellationToken);
        await db.FahndungKopfgeldAnteile.IgnoreQueryFilters().Where(x => x.DonorAgentId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DonorAgentId, (string?)null), cancellationToken);
        await db.FahndungEinsprueche.IgnoreQueryFilters().Where(x => x.DecidedById == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DecidedById, (string?)null), cancellationToken);
        await db.Hinweise.IgnoreQueryFilters().Where(x => x.HandlerId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.HandlerId, (string?)null), cancellationToken);
        await db.HinweisNachrichten.IgnoreQueryFilters().Where(x => x.AuthorAgentId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthorAgentId, (string?)null), cancellationToken);
        await db.Tickets.IgnoreQueryFilters().Where(x => x.HandlerId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.HandlerId, (string?)null), cancellationToken);
        await db.TicketNachrichten.IgnoreQueryFilters().Where(x => x.AuthorAgentId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthorAgentId, (string?)null), cancellationToken);
        await db.Tickets.IgnoreQueryFilters().Where(x => x.OpenedByAgentId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OpenedByAgentId, (string?)null), cancellationToken);
        // deleted, not nulled: the row IS the permission to read that ticket, and a nameless one would grant nothing
        await db.TicketBeteiligte.Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        // the released chart entry keeps its own copy of every value, so removing the pointer costs nothing
        await db.OeffentlicheFuehrungsprofile.Where(x => x.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.OeffentlicheFuehrungsprofile.IgnoreQueryFilters().Where(x => x.PublishedById == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PublishedById, (string?)null), cancellationToken);
        await db.KassenBuchungen.IgnoreQueryFilters().Where(x => x.BookedById == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BookedById, (string?)null), cancellationToken);
        await db.OeffentlicheFahndungen.IgnoreQueryFilters().Where(x => x.PublishedById == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PublishedById, (string?)null), cancellationToken);
        await db.OeffentlicheSeiten.IgnoreQueryFilters().Where(x => x.PublishedById == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PublishedById, (string?)null), cancellationToken);
        await db.OeffentlicheWarnungen.IgnoreQueryFilters().Where(x => x.PublishedById == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PublishedById, (string?)null), cancellationToken);
        await db.OeffentlicheLageberichte.IgnoreQueryFilters().Where(x => x.PublishedById == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PublishedById, (string?)null), cancellationToken);
        await db.OeffentlicheFraktionsprofile.IgnoreQueryFilters().Where(x => x.PublishedById == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PublishedById, (string?)null), cancellationToken);
        await db.Pressemitteilungen.IgnoreQueryFilters().Where(x => x.PublishedById == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PublishedById, (string?)null), cancellationToken);

        // recruiting: this person's own invites/applications go; applications they merely processed are detached
        await db.AgentInvites.IgnoreQueryFilters().Where(x => x.UsedByUserId == agentId).ExecuteDeleteAsync(cancellationToken);
        // bans hold Restrict FKs to both the agent and the application -> purge before either drops
        await db.Bewerbungssperren.IgnoreQueryFilters().Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.Bewerbungen.IgnoreQueryFilters().Where(x => x.ApplicantUserId == agentId).ExecuteDeleteAsync(cancellationToken);
        await db.Bewerbungen.IgnoreQueryFilters().Where(x => x.AssignedAgentId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AssignedAgentId, (string?)null), cancellationToken);

        // drop the user; Identity cascades AspNetUserLogins/Claims/Tokens/Roles -> Discord link severed
        var result = await userManager.DeleteAsync(agent);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Löschen fehlgeschlagen: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        // forensic trail (AuditLog has no FK to Agent, so it survives the delete)
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            AgentId = actor.GetAgentId(),
            AgentName = actor.GetCodename(),
            EntityType = nameof(Agent),
            EntityId = agentId,
            Action = AuditAction.Deleted,
            ChangesJson = JsonSerializer.Serialize(new { deletedCodename = codename, discordId }),
        });
        await db.SaveChangesAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>True if the Discord ID is a configured bootstrap admin (single or list key).</summary>
    private bool IsBootstrapAdmin(string? discordId)
        => BootstrapAdmins.Contains(configuration, discordId);

    private async Task<Agent> GetOrThrow(string agentId, CancellationToken cancellationToken = default)
    {
        var agent = await db.Users.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Agent '{agentId}' nicht gefunden.");
        await db.Entry(agent).ReloadAsync(cancellationToken);
        return agent;
    }

    /// <summary>Clear the staged picture and hand back the file that is now unreferenced.</summary>
    private static void AvatarPendingEmpty(Agent agent, out string? obsoleteFileName)
    {
        obsoleteFileName = agent.PendingAvatarFileName;
        agent.PendingAvatarFileName = null;
        agent.PendingAvatarContentType = null;
        agent.AvatarRequestedAt = null;
    }

    // a missing file must never fail the write that already succeeded
    private void AvatarFileDelete(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }
        try { avatars.Delete(fileName); }
        catch (IOException) { /* ignore */ }
        catch (InvalidOperationException) { /* ignore */ }
    }

    private static void PendingNameChangeEmpty(Agent agent)
    {
        agent.PendingCodename = null;
        agent.PendingRealName = null;
        agent.PendingBadgeNumber = null;
        agent.NameChangeRequestedAt = null;
    }

    /// <param name="ownValueIsPending">
    /// False on the approval path. There the value under test IS <c>PendingBadgeNumber</c>, so counting that as
    /// "the agent's own" made every approval look like a no-op rename and skipped the conflict query entirely -
    /// a safety net that read as if it worked. Only the currently held number counts as one's own there.
    /// </param>
    private async Task ValidateBadgeNumberAsync(
        Agent agent, string? badgeNumber, bool allowLegacyValue, bool ownValueIsPending = true)
    {
        if (string.IsNullOrWhiteSpace(badgeNumber))
        {
            return;
        }

        var isLegacyValue = string.Equals(BadgeNumbers.Normalize(agent.BadgeNumber), badgeNumber, StringComparison.OrdinalIgnoreCase)
            || (ownValueIsPending
                && string.Equals(BadgeNumbers.Normalize(agent.PendingBadgeNumber), badgeNumber, StringComparison.OrdinalIgnoreCase));
        if (!BadgeNumbers.IsAllowed(badgeNumber) && !(allowLegacyValue && isLegacyValue))
        {
            throw new InvalidOperationException("Die Dienstnummer muss eine römische Zahl zwischen I und XXV sein.");
        }

        // the agent's own value is no conflict with itself: without this, a legacy duplicate
        // between two rows would make the row unsavable without renaming its badge number
        if (isLegacyValue)
        {
            return;
        }

        var assigned = await db.Users.AsNoTracking()
            .Where(a => a.Id != agent.Id && (a.BadgeNumber != null || a.PendingBadgeNumber != null))
            .Select(a => new { a.BadgeNumber, a.PendingBadgeNumber })
            .ToListAsync();
        if (assigned.Any(a => string.Equals(BadgeNumbers.Normalize(a.BadgeNumber), badgeNumber, StringComparison.OrdinalIgnoreCase)
            || string.Equals(BadgeNumbers.Normalize(a.PendingBadgeNumber), badgeNumber, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Diese Dienstnummer ist bereits vergeben.");
        }
    }

    private void HistoryEntryAdd(string agentId, Rank? alt, Rank @new, ClaimsPrincipal actor, string reason)
        => db.AgentRankHistories.Add(new AgentRankHistory
        {
            AgentId = agentId,
            Alt = alt,
            New = @new,
            Timestamp = DateTime.UtcNow,
            ActorName = actor.GetCodename(),
            Reason = reason,
        });

    private void Audit(Agent target, AuditAction action, ClaimsPrincipal actor, string hint)
        => Audit(target, action, actor.GetAgentId(), actor.GetCodename(),
            JsonSerializer.Serialize(new { target = target.Codename, hint }));

    /// <summary>Audit row for a write with no acting principal (self-service through the login callback).</summary>
    private void Audit(Agent target, AuditAction action, string? actorId, string? actorName, string changesJson)
        => db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            AgentId = actorId,
            AgentName = actorName,
            EntityType = nameof(Agent),
            EntityId = target.Id,
            Action = action,
            ChangesJson = changesJson,
        });

    /// <summary>Persist agent; rotate the security stamp to force re-login when claims change.</summary>
    private async Task Save(Agent agent, bool newStamp)
    {
        var result = await userManager.UpdateAsync(agent);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Speichern fehlgeschlagen: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        }
        if (newStamp)
        {
            var stamp = await userManager.UpdateSecurityStampAsync(agent);
            if (!stamp.Succeeded)
            {
                throw new InvalidOperationException(
                    "Sicherheits-Stamp konnte nicht erneuert werden: " + string.Join("; ", stamp.Errors.Select(e => e.Description)));
            }
        }
    }
}
