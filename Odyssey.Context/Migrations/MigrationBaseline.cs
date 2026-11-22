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
/// first. <c>null</c> when no earlier chain can be upgraded at all — the state since the August 2026 squash, whose
/// predecessors had to be recreated.
/// </param>
/// <remarks>The procedure that changes this value is <c>docs/migration-rebaseline.md</c>.</remarks>
public sealed record MigrationBaseline(string BaselineMigrationId, string? BridgeRelease)
{
    /// <summary>The baseline of this build.</summary>
    public static MigrationBaseline Current { get; } = new("20260829095318_InitialCreate", BridgeRelease: null);
}
