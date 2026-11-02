using System.Security.Claims;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The content tool reads what hangs off a record. Its own gate is the whole point: two of the readers it
/// calls — keywords and own fields — carry no visibility check for internal agents at all.</summary>
public sealed class NooseiContentToolTests
{
    private const string OpenPerson = "p-open";
    private const string SecretPerson = "p-secret";

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void SeedPeople(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        db.People.Add(Seed.Person(id: OpenPerson, name: "Otto Offen"));
        db.People.Add(Seed.Person(id: SecretPerson, name: "Gerd Geheim", configure: p => p.IsClassified = true));
        db.SaveChanges();
    }

    private static void SeedComments(SqliteTestContext ctx, string personId, int count)
    {
        using var db = ctx.NewContext();
        for (var i = 1; i <= count; i++)
        {
            db.Comments.Add(new Comment
            {
                Id = $"{personId}-c{i}",
                EntityType = "Person",
                EntityId = personId,
                Text = $"Vermerk Nummer {i}",
                AuthorName = "Falke",
                CreatedAt = new DateTime(2026, 1, i, 12, 0, 0, DateTimeKind.Utc),
            });
        }
        db.SaveChanges();
    }

    /// <summary>The tool with only the readers a test needs real; the rest are doubles that return nothing.</summary>
    private static ReadRecordContentTool Build(
        SqliteTestContext ctx,
        ICommentService? comments = null,
        ITagService? tags = null,
        ICustomFieldValueService? customFields = null,
        ITaskforceChatService? chat = null)
        => new(
            ctx.Factory,
            comments ?? Substitute.For<ICommentService>(),
            Substitute.For<ISourceService>(),
            Substitute.For<IFollowupService>(),
            Substitute.For<ILinkService>(),
            customFields ?? Substitute.For<ICustomFieldValueService>(),
            tags ?? Substitute.For<ITagService>(),
            Substitute.For<IPersonDocService>(),
            Substitute.For<IObservationService>(),
            chat ?? Substitute.For<ITaskforceChatService>(),
            Substitute.For<IMeetingService>(),
            Substitute.For<IBewerbungService>(),
            Substitute.For<IPersonnelFileService>(),
            Substitute.For<IInformantService>(),
            Substitute.For<IPublicWantedService>(),
            Substitute.For<IPublicFactionProfileService>(),
            Substitute.For<IObjectionService>(),
            Substitute.For<ITipService>(),
            Substitute.For<IAuditLogQueryService>());

