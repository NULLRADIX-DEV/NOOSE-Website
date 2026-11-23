using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Llm;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>What the change protocol may never carry, held by a registry instead of by care during review.</summary>
/// <remarks>
/// The integration halves live next to the services that write these rows: <c>TipServiceTests</c> and
/// <c>TicketServiceTests</c> each attach the real interceptor and assert that an edit leaves no wording behind.
/// </remarks>
public class AuditRedactionTests
{
    [Fact]
    public void Citizen_correspondence_keeps_its_wording_out_of_the_protocol()
    {
        Assert.True(AuditRedaction.Hides(nameof(HinweisNachricht), nameof(HinweisNachricht.Text)));
        Assert.True(AuditRedaction.Hides(nameof(TicketNachricht), nameof(TicketNachricht.Text)));
    }

    [Fact]
    public void A_personal_text_snippet_keeps_its_wording_out_of_the_protocol()
    {
        // the panel that offers them says "Sie gehören dir allein", and /nachweis is read by every internal
        // agent - without this the sentence and the name the agent gave it would be house-wide reading
        Assert.True(AuditRedaction.Hides(nameof(TextSnippet), nameof(TextSnippet.Text)));
        Assert.True(AuditRedaction.Hides(nameof(TextSnippet), nameof(TextSnippet.Name)));
    }

    [Fact]
    public void A_notice_title_and_a_watched_search_keep_their_findings_out_of_the_protocol()
    {
        // a folded notice rewrites its title, and a watch report names hits found under its owner's rights;
        // SavedSearchWatchTests holds the interceptor half
        Assert.True(AuditRedaction.Hides(nameof(Notification), nameof(Notification.Title)));
        Assert.True(AuditRedaction.Hides(nameof(SavedSearch), nameof(SavedSearch.KnownHitsJson)));
        Assert.True(AuditRedaction.Hides(nameof(SavedSearch), nameof(SavedSearch.UnseenHitsJson)));
    }

    [Fact]
    public void A_fibi_chat_keeps_its_title_and_scope_stamp_out_of_the_protocol()
    {
        // the chat is owner-private; leadership gets counts and cost, never the content
        Assert.True(AuditRedaction.Hides(nameof(NooseiConversation), nameof(NooseiConversation.Title)));
        Assert.True(AuditRedaction.Hides(nameof(NooseiConversation), nameof(NooseiConversation.ScopeStamp)));
        Assert.False(AuditRedaction.Hides(nameof(NooseiConversation), nameof(NooseiConversation.MessageCount)));
    }

    [Fact]
    public void The_reason_a_promotion_was_turned_down_stays_out_of_the_protocol()
    {
        Assert.True(AuditRedaction.Hides(nameof(AgentPromotionRequest), nameof(AgentPromotionRequest.DecisionNote)));
    }

    [Fact]
    public void A_promotion_justification_is_deliberately_not_redacted()
    {
        // written once, never edited, and shown on the personnel file tab to every active agent: the protocol
        // (leadership and supervision) would add no reader
        Assert.False(AuditRedaction.Hides(nameof(AgentPromotionRequest), nameof(AgentPromotionRequest.Justification)));
        Assert.False(AuditRedaction.Hides(nameof(AgentPromotionRequest), nameof(AgentPromotionRequest.Status)));
    }

    [Fact]
    public async Task A_changed_chat_title_and_scope_stamp_leave_no_value_in_the_audit_row()
    {
        using var ctx = new SqliteTestContext();
        await using var db = NewAuditedContext(ctx);
        db.NooseiConversations.Add(new NooseiConversation
        {
            Id = "c1", AgentId = "a1", Title = "Geheimer alter Titel", ScopeStamp = "alt-stempel", MessageCount = 1,
        });
        await db.SaveChangesAsync();

        var chat = await db.NooseiConversations.SingleAsync();
        chat.Title = "Geheimer neuer Titel";
        chat.ScopeStamp = "neu-stempel";
        chat.MessageCount = 2;
        await db.SaveChangesAsync();

        var row = await ModifiedRowAsync(ctx, nameof(NooseiConversation));
        Assert.NotNull(row.ChangesJson);
        Assert.Contains("MessageCount", row.ChangesJson);
        Assert.DoesNotContain("Titel", row.ChangesJson);
        Assert.DoesNotContain("stempel", row.ChangesJson);
        Assert.DoesNotContain(nameof(NooseiConversation.Title), row.ChangesJson);
        Assert.DoesNotContain(nameof(NooseiConversation.ScopeStamp), row.ChangesJson);
    }

