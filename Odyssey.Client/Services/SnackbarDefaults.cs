using MudBlazor;

namespace Odyssey.Client.Services;

/// <summary>
/// The app's one snackbar configuration (issue #473 §6.7), applied to <c>AddMudServices</c> for the provider in
/// <c>App.razor</c>: bottom-right with the bottom inset, so a focused element is never left covered (2.4.11); a close
/// icon on every snackbar; and no stacking of an identical message. Durations stay per call — an error snackbar sets
/// <c>RequireInteraction</c> so it stays until dismissed, and the news Undo sets its 8 s.
/// </summary>
/// <remarks>
/// MudBlazor 9 renders the container as an empty, persistent <c>role="status"</c> region, so a snackbar is announced
/// on its own; the news pages therefore do not also say it through their <c>OdsLiveAnnouncer</c> (§6.7 fallback,
/// <c>NewsFeedback.SnackbarSpeaks</c>, pinned by <c>NewsSnackbarTests</c>). This configuration is app-wide: every
/// page's snackbars move to bottom-right and gain a close icon, and an identical message already on screen is not
/// stacked again.
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
