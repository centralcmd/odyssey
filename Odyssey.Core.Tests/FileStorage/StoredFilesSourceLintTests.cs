using System.Text.RegularExpressions;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>
/// Source-lints for the Files store on the object store (issue #448 §8.7). Nothing names the removed database copy of
/// the bytes, a <c>FileMetadata</c> row is removed only by the helper that also releases its stored object, only the
/// avatar path writes the <c>ContactAvatars</c> feature, and Finance code takes no new dependency on Journal.
/// Comments are stripped before matching, so a doc comment naming <c>FileBlob</c> is not a read.
/// </summary>
public partial class StoredFilesSourceLintTests
{
    private static readonly string[] ProductionProjects = ["Odyssey.Core", "Odyssey.Api", "Odyssey.Worker"];

    private static readonly HashSet<string> MetadataRemovalAllowList =
    [
        "Odyssey.Core/FileStorage/Files/StoredFileRemoval.cs",
    ];

    /// <summary>Finance files that already imported Journal before issue #448; the list may only shrink.</summary>
    private static readonly HashSet<string> FinanceToJournalAllowList =
    [
        "Odyssey.Core/Finance/BudgetItemService.cs",
        "Odyssey.Core/Finance/TransactionTagService.cs",
        "Odyssey.Core/Finance/ExchangeRateSync/ExchangeRateSyncService.cs",
    ];

    /// <summary>
    /// The database copy of the bytes (<c>FileBlob</c>, <c>FileMetadata.FileBlobId</c>) was dropped by
    /// <c>RemoveLegacyFileBlobStorage</c>; its allow-list went with it, and no production file may name it again.
    /// </summary>
    [Fact]
    public void Nothing_names_the_removed_database_copy_of_the_bytes()
    {
        Assert.NotEmpty(ProductionFiles());
        var readers = ProductionFiles().Where(f => LegacyRead().IsMatch(f.Code)).Select(f => f.Path).ToList();

        Assert.Empty(readers);
    }

    [Fact]
    public void A_file_metadata_row_is_removed_only_by_the_helper_that_releases_its_object()
    {
        var removers = ProductionFiles().Where(f => MetadataRemoval().IsMatch(f.Code)).Select(f => f.Path).ToList();

        Assert.NotEmpty(removers);
        Assert.All(removers, path => Assert.True(MetadataRemovalAllowList.Contains(path), $"{path} removes FileMetadata without StoredFileRemoval."));
        // An exemption may not outlive the removal it excuses.
        Assert.All(MetadataRemovalAllowList, path => Assert.Contains(path, removers));
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
        var root = SourceTree.Root;
        var files = SourceTree.Files(Path.Combine("Odyssey.Core", "Finance"))
            .Select(path => (Path: Relative(root, path), Code: SourceLint.StripComments(File.ReadAllText(path))))
            .ToList();

        Assert.NotEmpty(files);
        var offenders = files.Where(f => JournalReference().IsMatch(f.Code)).Select(f => f.Path).ToList();
        Assert.All(offenders, path => Assert.True(FinanceToJournalAllowList.Contains(path), $"{path} references Odyssey.Core.Journal."));
        Assert.All(FinanceToJournalAllowList, path => Assert.Contains(path, offenders));
    }

    /// <summary>Each pattern matches the shapes it exists to catch and skips its look-alikes, so a lint cannot pass by never matching.</summary>
    [Theory]
    [InlineData("LegacyRead", "var b = context.FileBlob.First();", true)]
    [InlineData("LegacyRead", "if (file.FileBlobId is { } id) {}", true)]
    [InlineData("LegacyRead", "var x = context.FileBlobBackfillLeases;", false)]
    [InlineData("MetadataRemoval", "context.FileMetadata.Remove(file);", true)]
    [InlineData("MetadataRemoval", "context.FileMetadata.RemoveRange(files);", true)]
    [InlineData("MetadataRemoval", "context.Set<FileMetadata>().Remove(file);", true)]
    [InlineData("MetadataRemoval", "await context.FileMetadata.Where(f => f.Id == id).ExecuteDeleteAsync();", true)]
    [InlineData("MetadataRemoval", "context.Contacts.Remove(contact);", false)]
    [InlineData("AvatarWrite", "await writer.StageNewFileAsync(\n context, StoredFileArea.ContactAvatars, s);", true)]
    [InlineData("AvatarWrite", "await writer.StageNewFileAsync(context, StoredFileArea.Finance, s);", false)]
    [InlineData("JournalReference", "using Odyssey.Core.Journal;", true)]
    [InlineData("JournalReference", "var x = new Journal.ContactService();", true)]
    [InlineData("JournalReference", "using Odyssey.Dtos.Journal;", false)]
    public void Each_pattern_catches_what_it_guards(string pattern, string code, bool matches)
    {
        var regex = pattern switch
        {
            "LegacyRead" => LegacyRead(),
            "MetadataRemoval" => MetadataRemoval(),
            "AvatarWrite" => AvatarWrite(),
            _ => JournalReference(),
        };

        Assert.Equal(matches, regex.IsMatch(code));
    }

    [GeneratedRegex(@"\.FileBlob\b|\bFileBlobId\b")]
    private static partial Regex LegacyRead();

    [GeneratedRegex(@"FileMetadata\s*(>\s*\(\s*\))?\s*\.\s*Remove(Range)?\s*\(|FileMetadata\b[^;]*\.\s*ExecuteDelete(Async)?\s*\(")]
    private static partial Regex MetadataRemoval();

    [GeneratedRegex(@"StageNewFileAsync\s*\([^;]*StoredFileArea\.ContactAvatars", RegexOptions.Singleline)]
    private static partial Regex AvatarWrite();

    [GeneratedRegex(@"\bOdyssey\.Core\.Journal\b|(?<![\w.])Journal\.[A-Z]")]
    private static partial Regex JournalReference();

    private static IEnumerable<(string Path, string Code)> ProductionFiles()
    {
        var root = SourceTree.Root;
        return ProductionProjects
            .SelectMany(project => SourceTree.Files(project))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Relative(root, path), SourceLint.StripComments(File.ReadAllText(path))));
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
