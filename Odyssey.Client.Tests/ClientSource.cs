namespace Odyssey.Client.Tests;

/// <summary>
/// Locates the <c>Odyssey.Client</c> source tree so the source-lints
/// (<see cref="RazorStringBindingTests"/>, <see cref="ListPageContractTests"/>,
/// <see cref="SourceConventionTests"/>) can scan the real <c>.razor</c> files rather than the
/// compiled output. The lints read text, not IL, so they need the checked-in sources — resolved by
/// walking up from the test binary until the solution root is in sight.
/// </summary>
internal static class ClientSource
{
    private static readonly Lazy<string> RootPath = new(Locate);

    /// <summary>Absolute path of the <c>Odyssey.Client</c> project directory.</summary>
    public static string Root => RootPath.Value;

    /// <summary>Every <c>.razor</c> file in the client, markup only.</summary>
    public static IEnumerable<string> RazorFiles() =>
        Directory.EnumerateFiles(Root, "*.razor", SearchOption.AllDirectories);

    /// <summary>Every <c>.razor</c> and <c>.razor.cs</c> file under the given client-relative directories.</summary>
    public static IEnumerable<string> RazorFilesIn(params string[] relativeDirs) =>
        relativeDirs
            .Select(dir => Path.Combine(Root, dir))
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.razor", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(dir, "*.razor.cs", SearchOption.AllDirectories)));

    /// <summary>
    /// Every checked-in C# and Razor source file in the client, build output excluded. For the lints
    /// that have to prove something is declared in exactly ONE place, which a <c>.razor</c>-only sweep
    /// can't do — the declaration usually lives in a plain <c>.cs</c>.
    /// </summary>
    public static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Root, "*.razor", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file));

    /// <summary>
    /// Resolves a path relative to the solution root — for the rare lint whose subject is a contract
    /// with another project (a client constant that must track a server one), where reading only the
    /// client half would let the two drift apart with the test still green.
    /// </summary>
    public static string Sibling(string relativePath) =>
        Path.Combine(Path.GetDirectoryName(Root)!, relativePath);

    /// <summary>Path of <paramref name="file"/> relative to the client root, for readable failures.</summary>
    public static string Relative(string file) => Path.GetRelativePath(Root, file);

    private static bool IsBuildOutput(string file)
    {
        var relative = Path.GetRelativePath(Root, file);
        return relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>The 1-based line number containing <paramref name="index"/> in <paramref name="text"/>.</summary>
    public static int LineAt(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    private static string Locate()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "Odyssey.Client");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(dir, "Odyssey.sln")))
                return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new InvalidOperationException(
            "Could not locate the Odyssey.Client source directory from " + AppContext.BaseDirectory);
    }
}
