using System.Globalization;
using Odyssey.Client.Pages.Finance.ExchangeRateSync;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages.Storage;

public enum StorageIssueSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>One condition needing attention: a title sentence and the text after it.</summary>
public sealed record StorageIssue(string Id, StorageIssueSeverity Severity, string Title, string Text);

/// <summary>
/// The Storage health panel's reading of <c>GET /api/file-storage/volumes</c> (Odyssey Design System ·
/// ui_kits/web/StorageHealth.jsx, issue #346). <see cref="Issues"/> is the ONE list both the panel's notes and
/// the System settings page-header signal read, so the two can never disagree about what is wrong.
/// </summary>
public static class StorageHealth
{
    /// <summary>The panel's id and the page signal's jump target.</summary>
    public const string PanelId = "ss-storage-health";

    /// <summary>
    /// Feature keys are a closed catalogue on the server. A key this build has no label for — one rebound away,
    /// or left by a rollback — is shown as its key, in mono, rather than guessed at.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FeatureLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PhotoThumbnails"] = "Photo thumbnails",
    };

    public static string? FeatureLabel(string featureKey) => FeatureLabels.GetValueOrDefault(featureKey);

    /// <summary>The conditions needing attention, most severe first.</summary>
    /// <param name="now">For the relative distance in the stopped clean-up note.</param>
    public static IReadOnlyList<StorageIssue> Issues(IReadOnlyList<FileStorageVolumeStatus>? volumes, DateTimeOffset now)
    {
        var issues = new List<StorageIssue>();
        if (volumes is null)
        {
            return issues;
        }

        foreach (var v in volumes)
        {
            if (v.State == FileStorageVolumeState.Degraded)
            {
                issues.Add(new($"deg-{v.Name}", StorageIssueSeverity.Error, $"Volume {v.Name} is degraded.",
                    "Files on it can’t be read or written, so uploads and downloads that use it fail with “try again later”. "
                    + "It is re-checked every 15 seconds and clears on its own once the disk answers. Check the disk and its mount."));
            }

            if (v.IntegrityFaultCount > 0)
            {
                issues.Add(new($"int-{v.Name}", StorageIssueSeverity.Error,
                    $"{Count(v.IntegrityFaultCount)} file{Plural(v.IntegrityFaultCount)} on {v.Name} failed verification.",
                    "Their content no longer matches what was stored, so they return an error rather than wrong content. "
                    + "Restore the volume from a backup; a file that matches again is cleared on the next check."));
            }

            if (v.ReconciliationHalted)
            {
                issues.Add(new($"halt-{v.Name}", StorageIssueSeverity.Warning, $"The disk check on {v.Name} has paused clean-up.",
                    $"Its last pass found {Count(v.LastPassOrphanCount ?? 0)} files the database doesn’t know about, more than the safety limit. "
                    + "That usually means the database and the file volume came from different backups. Nothing is set aside until a pass finds fewer."));
            }

            if (v.OrphanedFeatures.Count > 0)
            {
                issues.Add(new($"orph-{v.Name}", StorageIssueSeverity.Warning, $"Volume {v.Name} holds files for a feature this version doesn’t know.",
                    $"{string.Join(", ", v.OrphanedFeatures)}. The files are kept and not served, so a rollback can be undone. "
                    + "Deploy the version that knows the feature, or remove it on purpose."));
            }

            if (v.OverdueDeleteCount > 0)
            {
                var one = v.OverdueDeleteCount == 1;
                issues.Add(new($"od-{v.Name}", StorageIssueSeverity.Error,
                    $"{Count(v.OverdueDeleteCount)} deleted file{(one ? " is" : "s are")} past {(one ? "its" : "their")} erase time on {v.Name}.",
                    "They should have been erased over 2 hours ago and are still on disk, so personal data is kept longer than the deletion grace allows. "
                    + "Clean-up retries them every hour. If the number doesn’t fall, check the worker’s log and that it can write to the volume."));
            }

            if (v.State == FileStorageVolumeState.NotConfigured)
            {
                issues.Add(new($"nc-{v.Name}", StorageIssueSeverity.Info, $"Volume {v.Name} is recorded but no longer configured.",
                    "It holds no files, so nothing is affected. It stays listed so a removed volume is visible rather than forgotten."));
            }
        }

        // The maintenance fields are instance-wide and repeated on every entry; any entry carries them. A stopped
        // job explains a stale one, so the stale note gives way to it.
        if (volumes.FirstOrDefault() is { MaintenanceHaltedCheck: { } check } halted)
        {
            var when = halted.MaintenanceHaltedAtUtc is { } at
                ? $" {ExchangeRateSyncTime.Absolute(at)} ({ExchangeRateSyncTime.Relative(at, now)})"
                : string.Empty;
            issues.Add(new("maint-halt", StorageIssueSeverity.Error, "File clean-up is stopped: the worker’s storage check failed.",
                $"Check {check} failed{when}. Deleted files, including personal data, stay on disk until it passes. "
                + "The worker’s other jobs, weather and exchange-rate sync, keep running. "
                + "Its log names the volume and the fix; it re-checks every 10 minutes and resumes on its own."));
        }
        else if (volumes.FirstOrDefault() is { MaintenanceStale: true } maintenance)
        {
            issues.Add(new("stale", StorageIssueSeverity.Warning,
                maintenance.MaintenanceLastCompletedAtUtc is null
                    ? "File clean-up has never completed."
                    : "File clean-up hasn’t completed in over 3 hours.",
                "Deleted files, including personal data, stay on disk until it runs. Check that the worker container is running."));
        }

        return [.. issues.OrderBy(i => i.Severity)];
    }

    /// <summary>Bytes in 1024-steps: <c>412 GB</c>, <c>870.1 MB</c>, <c>12 bytes</c>.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var number = value >= 100 || unit == 0
            ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{number} {units[unit]}";
    }

    public static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Plural(int count) => count == 1 ? string.Empty : "s";
}
