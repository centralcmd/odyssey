using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.JobRuns;

/// <summary>A job as the page names it: its title, its in-sentence form, its glyph, and the claim area a nulled field names.</summary>
/// <param name="Name">"Exchange-rate sync" — a title, a sentence start.</param>
/// <param name="Lower">"exchange-rate sync" — mid-sentence.</param>
/// <param name="Icon">Material Icons ligature (decorative).</param>
/// <param name="Area">The access a caller lacks when this job's counters are nulled ("needs {area} access").</param>
/// <param name="RunLabel">The header menu's item, or <c>null</c> for a job this client cannot start.</param>
/// <param name="MenuIcon">The header menu item's SVG icon (MudMenuItem takes an SVG constant, never a ligature).</param>
public sealed record JobVisual(string Name, string Lower, string Icon, string? Area, string? RunLabel, string? MenuIcon);

/// <summary>A chip's word, tone and glyph — never a colour or an icon alone (#410 §6.1).</summary>
public sealed record JobChip(string Label, OdsChipTone Tone, string Icon);

/// <summary>
/// The Background jobs page's one presentation map (issue #410 §9.2, the <c>OdsTypeRegistries</c> pattern; Odyssey
/// Design System · <c>ui_kits/web/BackgroundJobs.jsx</c>, <c>BGJ_*</c>). Every word the page shows for a server enum is
/// here; the server sends names only and none of its text is ever rendered. An unknown member — the tolerant
/// converter's <c>Unknown</c>, or a value this build has no row for — reads "Unknown" with the neutral tone, and an
/// unknown job "Other job" with no Run item.
/// </summary>
public static class BackgroundJobPresentation
{
    /// <summary>The seven jobs, in catalogue (ordinal) order — the tiles', the Run items' and the Job filter's order.</summary>
    public static readonly IReadOnlyList<JobType> Jobs =
        [JobType.ExchangeRateSync, JobType.Weather, JobType.FileStorageMaintenance, JobType.PhotoThumbnails, JobType.PhotoOriginalBackfill,
            JobType.FileAnalysis, JobType.FileBlobBackfill,
            JobType.InstrumentPriceSync];

    public static readonly IReadOnlyDictionary<JobType, JobVisual> JobVisuals = new Dictionary<JobType, JobVisual>
    {
        [JobType.ExchangeRateSync] = new("Exchange-rate sync", "exchange-rate sync", "currency_exchange", "exchange-rate", "Run exchange-rate sync", MudBlazor.Icons.Material.Filled.CurrencyExchange),
        [JobType.Weather] = new("Weather", "weather", "cloud", "weather point", "Run weather", MudBlazor.Icons.Material.Filled.Cloud),
        [JobType.FileStorageMaintenance] = new("File-storage maintenance", "file-storage maintenance", "storage", "file storage", "Run file-storage maintenance", MudBlazor.Icons.Material.Filled.Storage),
        [JobType.PhotoThumbnails] = new("Photo thumbnails", "photo thumbnails", "photo_library", "photo", "Run photo thumbnails", MudBlazor.Icons.Material.Filled.PhotoLibrary),
        [JobType.PhotoOriginalBackfill] = new("Photo original move", "photo original move", "drive_file_move", "file storage", "Run photo original move", MudBlazor.Icons.Material.Filled.DriveFileMove),
        // The statement-analysis queue (issue #444). No Run item: it only runs analyses a user consented to, and the
        // server refuses a manual run (CanRunNow is false).
        [JobType.FileAnalysis] = new("File analysis", "file analysis", "document_scanner", "file analysis", null, null),
        // Issue #449: the photo move's icon on purpose — the same kind of job, told apart by its name.
        [JobType.FileBlobBackfill] = new("File move", "file move", "drive_file_move", "file storage", "Run file move", MudBlazor.Icons.Material.Filled.DriveFileMove),
        [JobType.InstrumentPriceSync] = new("Instrument-price sync", "instrument-price sync", "candlestick_chart", "instrument", "Run instrument-price sync", MudBlazor.Icons.Material.Filled.CandlestickChart),
    };

    public static readonly JobVisual OtherJob = new("Other job", "other job", "work_outline", null, null, null);

    public static JobVisual JobOf(JobType type) => JobVisuals.GetValueOrDefault(type, OtherJob);

    public static readonly JobChip UnknownChip = new("Unknown", OdsChipTone.Outline, "help_outline");

