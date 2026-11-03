using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>The limits and the transition matrix of a citizen ticket.</summary>
public class TicketRulesTests
{
    [Fact]
    public void A_message_is_as_long_as_a_tip_message()
    {
        // two numbers over the same thing drift; the constant is derived rather than repeated
        Assert.Equal(TipRules.MaxMessageLength, TicketRules.MaxMessageLength);
    }

    [Fact]
    public void The_agency_sender_is_a_constant_and_names_no_agent()
    {
        Assert.Equal("FIB – Führungsebene", TicketRules.AgencySender);
    }

    [Theory]
    [InlineData(TicketStatus.Offen, true)]
    [InlineData(TicketStatus.InBearbeitung, true)]
    [InlineData(TicketStatus.WartetAufBuerger, true)]
    [InlineData(TicketStatus.Geschlossen, false)]
    public void Open_covers_everything_but_the_closed_status(TicketStatus status, bool expected)
    {
        Assert.Equal(expected, TicketRules.IsOpen(status));
    }

    [Theory]
    [InlineData(TicketStatus.Offen, TicketStatus.InBearbeitung)]
    [InlineData(TicketStatus.Offen, TicketStatus.WartetAufBuerger)]
    [InlineData(TicketStatus.Offen, TicketStatus.Geschlossen)]
    [InlineData(TicketStatus.InBearbeitung, TicketStatus.WartetAufBuerger)]
    [InlineData(TicketStatus.InBearbeitung, TicketStatus.Geschlossen)]
    [InlineData(TicketStatus.WartetAufBuerger, TicketStatus.InBearbeitung)]
    [InlineData(TicketStatus.WartetAufBuerger, TicketStatus.Geschlossen)]
    [InlineData(TicketStatus.Geschlossen, TicketStatus.InBearbeitung)]
    public void Allowed_transitions(TicketStatus from, TicketStatus to)
    {
        Assert.True(TicketRules.IsTransitionAllowed(from, to));
    }

    [Theory]
    [InlineData(TicketStatus.Offen, TicketStatus.Offen)]
    [InlineData(TicketStatus.Geschlossen, TicketStatus.Geschlossen)]
    // reopening lands in handling, never back at "untouched": someone has read it by now
    [InlineData(TicketStatus.Geschlossen, TicketStatus.Offen)]
    [InlineData(TicketStatus.Geschlossen, TicketStatus.WartetAufBuerger)]
    [InlineData(TicketStatus.InBearbeitung, TicketStatus.Offen)]
    [InlineData(TicketStatus.WartetAufBuerger, TicketStatus.Offen)]
    public void Refused_transitions(TicketStatus from, TicketStatus to)
    {
        Assert.False(TicketRules.IsTransitionAllowed(from, to));
    }

    [Fact]
    public void Allowed_targets_never_contain_the_current_status()
    {
        foreach (var status in TicketStatusDisplay.All)
        {
            Assert.DoesNotContain(status, TicketRules.AllowedTargets(status));
        }
    }

    [Fact]
    public void A_closed_ticket_offers_exactly_one_way_out()
    {
        var targets = TicketRules.AllowedTargets(TicketStatus.Geschlossen);
        Assert.Equal(new[] { TicketStatus.InBearbeitung }, targets);
    }

    [Fact]
    public void Both_caps_are_positive_and_the_open_one_is_the_tighter()
    {
        Assert.True(TicketRules.MaxOpen > 0);
        Assert.True(TicketRules.PerDay > 0);
        Assert.True(TicketRules.MaxOpen <= TicketRules.PerDay);
        Assert.Equal(TimeSpan.FromHours(24), TicketRules.QuotaWindow);
    }

    [Fact]
    public void Every_status_display_has_a_citizen_wording()
    {
        foreach (var status in TicketStatusDisplay.All)
        {
            Assert.NotEqual("—", TicketStatusDisplay.Name(status));
            Assert.NotEqual("—", TicketStatusDisplay.CitizenName(status));
        }
    }

    [Fact]
    public void The_due_threshold_is_the_tighter_one()
    {
        Assert.True(TicketRules.ReactionDue > TimeSpan.Zero);
        Assert.True(TicketRules.ReactionDue < TicketRules.ReactionOverdue);
    }

