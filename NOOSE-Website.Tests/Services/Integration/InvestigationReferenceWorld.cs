using System.Security.Claims;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>One investigation with an entry of every type and a cast of viewers, shared by the reference tests.</summary>
public sealed class InvestigationReferenceWorld : IDisposable
{
    public const string Tf = "tf-ref";
    public const string Inv = "aaaaaaaa-0000-4000-8000-000000000001";
    public const string Other = "aaaaaaaa-0000-4000-8000-000000000002";
    public const string Charge = "aaaaaaaa-0000-4000-8000-000000000011";
    public const string Person = "aaaaaaaa-0000-4000-8000-000000000012";
    public const string Evidence = "aaaaaaaa-0000-4000-8000-000000000013";
    public const string Interrogation = "aaaaaaaa-0000-4000-8000-000000000014";
    public const string Task = "aaaaaaaa-0000-4000-8000-000000000015";
    public const string Note = "aaaaaaaa-0000-4000-8000-000000000016";
    /// <summary>Evidence #4, limited to the observer and the state attorney unit.</summary>
    public const string Restricted = "aaaaaaaa-0000-4000-8000-000000000017";
    public const string Photo = "aaaaaaaa-0000-4000-8000-000000000018";
    public const string QaEntry = "aaaaaaaa-0000-4000-8000-000000000019";
    public const string LeadRow = "aaaaaaaa-0000-4000-8000-000000000020";
    public const string RestrictionRow = "aaaaaaaa-0000-4000-8000-000000000021";
    public const string Missing = "bbbbbbbb-0000-4000-8000-000000000099";
    public const string FilePerson = "cccccccc-0000-4000-8000-000000000001";

    public const string CaseNumber = "ER-0042";
    public const string NoteText = "Geheimer Notiztext";

    /// <summary>Every entry type with the id of its row in <see cref="Inv"/>.</summary>
    public static readonly IReadOnlyList<(string Type, string Id)> Entries =
    [
        (nameof(InvestigationCharge), Charge), (nameof(InvestigationPerson), Person), (nameof(InvestigationEvidence), Evidence),
        (nameof(InvestigationInterrogation), Interrogation), (nameof(InvestigationTask), Task), (nameof(InvestigationNote), Note),
    ];

    /// <summary>The file and all its entries.</summary>
    public static readonly IReadOnlyList<(string Type, string Id)> All = [(nameof(Investigation), Inv), .. Entries];

    public SqliteTestContext Ctx { get; } = new();

