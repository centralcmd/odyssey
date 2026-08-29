namespace Odyssey.MigrationService;

/// <summary>The kinds of schema object the drift guard can recognise.</summary>
/// <remarks>
/// Every one of these is created by its own DDL statement, and MariaDB commits each independently —
/// which is what makes each of them a place an interrupted migration can leave a half-applied schema.
/// Adding a kind means adding a case here, a case in <c>MigrationRunner.CreatedBy</c> (which maps an
/// EF <c>MigrationOperation</c> onto it) and a source for it in the schema snapshot; nothing else.
/// </remarks>
public enum SchemaObjectKind
{
    Table,
    Column,
    Index,
    ForeignKey,
}

/// <summary>
/// One schema object, identified the way the engine identifies it: by kind, owning table and name.
/// For <see cref="SchemaObjectKind.Table"/>, <see cref="Name"/> repeats <see cref="Table"/>.
/// </summary>
public sealed record SchemaObject(SchemaObjectKind Kind, string Table, string Name)
{
    /// <summary>Phrased for the operator-facing message, which is this guard's whole deliverable.</summary>
    public override string ToString() => Kind switch
    {
        SchemaObjectKind.Table => $"table '{Table}'",
        SchemaObjectKind.Column => $"column '{Table}.{Name}'",
        SchemaObjectKind.Index => $"index '{Name}' on table '{Table}'",
        SchemaObjectKind.ForeignKey => $"foreign key '{Name}' on table '{Table}'",
        _ => $"object '{Name}' on table '{Table}'",
    };
}

/// <summary>
/// The schema objects a database already contains.
/// </summary>
/// <remarks>
/// Matching is case-insensitive. MariaDB's identifier casing depends on <c>lower_case_table_names</c>
/// and on the host filesystem, and for a detector the conservative reading is that two names differing
/// only in case are the same object — a false positive here is a loud, accurate-enough error, while a
/// false negative is the wedged stack this whole type exists to prevent.
/// </remarks>
public sealed class SchemaObjects
{
    private readonly HashSet<SchemaObject> objects;

    public SchemaObjects(IEnumerable<SchemaObject> objects)
    {
        this.objects = new HashSet<SchemaObject>(objects, SchemaObjectComparer.Instance);
    }

    public static SchemaObjects Empty { get; } = new([]);

    public bool Contains(SchemaObject candidate) => objects.Contains(candidate);

    private sealed class SchemaObjectComparer : IEqualityComparer<SchemaObject>
    {
        public static readonly SchemaObjectComparer Instance = new();

        public bool Equals(SchemaObject? left, SchemaObject? right) =>
            ReferenceEquals(left, right)
            || (left is not null
                && right is not null
                && left.Kind == right.Kind
                && StringComparer.OrdinalIgnoreCase.Equals(left.Table, right.Table)
                && StringComparer.OrdinalIgnoreCase.Equals(left.Name, right.Name));

        public int GetHashCode(SchemaObject value) =>
            HashCode.Combine(
                value.Kind,
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Table),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }
}

/// <summary>
/// One migration EF still considers pending, reduced to the objects it would <em>create</em>. Only
/// creations matter: replaying a creation against an object that already exists is what turns an
/// interrupted run into a permanent failure.
/// </summary>
public sealed record PendingMigrationObjects(string MigrationId, IReadOnlyList<SchemaObject> Creates);

/// <summary>A detected drift: the pending migration, and the first object of its that already exists.</summary>
public sealed record MigrationDrift(string MigrationId, string ExistingObject);

/// <summary>
/// Decides whether a context's pending migrations can still be applied, or whether the database has
/// drifted out of step with <c>__EFMigrationsHistory</c> (issue #468).
/// </summary>
/// <remarks>
/// <para>
/// The drift this catches comes from MariaDB committing DDL implicitly: a migration is <em>not</em>
/// atomic there, whatever transaction EF opens around it. Interrupt a run part-way and the objects it
/// created stay behind while the history row — written only after the migration's last DDL statement —
/// never lands. Every later run then replays the migration from the top and dies on the first object
/// that already exists, so the stack never comes up again.
/// </para>
/// <para>
/// The test is deliberately narrow: a pending migration that would <em>create</em> an object which is
/// already there. "Pending migrations exist and so do some tables" would be far easier to compute and
/// would fire on every ordinary upgrade — a migration adding a column to a live schema is exactly that
/// shape, and is perfectly healthy.
/// </para>
/// <para>
/// It reports; it never repairs. Writing the missing history row would be the tempting fix and is the
/// dangerous one: an interruption leaves an arbitrary <em>prefix</em> of the migration applied, so
/// marking it complete records a half-built schema as finished and moves the failure somewhere far less
/// obvious. Failing loudly with the repair spelled out is the whole goal.
/// </para>
/// </remarks>
public static class MigrationDriftDetector
{
    /// <summary>Returns the first drift found, or <c>null</c> when the pending migrations can be applied.</summary>
    public static MigrationDrift? Detect(
        IReadOnlyList<PendingMigrationObjects> pendingMigrations, SchemaObjects existing)
    {
        foreach (var migration in pendingMigrations)
        {
            foreach (var created in migration.Creates)
            {
                if (existing.Contains(created))
                {
                    return new MigrationDrift(migration.MigrationId, created.ToString());
                }
            }
        }

        return null;
    }
}
