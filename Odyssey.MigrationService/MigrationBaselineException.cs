using Odyssey.Context.Migrations;

namespace Odyssey.MigrationService;

/// <summary>
/// Thrown before anything runs when a non-empty database's <c>__EFMigrationsHistory</c> lacks this build's
/// baseline migration (issue #581). The message carries the remedy, so it survives wherever the exception
/// surfaces.
/// </summary>
/// <remarks>
/// Which of the two cases applies is decided by id order alone: a history whose newest id is earlier than the
/// baseline is <see cref="MigrationBaselineGap.BelowBaseline"/>, a later one <see cref="MigrationBaselineGap.ForeignSet"/>.
/// A database from an unrelated set whose ids happen to be earlier reads as the first, an accepted residual: the
/// runbook has the operator check the newest id against the bridge release's chain before following the advice,
/// and upgrading through the bridge would then stop on the drift guard rather than corrupt anything.
/// </remarks>
public sealed class MigrationBaselineException(MigrationBaseline baseline, string newestAppliedId)
    : Exception(BuildMessage(baseline, newestAppliedId))
{
    public string BaselineMigrationId { get; } = baseline.BaselineMigrationId;

    public string? BridgeRelease { get; } = baseline.BridgeRelease;

    public string NewestAppliedId { get; } = newestAppliedId;

    public MigrationBaselineGap Gap { get; } = GapOf(baseline, newestAppliedId);

    private static MigrationBaselineGap GapOf(MigrationBaseline baseline, string newest) =>
        string.CompareOrdinal(newest, baseline.BaselineMigrationId) < 0
            ? MigrationBaselineGap.BelowBaseline
            : MigrationBaselineGap.ForeignSet;

    private static string BuildMessage(MigrationBaseline baseline, string newest)
    {
        var start = $"This build's migrations start at '{baseline.BaselineMigrationId}'. ";

        if (GapOf(baseline, newest) == MigrationBaselineGap.ForeignSet)
        {
            return start +
                $"The database was built by a different migration set (newest applied migration '{newest}'), so it " +
                "cannot be upgraded by this build. Nothing was changed. See docs/migration-history-drift.md.";
        }

        var remedy = baseline.BridgeRelease is { } bridge
            ? $"Upgrade to {bridge} first, let its migrations job complete, then deploy this build."
            : "No earlier migration set can be upgraded; the database must be recreated.";

        return start +
            $"The database's newest applied migration is '{newest}', which is older. {remedy} Nothing was changed. " +
            "See docs/migration-rebaseline.md.";
    }
}

/// <summary>The two ways a history can lack the baseline id.</summary>
public enum MigrationBaselineGap
{
    /// <summary>The newest applied id is earlier than the baseline: the database predates the rebaseline.</summary>
    BelowBaseline,

    /// <summary>The newest applied id is later than the baseline: a different migration set built it.</summary>
    ForeignSet,
}