    public static readonly IReadOnlyDictionary<JobHealth, JobChip> Healths = new Dictionary<JobHealth, JobChip>
    {
        [JobHealth.Healthy] = new("Healthy", OdsChipTone.Income, "check_circle"),
        [JobHealth.Running] = new("Running", OdsChipTone.Info, "sync"),
        [JobHealth.Failing] = new("Failing", OdsChipTone.Error, "error"),
        [JobHealth.Overdue] = new("Overdue", OdsChipTone.Warning, "schedule"),
        [JobHealth.Held] = new("Held", OdsChipTone.Warning, "pause_circle"),
        [JobHealth.NotTicking] = new("Not ticking", OdsChipTone.Error, "heart_broken"),
        [JobHealth.NeverRun] = new("Never run", OdsChipTone.Outline, "hourglass_empty"),
        [JobHealth.NotConfigured] = new("Not configured", OdsChipTone.Outline, "settings"),
    };

    public static JobChip HealthOf(JobHealth health) => Healths.GetValueOrDefault(health, UnknownChip);

    /// <summary>The healths the Attention region lists (§3.4).</summary>
    public static readonly IReadOnlySet<JobHealth> AttentionHealths =
        new HashSet<JobHealth> { JobHealth.Failing, JobHealth.NotTicking, JobHealth.Held, JobHealth.Overdue };

    /// <summary>The statuses in ordinal order — the Status filter's and the Status sort's order.</summary>
    public static readonly IReadOnlyList<JobRunStatus> Statuses =
    [
        JobRunStatus.Running, JobRunStatus.Succeeded, JobRunStatus.PartiallySucceeded,
        JobRunStatus.Failed, JobRunStatus.Skipped, JobRunStatus.Held,
    ];

    public static readonly IReadOnlyDictionary<JobRunStatus, JobChip> StatusChips = new Dictionary<JobRunStatus, JobChip>
    {
        [JobRunStatus.Running] = new("Running", OdsChipTone.Info, "sync"),
        [JobRunStatus.Succeeded] = new("Succeeded", OdsChipTone.Income, "check_circle"),
        [JobRunStatus.PartiallySucceeded] = new("Partly succeeded", OdsChipTone.Warning, "incomplete_circle"),
        [JobRunStatus.Failed] = new("Failed", OdsChipTone.Error, "cancel"),
        [JobRunStatus.Skipped] = new("Skipped", OdsChipTone.Outline, "skip_next"),
        [JobRunStatus.Held] = new("Held", OdsChipTone.Warning, "pause_circle"),
    };

    public static JobChip StatusOf(JobRunStatus status) => StatusChips.GetValueOrDefault(status, UnknownChip);

    /// <summary>A finished run in a sentence: "{Job} run {words}" (§3.5 "On finish").</summary>
    public static readonly IReadOnlyDictionary<JobRunStatus, string> StatusSentences = new Dictionary<JobRunStatus, string>
    {
        [JobRunStatus.Running] = "is still running",
        [JobRunStatus.Succeeded] = "succeeded",
        [JobRunStatus.PartiallySucceeded] = "partly succeeded",
        [JobRunStatus.Failed] = "failed",
        [JobRunStatus.Skipped] = "had nothing to do",
        [JobRunStatus.Held] = "is held",
    };

    public static string StatusSentenceOf(JobRunStatus status) => StatusSentences.GetValueOrDefault(status, "finished");

    public static readonly IReadOnlyList<JobRunTrigger> Triggers = [JobRunTrigger.Scheduled, JobRunTrigger.Manual, JobRunTrigger.Purge];

    public static readonly IReadOnlyDictionary<JobRunTrigger, string> TriggerWords = new Dictionary<JobRunTrigger, string>
    {
        [JobRunTrigger.Scheduled] = "Scheduled",
        [JobRunTrigger.Manual] = "Manual",
        [JobRunTrigger.Purge] = "Purge",
    };

    public static string TriggerOf(JobRunTrigger trigger) => TriggerWords.GetValueOrDefault(trigger, "Unknown");

    public static readonly IReadOnlyDictionary<JobFailureCategory, string> FailureWords = new Dictionary<JobFailureCategory, string>
    {
        [JobFailureCategory.ProviderUnavailable] = "The provider didn’t respond",
        [JobFailureCategory.ProviderRateLimited] = "The provider asked us to slow down",
        [JobFailureCategory.ProviderMalformed] = "The provider sent data we couldn’t read",
        [JobFailureCategory.UnsupportedInput] = "Something asked for isn’t supported",
        [JobFailureCategory.Configuration] = "A setting is invalid",
        [JobFailureCategory.Environment] = "The server is missing something it needs",
        [JobFailureCategory.Cancelled] = "Stopped when the server shut down",
        [JobFailureCategory.Abandoned] = "Stopped unexpectedly",
        [JobFailureCategory.Internal] = "Something went wrong on the server",
        [JobFailureCategory.Storage] = "Storage wasn’t available",
    };

