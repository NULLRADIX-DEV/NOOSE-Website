using System.Net;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <summary>The skeleton an automatic press draft starts from.</summary>
/// <remarks>
/// Deliberately not a Phase-11 template. That token set is built for plain-text citizen messages: it encodes nothing
/// ("Müller &amp; Sohn"), redacts NAME to a block, falls back to "Bürger/in" for BUERGER and derives its length cap
/// from the ticket message cap. A release is HTML, has no citizen and wants to show the name — four mismatches, and a
/// new token in the shared renderer would travel unexpanded in every citizen template. The text is only a starting
/// point either way: the draft is always edited by hand, because nothing here is ever published automatically.
/// <para>
/// Takes a PublicWantedCard and nothing else, same argument as the Discord push: that record structurally cannot carry
/// a PersonId, the internal FIB-P case number, a codename or a score, so the draft cannot either.
/// </para>
/// </remarks>
public static class PressDraftText
{
    /// <summary>Title, teaser and body for a notice that was just closed.</summary>
    public static (string Title, string Teaser, string Html) ForCapture(PublicWantedCard notice)
    {
        var item = WantedKinds.IsItem(notice.Kind);
        var title = $"{(item ? "Sachfahndung" : "Fahndung")} abgeschlossen: {notice.DisplayName}";
        var teaser = $"Die öffentliche Ausschreibung {notice.CaseNumber} ist abgeschlossen.";
        var body =
            $"Die unter dem Aktenzeichen {notice.CaseNumber} öffentlich ausgeschriebene Fahndung nach "
            + $"{notice.DisplayName} ist abgeschlossen. Der Gegenstand der Ausschreibung wurde "
            + $"{(item ? "sichergestellt" : "gefasst")}.";
        const string closing =
            "Das Federal Investigation Bureau dankt allen Bürgerinnen und Bürgern, die mit ihren "
            + "Hinweisen zum Abschluss beigetragen haben.";

        return (title, teaser, Paragraphs(body, closing));
    }

    /// <summary>Encodes each line and wraps it in a paragraph; the boundary where text becomes markup.</summary>
    private static string Paragraphs(params string[] lines)
        => string.Concat(lines.Select(l => $"<p>{WebUtility.HtmlEncode(l)}</p>"));
}
