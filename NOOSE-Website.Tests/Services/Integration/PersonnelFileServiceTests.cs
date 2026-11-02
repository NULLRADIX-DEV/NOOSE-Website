using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for PersonnelFileService against in-memory SQLite.</summary>
public sealed class PersonnelFileServiceTests
{
    private static PersonnelFileService NewService(SqliteTestContext ctx, IDiscordWebhookService? discord = null)
        => new(ctx.Factory, discord ?? Substitute.For<IDiscordWebhookService>());

    private static ClaimsPrincipal Leadership(string id = "lead-1", string codename = "Falcon")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgentInCharge).WithCodename(codename).Build();

    private static ClaimsPrincipal Rank1(string id = "rookie-1")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static DateTime Utc(int day) => new(2026, 3, day, 12, 0, 0, DateTimeKind.Utc);

    // ---- GetRankHistoryAsync ----

    [Fact]
    public async Task GetRankHistoryAsync_ReturnsOnlyAgentEntries_NewestFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentRankHistories.Add(new AgentRankHistory { AgentId = "a1", New = Rank.Trainee, Timestamp = Utc(1) });
            db.AgentRankHistories.Add(new AgentRankHistory { AgentId = "a1", Alt = Rank.Trainee, New = Rank.SpecialAgent, Timestamp = Utc(3) });
            db.AgentRankHistories.Add(new AgentRankHistory { AgentId = "a1", Alt = Rank.SpecialAgent, New = Rank.SeniorSpecialAgent, Timestamp = Utc(2) });
            db.AgentRankHistories.Add(new AgentRankHistory { AgentId = "other", New = Rank.Trainee, Timestamp = Utc(5) });
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetRankHistoryAsync("a1");

        Assert.Equal(3, result.Count);
        Assert.All(result, r => Assert.Equal("a1", r.AgentId));
        Assert.Equal(Utc(3), result[0].Timestamp);
        Assert.Equal(Utc(2), result[1].Timestamp);
        Assert.Equal(Utc(1), result[2].Timestamp);
    }

    // ---- GetNotesAsync ----

    [Fact]
    public async Task GetNotesAsync_FiltersByAgentAndKind_NewestFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentNotes.Add(new AgentNote { AgentId = "a1", Kind = AgentNoteKind.Commendation, Text = "old", CreatedAt = Utc(1) });
            db.AgentNotes.Add(new AgentNote { AgentId = "a1", Kind = AgentNoteKind.Commendation, Text = "new", CreatedAt = Utc(4) });
            db.AgentNotes.Add(new AgentNote { AgentId = "a1", Kind = AgentNoteKind.Disciplinary, Text = "other-kind", CreatedAt = Utc(5) });
            db.AgentNotes.Add(new AgentNote { AgentId = "a2", Kind = AgentNoteKind.Commendation, Text = "other-agent", CreatedAt = Utc(6) });
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetNotesAsync("a1", AgentNoteKind.Commendation);

        Assert.Equal(2, result.Count);
        Assert.All(result, n => Assert.Equal(AgentNoteKind.Commendation, n.Kind));
        Assert.All(result, n => Assert.Equal("a1", n.AgentId));
        Assert.Equal("new", result[0].Text);
        Assert.Equal("old", result[1].Text);
    }

    // ---- NoteCreateAsync ----

    [Fact]
    public async Task NoteCreateAsync_PersistsNote_WithAuthorCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("target-1"));
            db.SaveChanges();
        }

        var note = await NewService(ctx).NoteCreateAsync(
            "target-1", AgentNoteKind.Disciplinary, null, Utc(1), Array.Empty<string>(),
            "<p>Zu spät erschienen</p>", Leadership(codename: "Falcon"));

        Assert.Equal("target-1", note.AgentId);
        Assert.Equal(AgentNoteKind.Disciplinary, note.Kind);
        Assert.Equal("Falcon", note.AuthorName);
        Assert.Equal(Utc(1), note.EntryDate);
        Assert.Contains("Zu spät erschienen", note.Text);

        using var verify = ctx.NewContext();
        var stored = verify.AgentNotes.Single();
        Assert.Equal(note.Id, stored.Id);
        Assert.Equal("target-1", stored.AgentId);
    }

    [Fact]
    public async Task NoteCreateAsync_NonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("target-1"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewService(ctx).NoteCreateAsync("target-1", AgentNoteKind.Commendation, null, Utc(1), Array.Empty<string>(), "<p>Lob</p>", Rank1()));

        using var verify = ctx.NewContext();
        Assert.Equal(0, verify.AgentNotes.Count());
    }

    [Fact]
    public async Task NoteCreateAsync_EmptyContent_Throws()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("target-1"));
            db.SaveChanges();
        }

        // Quill's empty editor collapses to whitespace after tag-stripping.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).NoteCreateAsync("target-1", AgentNoteKind.Commendation, null, Utc(1), Array.Empty<string>(), "<p><br></p>", Leadership()));

        using var verify = ctx.NewContext();
        Assert.Equal(0, verify.AgentNotes.Count());
    }

    [Fact]
    public async Task NoteCreateAsync_UnknownAgent_Throws()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).NoteCreateAsync("ghost", AgentNoteKind.Commendation, null, Utc(1), Array.Empty<string>(), "<p>Lob</p>", Leadership()));

        using var verify = ctx.NewContext();
        Assert.Equal(0, verify.AgentNotes.Count());
    }

    // ---- NoteDeleteAsync ----

    [Fact]
    public async Task NoteDeleteAsync_Leadership_RemovesNote()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentNotes.Add(new AgentNote { Id = "n1", AgentId = "a1", Kind = AgentNoteKind.Disciplinary, Text = "x", CreatedById = "someone-else" });
            db.SaveChanges();
        }

        await NewService(ctx).NoteDeleteAsync("n1", Leadership());

        using var verify = ctx.NewContext();
        Assert.False(verify.AgentNotes.Any(n => n.Id == "n1"));
    }

    [Fact]
    public async Task NoteDeleteAsync_Author_RemovesOwnNote()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentNotes.Add(new AgentNote { Id = "n1", AgentId = "a1", Kind = AgentNoteKind.Commendation, Text = "x", CreatedById = "author-1" });
            db.SaveChanges();
        }

        // Non-leadership, but the author of the note.
        var author = ClaimsPrincipalBuilder.Agent("author-1").WithRank(Rank.Trainee).Build();
        await NewService(ctx).NoteDeleteAsync("n1", author);

        using var verify = ctx.NewContext();
        Assert.False(verify.AgentNotes.Any(n => n.Id == "n1"));
    }

    [Fact]
    public async Task NoteDeleteAsync_NonAuthorNonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentNotes.Add(new AgentNote { Id = "n1", AgentId = "a1", Kind = AgentNoteKind.Commendation, Text = "x", CreatedById = "author-1" });
            db.SaveChanges();
        }

        var stranger = ClaimsPrincipalBuilder.Agent("stranger-1").WithRank(Rank.Trainee).Build();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => NewService(ctx).NoteDeleteAsync("n1", stranger));

        using var verify = ctx.NewContext();
        Assert.True(verify.AgentNotes.Any(n => n.Id == "n1"));
    }

    [Fact]
    public async Task NoteDeleteAsync_MissingNote_NoOp()
    {
        using var ctx = new SqliteTestContext();

        // Missing note returns silently, even for a non-leadership actor.
        await NewService(ctx).NoteDeleteAsync("nope", Rank1());

        using var verify = ctx.NewContext();
        Assert.Equal(0, verify.AgentNotes.Count());
    }

    // ---- GetPromotionRequestsAsync ----

    [Fact]
    public async Task GetPromotionRequestsAsync_FiltersByAgent_NewestFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentPromotionRequests.Add(new AgentPromotionRequest { AgentId = "a1", TargetRank = Rank.SpecialAgent, Status = PromotionStatus.Requested, CreatedAt = Utc(1) });
            db.AgentPromotionRequests.Add(new AgentPromotionRequest { AgentId = "a1", TargetRank = Rank.SeniorSpecialAgent, Status = PromotionStatus.Approved, CreatedAt = Utc(4) });
            db.AgentPromotionRequests.Add(new AgentPromotionRequest { AgentId = "a2", TargetRank = Rank.SpecialAgent, Status = PromotionStatus.Requested, CreatedAt = Utc(5) });
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetPromotionRequestsAsync("a1");

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("a1", r.AgentId));
        Assert.Equal(Utc(4), result[0].CreatedAt);
        Assert.Equal(Utc(1), result[1].CreatedAt);
    }

    // ---- GetOpenPromotionRequestsAsync ----

    [Fact]
    public async Task GetOpenPromotionRequestsAsync_ReturnsOnlyRequested_OldestFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentPromotionRequests.Add(new AgentPromotionRequest { AgentId = "a1", TargetRank = Rank.SpecialAgent, Status = PromotionStatus.Requested, CreatedAt = Utc(3) });
            db.AgentPromotionRequests.Add(new AgentPromotionRequest { AgentId = "a2", TargetRank = Rank.SpecialAgent, Status = PromotionStatus.Requested, CreatedAt = Utc(1) });
            db.AgentPromotionRequests.Add(new AgentPromotionRequest { AgentId = "a3", TargetRank = Rank.SpecialAgent, Status = PromotionStatus.Approved, CreatedAt = Utc(2) });
            db.AgentPromotionRequests.Add(new AgentPromotionRequest { AgentId = "a4", TargetRank = Rank.SpecialAgent, Status = PromotionStatus.Rejected, CreatedAt = Utc(4) });
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetOpenPromotionRequestsAsync();

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal(PromotionStatus.Requested, r.Status));
        Assert.Equal(Utc(1), result[0].CreatedAt);
        Assert.Equal(Utc(3), result[1].CreatedAt);
    }

    // ---- PromotionRequestAsync ----

    [Fact]
    public async Task PromotionRequestAsync_PersistsRequest_WithRequesterCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("target-1"));
            db.SaveChanges();
        }

        var request = await NewService(ctx).PromotionRequestAsync(
            "target-1", Rank.SeniorSpecialAgent, "<p>Verdient</p>", Leadership(codename: "Owl"));

        Assert.Equal("target-1", request.AgentId);
        Assert.Equal(Rank.SeniorSpecialAgent, request.TargetRank);
        Assert.Equal(PromotionStatus.Requested, request.Status);
        Assert.Equal("Owl", request.RequesterName);
        Assert.NotNull(request.Justification);
        Assert.Contains("Verdient", request.Justification!);

        using var verify = ctx.NewContext();
        Assert.Equal(1, verify.AgentPromotionRequests.Count(r => r.AgentId == "target-1"));
    }

    [Theory]
    [InlineData("teamlead")]
    [InlineData("teamlead-admin")]
    [InlineData("partner")]
    [InlineData("terminated")]
    public async Task PromotionRequestAsync_Throws_WhenAgentNotSelectable(string id)
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(id switch
            {
                "teamlead" => Seed.Agent(id, configure: a => a.IsTeamLead = true),
                "teamlead-admin" => Seed.Agent(id, configure: a => { a.IsTeamLead = true; a.IsAdmin = true; }),
                "partner" => Seed.Agent(id, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
                _ => Seed.Agent(id, status: AgentStatus.Terminated),
            });
            db.SaveChanges();
        }

        // TargetRank is a NOOSE Rank; it must never land on a partner or departed account
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).PromotionRequestAsync(id, Rank.SpecialAgent, null, Leadership()));

        using var verify = ctx.NewContext();
        Assert.Equal(0, verify.AgentPromotionRequests.Count(r => r.AgentId == id));
    }

    [Fact]
    public async Task PromotionRequestAsync_EmptyJustification_StoredAsNull()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("target-1"));
            db.SaveChanges();
        }

        var request = await NewService(ctx).PromotionRequestAsync(
            "target-1", Rank.SpecialAgent, "   ", Leadership());

        Assert.Null(request.Justification);
    }

    [Fact]
    public async Task PromotionRequestAsync_NonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("target-1"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewService(ctx).PromotionRequestAsync("target-1", Rank.SpecialAgent, null, Rank1()));

        using var verify = ctx.NewContext();
        Assert.Equal(0, verify.AgentPromotionRequests.Count());
    }

    [Fact]
    public async Task PromotionRequestAsync_UnknownAgent_Throws()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).PromotionRequestAsync("ghost", Rank.SpecialAgent, null, Leadership()));
    }

    [Fact]
    public async Task PromotionRequestAsync_ExistingOpenRequest_Throws()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("target-1"));
            db.AgentPromotionRequests.Add(new AgentPromotionRequest { AgentId = "target-1", TargetRank = Rank.SpecialAgent, Status = PromotionStatus.Requested, CreatedAt = Utc(1) });
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).PromotionRequestAsync("target-1", Rank.SeniorSpecialAgent, null, Leadership()));

        using var verify = ctx.NewContext();
        Assert.Equal(1, verify.AgentPromotionRequests.Count(r => r.AgentId == "target-1"));
    }
}