    public static string FailureOf(JobFailureCategory category) => FailureWords.GetValueOrDefault(category, "Unknown");

    /// <summary>Lower-case: they follow "Held: " and "{Job} is held: ".</summary>
    public static readonly IReadOnlyDictionary<JobHeldReason, string> HeldWords = new Dictionary<JobHeldReason, string>
    {
        [JobHeldReason.StorageCheckFailed] = "storage check failed",
        [JobHeldReason.BreakerTripped] = "paused after repeated crashes",
        [JobHeldReason.DecoderPaused] = "paused while a stuck decode ends",
        [JobHeldReason.DecoderPolicyFailed] = "the image decoder failed its security check",
        // Generic since issue #449: thumbnails, the photo move and the file move all read it.
        [JobHeldReason.StorageUnavailable] = "file storage isn’t available",
        [JobHeldReason.BackingOff] = "paused after a storage or database error",
    };

    /// <summary>
    /// The photo-original move's park codes (issue #428) as its status card lists them, in the card's order. A code this
    /// client does not know is counted under "Other" — the wire carries the code, never a sentence.
    /// </summary>
    public static readonly IReadOnlyList<(string Code, string Label)> PhotoParkCodes =
    [
        ("hash", "Checksum mismatch"),
        ("read", "Couldn’t be read"),
        ("too-large", "Too large"),
        ("error", "Unexpected error"),
    ];

    /// <summary>The file move's park codes (issue #449): the photo move's plus <c>type</c>, which only the file move parks for.</summary>
    public static readonly IReadOnlyList<(string Code, string Label)> FileParkCodes =
    [
        ("hash", "Checksum mismatch"),
        ("read", "Couldn’t be read"),
        ("too-large", "Too large"),
        ("type", "Unsupported type"),
        ("error", "Unexpected error"),
    ];

    /// <summary>One line per code with a count above 0 in the given order, then any parked item under a code this client does not know.</summary>
    public static IReadOnlyList<string> ParkedLines(IReadOnlyDictionary<string, int> byCode, int parked, IReadOnlyList<(string Code, string Label)> codes)
    {
        var lines = new List<string>();
        var known = 0;
        foreach (var (code, label) in codes)
        {
            var count = byCode.GetValueOrDefault(code);
            known += count;
            if (count > 0)
                lines.Add($"{label}: {Count(count)}");
        }

        if (parked > known)
            lines.Add($"Other: {Count(parked - known)}");
        return lines;
    }

    /// <summary>The photo move's card (issue #428). Its DTO is unchanged: <c>remaining</c> counts parked photos too.</summary>
    public static BackfillMoveView PhotoMoveView(PhotoBackfillStatus s)
    {
        var parked = Math.Max(0, s.Failed);
        var waiting = Math.Max(0, s.Remaining - parked);
        return new BackfillMoveView(
            s.Remaining <= 0 ? "Every photo original is on disk."
            : waiting > 0 ? $"{Plural(waiting, "photo original", "photo originals")} still to move from the database to disk."
            : "Only parked photos are left. They stay readable from the database until an administrator clears them.",
            [("To move", waiting), ("Parked", parked), ("Copied and kept", s.CopiedShared)],
            null,
            ParkedLines(s.FailedByCode, parked, PhotoParkCodes),
            "Parked photos by reason",
            s.BackingOffUntilUtc,
            s.LastRunAtUtc);
    }

    /// <summary>
    /// The file move's card (issue #449 §4.2). The server sends <c>toMove</c>, <c>waitingOnPhotos</c> and <c>failed</c>
    /// disjoint, so nothing is subtracted. State precedence: Done → Moving → Waiting on photos → Only parked left.
    /// </summary>
    public static BackfillMoveView FileMoveView(FileBackfillStatus s)
    {
        var lead = s.Remaining <= 0 ? "Every file is on disk."
            : s.ToMove > 0 ? $"{Plural(s.ToMove, "file", "files")} still to move from the database to disk."
            : s.WaitingOnPhotos > 0 ? $"{Plural(s.WaitingOnPhotos, "file is", "files are")} waiting for the photo move to finish."
            : "Only parked files are left. They stay readable from the database until an administrator clears them.";
        var detail = $"On disk: {Plural(s.OnDisk.Finance, "finance file", "finance files")} · "
            + $"{Plural(s.OnDisk.Journal, "journal file", "journal files")} · "
            + $"{Plural(s.OnDisk.ContactAvatars, "contact avatar", "contact avatars")}";
        return new BackfillMoveView(
            lead,
            [("To move", Math.Max(0, s.ToMove)), ("Parked", Math.Max(0, s.Failed)), ("Waiting on photos", Math.Max(0, s.WaitingOnPhotos))],
            detail,
            ParkedLines(s.FailedByCode, Math.Max(0, s.Failed), FileParkCodes),
            "Parked files by reason",
            s.BackingOffUntilUtc,
            s.LastRunAtUtc);
    }

