using MudBlazor;
using NOOSE_Website.Components.Common.Shared;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Components.Pages.Personnel.Shared;

/// <summary>The decider's final word on a promotion request: mandatory to approve, optional to reject.</summary>
public static class PromotionReason
{
    /// <summary>Null on cancel, else the trimmed reason (empty only for a rejection).</summary>
    public static Task<string?> AskAsync(IDialogService dialog, string codename, Rank target, bool approved)
        => ReasonDialog.ShowAsync(dialog,
            approved ? "Beförderung genehmigen?" : "Beförderung ablehnen?",
            approved
                ? $"{codename} erhält den Dienstgrad {RankDisplay.Name(target)}. Der Beförderungsgrund steht danach am "
                  + "Antrag und in der Discord-Meldung zur Beförderung. Der Agent muss sich neu anmelden."
                : $"Der Antrag für {codename} wird abgelehnt. Ein Grund ist optional und steht danach am Antrag.",
            confirmText: approved ? "Genehmigen" : "Ablehnen",
            label: approved ? "Beförderungsgrund" : "Ablehnungsgrund",
            mandatoryReason: approved,
            colour: approved ? Color.Success : Color.Error,
            lines: 4,
            maxLength: PromotionRules.MaxDecisionNoteLength);

    public static string Label(PromotionStatus status)
        => status == PromotionStatus.Approved ? "Beförderungsgrund" : "Ablehnungsgrund";
}
