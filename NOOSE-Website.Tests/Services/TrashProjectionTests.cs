using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Activities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The flattening that lets every record type share one trash table.</summary>
public class TrashProjectionTests
{
    private static readonly DateTime Deleted = new(2026, 3, 12, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Named_records_use_the_name_as_title_and_the_case_number_as_reference()
    {
        var row = TrashProjection.Person(new Person
        {
            Id = "p1", Name = "Marcus Klein", CaseNumber = "FIB-P-2026-0042", DeletedAt = Deleted,
        });

        Assert.Equal("personen", row.Kind);
        Assert.Equal("p1", row.Id);
        Assert.Equal("FIB-P-2026-0042", row.Reference);
        Assert.Equal("Marcus Klein", row.Title);
        Assert.Null(row.Detail);
        Assert.Equal(Deleted, row.DeletedAt);
    }

    [Fact]
    public void Titled_records_use_the_title()
    {
        var row = TrashProjection.Case(new Case
        {
            Id = "c1", Title = "Razzia Vinewood", CaseNumber = "FIB-V-2026-0007", DeletedAt = Deleted,
        });

        Assert.Equal("vorgaenge", row.Kind);
        Assert.Equal("FIB-V-2026-0007", row.Reference);
        Assert.Equal("Razzia Vinewood", row.Title);
        Assert.Null(row.Detail);
    }

    [Fact]
    public void Appointments_keep_their_start_as_detail()
    {
        var start = new DateTime(2026, 3, 12, 14, 0, 0, DateTimeKind.Local);
        var row = TrashProjection.Appointment(new Appointment
        {
            Id = "a1", Title = "Lagebesprechung", CaseNumber = "FIB-T-2026-0001", Start = start, DeletedAt = Deleted,
        });

        Assert.Equal("kalender", row.Kind);
        Assert.Equal("12.03.2026 14:00", row.Detail);
    }

    [Fact]
    public void Meetings_keep_start_place_and_status_as_detail()
    {
        var row = TrashProjection.Meeting(new Meeting
        {
            Id = "m1",
            Title = "Wochenbesprechung",
            CaseNumber = "FIB-B-2026-0003",
            Start = new DateTime(2026, 3, 12, 14, 0, 0, DateTimeKind.Local),
            Location = "Konferenzraum",
            Status = MeetingStatus.Canceled,
            DeletedAt = Deleted,
        });

        Assert.Equal("12.03.2026 14:00 · Konferenzraum · Abgesagt", row.Detail);
    }

    [Fact]
    public void Meetings_without_a_place_omit_the_empty_part()
    {
        var row = TrashProjection.Meeting(new Meeting
        {
            Id = "m2",
            Title = "Kurzabsprache",
            Start = new DateTime(2026, 3, 12, 14, 0, 0, DateTimeKind.Local),
            Location = null,
            Status = MeetingStatus.Planned,
        });

        Assert.Equal("12.03.2026 14:00 · Geplant", row.Detail);
    }

    [Fact]
    public void Activities_have_no_case_number_and_show_their_owner()
    {
        var row = TrashProjection.Activity(new AgentActivityListItem
        {
            Id = "act1", Title = "Streife Sandy Shores", OwnerName = "Falke", DeletedAt = Deleted,
        });

        Assert.Equal("aktivitaeten", row.Kind);
        Assert.Null(row.Reference);
        Assert.Equal("Streife Sandy Shores", row.Title);
        Assert.Equal("Falke", row.Detail);
    }

    [Fact]
    public void Absences_are_identified_by_agent_with_the_period_as_detail()
    {
        var row = TrashProjection.Absence(new Absence
        {
            Id = "ab1",
            AgentId = "agent-7",
            Agent = new Agent { Codename = "Falke" },
            FromDate = new DateOnly(2026, 3, 12),
            ToDate = new DateOnly(2026, 3, 15),
            Days = 4,
            Category = AbsenceCategory.Vacation,
            DeletedAt = Deleted,
        });

        Assert.Equal("abmeldungen", row.Kind);
        Assert.Null(row.Reference);
        Assert.Equal("Falke", row.Title);
        Assert.Equal("12.03.2026 – 15.03.2026 · 4 Tage · Urlaub", row.Detail);
    }

    [Fact]
    public void Firearm_uses_show_who_and_when_but_never_the_reason()
    {
        var row = TrashProjection.FirearmUse(new NOOSE_Website.Data.Entities.Personnel.FirearmUse
        {
            Id = "w1",
            AgentId = "agent-7",
            Agent = new Agent { Codename = "Falke" },
            Timestamp = new DateTime(2026, 3, 12, 14, 0, 0, DateTimeKind.Local),
            Reason = "Warnschuss auf dem Parkdeck",
            DeletedAt = Deleted,
        });

        Assert.Equal("schusswaffengebrauch", row.Kind);
        Assert.Null(row.Reference);
        Assert.Equal("Falke", row.Title);
        Assert.Equal("12.03.2026 14:00", row.Detail);
        Assert.DoesNotContain("Warnschuss", row.Title + row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Tips_show_neither_the_citizen_nor_the_text()
    {
        // the trash list is read by every handler, and the anonymity promise does not pause on deletion
        var row = TrashProjection.Tip(new Hinweis
        {
            Id = "h1",
            CaseNumber = "FIB-H-2026-0001",
            Text = "Gesehen am Hafen, Kennzeichen 12ABC",
            Status = TipStatus.InPruefung,
            DeletedAt = new DateTime(2026, 3, 12, 8, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal("hinweise", row.Kind);
        Assert.Equal("FIB-H-2026-0001", row.Reference);
        Assert.Equal("Bürgerhinweis FIB-H-2026-0001", row.Title);
        Assert.Equal("In Prüfung", row.Detail);
        Assert.DoesNotContain("Hafen", row.Title + row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Absences_fall_back_to_the_agent_id_when_the_agent_is_not_loaded()
    {
        var row = TrashProjection.Absence(new Absence
        {
            Id = "ab2",
            AgentId = "agent-7",
            Agent = null,
            FromDate = new DateOnly(2026, 3, 12),
            ToDate = new DateOnly(2026, 3, 12),
            Days = 1,
            Category = AbsenceCategory.Sick,
        });

        Assert.Equal("agent-7", row.Title);
    }
}
