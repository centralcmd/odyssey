namespace Odyssey.MigrationService;

/// <summary>
/// Thrown instead of letting a migration replay onto a schema that already carries its objects
/// (issue #468). Carries the repair instructions in its own message, so the guidance survives wherever
/// the exception surfaces — a log line, a stack trace, or the DCP stdout file an Aspire user has to go
/// digging for.
/// </summary>
/// <remarks>
/// Since one context owns the history table, an applied id this build does not ship is real evidence of a
/// superseded set — but only when it is LATER than the baseline (issue #581 §9). Ids earlier than the baseline are
/// the retired ids a rebaseline leaves on every healthy database, and counting them would report an ordinary
/// interruption as a superseded set. With such ids present the message names that cause; without them it names
/// both causes, because an interruption and a renumber leave the same schema behind.
/// </remarks>
public sealed class MigrationDriftException(MigrationDrift drift, IReadOnlyList<string>? supersededIds = null)
    : Exception(BuildMessage(drift, supersededIds ?? []))
{
    /// <summary>The most superseded ids the message lists.</summary>
    internal const int SupersededIdLimit = 5;

    public string MigrationId { get; } = drift.MigrationId;

    public string ExistingObject { get; } = drift.ExistingObject;

    /// <summary>History ids absent from this build and later than the baseline; empty when none.</summary>
    public IReadOnlyList<string> SupersededIds { get; } = supersededIds ?? [];

    private static string BuildMessage(MigrationDrift drift, IReadOnlyList<string> superseded)
    {
        var head =
            $"Migration '{drift.MigrationId}' is recorded as pending, but the {drift.ExistingObject} " +
            "it creates already exists. The database is out of step with __EFMigrationsHistory. ";

        if (superseded.Count > 0)
        {
            var listed = string.Join(", ", superseded.Take(SupersededIdLimit).Select(id => $"'{id}'"));
            var more = superseded.Count > SupersededIdLimit ? $" and {superseded.Count - SupersededIdLimit} more" : "";
            return head +
                $"__EFMigrationsHistory holds migrations this build does not ship ({listed}{more}): the database was " +
                "built by a superseded set of migrations, which is what a renumber leaves behind. Re-running cannot " +
                "recover, and writing the new id into the history would record a schema the database does not " +
                "have. Repair the database before starting again — see docs/migration-history-drift.md.";
        }

        return head +
            "Two things cause that, and the repair is the same for both: either an earlier run was interrupted after " +
            "MariaDB had committed some of the migration's DDL but before its history row was written " +
            "(MariaDB commits DDL implicitly, so a migration is not atomic there), or the database was built " +
            "by a superseded set of migrations — check whether __EFMigrationsHistory holds ids this build no " +
            "longer ships, which is what a squash or a renumber leaves behind. Re-running cannot recover on " +
            "its own: the migration would replay from the start and fail on the object that is already " +
            "present. Repair the database before starting again — see docs/migration-history-drift.md. In " +
            "development the fastest repair is to drop and recreate the database, which re-applies every " +
            "migration cleanly.";
    }
}