    [Fact]
    public async Task A_decided_promotion_keeps_the_decision_note_out_of_the_audit_row_but_records_the_status()
    {
        using var ctx = new SqliteTestContext();
        await using var db = NewAuditedContext(ctx);
        db.AgentPromotionRequests.Add(new AgentPromotionRequest
        {
            Id = "p1", AgentId = "a1", TargetRank = Rank.SpecialAgent, Justification = "Begruendung im Klartext",
        });
        await db.SaveChangesAsync();

        var request = await db.AgentPromotionRequests.SingleAsync();
        request.Status = PromotionStatus.Rejected;
        request.DecisionNote = "Ablehnungsgrund im Klartext";
        request.Justification = "Neue Begruendung im Klartext";
        await db.SaveChangesAsync();

        var row = await ModifiedRowAsync(ctx, nameof(AgentPromotionRequest));
        Assert.NotNull(row.ChangesJson);
        Assert.Contains(nameof(AgentPromotionRequest.Status), row.ChangesJson);
        Assert.DoesNotContain(nameof(AgentPromotionRequest.DecisionNote), row.ChangesJson);
        Assert.DoesNotContain("Ablehnungsgrund", row.ChangesJson);
        // the decision on Justification: not redacted, so its pair is there (ASCII, as the JSON escapes umlauts)
        Assert.Contains("Neue Begruendung im Klartext", row.ChangesJson);
    }

    [Fact]
    public async Task A_soft_deleted_chat_does_not_carry_its_title_either()
    {
        using var ctx = new SqliteTestContext();
        await using var db = NewAuditedContext(ctx);
        db.NooseiConversations.Add(new NooseiConversation { Id = "c2", AgentId = "a1", Title = "Titel vor dem Löschen" });
        await db.SaveChangesAsync();

        var chat = await db.NooseiConversations.SingleAsync();
        chat.Title = "Titel beim Löschen";
        db.NooseiConversations.Remove(chat);
        await db.SaveChangesAsync();

        var created = await ctx.NewContext().AuditLogs.SingleAsync(
            a => a.EntityType == nameof(NooseiConversation) && a.Action == AuditAction.Created);
        Assert.Null(created.ChangesJson);
        var deleted = await ctx.NewContext().AuditLogs.SingleAsync(
            a => a.EntityType == nameof(NooseiConversation) && a.Action == AuditAction.Deleted);
        Assert.DoesNotContain("Titel", deleted.ChangesJson ?? string.Empty);
    }

    [Fact]
    public void Everything_else_stays_visible()
    {
        // a note on a file is supposed to show its before and after; CommentServiceTests holds that line
        Assert.False(AuditRedaction.Hides(nameof(Comment), nameof(Comment.Text)));
        // the rule is scoped to the field, not to the whole row
        Assert.False(AuditRedaction.Hides(nameof(TicketNachricht), nameof(TicketNachricht.Audience)));
        Assert.False(AuditRedaction.Hides(nameof(Hinweis), nameof(Hinweis.Text)));
        Assert.False(AuditRedaction.Hides("Person", "Name"));
    }

    [Fact]
    public void The_lookup_is_case_sensitive_on_the_clr_names_the_interceptor_stamps()
    {
        Assert.False(AuditRedaction.Hides("ticketnachricht", "Text"));
        Assert.False(AuditRedaction.Hides(nameof(TicketNachricht), "text"));
    }

    private static AppDbContext NewAuditedContext(SqliteTestContext ctx)
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options);

    private static async Task<AuditLog> ModifiedRowAsync(SqliteTestContext ctx, string entityType)
    {
        await using var db = ctx.NewContext();
        return await db.AuditLogs.SingleAsync(a => a.EntityType == entityType && a.Action == AuditAction.Modified);
    }

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }
}
