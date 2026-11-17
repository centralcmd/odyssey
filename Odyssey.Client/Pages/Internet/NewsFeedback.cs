using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>
/// How a news write's outcome reaches the user (issue #473 §6.7, §7.1, §7.2). Every message a snackbar shows is
/// returned so the page's one <c>OdsLiveAnnouncer</c> can say it once: announcements never rely on the snackbar.
/// Error snackbars stay until dismissed. These are the inline treatments §5.5 allows beside
/// <c>ApiInteropExtensions</c>: a <c>news-busy</c> answer to an action reads "Busy. Try again in a moment." rather
/// than the server's detail.
/// </summary>
internal static class NewsFeedback
{
    public const string BusyText = "Busy. Try again in a moment.";

    public const string GoneText = "No longer available.";

    /// <summary>A <c>503 news-busy</c>: a statement hit its time limit or a lock wait (§7.1).</summary>
    public static bool IsBusy(ApiProblem? problem) =>
        string.Equals(problem?.Code, NewsErrorCodes.Busy, StringComparison.Ordinal);

    public static bool Is(ApiProblem? problem, string code) =>
        string.Equals(problem?.Code, code, StringComparison.Ordinal);

    /// <summary>Shows a failed action and returns the text shown, for the page's announcer.</summary>
    public static string Failure(ISnackbar snackbar, ApiProblem? problem, string failureLead)
    {
        var text = IsBusy(problem) ? BusyText : $"{failureLead}: {problem?.Message ?? "Request failed."}";
        snackbar.Add(text, Severity.Error, config => config.RequireInteraction = true);
        return text;
    }

    /// <summary>A 404 on an action: the row was removed elsewhere (§7.2). Returns the text shown.</summary>
    public static string Gone(ISnackbar snackbar)
    {
        snackbar.Add(GoneText, Severity.Warning);
        return GoneText;
    }

    /// <summary>Shows a completed action and returns the text shown, for the page's announcer.</summary>
    public static string Success(ISnackbar snackbar, string text)
    {
        snackbar.Add(text, Severity.Success);
        return text;
    }

    /// <summary>"3 feeds" / "1 article" — a count with its noun, in the reader's number format.</summary>
    public static string Count(int value, string one, string many) =>
        $"{value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} {(value == 1 ? one : many)}";
}
