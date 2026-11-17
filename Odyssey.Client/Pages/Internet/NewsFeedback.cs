using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>
/// How a news write's outcome reaches the user (issue #473 §6.7, §7.1, §7.2). Each status change is spoken exactly
/// once (AC 29): by the page's one <c>OdsLiveAnnouncer</c> when the snackbar is silent, else by the snackbar alone.
/// MudBlazor 9 renders the snackbar container as <c>role="status"</c> and each error snackbar as <c>role="alert"</c>,
/// so the snackbar IS a live region and, as §6.7 provides for that case, the announcer call is skipped:
/// <see cref="Spoken"/> returns <c>null</c> for a message a snackbar shows. <c>NewsSnackbarTests</c> pins the
/// provider's markup, so a MudBlazor that stops announcing fails a test and flips <see cref="SnackbarSpeaks"/>.
/// Error snackbars stay until dismissed. These are the inline treatments §5.5 allows beside
/// <c>ApiInteropExtensions</c>: a <c>news-busy</c> answer to an action reads "Busy. Try again in a moment." rather
/// than the server's detail.
/// </summary>
internal static class NewsFeedback
{
    /// <summary>What a user action shows on <c>503 news-busy</c> (§7.2).</summary>
    public const string BusyText = "Busy. Try again in a moment.";

    public const string GoneText = "No longer available.";

    /// <summary>The snackbar container is a live region (see the summary above); pinned by <c>NewsSnackbarTests</c>.</summary>
    public const bool SnackbarSpeaks = true;

    /// <summary>
    /// What the page's announcer should say for a message a snackbar has just shown: nothing while the snackbar speaks
    /// for itself, so the change is never heard twice; the text otherwise.
    /// </summary>
    public static string? Spoken(string text) => SnackbarSpeaks ? null : text;

    /// <summary>A <c>503 news-busy</c>: a statement hit its time limit or a lock wait (§7.1).</summary>
    public static bool IsBusy(ApiProblem? problem) =>
        string.Equals(problem?.Code, NewsErrorCodes.Busy, StringComparison.Ordinal);

    public static bool Is(ApiProblem? problem, string code) =>
        string.Equals(problem?.Code, code, StringComparison.Ordinal);

    /// <summary>Shows a failed action; returns what the page's announcer should say (<see cref="Spoken"/>).</summary>
    public static string? Failure(ISnackbar snackbar, ApiProblem? problem, string failureLead)
    {
        var text = IsBusy(problem) ? BusyText : $"{failureLead}: {problem?.Message ?? "Request failed."}";
        snackbar.Add(text, Severity.Error, config => config.RequireInteraction = true);
        return Spoken(text);
    }

    /// <summary>A 404 on an action: the row was removed elsewhere (§7.2). Returns what the announcer should say.</summary>
    public static string? Gone(ISnackbar snackbar)
    {
        snackbar.Add(GoneText, Severity.Warning);
        return Spoken(GoneText);
    }

    /// <summary>Shows a completed action; returns what the page's announcer should say (<see cref="Spoken"/>).</summary>
    public static string? Success(ISnackbar snackbar, string text)
    {
        snackbar.Add(text, Severity.Success);
        return Spoken(text);
    }

    /// <summary>"3 feeds" / "1 article" — a count with its noun, in the reader's number format.</summary>
    public static string Count(int value, string one, string many) =>
        $"{value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} {(value == 1 ? one : many)}";
}
