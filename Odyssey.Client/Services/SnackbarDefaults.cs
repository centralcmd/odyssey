using MudBlazor;

namespace Odyssey.Client.Services;

/// <summary>
/// The app's one snackbar configuration (issue #473 §6.7), applied to <c>AddMudServices</c> for the provider in
/// <c>App.razor</c>: bottom-right with the bottom inset, so a focused element is never left covered (2.4.11); a close
/// icon on every snackbar; and no stacking of an identical message. Durations stay per call — an error snackbar sets
/// <c>RequireInteraction</c> so it stays until dismissed, and the news Undo sets its 8 s.
/// </summary>
/// <remarks>
/// The snackbar container is not a live region, so a page never relies on it to be announced: its own
/// <c>OdsLiveAnnouncer</c> says every status change once (AC 29, pinned by <c>NewsSnackbarTests</c>).
/// </remarks>
public static class SnackbarDefaults
{
    public static void Configure(SnackbarConfiguration configuration)
    {
        configuration.PositionClass = Defaults.Classes.Position.BottomRight;
        configuration.ShowCloseIcon = true;
        configuration.PreventDuplicates = true;
    }
}
