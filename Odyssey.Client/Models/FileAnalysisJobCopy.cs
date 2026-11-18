using System.Globalization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Models;

/// <summary>What a terminal state's primary action does (issue #445 §7). Every non-close action goes back through consent.</summary>
public enum FileAnalysisTerminalAction
{
    Close,
    Retry,
    Review,
}

/// <summary>A terminal state's client-owned copy: heading, body, action, and whether it shows the sent line.</summary>
public sealed record FileAnalysisTerminalCopy(string Heading, string Body, FileAnalysisTerminalAction Action, bool ShowSent = true);

/// <summary>
/// Every word the analyze dialog shows for a queued job's outcome (issue #445 §7; Odyssey Design System ·
/// <c>AnalyzeFileModal.jsx</c> <c>FAN_FAILED_COPY</c> / <c>FAN_CANCELLED_COPY</c>). Keyed on the server's
/// <c>FailureCode</c> alone: no server text — <c>FailureMessage</c>, a ProblemDetails title or detail — is ever rendered.
/// Whether the document left is stated only by <see cref="SentLine"/>.
/// </summary>
public static class FileAnalysisJobCopy
{
    private static readonly FileAnalysisTerminalCopy GenericFailed =
        new("Something went wrong", "The analysis couldn’t finish.", FileAnalysisTerminalAction.Retry);

    private static readonly IReadOnlyDictionary<string, FileAnalysisTerminalCopy> Failed =
        new Dictionary<string, FileAnalysisTerminalCopy>(StringComparer.Ordinal)
        {
            [FileAnalysisCodes.ProviderError] = new("We couldn’t read this statement", "The analysis service returned an error.", FileAnalysisTerminalAction.Retry),
            [FileAnalysisCodes.ProviderCredentialError] = new("Analysis isn’t set up correctly", "An administrator needs to check the analysis credentials.", FileAnalysisTerminalAction.Close),
            [FileAnalysisCodes.ConfigurationUnavailable] = new("Analysis is temporarily unavailable", "The analysis settings couldn’t be read. Try again in a few minutes.", FileAnalysisTerminalAction.Retry),
            [FileAnalysisCodes.DestinationChanged] = new("Who processes your document changed", "It changed after you agreed. Review the new details first.", FileAnalysisTerminalAction.Review),
            [FileAnalysisCodes.DisclosureChanged] = new("Who processes your document changed", "It changed after you agreed. Review the new details first.", FileAnalysisTerminalAction.Review),
            [FileAnalysisCodes.PromptVersionMismatch] = new("The server was being updated", "Try again now.", FileAnalysisTerminalAction.Retry),
            [FileAnalysisCodes.NotStarted] = new("The analysis didn’t start", "It waited too long to start. If this keeps happening, tell an administrator.", FileAnalysisTerminalAction.Retry),
            [FileAnalysisCodes.Interrupted] = new("The analysis stopped", "It stopped before it finished.", FileAnalysisTerminalAction.Retry),
            [FileAnalysisCodes.InternalError] = GenericFailed,
        };

    private static readonly FileAnalysisTerminalCopy UnknownCancelled =
        new("Analysis was stopped", "The analysis was stopped.", FileAnalysisTerminalAction.Close);

    private static readonly IReadOnlyDictionary<string, FileAnalysisTerminalCopy> Cancelled =
        new Dictionary<string, FileAnalysisTerminalCopy>(StringComparer.Ordinal)
        {
            [FileAnalysisCodes.Disabled] = new("Analysis was switched off", "An administrator switched off analysis before it started.", FileAnalysisTerminalAction.Close),
            [FileAnalysisCodes.RequesterUnavailable] = new("Analysis was stopped", "The account that started it can no longer analyze files.", FileAnalysisTerminalAction.Close),
            [FileAnalysisCodes.FileChanged] = new("Analysis was stopped", "The file was changed or removed after you started the analysis.", FileAnalysisTerminalAction.Close),
        };

    /// <summary>A <c>Failed</c> job's copy; an unknown or missing code reads as the generic row.</summary>
    public static FileAnalysisTerminalCopy ForFailed(string? code) =>
        code is not null && Failed.TryGetValue(code, out var copy) ? copy : GenericFailed;

    /// <summary>A <c>Cancelled</c> job's copy; an unknown or missing code reads "Analysis was stopped".</summary>
    public static FileAnalysisTerminalCopy ForCancelled(string? code) =>
        code is not null && Cancelled.TryGetValue(code, out var copy) ? copy : UnknownCancelled;

