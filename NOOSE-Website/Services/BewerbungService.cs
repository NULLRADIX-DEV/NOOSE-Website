using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IBewerbungService" />
public class BewerbungService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumbers,
    ISourcesStorageService storage,
    BewerbungBroadcaster broadcaster,
    IBewerbungssperreService sperren,
    INotificationService notifications,
    IApplicationCaseService applicationCases,
    ILogger<BewerbungService> logger) : IBewerbungService
{
    public async Task<Bewerbung?> GetOwnAsync(ClaimsPrincipal applicant, CancellationToken cancellationToken = default)
    {
        var userId = applicant.GetAgentId();
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Bewerbungen.AsNoTracking()
            .Where(b => b.ApplicantUserId == userId)
            .OrderByDescending(b => b.SubmittedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Bewerbung> SubmitAsync(BewerbungSubmitModel model, Stream? attachment, string? originalName,
        string? contentType, ClaimsPrincipal applicant, long attachmentSize = 0,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireApplicant(applicant);
        var userId = applicant.GetAgentId()
            ?? throw new InvalidOperationException("Kein Benutzerkontext.");

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            throw new InvalidOperationException("Bitte gib deinen Namen an.");
        }
        if (model.StreamConsent is null)
        {
            throw new InvalidOperationException("Bitte gib an, ob deine Bewerbung gestreamt werden darf.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // block only while an open or accepted application exists; rejected/closed may re-apply once unbanned
        bool hasActive = await db.Bewerbungen.AnyAsync(b => b.ApplicantUserId == userId
            && b.Status != BewerbungStatus.Abgelehnt && b.Status != BewerbungStatus.Geschlossen, cancellationToken);
        if (hasActive)
        {
            throw new InvalidOperationException("Es liegt bereits eine Bewerbung für dieses Konto vor.");
        }

        var ban = await sperren.GetActiveAsync(userId, cancellationToken);
        if (ban is { IsBlacklist: true })
        {
            throw new InvalidOperationException("Du kannst dich derzeit nicht beim FIB bewerben.");
        }
        if (ban is { BannedUntil: { } until })
        {
            throw new InvalidOperationException($"Du bist noch bis zum {until.ToLocalTime():dd.MM.yyyy} für Bewerbungen gesperrt.");
        }

        string? fileNameSaved = null;
        if (attachment is not null && !string.IsNullOrWhiteSpace(originalName))
        {
            // unconditional: a browser that reports no content type used to skip the allowlist entirely
            if (!storage.IsAllowedType(contentType ?? string.Empty))
            {
                throw new InvalidOperationException("Dieser Dateityp ist nicht erlaubt.");
            }
            // the size too, server-side: the page's OpenReadStream limit is the client's own bound, and this path
            // is reachable over SignalR without it
            // fail closed: a size of 0 means the caller stated none, and a bound that a caller can skip by
            // omitting it is no bound at all
            if (attachmentSize <= 0)
            {
                throw new InvalidOperationException("Zur Größe des Anhangs liegt keine Angabe vor.");
            }
            // MaxBytes of 0 means the storage declares no limit, not a limit of zero
            if (storage.MaxBytes > 0 && attachmentSize > storage.MaxBytes)
            {
                throw new InvalidOperationException(
                    $"Der Anhang ist zu groß (maximal {storage.MaxBytes / (1024 * 1024)} MB).");
            }
            fileNameSaved = await storage.SaveAsync(attachment, originalName!, cancellationToken);
        }

        var bewerbung = new Bewerbung
        {
            ApplicantUserId = userId,
            AcademicDegree = Trim(model.AcademicDegree),
            Name = model.Name.Trim(),
            BirthDate = model.BirthDate,
            Employer = Trim(model.Employer),
            PriorExperience = Trim(model.PriorExperience),
            CoverLetter = Trim(model.CoverLetter),
            StreamConsent = model.StreamConsent,
            AttachmentFileNameSaved = fileNameSaved,
            AttachmentOriginalName = Trim(originalName),
            AttachmentContentType = Trim(contentType),
            Status = BewerbungStatus.Eingereicht,
            SubmittedAt = DateTime.UtcNow,
        };

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            bewerbung.CaseNumber = await caseNumbers.NextAsync(db, "B", cancellationToken);
            db.Bewerbungen.Add(bewerbung);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            // the copy is the only side effect a rollback does not undo, same as in TipService.SubmitAsync
            if (fileNameSaved is not null)
            {
                try { storage.Delete(fileNameSaved); } catch { /* best effort */ }
            }
            throw;
        }

        try
        {
            var recipients = await HrbRecipientIdsAsync(db, cancellationToken);
            await notifications.NotifyManyAsync(recipients, NotificationType.Recruiting,
                $"Neue Bewerbung: {bewerbung.Name} ({bewerbung.CaseNumber})", $"/bewerbungen/{bewerbung.Id}", userId, cancellationToken);
        }
        catch { /* best effort */ }

        return bewerbung;
    }

    public async Task<List<Bewerbung>> ListAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Bewerbungen.AsNoTracking()
            .OrderByDescending(b => b.SubmittedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Bewerbung?> GetForHrbAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Bewerbungen.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    public async Task<Bewerbung?> GetForFileAccessAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var bewerbung = await db.Bewerbungen.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (bewerbung is null)
        {
            return null;
        }
        var isOwner = bewerbung.ApplicantUserId == actor.GetAgentId();
        return isOwner || actor.IsHrbOrLeadership() ? bewerbung : null;
    }

    public async Task AssignSelfAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var bewerbung = await GetOrThrow(db, id, cancellationToken);
        bewerbung.AssignedAgentId = actor.GetAgentId();
        bewerbung.AssignedAgentName = actor.GetCodename();
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(id);

        // auto-create the Bewerbungsverfahren case + Sicherheitsüberprüfung document; the assignment stands regardless
        try { await applicationCases.EnsureSecurityCheckCaseAsync(id, actor, cancellationToken); }
        catch (Exception ex) { logger.LogError(ex, "Auto case/document provisioning for application {Id} failed.", id); }
    }

    public async Task SetStatusAsync(string id, BewerbungStatus target, string? note, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // write access before the rank check: the ban below is a second write that would fail on its own
        Permission.RequireRecruitingDecision(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var bewerbung = await GetOrThrow(db, id, cancellationToken);

        if (!IsTransitionAllowed(bewerbung.Status, target))
        {
            throw new InvalidOperationException(
                BewerbungStatusDisplay.IsTerminal(bewerbung.Status)
                    ? "Diese Bewerbung ist bereits abgeschlossen."
                    : $"Wechsel von „{BewerbungStatusDisplay.Name(bewerbung.Status)}“ zu „{BewerbungStatusDisplay.Name(target)}“ ist nicht möglich.");
        }

        bewerbung.Status = target;
        if (BewerbungStatusDisplay.IsTerminal(target))
        {
            bewerbung.DecidedByName = actor.GetCodename();
            bewerbung.DecidedAt = DateTime.UtcNow;
            bewerbung.DecisionNote = Trim(note);
        }
        await db.SaveChangesAsync(cancellationToken);

        // the ban runs before the broadcast: a listener refreshed by Report must not read the state in between
        if (target is BewerbungStatus.Abgelehnt or BewerbungStatus.Geschlossen)
        {
            try { await sperren.BanAsync(bewerbung.ApplicantUserId, bewerbung.Id, bewerbung.Name, Trim(note), actor, cancellationToken); }
            catch (Exception ex) { logger.LogError(ex, "Ban after closing application {Id} failed; the decision stands.", id); }
        }

        if (target is BewerbungStatus.Angenommen)
        {
            try
            {
                var ban = await sperren.GetActiveAsync(bewerbung.ApplicantUserId, cancellationToken);
                if (ban is { IsBlacklist: false })
                {
                    await sperren.LiftAsync(ban.Id, actor, cancellationToken); // lift only the temp ban
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Lifting the ban after accepting application {Id} failed.", id); }
        }
        broadcaster.Report(id);

        try
        {
            await notifications.NotifyAsync(bewerbung.ApplicantUserId, NotificationType.Recruiting,
                $"Statusänderung: {BewerbungStatusDisplay.ApplicantName(target)}", "/portal/status", cancellationToken);
        }
        catch { /* best effort */ }

        await MentionNotify.DeltaAsync(notifications, null, bewerbung.DecisionNote,
            "einer Bewerbungs-Entscheidung", nameof(Bewerbung), id, actor, cancellationToken);
    }

    public async Task ReopenAsync(string id, BewerbungStatus target, string? note, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // write access before the rank check: lifting the ban below is a second write
        Permission.RequireRecruitingDecision(actor);
        if (BewerbungStatusDisplay.IsTerminal(target))
        {
            throw new InvalidOperationException("Eine Bewerbung lässt sich nur in einen offenen Schritt zurückstellen.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var bewerbung = await GetOrThrow(db, id, cancellationToken);
        var previous = bewerbung.Status;
        if (previous is not (BewerbungStatus.Angenommen or BewerbungStatus.Abgelehnt))
        {
            throw new InvalidOperationException("Nur angenommene oder abgelehnte Bewerbungen lassen sich wieder öffnen.");
        }

        // same rule as on submit: one running application per account
        bool otherActive = await db.Bewerbungen.AnyAsync(b => b.Id != id && b.ApplicantUserId == bewerbung.ApplicantUserId
            && b.Status != BewerbungStatus.Abgelehnt && b.Status != BewerbungStatus.Geschlossen, cancellationToken);
        if (otherActive)
        {
            throw new InvalidOperationException("Für dieses Konto läuft bereits eine andere Bewerbung.");
        }

        bewerbung.Status = target;
        if (target is BewerbungStatus.Eingereicht or BewerbungStatus.InSicherheitspruefung)
        {
            bewerbung.SecurityCheckPassed = null; // the check runs again
        }
        var decidedBy = bewerbung.DecidedByName;
        var decidedAt = bewerbung.DecidedAt;
        var decisionNote = bewerbung.DecisionNote;
        bewerbung.DecidedByName = null;
        bewerbung.DecidedAt = null;
        bewerbung.DecisionNote = null;

        // the decision fields are cleared, so the internal thread keeps the trace
        var trimmedNote = Trim(note);
        var trace = $"Bewerbung wieder geöffnet ({BewerbungStatusDisplay.Name(previous)} → {BewerbungStatusDisplay.Name(target)}).";
        if (!string.IsNullOrWhiteSpace(decidedBy) || decidedAt is not null)
        {
            trace += $" Vorherige Entscheidung{(string.IsNullOrWhiteSpace(decidedBy) ? "" : $" von {decidedBy}")}"
                     + $"{(decidedAt is { } at ? $" am {at.ToLocalTime():dd.MM.yyyy HH:mm}" : "")}.";
        }
        if (decisionNote is not null)
        {
            trace += $" Damalige Notiz: {decisionNote}";
        }
        if (trimmedNote is not null)
        {
            trace += $" Begründung: {trimmedNote}";
        }
        db.BewerbungMessages.Add(new BewerbungMessage
        {
            BewerbungId = id,
            Audience = BewerbungMessageAudience.Intern,
            Text = trace,
            AuthorName = actor.GetCodename(),
        });
        await db.SaveChangesAsync(cancellationToken);

        // the rejection left a temp ban; lift it before the broadcast so the ban panel reads the new state
        if (previous is BewerbungStatus.Abgelehnt)
        {
            try
            {
                var ban = await sperren.GetActiveAsync(bewerbung.ApplicantUserId, cancellationToken);
                if (ban is { IsBlacklist: false })
                {
                    await sperren.LiftAsync(ban.Id, actor, cancellationToken);
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Lifting the ban after reopening application {Id} failed.", id); }
        }
        broadcaster.Report(id);

        try
        {
            await notifications.NotifyAsync(bewerbung.ApplicantUserId, NotificationType.Recruiting,
                $"Statusänderung: {BewerbungStatusDisplay.ApplicantName(target)}", "/portal/status", cancellationToken);
        }
        catch { /* best effort */ }

        await MentionNotify.DeltaAsync(notifications, null, trimmedNote,
            "einer Bewerbungs-Entscheidung", nameof(Bewerbung), id, actor, cancellationToken);
    }

    public async Task SetSecurityResultAsync(string id, bool passed, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // write access before the rank check: a failed check rejects, and the ban below is a second write
        Permission.RequireRecruitingDecision(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var bewerbung = await GetOrThrow(db, id, cancellationToken);
        if (BewerbungStatusDisplay.IsTerminal(bewerbung.Status))
        {
            throw new InvalidOperationException("Diese Bewerbung ist bereits abgeschlossen.");
        }

        bewerbung.SecurityCheckPassed = passed;
        if (passed)
        {
            bewerbung.Status = BewerbungStatus.ImTest; // security cleared → advance to the test stage
        }
        else
        {
            bewerbung.Status = BewerbungStatus.Abgelehnt;
            bewerbung.DecidedByName = actor.GetCodename();
            bewerbung.DecidedAt = DateTime.UtcNow;
            bewerbung.DecisionNote = "Sicherheitsüberprüfung nicht bestanden.";
        }
        await db.SaveChangesAsync(cancellationToken);

        // ban first, then broadcast: the detail page refreshes its ban panel off Report
        if (!passed)
        {
            try { await sperren.BanAsync(bewerbung.ApplicantUserId, bewerbung.Id, bewerbung.Name, bewerbung.DecisionNote, actor, cancellationToken); }
            catch (Exception ex) { logger.LogError(ex, "Ban after a failed security check on application {Id} failed; the rejection stands.", id); }
        }
        broadcaster.Report(id);

        try
        {
            await notifications.NotifyAsync(bewerbung.ApplicantUserId, NotificationType.Recruiting,
                passed ? $"Statusänderung: {BewerbungStatusDisplay.ApplicantName(BewerbungStatus.ImTest)}" : "Deine Bewerbung wurde abgeschlossen.",
                "/portal/status", cancellationToken);
        }
        catch { /* best effort */ }
    }

    public async Task LinkPersonAsync(string id, string? personId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var bewerbung = await GetOrThrow(db, id, cancellationToken);

        if (string.IsNullOrWhiteSpace(personId))
        {
            bewerbung.LinkedPersonId = null;
        }
        else
        {
            if (!await db.People.AnyAsync(p => p.Id == personId, cancellationToken))
            {
                throw new InvalidOperationException("Die ausgewählte Person wurde nicht gefunden.");
            }
            bewerbung.LinkedPersonId = personId;
        }
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(id);
    }

    public async Task<LinkedPersonInfo?> GetLinkedPersonAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var personId = await db.Bewerbungen.AsNoTracking()
            .Where(b => b.Id == id).Select(b => b.LinkedPersonId).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(personId))
        {
            return null;
        }
        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId, cancellationToken);
        if (person is null)
        {
            return null;
        }
        // hide a classified person from HRB members without classified-read
        if (person.IsClassified && !actor.MayClassifiedRead())
        {
            return null;
        }
        return new LinkedPersonInfo(person.Id, person.Name, person.CaseNumber,
            person.ThreatScore, person.ThreatConfidence, person.ScoreCalculatedAt, person.IsClassified);
    }

    public async Task<List<BewerbungMessage>> GetMessagesAsync(string id, BewerbungMessageAudience audience, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await EnsureCanReadAsync(db, id, audience, actor, cancellationToken);
        var messages = await db.BewerbungMessages.AsNoTracking()
            .Where(m => m.BewerbungId == id && m.Audience == audience)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        // the applicant never learns which agent answered
        if (audience == BewerbungMessageAudience.Bewerber && !actor.IsHrbOrLeadership())
        {
            foreach (var m in messages)
            {
                m.AuthorName = null;
            }
        }
        return messages;
    }

    public async Task<BewerbungMessage> PostInternalAsync(string id, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        var content = (text ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            throw new InvalidOperationException("Die Nachricht darf nicht leer sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        _ = await GetOrThrow(db, id, cancellationToken);

        var message = new BewerbungMessage
        {
            BewerbungId = id,
            Audience = BewerbungMessageAudience.Intern,
            Text = content,
            AuthorName = actor.GetCodename(),
        };
        db.BewerbungMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(id);

        try
        {
            var who = string.IsNullOrWhiteSpace(actor.GetCodename()) ? "Ein Agent" : actor.GetCodename();
            await notifications.NotifyMentionedAsync(content, $"{who} hat dich in einer Bewerbung erwähnt.",
                SearchNavigation.For(nameof(Bewerbung), id), nameof(Bewerbung), id, actor, cancellationToken);
        }
        catch { /* best effort */ }

        return message;
    }

    public async Task<BewerbungMessage> EditInternalMessageAsync(string messageId, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        var content = (text ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            throw new InvalidOperationException("Die Nachricht darf nicht leer sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var message = await db.BewerbungMessages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.Audience == BewerbungMessageAudience.Intern, cancellationToken);
        if (message is null)
        {
            throw new InvalidOperationException("Die Nachricht wurde nicht gefunden.");
        }
        if (message.CreatedById != actor.GetAgentId())
        {
            throw new InvalidOperationException("Nur eigene Nachrichten können bearbeitet werden.");
        }

        message.Text = content;
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(message.BewerbungId);
        return message;
    }

    public async Task<BewerbungMessage> PostToApplicantAsync(string id, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        var content = (text ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            throw new InvalidOperationException("Die Nachricht darf nicht leer sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var bewerbung = await GetOrThrow(db, id, cancellationToken);

        // sanitize the rich-text HTML, then redact the sender's name token (the applicant is addressed by name)
        content = HtmlCleanup.Clean(content);
        content = BewerbungTemplateRenderer.Redact(content, bewerbung.Name);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Die Nachricht darf nicht leer sein.");
        }

        var message = new BewerbungMessage
        {
            BewerbungId = id,
            Audience = BewerbungMessageAudience.Bewerber,
            Text = content,
            AuthorName = actor.GetCodename(),
            AuthorIsApplicant = false,
        };
        db.BewerbungMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(id);

        try
        {
            await notifications.NotifyAsync(bewerbung.ApplicantUserId, NotificationType.Recruiting,
                "Neue Nachricht zu deiner Bewerbung", "/portal/status", cancellationToken);
        }
        catch { /* best effort */ }

        return message;
    }

    public async Task<BewerbungMessage> PostAsApplicantAsync(string id, string text, ClaimsPrincipal applicant, CancellationToken cancellationToken = default)
    {
        Permission.RequireApplicant(applicant);
        var content = (text ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            throw new InvalidOperationException("Die Nachricht darf nicht leer sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var bewerbung = await GetOrThrow(db, id, cancellationToken);
        if (bewerbung.ApplicantUserId != applicant.GetAgentId())
        {
            throw new UnauthorizedAccessException("Das ist nicht deine Bewerbung.");
        }

        // sanitize the applicant's rich-text HTML before it is rendered to HRB
        content = HtmlCleanup.Clean(content);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Die Nachricht darf nicht leer sein.");
        }

        var message = new BewerbungMessage
        {
            BewerbungId = id,
            Audience = BewerbungMessageAudience.Bewerber,
            Text = content,
            AuthorIsApplicant = true,
        };
        db.BewerbungMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(id);

        try
        {
            var recipient = bewerbung.AssignedAgentId;
            if (!string.IsNullOrEmpty(recipient))
            {
                await notifications.NotifyAsync(recipient, NotificationType.Recruiting,
                    $"Bewerber-Antwort ({bewerbung.CaseNumber})", $"/bewerbungen/{id}", cancellationToken);
            }
            else
            {
                var recipients = await HrbRecipientIdsAsync(db, cancellationToken);
                await notifications.NotifyManyAsync(recipients, NotificationType.Recruiting,
                    $"Bewerber-Antwort ({bewerbung.CaseNumber})", $"/bewerbungen/{id}", applicant.GetAgentId(), cancellationToken);
            }
        }
        catch { /* best effort */ }

        return message;
    }

    private async Task EnsureCanReadAsync(AppDbContext db, string id, BewerbungMessageAudience audience, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (audience == BewerbungMessageAudience.Intern)
        {
            Permission.RequireHrbOrLeadership(actor);
            return;
        }
        // Bewerber audience: owner or HRB/leadership
        if (actor.IsHrbOrLeadership())
        {
            return;
        }
        var ownerId = await db.Bewerbungen.AsNoTracking()
            .Where(b => b.Id == id).Select(b => b.ApplicantUserId).FirstOrDefaultAsync(cancellationToken);
        if (ownerId is null || ownerId != actor.GetAgentId())
        {
            throw new UnauthorizedAccessException("Diese Konversation ist für dich nicht zugänglich.");
        }
    }

    private static async Task<Bewerbung> GetOrThrow(AppDbContext db, string id, CancellationToken cancellationToken)
        => await db.Bewerbungen.FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
           ?? throw new InvalidOperationException("Bewerbung nicht gefunden.");

    private static Task<List<string>> HrbRecipientIdsAsync(AppDbContext db, CancellationToken cancellationToken)
        => db.Users.OnlySelectable()
            .Where(u => u.IsHRB || u.IsAdmin || u.Rank >= Rank.SectionChief)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

    private static bool IsTransitionAllowed(BewerbungStatus current, BewerbungStatus target)
    {
        // re-deciding between the two decision states stays open
        if ((current, target) is (BewerbungStatus.Angenommen, BewerbungStatus.Abgelehnt)
                              or (BewerbungStatus.Abgelehnt, BewerbungStatus.Angenommen))
        {
            return true;
        }
        // a closed application is final
        if (BewerbungStatusDisplay.IsTerminal(current))
        {
            return false;
        }
        // closing or rejecting is allowed from any non-terminal state
        if (target is BewerbungStatus.Abgelehnt or BewerbungStatus.Geschlossen)
        {
            return true;
        }
        return (current, target) switch
        {
            (BewerbungStatus.Eingereicht, BewerbungStatus.InSicherheitspruefung) => true,
            (BewerbungStatus.InSicherheitspruefung, BewerbungStatus.ImTest) => true,
            (BewerbungStatus.InSicherheitspruefung, BewerbungStatus.ImVorstellungsgespraech) => true,
            (BewerbungStatus.ImTest, BewerbungStatus.ImVorstellungsgespraech) => true,
            (BewerbungStatus.ImVorstellungsgespraech, BewerbungStatus.Angenommen) => true,
            _ => false,
        };
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