    [Fact]
    public async Task ReadContent_GivesTheSameAnswer_ForMissingAndForbidden()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx);
        var tool = Build(ctx);

        var forbidden = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{SecretPerson}}"}"""), NooseiToolContext.From(Junior()));
        var missing = await tool.InvokeAsync(
            Args("""{"typ":"Person","id":"gibt-es-nicht"}"""), NooseiToolContext.From(Junior()));

        Assert.Equal(missing.Text, forbidden.Text);
        Assert.True(forbidden.IsError);
        Assert.DoesNotContain("Gerd Geheim", forbidden.Text);
    }

    [Fact]
    public async Task ReadContent_ReturnsEveryCommentAndNamesTheTotal()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx);
        SeedComments(ctx, OpenPerson, 3);
        var tool = Build(ctx, comments: new CommentService(ctx.Factory, Substitute.For<INotificationService>()));

        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{OpenPerson}}","inhalt":"kommentare"}"""),
            NooseiToolContext.From(Junior()));

        Assert.False(result.IsError);
        Assert.Contains("Vermerk Nummer 1", result.Text);
        Assert.Contains("Vermerk Nummer 3", result.Text);
        Assert.Contains("(3 von 3)", result.Text);
    }

    [Fact]
    public async Task ReadContent_SaysHowManyAreLeft_WhenItPaginates()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx);
        SeedComments(ctx, OpenPerson, 5);
        var tool = Build(ctx, comments: new CommentService(ctx.Factory, Substitute.For<INotificationService>()));

        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{OpenPerson}}","inhalt":"kommentare","max":2}"""),
            NooseiToolContext.From(Junior()));

        // "two comments" must never read as "that is all of them"
        Assert.Contains("(2 von 5)", result.Text);
        Assert.Contains("weiterlesen mit ab=2", result.Text);
        Assert.DoesNotContain("Vermerk Nummer 3", result.Text);
    }

    [Fact]
    public async Task ReadContent_ContinuesFromTheGivenOffset()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx);
        SeedComments(ctx, OpenPerson, 5);
        var tool = Build(ctx, comments: new CommentService(ctx.Factory, Substitute.For<INotificationService>()));

        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{OpenPerson}}","inhalt":"kommentare","max":2,"ab":2}"""),
            NooseiToolContext.From(Junior()));

        Assert.Contains("(2 von 5, ab 2)", result.Text);
        Assert.Contains("Vermerk Nummer 3", result.Text);
        Assert.DoesNotContain("Vermerk Nummer 1", result.Text);
    }

    /// <summary>The reason the tool gates the parent itself.</summary>
    [Fact]
    public async Task ReadContent_WithholdsKeywordsAndOwnFieldsOfAnInvisibleRecord()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx);
        using (var db = ctx.NewContext())
        {
            db.Tags.Add(new Tag { Id = "t1", Name = "Waffenhandel" });
            db.TagMappings.Add(new TagMapping { Id = "m1", TagId = "t1", EntityType = "Person", EntityId = SecretPerson });
            db.CustomFieldDefinitions.Add(new CustomFieldDefinition
            {
                Id = "d1", Name = "Deckname", EntityType = "Person", IsActive = true, Order = 1,
            });
            db.CustomFieldValues.Add(new CustomFieldValue
            {
                Id = "v1", CustomFieldDefinitionId = "d1", EntityType = "Person", EntityId = SecretPerson,
                Value = "Schattenmann",
            });
            db.SaveChanges();
        }

        // the real readers: neither of them checks the parent, they trust the page that renders them
        var tool = Build(ctx,
            tags: new TagService(ctx.Factory),
            customFields: new CustomFieldValueService(ctx.Factory, Substitute.For<INotificationService>()));

        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{SecretPerson}}"}"""), NooseiToolContext.From(Junior()));

        Assert.True(result.IsError);
        Assert.DoesNotContain("Waffenhandel", result.Text);
        Assert.DoesNotContain("Schattenmann", result.Text);
    }

    [Fact]
    public async Task ReadContent_LetsLeadershipSeeTheKeywordsOfAClassifiedRecord()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx);
        using (var db = ctx.NewContext())
        {
            db.Tags.Add(new Tag { Id = "t1", Name = "Waffenhandel" });
            db.TagMappings.Add(new TagMapping { Id = "m1", TagId = "t1", EntityType = "Person", EntityId = SecretPerson });
            db.SaveChanges();
        }
        var tool = Build(ctx, tags: new TagService(ctx.Factory));

        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{SecretPerson}}","inhalt":"stichworte"}"""),
            NooseiToolContext.From(Leader()));

        Assert.False(result.IsError);
        Assert.Contains("Waffenhandel", result.Text);
    }

    [Fact]
    public async Task ReadContent_RefusesASectionThatBelongsToAnotherKindOfRecord()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx);
        var chat = Substitute.For<ITaskforceChatService>();
        var tool = Build(ctx, chat: chat);

        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{OpenPerson}}","inhalt":"chat"}"""),
            NooseiToolContext.From(Leader()));

        // silently answering "no messages" would read as "this is quiet", not "wrong question"
        Assert.True(result.IsError);
        Assert.Contains("Person", result.Text);
        Assert.Contains("kommentare", result.Text);
        await chat.DidNotReceive().GetMessagesAsync(
            Arg.Any<string>(), Arg.Any<ViewerScope>(), Arg.Any<int>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadContent_ReferencesTheParentRecordAndNothingElse()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx);
        SeedComments(ctx, OpenPerson, 2);
        var tool = Build(ctx, comments: new CommentService(ctx.Factory, Substitute.For<INotificationService>()));

        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{OpenPerson}}","inhalt":"kommentare"}"""),
            NooseiToolContext.From(Junior()));

        // children have no route of their own; a chip must lead to the record they sit in
        var reference = Assert.Single(result.Refs!);
        Assert.Equal("Person", reference.Kind);
        Assert.Equal(OpenPerson, reference.Id);
    }

    [Fact]
    public async Task ReadContent_RejectsASectionThatDoesNotExist()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx);
        var tool = Build(ctx);

        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{OpenPerson}}","inhalt":"kaffeekasse"}"""),
            NooseiToolContext.From(Leader()));

        Assert.True(result.IsError);
        Assert.Contains("kommentare", result.Text);
    }
}
