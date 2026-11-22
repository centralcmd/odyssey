using System.Text.RegularExpressions;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// No two source files of this tier name the same <c>odyssey_…</c> database. The tier's collections run in parallel
/// against one MariaDB server (see <see cref="MariaDbFixture"/>), so a name two classes share is two tests dropping and
/// recreating one database under each other — a flake that depends on scheduling, not a failure that names its cause.
/// This makes it a failure that does.
/// </summary>
/// <remarks>
/// A source scan rather than reflection over <c>const string Database</c> fields, because a name is just as often a
/// literal inside a method (<c>"odyssey_seeder_it"</c>) or the prefix of an interpolated one
/// (<c>$"odyssey_newsapi_{Guid.NewGuid():N}"</c>). Repeating a name within one file is fine: one class owns it. Like
/// <see cref="ModelSnapshotDriftTests"/>, it needs no Docker, so it is a plain <c>[Fact]</c> outside every collection.
/// </remarks>
public sealed class DatabaseNameUniquenessTests
{
    // A string literal (plain or interpolated) that starts with "odyssey_": the whole name, or the literal prefix of an
    // interpolated one up to its first hole.
    private static readonly Regex DatabaseName = new(
        "\\$?\"(?<name>odyssey_[A-Za-z0-9_]+)[\"{]", RegexOptions.CultureInvariant);

    // Not database names. The fixture's own credentials.
    private static readonly HashSet<string> NotDatabases = new(StringComparer.Ordinal) { "odyssey_password" };

    [Fact]
    public void No_two_source_files_name_the_same_database()
    {
        var owners = SourceFiles()
            .SelectMany(path => DatabaseName.Matches(File.ReadAllText(path))
                .Select(match => match.Groups["name"].Value)
                .Where(name => !NotDatabases.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .Select(name => (Name: name, File: Path.GetFileName(path))))
            .ToList();
        Assert.True(owners.Count > 50, $"The scan found only {owners.Count} database names; it is no longer looking at the tier.");

        var shared = owners
            .GroupBy(owner => owner.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(owner => owner.File))}")
            .ToList();
        Assert.True(shared.Count == 0,
            "These databases are named by more than one file. The tier's collections run in parallel against one server, " +
            "so give each class a name of its own:\n" + string.Join("\n", shared));
    }

    /// <summary>
    /// A class that keeps data in the shared <c>odyssey</c> or <c>odyssey_relational</c> database is in the shared
    /// collection, or the timing one, which runs alone. Only the two unmistakable spellings are checked — the relational
    /// connection string, and a photo store host over the shared one; connecting to <c>odyssey</c> to create or drop
    /// another database is not a use of it, and is everywhere.
    /// </summary>
    [Fact]
    public void Every_class_using_a_shared_database_is_in_the_shared_collection()
    {
        var users = SourceFiles()
            .Select(path => (File: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .Where(file => SharedDatabaseUse.IsMatch(file.Text))
            .ToList();
        Assert.NotEmpty(users);

        var misplaced = users
            .Where(file => !Regex.IsMatch(file.Text,
                $"\\[Collection\\((?:{nameof(MariaDbSharedDatabaseCollection)}|{nameof(MariaDbTimingCollection)})\\.Name\\)\\]"))
            .Select(file => file.File)
            .ToList();
        Assert.True(misplaced.Count == 0,
            $"These files use a shared database outside [Collection({nameof(MariaDbSharedDatabaseCollection)}.Name)], so they " +
            "run in parallel with the classes that share it: " + string.Join(", ", misplaced));
    }

    private static readonly Regex SharedDatabaseUse = new(
        @"fixture\.RelationalConnectionString|PhotoStoreHost\.CreateAsync\(\s*fixture\.OdysseyConnectionString",
        RegexOptions.CultureInvariant);

    // Every source file of this project but this one, whose own comments quote example names and spellings.
    private static IEnumerable<string> SourceFiles() =>
        SourceTree.Files("Odyssey.IntegrationTests")
            .Where(path => Path.GetFileName(path) != nameof(DatabaseNameUniquenessTests) + ".cs");
}
