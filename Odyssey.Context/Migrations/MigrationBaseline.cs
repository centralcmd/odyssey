#nullable enable

namespace Odyssey.Context.Migrations;

/// <summary>
/// Where this build's migration chain starts (issue #581). A rebaseline collapses the chain into one migration
/// that keeps the id of the newest migration it replaces, so a database already at that point sees it as
/// applied; a non-empty database whose history lacks <see cref="BaselineMigrationId"/> is refused by
/// <c>MigrationRunner</c> before anything runs.
/// </summary>
/// <param name="BaselineMigrationId">The earliest migration id this build ships.</param>
/// <param name="BridgeRelease">
/// The last release that shipped the replaced chain, which a database below the baseline must upgrade through
/// first. <c>null</c> when no earlier chain can be upgraded at all (the August 2026 squash, whose predecessors had to
/// be recreated, was such a baseline).
/// </param>
/// <remarks>The procedure that changes this value is <c>docs/migration-rebaseline.md</c>.</remarks>
public sealed record MigrationBaseline(string BaselineMigrationId, string? BridgeRelease)
{
    /// <summary>
    /// The baseline of this build: the November 2026 rebaseline (issue #581), cut at v0.51.0's newest migration. Its
    /// class is <c>InitialCreate</c>; the id is the cut's, verbatim, so databases already at the cut see it applied.
    /// </summary>
    public static MigrationBaseline Current { get; } = new("20261122104734_AddRecipes", BridgeRelease: "v0.51.0");
}
