using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Worker.Tests.Thumbnails;

/// <summary>
/// Source lints for issue #362 §6: native ImageMagick loads in the worker alone, and the thumbnail code never logs an
/// exception message (one can carry a file name or a path).
/// </summary>
public sealed partial class ThumbnailSourceLintTests
{
    private static readonly string Root = RepositoryRoot();

    [Fact]
    public void ImageMagick_is_referenced_only_by_the_worker_thumbnails_folder()
    {
        var allowed = Path.Combine(Root, "Odyssey.Worker", "Thumbnails") + Path.DirectorySeparatorChar;
        var offenders = SourceFiles("*.cs")
            .Where(file => !file.StartsWith(allowed, StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Odyssey.Worker.Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("using ImageMagick", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);

        var projects = SourceFiles("*.csproj")
            .Where(file => File.ReadAllText(file).Contains("Magick.NET", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["Odyssey.Worker.csproj"], projects);
    }

    [Fact]
    public void Thumbnail_code_never_reads_an_exception_message()
    {
        var folders = new[]
        {
            Path.Combine(Root, "Odyssey.Worker", "Thumbnails"),
            Path.Combine(Root, "Odyssey.Core", "Journal", "Thumbnails"),
        };
        var files = folders.SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)).ToList();
        Assert.NotEmpty(files);

        var offenders = files.Where(file => MessageRead().IsMatch(File.ReadAllText(file))).ToList();

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(Root, pattern, SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    // Any identifier: an exception may be bound under any name (`ex`, `failure`, `inner`, ...).
    [GeneratedRegex(@"\b\w+\.Message\b|\.InnerException\b")]
    private static partial Regex MessageRead();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Odyssey.sln not found above the test output.");
    }
}
