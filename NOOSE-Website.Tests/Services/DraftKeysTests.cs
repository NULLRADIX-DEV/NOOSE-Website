using NOOSE_Website.Components.Common.Shared;
using NOOSE_Website.Components.Pages.Laws.Shared;
using NOOSE_Website.Components.Pages.People.Shared;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Services;

/// <summary>The keys a plain text field keeps its browser draft under, and the prefix a caller drops them by.</summary>
/// <remarks>
/// A dialog closes before its caller saves, so the field and the caller build the key without talking to each
/// other. Every mismatch is silent: the draft either lingers and offers saved text again, or goes with a neighbour.
/// </remarks>
public class DraftKeysTests
{
    private const string Agent = "agent-1";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_an_agent_nothing_is_kept(string? agentId)
    {
        // an anonymous page must not write text under a key every visitor shares
        Assert.Null(DraftKeys.Field(agentId, "dok:neu:1:info"));
        Assert.Null(DraftKeys.Scope(agentId, "dok:neu:1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_key_the_field_keeps_nothing(string? draftKey)
        => Assert.Null(DraftKeys.Field(Agent, draftKey));

    [Fact]
    public void A_form_without_a_scope_gives_its_fields_no_key()
    {
        // the opt-in: a dialog nobody handed a scope stays without drafts instead of sharing one
        Assert.Null(DraftKeys.In(null, "info"));
        Assert.Null(DraftKeys.In("", "info"));
        Assert.Null(DraftKeys.In("dok:neu:1", ""));
    }

    [Fact]
    public void Every_field_of_a_scope_is_covered_by_the_scope_prefix()
    {
        var prefix = DraftKeys.Scope(Agent, "dok:neu:person-1")!;

        foreach (var field in new[] { "grund", "info" })
        {
            var key = DraftKeys.Field(Agent, DraftKeys.In("dok:neu:person-1", field))!;
            Assert.StartsWith(prefix, key, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_scope_does_not_reach_into_a_longer_one()
    {
        // without the closing separator saving dok 1 would throw away the open draft of dok 12
        var prefix = DraftKeys.Scope(Agent, "dok:1")!;
        var neighbour = DraftKeys.Field(Agent, DraftKeys.In("dok:12", "info"))!;

        Assert.False(neighbour.StartsWith(prefix, StringComparison.Ordinal));
    }

    [Fact]
    public void No_dialog_scope_reaches_into_another()
    {
        // dropping a scope deletes every key under its prefix: "dok:neu" once took every person's new dok along
        string[] scopes =
        [
            DocDialog.NewDraftScope("p1"), DocDialog.EditDraftScope("d1"), DocCreateDialog.DraftScope,
            ObservationDialog.NewDraftScope("p1"), ObservationDialog.EditDraftScope("o1"),
            SourceDialog.NewDraftScope("Person", "p1"),
            PublicTemplateDialog.DraftScopeFor(null, PublicTemplateKind.TicketEingang),
            PublicTemplateDialog.DraftScopeFor("t1", PublicTemplateKind.TicketEingang),
        ];

        var overlaps = scopes
            .SelectMany(a => scopes.Where(b => a != b
                && DraftKeys.Scope(Agent, b)!.StartsWith(DraftKeys.Scope(Agent, a)!, StringComparison.Ordinal))
                .Select(b => $"{a} → {b}"))
            .ToList();

        Assert.Empty(overlaps);
    }

    [Fact]
    public void Two_agents_on_one_device_never_share_a_key()
    {
        var mine = DraftKeys.Field("agent-1", "vermerk:neu:Person:p1");
        var theirs = DraftKeys.Field("agent-2", "vermerk:neu:Person:p1");

        Assert.NotEqual(mine, theirs);
        Assert.False(theirs!.StartsWith(DraftKeys.Scope("agent-1", "vermerk:neu")!, StringComparison.Ordinal));
    }

    [Fact]
    public void A_plain_field_can_never_meet_a_rich_text_draft()
    {
        // the editor stores "{agent}:{key}"; a plain field restoring stored html would show raw tags
        var plain = DraftKeys.Field(Agent, "dokument:neu")!;
        var rich = Agent + ":" + "dokument:neu";

        Assert.NotEqual(rich, plain);
        Assert.False(rich.StartsWith(DraftKeys.Scope(Agent, "dokument")!, StringComparison.Ordinal));
    }
}
