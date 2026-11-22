namespace Odyssey.MigrationService.Tests;

/// <summary>Locates files in the repository checkout, for the source-lints and the committed fingerprint.</summary>
internal static class RepositoryFiles
{
    public static string Root
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir, "Odyssey.sln")))
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }

            throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
        }
    }

    /// <summary>Every C# source file under <paramref name="project"/>, build output excluded.</summary>
    public static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(Root, project), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
