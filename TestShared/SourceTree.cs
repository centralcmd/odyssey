// Linked (not referenced) into every test project that reads checked-in files, so the source-lints and the
// configuration-parity tests share one repository-root lookup and one file enumeration instead of a hand-written
// walk per file.
// Every type here is internal: each test assembly compiles its own copy.

namespace Odyssey.Testing;

/// <summary>
/// The checked-in repository, located by walking up from the test binary until <c>Odyssey.sln</c> is in sight.
/// Source-lints read text, not IL, and some contracts live in files no assembly compiles (compose files, the
/// deployment guide, nginx templates), so the only way to keep them in step with the code is to read them.
/// </summary>
internal static class SourceTree
{
    private const string SolutionFileName = "Odyssey.sln";

    private static readonly Lazy<string> RootPath = new(Locate);

    /// <summary>Absolute path of the repository root (the directory holding <c>Odyssey.sln</c>).</summary>
    public static string Root => RootPath.Value;

    /// <summary>Absolute path of <paramref name="parts"/> joined under the repository root. Works for directories too.</summary>
    public static string File(params string[] parts) =>
        Path.Combine([Root, .. parts]);

    /// <summary>The text of a checked-in file, by its repository-relative path parts.</summary>
    public static string ReadAllText(params string[] parts) =>
        System.IO.File.ReadAllText(File(parts));

    /// <summary>The lines of a checked-in file, by its repository-relative path parts.</summary>
    public static string[] ReadAllLines(params string[] parts) =>
        System.IO.File.ReadAllLines(File(parts));

    /// <summary>
    /// Every file under <paramref name="directory"/> (repository-relative, or absolute) matching any of
    /// <paramref name="patterns"/> (default <c>*.cs</c>), recursively, in a stable order. Directories that are not
    /// source are pruned, never descended into: <c>bin/</c> and <c>obj/</c> build output (a generated file there
    /// would make a lint flaky between builds), <c>.git/</c>, <c>node_modules/</c>, and <c>.claude/worktrees/</c> —
    /// other agents' full checkouts of this repository, whose copies would make every "exactly one implementation"
    /// lint fail in the main checkout.
    /// </summary>
    public static IEnumerable<string> Files(string directory, params string[] patterns)
    {
        var start = Path.IsPathRooted(directory) ? directory : File(directory);
        string[] globs = patterns.Length == 0 ? ["*.cs"] : patterns;
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in globs
                .SelectMany(glob => Directory.EnumerateFiles(current, glob, SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(current).OrderDescending(StringComparer.Ordinal))
            {
                if (!IsExcludedDirectory(child))
                {
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>Path of <paramref name="path"/> relative to the repository root, for readable failures.</summary>
    public static string Relative(string path) => Path.GetRelativePath(Root, path);

    /// <summary>
    /// True for a path inside a directory <see cref="Files"/> prunes — build output, <c>.git</c>,
    /// <c>node_modules</c> or <c>.claude/worktrees</c> — below the repository root.
    /// </summary>
    public static bool IsExcluded(string path)
    {
        var segments = Segments(path);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (IsExcludedSegment(segments, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExcludedDirectory(string directory)
    {
        var segments = Segments(directory);
        return IsExcludedSegment(segments, segments.Length - 1);
    }

    private static string[] Segments(string path) =>
        Path.GetRelativePath(Root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsExcludedSegment(string[] segments, int index) =>
        segments[index] is "bin" or "obj" or ".git" or "node_modules"
        || (segments[index] == "worktrees" && index > 0 && segments[index - 1] == ".claude");

    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root ({SolutionFileName}) above {AppContext.BaseDirectory}.");
    }
}