    /// <summary>The POST timed out or never reached the server: a job may have been enqueued, so no sent line.</summary>
    public static readonly FileAnalysisTerminalCopy PostTimeout =
        new("We couldn’t confirm the analysis started", "Check the file’s row in a moment.", FileAnalysisTerminalAction.Close, ShowSent: false);

    /// <summary>A POST <c>503</c> with no code (a proxy during a redeploy) — never the disabled state.</summary>
    public static readonly FileAnalysisTerminalCopy PostUnavailable =
        new("Analysis is temporarily unavailable", "Try again in a few minutes.", FileAnalysisTerminalAction.Retry, ShowSent: false);

    /// <summary>Any other POST refusal: no job exists, so no sent line.</summary>
    public static readonly FileAnalysisTerminalCopy PostRefused = GenericFailed with { ShowSent = false };

    /// <summary>A poll answered <c>403</c>: the caller's permissions changed.</summary>
    public static readonly FileAnalysisTerminalCopy PollForbidden =
        new("You can’t view this analysis", "Your permissions changed.", FileAnalysisTerminalAction.Close, ShowSent: false);

    /// <summary>The fallback processor name when a job carries no stamped one.</summary>
    public const string FallbackProcessor = "the analysis provider";

    /// <summary>The one sentence that says whether the document left, from the server's <c>DocumentSent</c>.</summary>
    public static string SentLine(FileAnalysisDocumentSent sent, string? processor)
    {
        var who = string.IsNullOrWhiteSpace(processor) ? FallbackProcessor : processor;
        return sent switch
        {
            FileAnalysisDocumentSent.Yes => $"Your document was sent to {who}.",
            FileAnalysisDocumentSent.Possibly => $"Your document may have been sent to {who}.",
            _ => "Your document was not sent.",
        };
    }

    /// <summary>Whether a job's document may already have left — what the second-transfer alert keys on.</summary>
    public static bool MayHaveBeenSent(FileAnalysisDocumentSent? sent) =>
        sent is FileAnalysisDocumentSent.Yes or FileAnalysisDocumentSent.Possibly;

    /// <summary>The too-large body: the server's limit when the <c>422</c> carried one, else a sentence without a number.</summary>
    public static string TooLargeBody(long? limitBytes) =>
        limitBytes is { } bytes && bytes > 0 ? $"The limit is {FormatBytes(bytes)}." : "It’s over the analysis size limit.";

    /// <summary>A byte count in binary megabytes, as the upload limits read ("32 MB").</summary>
    public static string FormatBytes(long bytes)
    {
        var megabytes = bytes / (1024d * 1024d);
        return megabytes >= 1
            ? $"{megabytes.ToString(megabytes % 1 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture)} MB"
            : $"{Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture)} KB";
    }

    public const string AlreadyRunning = "An analysis of this file is already running. Showing its progress.";
    public const string TakingLonger = "This is taking longer than usual.";
    public const string StartingAnalysis = "Starting analysis…";
    public const string PollHiccup = "Can’t reach the server to check progress. Still trying.";
    public const string SecondTransferFormat = "Analyzing again sends the document to {0} again.";
}

/// <summary>
/// The poller's timing rules (issue #445 §5): the interval the server names, a transient-failure back-off, and the
/// count of consecutive failures that shows the hiccup notice. Pure, so the rules are tested without a clock.
/// </summary>
public static class FileAnalysisPollPolicy
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(10);

    /// <summary>Three transient failures in a row show "Can't reach the server…".</summary>
    public const int HiccupAfter = 3;

    /// <summary>An unknown <c>Status</c> is treated as non-terminal for at most this many polls.</summary>
    public const int UnknownStatusPolls = 3;

    /// <summary>"This is taking longer than usual" after this long on the client clock.</summary>
    public static readonly TimeSpan TakingLongerAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The next poll's delay from a <c>Retry-After</c> given as delta-seconds (an HTTP-date is ignored by the caller,
    /// which passes only the delta form): clamped to 1–10 s, else 2 s.
    /// </summary>
    public static TimeSpan Interval(TimeSpan? retryAfterDelta) =>
        retryAfterDelta is { } delta ? Clamp(delta) : DefaultInterval;

    /// <summary>The wait after the <paramref name="failures"/>-th consecutive transient failure: 2, 4, 8, then 10 s.</summary>
    public static TimeSpan Backoff(int failures) => failures switch
    {
        <= 1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(4),
        3 => TimeSpan.FromSeconds(8),
        _ => MaxInterval,
    };

    private static TimeSpan Clamp(TimeSpan value) =>
        value < MinInterval ? MinInterval : value > MaxInterval ? MaxInterval : value;
}
