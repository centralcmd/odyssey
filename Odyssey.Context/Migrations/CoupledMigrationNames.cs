#nullable enable

namespace Odyssey.Context.Migrations;

/// <summary>
/// Every migration NAME that code outside the migrations themselves depends on (issue #581 §3). Each one is
/// matched by suffix (<see cref="Matches"/>) against an id such as <c>20261122104734_AddRecipes</c>.
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
    /// <summary>
    /// The data migration that folds an ingredient's preparation and range into its name and drops both columns
    /// (issue #595). Named by its seam test in <c>Odyssey.IntegrationTests</c>.
    /// </summary>
    public const string RemoveIngredientPreparationAndRange = nameof(RemoveIngredientPreparationAndRange);

    /// <summary>
    /// Every coupling above. The November 2026 rebaseline emptied the list: every coupling named a migration it folded
    /// away, and the code behind each went with it.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [RemoveIngredientPreparationAndRange];

    /// <summary>Whether <paramref name="migrationId"/> is the migration named <paramref name="name"/>.</summary>
    public static bool Matches(string migrationId, string name) =>
        migrationId.EndsWith("_" + name, StringComparison.Ordinal);
}
