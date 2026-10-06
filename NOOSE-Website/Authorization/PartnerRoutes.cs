using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Authorization;

/// <summary>Relative routes a partner may open; everything else is blocked centrally (MainLayout/PrintLayout).</summary>
public static class PartnerRoutes
{
    private static readonly string[] AllowedPrefixes =
    {
        "personen",
        "fraktionen",
        "personengruppen",
        "parteien",
        "operationen",
        "vorgaenge",
        "taskforces",
        "dokumente",
        "gesetze",
        "suche",
    };

    private static readonly string[] BlockedSuffixes = { "/neu", "/bearbeiten", "/papierkorb" };

    // opened by an agency function
    private static readonly (PartnerFeature Feature, string Prefix)[] FeaturePrefixes =
    {
        (PartnerFeature.Graph, "graph"),
        (PartnerFeature.Radio, "funk"),
        (PartnerFeature.SituationReports, "lageberichte"),
        (PartnerFeature.ShareRequests, "anfragen"),
    };

    // create routes a partner may open despite the blanket create-block (document authoring is universal)
    private static readonly string[] AuthoringRoutes = { "dokumente/neu" };

    /// <summary>True for the document editor (create or edit); per-record ownership is enforced server-side.</summary>
    private static bool IsDocumentAuthoringRoute(string path)
        => AuthoringRoutes.Contains(path)
            || (path.StartsWith("dokumente/") && path.EndsWith("/bearbeiten"));

    /// <summary>True if a partner may open this relative path (dashboard and own profile always allowed; function pages only with the agency function).</summary>
    public static bool IsAllowed(string? relativePath, PartnerFeature features = PartnerFeature.None)
    {
        var path = Normalize(relativePath);
        if (path.Length == 0 || path == "dashboard" || path == "profil" || path.StartsWith("profil/"))
        {
            return true;
        }
        if (IsDocumentAuthoringRoute(path))
        {
            return true;
        }
        // a public route is never blocked for a partner: he can open the same page logged out, so the refusal would
        // claim a restriction that does not exist
        if (PublicRoutes.IsPublic("/" + path))
        {
            return true;
        }
        // the citizen portal for the same reason, one step further in: it is open to every signed-in account
        // (MayUseCitizenPortal), and BuergerLayout does not consult this list at all — so blocking it here would
        // refuse a partner exactly one citizen page, the printable one, and nothing else
        if (path == "buerger" || path.StartsWith("buerger/"))
        {
            return true;
        }
        if (BlockedSuffixes.Any(s => ("/" + path).EndsWith(s)))
        {
            return false;
        }
        if (FeaturePrefixes.Any(f => features.HasFlag(f.Feature) && (path == f.Prefix || path.StartsWith(f.Prefix + "/"))))
        {
            return true;
        }
        return AllowedPrefixes.Any(p => path == p || path.StartsWith(p + "/"));
    }

    /// <summary>Strips query/fragment, trims slashes, lowercases.</summary>
    private static string Normalize(string? relativePath)
        => (relativePath ?? string.Empty).Split('?')[0].Split('#')[0].Trim('/').ToLowerInvariant();
}