    [Fact]
    public void A_ticket_nobody_waits_on_carries_no_reaction_light()
    {
        var now = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);

        // the agency answered last, so there is no unanswered line to age
        Assert.Equal(TicketReaction.Keine, TicketRules.Reaction(TicketStatus.Offen, null, now));

        // and a closed ticket does not compete for attention, however old its last citizen line is
        Assert.Equal(TicketReaction.Keine,
            TicketRules.Reaction(TicketStatus.Geschlossen, now.AddDays(-30), now));
    }

    [Theory]
    [InlineData(0.0, TicketReaction.Frisch)]
    [InlineData(11.9, TicketReaction.Frisch)]
    [InlineData(12.0, TicketReaction.Faellig)]
    [InlineData(23.9, TicketReaction.Faellig)]
    [InlineData(24.0, TicketReaction.Ueberfaellig)]
    [InlineData(240.0, TicketReaction.Ueberfaellig)]
    public void The_light_turns_on_the_hour_of_each_threshold(double waitedHours, TicketReaction expected)
    {
        var now = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
        var since = now.AddHours(-waitedHours);

        Assert.Equal(expected, TicketRules.Reaction(TicketStatus.Offen, since, now));
    }

    [Fact]
    public void The_priority_bands_never_fall_below_one()
    {
        // multiplied literally, one zero factor would erase the whole order of a ticket
        foreach (var category in TicketKategorieDisplay.All)
        {
            Assert.True(TicketPriority.CategoryBand(category) >= 1);
        }
        Assert.Equal(1, TicketPriority.WaitBand(null));
        Assert.Equal(1, TicketPriority.WaitBand(TimeSpan.Zero));
        Assert.True(TicketPriority.Compute(TicketKategorie.Sonstiges, null, 0) >= TicketPriority.Min);
    }

    [Fact]
    public void An_unsorted_concern_is_not_the_bottom_of_the_desk()
    {
        // "Sonstiges" may be anything, so it must not rank below a filed request for information
        Assert.Equal(TicketPriority.CategoryBand(TicketKategorie.Auskunft),
            TicketPriority.CategoryBand(TicketKategorie.Sonstiges));
        Assert.True(TicketPriority.CategoryBand(TicketKategorie.Vermisstenmeldung)
            > TicketPriority.CategoryBand(TicketKategorie.Beschwerde));
    }

    [Fact]
    public void Waiting_longer_never_lowers_the_order()
    {
        var previous = 0;
        foreach (var hours in new[] { 0.0, 1.0, 12.0, 24.0, 72.0, 240.0 })
        {
            var band = TicketPriority.WaitBand(TimeSpan.FromHours(hours));
            Assert.True(band >= previous, $"{hours} h fell back to {band}");
            previous = band;
        }
        Assert.Equal(5, TicketPriority.WaitBand(TimeSpan.FromDays(30)));
    }

    [Fact]
    public void A_hand_set_priority_stays_inside_the_computed_range()
    {
        Assert.Equal(TicketPriority.Min, TicketPriority.Clamp(0));
        Assert.Equal(TicketPriority.Min, TicketPriority.Clamp(-40));
        Assert.Equal(TicketPriority.Max, TicketPriority.Clamp(TicketPriority.Max + 1));
        Assert.Equal(17, TicketPriority.Clamp(17));
    }

    [Fact]
    public void The_computed_order_never_leaves_its_own_range()
    {
        foreach (var category in TicketKategorieDisplay.All)
        {
            foreach (var hours in new[] { 0.0, 6.0, 30.0, 400.0 })
            {
                foreach (var tips in new[] { 0, 2, 9, 200 })
                {
                    var value = TicketPriority.Compute(category, TimeSpan.FromHours(hours), tips);
                    Assert.InRange(value, TicketPriority.Min, TicketPriority.Max);
                }
            }
        }
    }

    [Fact]
    public void Every_lit_reaction_has_a_wording()
    {
        foreach (var reaction in new[]
                 { TicketReaction.Frisch, TicketReaction.Faellig, TicketReaction.Ueberfaellig })
        {
            Assert.NotEqual("—", TicketReactionDisplay.Name(reaction));
        }
        // the unlit state has none on purpose: the column renders a dash, not a chip
        Assert.Equal("—", TicketReactionDisplay.Name(TicketReaction.Keine));
    }
}