    /// <summary>The reason's words, or <c>null</c> for none or one this client does not know — the caller's generic copy then applies.</summary>
    public static string? HeldOf(JobHeldReason? reason) =>
        reason is { } known && HeldWords.TryGetValue(known, out var words) ? words : null;

    public static readonly IReadOnlyDictionary<WeatherFetchFailureKind, string> WeatherFailureWords = new Dictionary<WeatherFetchFailureKind, string>
    {
        [WeatherFetchFailureKind.Timeout] = "MET Norway didn’t answer in time",
        [WeatherFetchFailureKind.ProviderRejected] = "MET Norway refused the request",
        [WeatherFetchFailureKind.RateLimited] = "MET Norway asked us to slow down",
        [WeatherFetchFailureKind.ProviderError] = "MET Norway reported an error",
        [WeatherFetchFailureKind.MalformedPayload] = "MET Norway sent data we couldn’t read",
        [WeatherFetchFailureKind.NotConfigured] = "Weather isn’t set up",
        [WeatherFetchFailureKind.ResponseTooLarge] = "MET Norway’s response was too large",
    };

    public static string WeatherFailureOf(WeatherFetchFailureKind kind) => WeatherFailureWords.GetValueOrDefault(kind, "Unknown");

    /// <summary>The sync's own error words — the existing exchange-rate sync vocabulary, not a second copy.</summary>
    public static string ExchangeRateErrorOf(ExchangeRateSyncErrorKind kind) => OdsTypeRegistries.ExchangeRateSyncErrorOf(kind);

    /// <summary>The worker-health alert's line for a non-running thumbnail worker; <c>null</c> while it runs.</summary>
    public static string? ThumbnailWorkerLine(ThumbnailWorkerState state, DateTime? resumesAtUtc) => state switch
    {
        ThumbnailWorkerState.Running => null,
        ThumbnailWorkerState.Paused => "Photo thumbnails are paused while a stuck decode ends.",
        ThumbnailWorkerState.Tripped => $"Photo thumbnails are paused after repeated crashes{(resumesAtUtc is { } at ? $" until {Services.RunTime.Clock(at)}" : string.Empty)}.",
        ThumbnailWorkerState.PolicyFailed => "Photo thumbnails are stopped: the image decoder failed its security check.",
        ThumbnailWorkerState.StorageUnavailable => "Photo thumbnails are stopped: thumbnail storage isn’t available.",
        ThumbnailWorkerState.NotRunning => "No photo-thumbnail worker has reported in the last 3 minutes.",
        _ => "Photo thumbnails report a state this page doesn’t know.",
    };

    /// <summary>"1.8 s", "4 min", "1.2 h".</summary>
    public static string Duration(long ms) =>
        ms < 60_000 ? $"{(ms / 1000.0).ToString("0.0", CultureInfo.CurrentCulture)} s"
        : ms < 3_600_000 ? $"{Math.Round(ms / 60_000.0).ToString("0", CultureInfo.CurrentCulture)} min"
        : $"{(ms / 3_600_000.0).ToString("0.0", CultureInfo.CurrentCulture)} h";

    public static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Plural(int n, string one, string many) => $"{Count(n)} {(n == 1 ? one : many)}";

    // ── Filter options: the value is the enum's exact name, which is what the allow-list accepts ───────

    public static readonly IReadOnlyList<OdsOption> JobOptions =
        [.. Jobs.Select(t => new OdsOption(t.ToString(), JobVisuals[t].Name) { Icon = JobVisuals[t].Icon })];

    public static readonly IReadOnlyList<OdsOption> StatusOptions =
        [.. Statuses.Select(s => new OdsOption(s.ToString(), StatusChips[s].Label) { Icon = StatusChips[s].Icon })];

    public static readonly IReadOnlyList<OdsOption> TriggerOptions =
        [.. Triggers.Select(t => new OdsOption(t.ToString(), TriggerWords[t]))];
}

/// <summary>
/// What a move's status card renders (issue #449 §4.2): already chosen, worded and pluralised, so the card does no arithmetic
/// on counts. Built by <see cref="BackgroundJobPresentation.PhotoMoveView"/> and <see cref="BackgroundJobPresentation.FileMoveView"/>.
/// </summary>
public sealed record BackfillMoveView(
    string Lead,
    IReadOnlyList<(string Label, int Value)> Tiles,
    string? DetailLine,
    IReadOnlyList<string> ParkedLines,
    string ParkedListCaption,
    DateTime? BackingOffUntilUtc,
    DateTime? LastRunAtUtc);
