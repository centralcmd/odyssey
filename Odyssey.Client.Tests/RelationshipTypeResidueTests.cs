using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// A source-lint asserting the contact <c>RelationshipType</c> field stays removed (issue #52 AC #11).
/// </summary>
/// <remarks>
/// <para>
/// Two halves, because the field had two spellings. Both sweep the APPLICATION projects — the ones
/// that could reintroduce it — the same scoping <see cref="ContactMutationLockRetiredTests"/> uses.
/// The PascalCase <b>identifier</b> may survive only in the timestamped migration and designer files,
/// which are immutable historical records; the vCard <b>property name</b> may survive nowhere in that
/// set at all, which is what leaves the import-tolerance fixture AC #7 requires free to name it (it
/// lives in a test project, outside the swept set). That is how the two halves stop contradicting
/// each other: the fixture is not a production source.
/// </para>
/// <para>
/// The migration glob is load-bearing rather than decorative:
/// <c>OdysseyContextModelSnapshot.cs</c> has no underscore in its name, so <c>*_*.cs</c> excludes it
/// mechanically. The snapshot is generated STATE, not history — a stale one would leave the model and
/// the schema disagreeing with nothing in the repository to catch it.
/// </para>
/// <para>
/// <c>Odyssey Design System/</c> is excluded. It is vendored, not source: <c>CONTRIBUTING.md</c>
/// prohibits hand-editing it, so a check demanding an edit there could not be satisfied at all — the
/// correction is the upstream re-export tracked in issue #54, and nothing in the shipped app loads the
/// bundle. Belt-and-braces rather than load-bearing, since the design system uses camelCase
/// <c>relationshipType</c> while this half greps the PascalCase identifier; kept as documentation of
/// the decision.
/// </para>
/// <para>
/// A lint rather than a behavioural test because the failure mode is <b>reintroduction</b>: nothing
/// consumed the value, so a branch that brings the property back compiles and renders identically.
/// It lives in this project because this is where the repository's source-lints live; the subject is
/// the whole solution, so it reads through <see cref="ClientSource.Sibling"/>.
/// </para>
/// </remarks>
public class RelationshipTypeResidueTests
{
    /// <summary>
    /// Every project that ships. The test projects are out of scope on purpose — the ACs' own fixtures
    /// have to name both spellings to assert the compatibility behaviour, and a lint that tripped over
    /// its own subject would have to carry a file allow-list that any later test naming the field in
    /// prose would break.
    /// </summary>
    private static readonly string[] ApplicationProjects =
    [
        "Odyssey.Api", "Odyssey.ApiClient", "Odyssey.AppHost", "Odyssey.Client", "Odyssey.Context",
        "Odyssey.Core", "Odyssey.Dtos", "Odyssey.MigrationService", "Odyssey.TestData",
    ];

    [Fact]
    public void The_identifier_survives_only_in_the_historical_migration_files()
    {
        var offenders = ApplicationSourceFiles()
            .Where(file => File.ReadAllText(file).Contains("RelationshipType", StringComparison.Ordinal))
            .Select(RelativeToRepo)
            .Where(path => !IsHistoricalMigration(path))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "PersonDetails.RelationshipType, its enum, its DTO property, its client registry and its "
            + "draft carrier were removed in issue #52: nothing branched on the value, no UI surface "
            + "could show, edit or clear it, and it was stored personal data about a named person that "
            + "the product did not use. Only the timestamped files under Odyssey.Context/Migrations/ "
            + "may still name it — the model snapshot is generated state, not history. Found in: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void The_vcard_property_survives_only_in_the_import_tolerance_fixture()
    {
        var offenders = ApplicationSourceFiles()
            .Where(file => File.ReadAllText(file).Contains("X-ODYSSEY-RELATIONSHIP", StringComparison.Ordinal))
            .Select(RelativeToRepo)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "X-ODYSSEY-RELATIONSHIP is neither emitted on export nor looked up on import any more. It "
            + "may appear only in the test fixture asserting that an incoming line is TOLERATED rather "
            + "than rejected (issue #52 AC #7), never in a shipping source. Found in: "
            + string.Join(", ", offenders));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every checked-in C# and Razor file in the shipping projects. Build output is excluded; the
    /// migrations folder is deliberately INCLUDED so the model snapshot is swept.
    /// </summary>
    private static IEnumerable<string> ApplicationSourceFiles() =>
        ApplicationProjects
            .Select(ClientSource.Sibling)
            .Where(Directory.Exists)
            .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(project, "*.razor", SearchOption.AllDirectories)))
            .Where(file => !IsBuildOutput(file));

    private static bool IsHistoricalMigration(string path)
    {
        var normalized = Normalize(path);
        return normalized.StartsWith("Odyssey.Context/Migrations/", StringComparison.Ordinal)
            && Path.GetFileName(normalized).Contains('_', StringComparison.Ordinal);
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string RelativeToRepo(string file) =>
        Path.GetRelativePath(Path.GetDirectoryName(ClientSource.Root)!, file);

    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
}
