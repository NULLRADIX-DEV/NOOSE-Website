namespace NOOSE_Website.Authorization;

/// <summary>Name constants for all authorization policies.</summary>
public static class Policies
{
    /// <summary>Signed in with status Active. App-wide default.</summary>
    public const string ActiveAgent = "AktiverAgent";

    /// <summary>Leadership: rank ≥ Supervisory Special Agent or admin.</summary>
    public const string Leadership = "Fuehrung";

    /// <summary>The write half of <see cref="Leadership"/>: the changelog editor on /einstellungen.</summary>
    /// <remarks>
    /// Mirrors <c>Permission.RequireChangelogWrite</c>. <see cref="LeadershipPage"/> has no write check, so it
    /// opens the editorial controls for the read-only supervision and for the demo principal - which carries
    /// Director - and both then get a refusal from the service after filling in a dialog.
    /// </remarks>
    public const string LeadershipWrite = "FuehrungSchreiben";

    /// <summary>Technical system role.</summary>
    public const string Admin = "Admin";

    /// <summary>Configured bootstrap admin: only gate for demo mode and demo data.</summary>
    public const string BootstrapAdmin = "BootstrapAdmin";

    /// <summary>Configured AI owner: the only one who may change NOOSEI quotas; everyone else reads them.</summary>
    public const string AiOwner = "KiEigner";

    /// <summary>May write at all (everyone except read-only supervision). For mutation controls in AuthorizeView.</summary>
    public const string WriteAccess = "Schreibrecht";

    /// <summary>May author a library document: writers or external partners (create only).</summary>
    public const string DocumentAuthor = "DokumentErstellen";

    /// <summary>Read-only supervision active. For the global read-only banner.</summary>
    public const string OnlyReadMode = "NurLeseModus";

    /// <summary>External partner (DoJ/LSPD/LSMD): reduced read-only navigation and views.</summary>
    public const string PartnerView = "PartnerAnsicht";

    /// <summary>Internal NOOSE agent (not a partner): full navigation and internal pages.</summary>
    public const string InternalAgent = "InternerAgent";

    /// <summary>Leadership page access: leadership OR read-only supervision (opens read-only; write buttons stay on Leadership).</summary>
    public const string LeadershipPage = "FuehrungSeite";

    /// <summary>Highest-classification page access: like HighestClassification plus read-only supervision.</summary>
    public const string HighestClassificationPage = "HoechsteEinstufungSeite";

    /// <summary>Admin page access: admin OR read-only supervision (opens read-only).</summary>
    public const string AdminPage = "AdminSeite";

    /// <summary>Counter-intelligence cockpit: leadership but never read-only supervision.</summary>
    public const string CounterIntel = "Gegenaufklaerung";

    /// <summary>Set "secured state-threatening" directly: rank ≥ Senior Special Agent or admin.</summary>
    public const string HighestClassification = "HoechsteEinstufung";

    /// <summary>Decide promotions: rank ≥ Deputy Director or admin.</summary>
    public const string PromotionDecide = "BefoerderungEntscheiden";

    /// <summary>Decide promotions AND be allowed to write.</summary>
    /// <remarks>
    /// Mirrors <c>Permission.RequirePromotionDecide</c>. The plain policy has no write check, so it shows the
    /// approve and reject buttons to the read-only supervision and to the demo visitor - both carry Director.
    /// </remarks>
    public const string PromotionDecideWrite = "BefoerderungEntscheidenSchreiben";

    /// <summary>Move agents between divisions AND be allowed to write: Board or admin.</summary>
    /// <remarks>
    /// Mirrors <c>Permission.RequireDivisionSet</c>. A rank-only check would show the division picker to the
    /// read-only supervision and to the demo visitor, who carries Director, and refuse after the dialog.
    /// </remarks>
    public const string DivisionSetWrite = "AbteilungSetzenSchreiben";

    /// <summary>Applicant portal access: signed in with status Applicant.</summary>
    public const string ApplicantPortal = "BewerberPortal";

    /// <summary>Recruiting management access: HRB member or leadership.</summary>
    public const string HrbOrLeadership = "HrbOderFuehrung";

    /// <summary>The write half of <see cref="HrbOrLeadership"/>: the handbook, and ticking off a module.</summary>
    /// <remarks>
    /// Mirrors <c>Permission.RequireHrbOrLeadershipWrite</c>. The plain policy has no write check, so it opens
    /// the editorial controls for the read-only supervision and for the demo principal - which carries HRB and
    /// Director together - and both then get a refusal from the service after filling in a dialog.
    /// </remarks>
    public const string HrbOrLeadershipWrite = "HrbOderFuehrungSchreiben";

    /// <summary>Citizen portal access: signed in with status Civilian.</summary>
    public const string CitizenPortal = "BuergerPortal";
}
