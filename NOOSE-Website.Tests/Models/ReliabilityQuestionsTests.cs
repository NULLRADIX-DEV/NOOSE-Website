using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Tests.Models;

public class ReliabilityQuestionsTests
{
    [Fact]
    public void Keys_AreUnique()
        => Assert.Equal(ReliabilityQuestions.All.Count, ReliabilityQuestions.All.Select(q => q.Key).Distinct().Count());

    [Fact]
    public void NewAnswers_HasOneUnansweredRowPerQuestion()
    {
        var answers = ReliabilityQuestions.NewAnswers();

        Assert.Equal(ReliabilityQuestions.All.Select(q => q.Key), answers.Select(a => a.Key));
        Assert.All(answers, a => Assert.Null(a.Answer));
    }

    [Fact]
    public void Validate_AllAnswered_GivesNull()
        => Assert.Null(ReliabilityQuestions.Validate(ReliabilityAnswers.AllNo()));

    [Fact]
    public void Validate_FreshForm_Complains()
        => Assert.NotNull(ReliabilityQuestions.Validate(ReliabilityQuestions.NewAnswers()));

    [Fact]
    public void Validate_MissingQuestion_Complains()
        => Assert.NotNull(ReliabilityQuestions.Validate(ReliabilityAnswers.AllNo().Skip(1)));

    [Fact]
    public void Validate_Null_Complains()
        => Assert.NotNull(ReliabilityQuestions.Validate(null));

    [Fact]
    public void Validate_YesWithoutDetails_NamesTheQuestion()
    {
        var answers = ReliabilityAnswers.AllNo();
        answers[1].Answer = true;

        var problem = ReliabilityQuestions.Validate(answers);

        Assert.Contains(ReliabilityQuestions.All[1].Label, problem);
    }

    [Fact]
    public void Validate_YesWithDetails_GivesNull()
    {
        var answers = ReliabilityAnswers.AllNo();
        answers[1].Answer = true;
        answers[1].Details = "Erbschaft";

        Assert.Null(ReliabilityQuestions.Validate(answers));
    }

    [Fact]
    public void Serialize_DropsDetailsOnNo_AndUnknownKeys_AndCapsLength()
    {
        var answers = ReliabilityAnswers.AllNo();
        answers[0].Details = "nicht gefragt";
        answers[1].Answer = true;
        answers[1].Details = new string('x', 2000);
        answers.Add(new ReliabilityAnswer { Key = "erfunden", Answer = true, Details = "x" });

        var stored = ReliabilityQuestions.Parse(ReliabilityQuestions.Serialize(answers));

        Assert.Equal(ReliabilityQuestions.All.Select(q => q.Key), stored.Select(a => a.Key));
        Assert.Null(stored[0].Details);
        Assert.Equal(ReliabilityQuestions.MaxDetailsLength, stored[1].Details!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void Parse_EmptyOrBroken_GivesEmpty(string? json)
        => Assert.Empty(ReliabilityQuestions.Parse(json));

    [Fact]
    public void Describe_UsesTheLabel()
    {
        Assert.Equal("Ungewöhnliche Schulden: nein",
            ReliabilityQuestions.Describe(new ReliabilityAnswer { Key = "schulden", Answer = false }));
        Assert.Equal("Ungewöhnliche Schulden: ja (Kredit)",
            ReliabilityQuestions.Describe(new ReliabilityAnswer { Key = "schulden", Answer = true, Details = "Kredit" }));
    }
}
