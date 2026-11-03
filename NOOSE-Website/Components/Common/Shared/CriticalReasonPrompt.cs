using MudBlazor;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Components.Common.Shared;

/// <summary>Reason dialogs in front of a critical record change; null means the user backed out.</summary>
public static class CriticalReasonPrompt
{
    private const string Trail = " Der Grund steht danach im Zeitstrahl der Akte und im Änderungsprotokoll.";

    /// <summary>Asks why a record goes to the trash.</summary>
    public static Task<string?> DeleteAsync(IDialogService dialog, string title, string text)
        => ReasonDialog.ShowAsync(dialog, title, text + Trail, confirmText: "Löschen", label: "Grund",
            mandatoryReason: true, colour: Color.Error, maxLength: CriticalChange.MaxReasonLength);

    /// <summary>Asks why a Verschlusssache is loosened; empty when the change needs no reason.</summary>
    public static async Task<string?> SecrecyAsync(IDialogService dialog, DocumentClassification old,
        DocumentClassification @new)
    {
        if (!CriticalChange.LoosensSecrecy(old, @new))
        {
            return string.Empty;
        }
        return await ReasonDialog.ShowAsync(dialog, "Verschlusssache lockern?",
            $"Die Stufe ändert sich von „{DocumentClassificationDisplay.Label(old)}“ auf "
            + $"„{DocumentClassificationDisplay.Label(@new)}“; die Akte wird damit für mehr Agenten lesbar." + Trail,
            confirmText: "Speichern", label: "Grund", mandatoryReason: true, colour: Color.Warning,
            maxLength: CriticalChange.MaxReasonLength);
    }

    /// <summary>Asks why a wanted flag is lifted.</summary>
    public static Task<string?> WantedLiftAsync(IDialogService dialog)
        => ReasonDialog.ShowAsync(dialog, "Fahndung aufheben?",
            "Die Person verschwindet vom Fahndungs-Board." + Trail, confirmText: "Aufheben", label: "Grund",
            mandatoryReason: true, maxLength: CriticalChange.MaxReasonLength);
}
