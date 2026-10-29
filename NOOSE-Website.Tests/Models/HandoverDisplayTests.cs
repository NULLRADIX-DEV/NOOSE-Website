using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Tests.Models;

/// <summary>Counts, closing notes and the outcome line of the handover dialog.</summary>
public sealed class HandoverDisplayTests
{
    [Theory]
    [InlineData(HandoverKind.Job, 1, "1 Aufgabe")]
    [InlineData(HandoverKind.Job, 3, "3 Aufgaben")]
    [InlineData(HandoverKind.Case, 2, "2 Vorgänge")]
    [InlineData(HandoverKind.Informant, 1, "1 V-Person")]
    public void CountsSingularAndPlural(HandoverKind kind, int count, string expected)
        => Assert.Equal(expected, HandoverDisplay.Count(kind, count));

    [Fact]
    public void OnlyRequestsAndAppointmentsClose()
    {
        var closing = Enum.GetValues<HandoverKind>().Where(HandoverDisplay.Closes);

        Assert.Equal([HandoverKind.FinancingRequest, HandoverKind.Appointment], closing);
    }

    [Fact]
    public void EveryClosingKindSaysWhatHappens()
    {
        Assert.All(Enum.GetValues<HandoverKind>().Where(HandoverDisplay.Closes),
            kind => Assert.StartsWith("Ohne Nachfolger:", HandoverDisplay.Closing(kind)));
        Assert.Equal(string.Empty, HandoverDisplay.Closing(HandoverKind.Job));
    }

    [Theory]
    [InlineData(3, 2, "3 Einträge übergeben, 2 geschlossen.")]
    [InlineData(1, 0, "1 Eintrag übergeben.")]
    [InlineData(0, 1, "1 Eintrag geschlossen.")]
    [InlineData(0, 0, "Nichts geändert: die Einträge hingen nicht mehr an diesem Agenten.")]
    public void TheOutcomeLineNamesWhatHappened(int passed, int closed, string expected)
        => Assert.Equal(expected, HandoverDisplay.Done(new HandoverResult(passed, closed)));
}
