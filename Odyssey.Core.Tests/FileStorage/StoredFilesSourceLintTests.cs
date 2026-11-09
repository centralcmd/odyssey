using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>
/// Source-lints for the file move (issue #448 §8.7). Bytes still in the database are read in a closed set of places, a
/// <c>FileMetadata</c> row is removed only by the helper that also releases its stored object, only the avatar path
/// writes the <c>ContactAvatars</c> feature, and Finance code takes no new dependency on Journal.
/// Comments are stripped before matching, so a doc comment naming <c>FileBlob</c> is not a read.
/// </summary>
public partial class StoredFilesSourceLintTests
{
    private static readonly string[] ProductionProjects = ["Odyssey.Core", "Odyssey.Api", "Odyssey.Worker"];

    /// <summary>
    /// The places that may still read <c>FileBlob</c> or <c>FileMetadata.FileBlobId</c>. The three photo files leave the
    /// list with the photo move's own follow-up; the file move's last release drops the table and the list together.
    /// </summary>
    private static readonly HashSet<string> LegacyReadAllowList =
    [
        "Odyssey.Core/FileStorage/Files/StoredFileContentSource.cs",
        "Odyssey.Core/FileStorage/Files/StoredFileRemoval.cs",
        "Odyssey.Core/FileStorage/Files/FileBlobBackfillJob.cs",
        "Odyssey.Core/FileStorage/Files/FileBackfillStatusService.cs",
        "Odyssey.Core/FileExport/AdminFileExportService.cs",
        "Odyssey.Core/Journal/Originals/PhotoOriginalBackfillJob.cs",
        "Odyssey.Core/Journal/Originals/PhotoOriginalSource.cs",
    ];

    private static readonly HashSet<string> MetadataRemovalAllowList =
    [
        "Odyssey.Core/FileStorage/Files/StoredFileRemoval.cs",
        "Odyssey.Core/Journal/Originals/PhotoOriginalBackfillJob.cs",
    ];

    /// <summary>Finance files that already imported Journal before issue #448; the list may only shrink.</summary>
    private static readonly HashSet<string> FinanceToJournalAllowList =
    [
        "Odyssey.Core/Finance/BudgetItemService.cs",
        "Odyssey.Core/Finance/TransactionTagService.cs",
        "Odyssey.Core/Finance/ExchangeRateSync/ExchangeRateSyncService.cs",
    ];

    [Fact]
    public void Bytes_in_the_database_are_read_only_by_the_allow_listed_files()
    {
        var readers = ProductionFiles().Where(f => LegacyRead().IsMatch(f.Code)).Select(f => f.Path).ToList();

        Assert.NotEmpty(readers);
        Assert.All(readers, path => Assert.True(LegacyReadAllowList.Contains(path), $"{path} reads FileBlob outside the content source."));
        Assert.All(LegacyReadAllowList, path => Assert.Contains(path, readers));
    }

    [Fact]
    public void A_file_metadata_row_is_removed_only_by_the_helper_that_releases_its_object()
    {
        var removers = ProductionFiles().Where(f => MetadataRemoval().IsMatch(f.Code)).Select(f => f.Path).ToList();

        Assert.NotEmpty(removers);
        Assert.All(removers, path => Assert.True(MetadataRemovalAllowList.Contains(path), $"{path} removes FileMetadata without StoredFileRemoval."));
    }

    [Fact]
    public void Only_the_avatar_path_writes_the_contact_avatar_feature()
    {
        var writers = ProductionFiles()
            .Where(f => AvatarWrite().IsMatch(f.Code))
            .Select(f => f.Path)
            .ToList();

        Assert.Equal(["Odyssey.Core/Journal/Avatar/ContactAvatarService.cs"], writers);
    }

    [Fact]
    public void Finance_takes_no_new_dependency_on_journal()
    {
        var root = SolutionRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "Odyssey.Core", "Finance"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Select(path => (Path: Relative(root, path), Code: StripComments(File.ReadAllText(path))))
            .ToList();

        Assert.NotEmpty(files);
        var offenders = files.Where(f => JournalReference().IsMatch(f.Code)).Select(f => f.Path).ToList();
        Assert.All(offenders, path => Assert.True(FinanceToJournalAllowList.Contains(path), $"{path} references Odyssey.Core.Journal."));
    }

    [GeneratedRegex(@"\.FileBlob\b|\bFileBlobId\b")]
    private static partial Regex LegacyRead();

    [GeneratedRegex(@"FileMetadata\s*\.\s*Remove(Range)?\s*\(")]
    private static partial Regex MetadataRemoval();

    [GeneratedRegex(@"StageNewFileAsync\s*\([^;]*StoredFileArea\.ContactAvatars", RegexOptions.Singleline)]
    private static partial Regex AvatarWrite();

    [GeneratedRegex(@"\bOdyssey\.Core\.Journal\b|(?<![\w.])Journal\.[A-Z]")]
    private static partial Regex JournalReference();

    private static IEnumerable<(string Path, string Code)> ProductionFiles()
    {
        var root = SolutionRoot();
        return ProductionProjects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !IsBuildOutput(path) && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Relative(root, path), StripComments(File.ReadAllText(path))));
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string StripComments(string code) =>
        Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"//.*?$", string.Empty, RegexOptions.Multiline);

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the solution root from the test binary.");
    }
}
