using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IPlatzhalterService" />
public partial class PlaceholderService(IDbContextFactory<AppDbContext> dbFactory) : IPlaceholderService
{
    public IReadOnlyList<(string Token, string Description)> AvailablePlaceholder { get; } = new[]
    {
        ("{{Name}}", "Name der Akte bzw. des Agenten, an die/den der Text gehängt wird"),
        ("{{Aktenzeichen}}", "Aktenzeichen dieser Akte (Agenten haben keins)"),
        ("{{Datum}}", "Aktuelles Datum (TT.MM.JJJJ)"),
        ("{{Uhrzeit}}", "Aktuelle Uhrzeit (HH:MM)"),
        ("{{Agent}}", "Dein Codename"),
        ("{{Dienstgrad}}", "Dein Dienstgrad"),
    };

    public Task<string> ApplyAsync(string html, string? entityType, string? entityId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => ReplaceAsync(html, entityType, entityId, actor, encode: true, keepUnresolved: false, cancellationToken);

    public Task<string> ApplyPlainAsync(string text, string? entityType, string? entityId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => ReplaceAsync(text, entityType, entityId, actor, encode: false, keepUnresolved: true, cancellationToken);

    /// <param name="encode">HTML-encode the substituted values. False for a plain-text target.</param>
    /// <param name="keepUnresolved">Leave a record token standing when no record answered for it.</param>
    private async Task<string> ReplaceAsync(string input, string? entityType, string? entityId, ClaimsPrincipal actor,
        bool encode, bool keepUnresolved, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var name = string.Empty;
        var caseNumber = string.Empty;
        var resolved = false;

        if (!string.IsNullOrWhiteSpace(entityType) && !string.IsNullOrWhiteSpace(entityId))
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            // resolve visible records only (no classified names for non-leadership)
            if (await Visibility.IsRecordVisibleAsync(db, entityType!, entityId!, ViewerScope.From(actor), cancellationToken))
            {
                if (await RecordNameAsync(db, entityType!, entityId!, cancellationToken) is { } record)
                {
                    name = record.Name;
                    caseNumber = record.CaseNumber;
                    resolved = true;
                }
            }
        }

        var now = DateTime.Now;
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Datum"] = now.ToString("dd.MM.yyyy"),
            ["Uhrzeit"] = now.ToString("HH:mm"),
            ["Agent"] = actor.GetCodename() ?? string.Empty,
            ["Dienstgrad"] = actor.GetRank() is { } dg ? RankDisplay.Name(dg) : string.Empty,
        };
        // the two record tokens only join the table once a record answered, so the plain path can tell
        // "there was nothing to put here" from "there is nothing here at all"
        if (resolved || !keepUnresolved)
        {
            replacements["Name"] = name;
            replacements["Aktenzeichen"] = caseNumber;
        }

        // leave unknown tokens untouched
        return TokenRegex().Replace(input, m =>
        {
            var key = m.Groups[1].Value;
            if (!replacements.TryGetValue(key, out var value))
            {
                return m.Value;
            }
            return encode ? System.Net.WebUtility.HtmlEncode(value) : value;
        });
    }

    /// <summary>Name and case number of the record, or null when the type is none this method knows.</summary>
    /// <remarks>
    /// Null is not the same as two empty strings. A caller that leaves a token standing when nothing answered
    /// has to tell "there is no such record" from "the record has no case number" - and a comment hangs on
    /// plenty of types this method never learned (a meeting, an appointment, an evidence item).
    /// </remarks>
    private static async Task<(string Name, string CaseNumber)?> RecordNameAsync(AppDbContext db, string type, string id, CancellationToken ct)
    {
        switch (type)
        {
            case nameof(Person):
            {
                var x = await db.People.Where(p => p.Id == id).Select(p => new { p.Name, p.CaseNumber }).FirstOrDefaultAsync(ct);
                return x is null ? null : (x.Name ?? string.Empty, x.CaseNumber ?? string.Empty);
            }
            case nameof(Faction):
            {
                var x = await db.Factions.Where(f => f.Id == id).Select(f => new { f.Name, f.CaseNumber }).FirstOrDefaultAsync(ct);
                return x is null ? null : (x.Name ?? string.Empty, x.CaseNumber ?? string.Empty);
            }
            case nameof(PersonGroup):
            {
                var x = await db.PersonGroups.Where(g => g.Id == id).Select(g => new { g.Name, g.CaseNumber }).FirstOrDefaultAsync(ct);
                return x is null ? null : (x.Name ?? string.Empty, x.CaseNumber ?? string.Empty);
            }
            case nameof(Party):
            {
                var x = await db.Parties.Where(p => p.Id == id).Select(p => new { p.Name, p.CaseNumber }).FirstOrDefaultAsync(ct);
                return x is null ? null : (x.Name ?? string.Empty, x.CaseNumber ?? string.Empty);
            }
            case nameof(DrugRoute):
            {
                var x = await db.DrugRoutes.Where(r => r.Id == id).Select(r => new { Name = r.Designation, r.CaseNumber }).FirstOrDefaultAsync(ct);
                return x is null ? null : (x.Name ?? string.Empty, x.CaseNumber ?? string.Empty);
            }
            case nameof(Operation):
            {
                var x = await db.Operations.Where(o => o.Id == id).Select(o => new { Name = o.Title, o.CaseNumber }).FirstOrDefaultAsync(ct);
                return x is null ? null : (x.Name ?? string.Empty, x.CaseNumber ?? string.Empty);
            }
            case nameof(Taskforce):
            {
                var x = await db.Taskforces.Where(t => t.Id == id).Select(t => new { t.Name, t.CaseNumber }).FirstOrDefaultAsync(ct);
                return x is null ? null : (x.Name ?? string.Empty, x.CaseNumber ?? string.Empty);
            }
            case nameof(Case):
            {
                var x = await db.Cases.Where(v => v.Id == id).Select(v => new { Name = v.Title, v.CaseNumber }).FirstOrDefaultAsync(ct);
                return x is null ? null : (x.Name ?? string.Empty, x.CaseNumber ?? string.Empty);
            }
            case nameof(Job):
            {
                var x = await db.Jobs.Where(a => a.Id == id).Select(a => new { Name = a.Title, a.CaseNumber }).FirstOrDefaultAsync(ct);
                return x is null ? null : (x.Name ?? string.Empty, x.CaseNumber ?? string.Empty);
            }
            case nameof(Agent):
            {
                // codename, never the real name — personnel templates are read by non-real-name viewers too
                var x = await db.Users.Where(a => a.Id == id).Select(a => a.Codename).FirstOrDefaultAsync(ct);
                return x is null ? null : (x, string.Empty);
            }
            default:
                return null;
        }
    }

    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}")]
    private static partial Regex TokenRegex();
}