    public InvestigationReferenceWorld()
    {
        using var db = Ctx.NewContext();
        db.Users.AddRange(
            Seed.Agent("chief", Rank.SectionChief),
            Seed.Agent("sup", Rank.SpecialAgent, configure: a => a.IsTeamLead = true),
            Seed.Agent("tf-lead", Rank.SpecialAgent),
            Seed.Agent("member", Rank.SpecialAgent),
            Seed.Agent("lead", Rank.SpecialAgent),
            Seed.Agent("inv", Rank.SpecialAgent),
            Seed.Agent("obs", Rank.SpecialAgent),
            Seed.Agent("noevid", Rank.SpecialAgent),
            Seed.Agent("manager", Rank.SpecialAgent),
            Seed.Agent("outsider", Rank.SpecialAgent),
            Seed.Agent("messer-agent", Rank.SpecialAgent, configure: a => a.Codename = "Messerschmidt"),
            Seed.Agent("sid", configure: a => Partner(a, PartnerUnits.DojSid, lead: false)),
            Seed.Agent("sid-lead", configure: a => Partner(a, PartnerUnits.DojSid, lead: true)),
            Seed.Agent("sta", configure: a => Partner(a, PartnerUnits.DojStateAttorney, lead: false)),
            Seed.Agent("dojchief", configure: a => Partner(a, PartnerUnits.DojChief, lead: false)));
        db.PartnerUnits.Find(PartnerUnits.DojStateAttorney)!.InvestigationDefaultsJson = """{"beweismittel":1}""";
        db.Taskforces.Add(new Taskforce { Id = Tf, Name = "TF", CaseNumber = "FIB-TF-2026-0042", Scope = TaskforceScope.CrossAgency });
        db.TaskforceAgents.AddRange(
            new TaskforceAgent { TaskforceId = Tf, AgentId = "tf-lead", Role = TaskforceRole.LeadInvestigator },
            new TaskforceAgent { TaskforceId = Tf, AgentId = "member", Role = TaskforceRole.Member });
        db.TaskforcePartnerAgencies.Add(new TaskforcePartnerAgency { TaskforceId = Tf, Agency = PartnerAgency.DoJ });

        db.Investigations.AddRange(
            new Investigation { Id = Inv, TaskforceId = Tf, Title = "Akte Messer", CaseNumber = CaseNumber, OpenedAt = new DateTime(2026, 11, 1) },
            new Investigation { Id = Other, TaskforceId = Tf, Title = "Akte Hafen", CaseNumber = "ER-0050", OpenedAt = new DateTime(2026, 11, 2) });
        db.InvestigationParticipants.AddRange(
            new InvestigationParticipant { Id = LeadRow, InvestigationId = Inv, AgentId = "lead", Role = InvestigationRole.Lead },
            new InvestigationParticipant { InvestigationId = Inv, AgentId = "inv", Role = InvestigationRole.Investigator },
            new InvestigationParticipant { InvestigationId = Inv, AgentId = "obs", Role = InvestigationRole.Observer },
            new InvestigationParticipant { InvestigationId = Inv, AgentId = "noevid", Role = InvestigationRole.Investigator,
                TabAccessJson = """{"beweismittel":0}""" },
            new InvestigationParticipant { InvestigationId = Inv, AgentId = "manager", Role = InvestigationRole.Investigator,
                TabAccessJson = """{"beteiligte":2}""" },
            new InvestigationParticipant { InvestigationId = Inv, AgentId = "sid", Role = InvestigationRole.Investigator,
                TabAccessJson = """{"tatvorwurf":1,"tathergang":1,"personen":1,"beweismittel":1,"vernehmungen":1,"aufgaben":1,"notizen":1,"anordnungen":1,"beteiligte":1,"verlauf":1}""" },
            new InvestigationParticipant { InvestigationId = Inv, PartnerUnitId = PartnerUnits.DojStateAttorney, Role = InvestigationRole.Investigator },
            new InvestigationParticipant { InvestigationId = Other, AgentId = "outsider", Role = InvestigationRole.Lead },
            new InvestigationParticipant { InvestigationId = Other, AgentId = "lead", Role = InvestigationRole.Investigator });

        db.InvestigationCharges.Add(new InvestigationCharge { Id = Charge, InvestigationId = Inv, Paragraph = "§ 12", LawBook = "StGB" });
        db.InvestigationPeople.Add(new InvestigationPerson { Id = Person, InvestigationId = Inv, Name = "Max Mustermann", Role = "Beschuldigter" });
        db.InvestigationEvidence.AddRange(
            new InvestigationEvidence { Id = Evidence, InvestigationId = Inv, Number = 3, Name = "Messer" },
            new InvestigationEvidence { Id = Restricted, InvestigationId = Inv, Number = 4, Name = "Pistole" });
        db.InvestigationEvidencePhotos.Add(new InvestigationEvidencePhoto
        {
            Id = Photo, InvestigationId = Inv, EvidenceId = Evidence, FileNameSaved = "x.png", OriginalName = "x.png", ContentType = "image/png",
        });
        db.InvestigationInterrogations.Add(new InvestigationInterrogation { Id = Interrogation, InvestigationId = Inv, Name = "Erika Muster" });
        db.InvestigationInterrogationEntries.Add(new InvestigationInterrogationEntry
        {
            Id = QaEntry, InterrogationId = Interrogation, Question = "Wo?", Answer = "Dort.",
        });
        db.InvestigationTasks.Add(new InvestigationTask
        {
            Id = Task, InvestigationId = Inv, Text = $"Prüfen {MentionParser.Token("Person", FilePerson)} morgen",
        });
        db.InvestigationNotes.Add(new InvestigationNote
        {
            Id = Note, InvestigationId = Inv, Text = NoteText, CreatedAt = new DateTime(2026, 11, 14, 17, 30, 0, DateTimeKind.Utc),
        });
        db.InvestigationItemRestrictions.AddRange(
            new InvestigationItemRestriction
            {
                Id = RestrictionRow, InvestigationId = Inv, ItemType = nameof(InvestigationEvidence), ItemId = Restricted, AgentId = "obs",
            },
            new InvestigationItemRestriction
            {
                InvestigationId = Inv, ItemType = nameof(InvestigationEvidence), ItemId = Restricted, PartnerUnitId = PartnerUnits.DojStateAttorney,
            });
        db.People.Add(Seed.Person(FilePerson, "Otto Offen"));
        db.SaveChanges();
    }

    private static void Partner(NOOSE_Website.Data.Entities.Agent a, string unitId, bool lead)
    {
        a.PartnerAgency = PartnerAgency.DoJ;
        a.PartnerUnitId = unitId;
        a.IsPartnerUnitLead = lead;
    }

    /// <summary>The principal for a seeded account name.</summary>
    public static ClaimsPrincipal Viewer(string name) => name switch
    {
        "chief" => ClaimsPrincipalBuilder.Agent("chief").WithRank(Rank.SectionChief).Build(),
        "sup" => ClaimsPrincipalBuilder.Agent("sup").WithRank(Rank.SpecialAgent).AsTeamLead().Build(),
        "sid" => ClaimsPrincipalBuilder.Agent("sid").AsPartnerUnit(PartnerAgency.DoJ, PartnerUnits.DojSid).Build(),
        "sid-lead" => ClaimsPrincipalBuilder.Agent("sid-lead").AsPartnerUnit(PartnerAgency.DoJ, PartnerUnits.DojSid, lead: true).Build(),
        "sta" => ClaimsPrincipalBuilder.Agent("sta").AsPartnerUnit(PartnerAgency.DoJ, PartnerUnits.DojStateAttorney).Build(),
        "dojchief" => ClaimsPrincipalBuilder.Agent("dojchief").AsPartnerUnit(PartnerAgency.DoJ, PartnerUnits.DojChief).Build(),
        _ => ClaimsPrincipalBuilder.Agent(name).WithRank(Rank.SpecialAgent).Build(),
    };

    public static ViewerScope Scope(string name) => ViewerScope.From(Viewer(name));

    public void Dispose() => Ctx.Dispose();
}
