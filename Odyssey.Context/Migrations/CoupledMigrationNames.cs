#nullable enable

namespace Odyssey.Context.Migrations;

/// <summary>
/// Every migration NAME that code outside the migrations themselves depends on (issue #581 §3). Each one is
/// matched by suffix (<see cref="Matches"/>) against an id such as <c>20261102001234_UnifyLocationsContract</c>.
/// </summary>
/// <remarks>
/// <para>
/// One list, so a rebaseline that deletes a migration also breaks the code that still expects it, loudly.
/// <c>CoupledMigrationNamesTests</c> requires every entry to resolve to exactly one migration that is not the
/// baseline (the baseline inherits the cut migration's id, so a name equal to the cut would still resolve after
/// a squash while the guard behind it silently pointed at the whole schema). A source-lint fails on a migration
/// name written as a literal anywhere else in <c>Odyssey.MigrationService</c> or <c>Odyssey.Core</c>.
/// </para>
/// <para>
/// Adding a coupling: add the constant here and to <see cref="All"/>, and read it from here.
/// </para>
/// </remarks>
public static class CoupledMigrationNames
{
    /// <summary>The migration the issue #380 location conversion runs after.</summary>
    public const string UnifyLocationsExpand = "UnifyLocationsExpand";

    /// <summary>The migration that runs after the location conversion and drops the old columns.</summary>
    public const string UnifyLocationsContract = "UnifyLocationsContract";

    /// <summary>Adds <c>CK_ExchangeRates_RatePositive</c>; the runner refuses it over a non-positive rate.</summary>
    public const string AddExchangeRatePositiveCheck = "AddExchangeRatePositiveCheck";

    /// <summary>The issue #218 data migration whose pending effect the migrations job reports first.</summary>
    public const string RetirePropertyAndVehicleAccountTypes = "RetirePropertyAndVehicleAccountTypes";

    public static IReadOnlyList<string> All { get; } =
    [
        UnifyLocationsExpand,
        UnifyLocationsContract,
        AddExchangeRatePositiveCheck,
        RetirePropertyAndVehicleAccountTypes,
    ];

    /// <summary>Whether <paramref name="migrationId"/> is the migration named <paramref name="name"/>.</summary>
    public static bool Matches(string migrationId, string name) =>
        migrationId.EndsWith("_" + name, StringComparison.Ordinal);
}
