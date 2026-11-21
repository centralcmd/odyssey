namespace Odyssey.Client.Services;

/// <summary>
/// Whether a snackbar speaks for itself (issue #473 §6.7, reused by #520 v6 F1). MudBlazor 9 renders the snackbar
/// container as <c>role="status"</c> and each error snackbar as <c>role="alert"</c>, so a message a snackbar shows is
/// already announced, and a page announcer that repeated it would be heard twice. <c>NewsSnackbarTests</c> pins this
/// against the provider's real markup: a MudBlazor that stops announcing fails that test, the flag flips, and every
/// reader — the news pages and the calendar — then routes its toasts through its own announcer instead.
/// </summary>
public static class SnackbarAnnouncement
{
    /// <summary>The snackbar container is a live region; pinned by <c>NewsSnackbarTests</c>.</summary>
    public const bool SnackbarSpeaks = true;

    /// <summary>
    /// What a page's announcer should say for a message a snackbar has just shown: nothing while the snackbar speaks
    /// for itself, so the message is never heard twice; the text otherwise.
    /// </summary>
    public static string? Spoken(string text) => SnackbarSpeaks ? null : text;
}
