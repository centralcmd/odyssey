namespace Odyssey.MigrationService;

/// <summary>
/// Thrown instead of letting a migration replay onto a schema that already carries its objects
/// (issue #468). Carries the repair instructions in its own message, so the guidance survives wherever
/// the exception surfaces — a log line, a stack trace, or the DCP stdout file an Aspire user has to go
/// digging for.
/// </summary>
/// <remarks>
/// The message names both causes rather than asserting one, because the guard cannot tell them apart and
/// should not pretend to. Distinguishing them would mean checking __EFMigrationsHistory for ids this
/// build no longer ships — but the contexts share one history table, so rows belonging to the *other*
/// context always look unknown, and the check would report a squash on every healthy database. Naming
/// both costs a sentence; guessing sends the reader looking for an interruption that never happened.
/// </remarks>
public sealed class MigrationDriftException(string contextName, MigrationDrift drift)
    : Exception(BuildMessage(contextName, drift))
{
    public string ContextName { get; } = contextName;

    public string MigrationId { get; } = drift.MigrationId;

    public string ExistingObject { get; } = drift.ExistingObject;

    private static string BuildMessage(string contextName, MigrationDrift drift) =>
        $"{contextName}: migration '{drift.MigrationId}' is recorded as pending, but the {drift.ExistingObject} " +
        "it creates already exists. The database is out of step with __EFMigrationsHistory. Two things " +
        "cause that, and the repair is the same for both: either an earlier run was interrupted after " +
        "MariaDB had committed some of the migration's DDL but before its history row was written " +
        "(MariaDB commits DDL implicitly, so a migration is not atomic there), or the database was built " +
        "by a superseded set of migrations — check whether __EFMigrationsHistory holds ids this build no " +
        "longer ships, which is what a squash or a renumber leaves behind. Re-running cannot recover on " +
        "its own: the migration would replay from the start and fail on the object that is already " +
        "present. Repair the database before starting again — see docs/migration-history-drift.md. In " +
        "development the fastest repair is to drop and recreate the database, which re-applies every " +
        "migration cleanly.";
}
