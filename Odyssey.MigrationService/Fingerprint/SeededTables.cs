namespace Odyssey.MigrationService.Fingerprint;

/// <summary>What the schema fingerprint prints of a seeded table against a live database (issue #581 §8).</summary>
internal enum SeededTableClass
{
    /// <summary>Every column of every row: the values never change at runtime or by an administrator.</summary>
    Rows,

    /// <summary>The primary keys only: values change, the set of keys does not. The default.</summary>
    Keys,

    /// <summary>Nothing: administrators add or remove rows, so even the keys may legitimately differ.</summary>
    Excluded,
}

/// <param name="Table">The table name as the engine reports it.</param>
/// <param name="Reason">Why the class is safe; required, and asserted non-empty by a test.</param>
internal sealed record SeededTable(string Table, SeededTableClass Class, string Reason);

/// <summary>
/// Every table the model seeds with <c>HasData</c>, classified. <c>SeededTablesTests</c> fails when this list and
/// the model's seeded tables differ, so a new seed cannot be left out. The class governs only the fingerprint's
/// operator sections; the integration tier's parity section always carries every row.
/// </summary>
internal static class SeededTables
{
    public static IReadOnlyList<SeededTable> All { get; } =
    [
        new("AspNetRoles", SeededTableClass.Rows,
            "Fixed ids, names and ConcurrencyStamp constants (RoleDefinitions); nothing writes a role row at runtime."),
        new("Currencies", SeededTableClass.Excluded,
            "CurrenciesController adds and deletes rows, so an administrator's change must not read as drift."),
        new("SystemSettings", SeededTableClass.Keys,
            "Administrators edit the values at /settings; the keys are the seeded set."),
        new("NewsStorageTotals", SeededTableClass.Keys,
            "A singleton byte counter the worker updates."),
        new("FileStorageMaintenanceLeases", SeededTableClass.Keys,
            "A singleton lease the worker rewrites on every tick."),
        new("PhotoThumbnailBuckets", SeededTableClass.Keys,
            "A singleton bucket counter the thumbnail writer increments."),
    ];
}
