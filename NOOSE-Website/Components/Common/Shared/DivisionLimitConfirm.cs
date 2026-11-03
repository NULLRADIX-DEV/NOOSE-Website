using MudBlazor;
using NOOSE_Website.Components.Pages.People.Shared;
using NOOSE_Website.Services;

namespace NOOSE_Website.Components.Common.Shared;

/// <summary>Runs a division write; a full division asks once and repeats the write with the confirmation.</summary>
public static class DivisionLimitConfirm
{
    /// <summary>True when the write went through, false when the operator declined the over-limit seat.</summary>
    public static async Task<bool> RunAsync(IDialogService dialog, Func<bool, Task> write)
    {
        try
        {
            await write(false);
            return true;
        }
        catch (DivisionLimitExceededException ex)
        {
            var confirmed = await ConfirmDialog.ShowAsync(dialog, "Abteilungslimit überschreiten?",
                ex.Message + " Trotzdem zuweisen?", "Trotzdem zuweisen", Color.Warning);
            if (!confirmed)
            {
                return false;
            }
            await write(true);
            return true;
        }
    }
}
