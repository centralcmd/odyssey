using System.Text.RegularExpressions;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.Worker.Tests.Thumbnails;

/// <summary>
/// Source lints for issue #362 §6: native ImageMagick loads in the worker alone, and the thumbnail code never logs an
/// exception message (one can carry a file name or a path).
/// </summary>
public sealed partial class ThumbnailSourceLintTests
{
    [Fact]
    public void ImageMagick_is_referenced_only_by_the_worker_thumbnails_folder()
    {
        var allowed = SourceTree.File("Odyssey.Worker", "Thumbnails") + Path.DirectorySeparatorChar;
        var offenders = SourceTree.Files(SourceTree.Root, "*.cs")
            .Where(file => !file.StartsWith(allowed, StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Odyssey.Worker.Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("using ImageMagick", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);

        var projects = SourceTree.Files(SourceTree.Root, "*.csproj")
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
            SourceTree.File("Odyssey.Worker", "Thumbnails"),
            SourceTree.File("Odyssey.Core", "Journal", "Thumbnails"),
        };
        var files = folders.SelectMany(folder => SourceTree.Files(folder)).ToList();
        Assert.NotEmpty(files);

        var offenders = files.Where(file => MessageRead().IsMatch(File.ReadAllText(file))).ToList();

        Assert.Empty(offenders);
    }

    // Any identifier: an exception may be bound under any name (`ex`, `failure`, `inner`, ...).
    [GeneratedRegex(@"\b\w+\.Message\b|\.InnerException\b")]
    private static partial Regex MessageRead();
}
