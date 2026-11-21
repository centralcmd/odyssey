using Odyssey.Testing;

namespace Odyssey.Client.Tests;

/// <summary>
/// The <c>Odyssey.Client</c> source tree, so the source-lints (<see cref="RazorStringBindingTests"/>,
/// <see cref="ListPageContractTests"/>, <see cref="SourceConventionTests"/>) can scan the real <c>.razor</c> files
/// rather than the compiled output. A client-scoped view of <see cref="SourceTree"/>, which owns the repository-root
/// lookup and the build-output exclusion; comment stripping is <see cref="SourceLint"/>.
/// </summary>
internal static class ClientSource
{
    /// <summary>Absolute path of the <c>Odyssey.Client</c> project directory.</summary>
    public static string Root { get; } = SourceTree.File("Odyssey.Client");

    /// <summary>Every <c>.razor</c> file in the client, markup only.</summary>
    public static IEnumerable<string> RazorFiles() => SourceTree.Files(Root, "*.razor");

    /// <summary>
    /// Every <c>.razor</c> and <c>.razor.cs</c> file under the given client-relative directories, plus each
    /// component's further <c>partial</c> code-behind files (<c>Settings.Catalogue.cs</c> beside
    /// <c>Settings.razor</c>), so splitting a code-behind never moves code out of a lint's scope.
    /// </summary>
    public static IEnumerable<string> RazorFilesIn(params string[] relativeDirs) =>
        relativeDirs
            .Select(dir => Path.Combine(Root, dir))
            .SelectMany(dir => SourceTree.Files(dir, "*.razor", "*.razor.cs")
                .Concat(SourceTree.Files(dir, "*.razor").SelectMany(PartialFilesOf)));

    /// <summary>
    /// A component's whole code-behind: <paramref name="codeBehindPath"/> (a client-relative <c>.razor.cs</c>
    /// path) followed by every sibling <c>partial</c> file of the same component (<c>&lt;Name&gt;.*.cs</c>),
    /// in ordinal name order. For the lints that read one component's code — a member moved into a partial
    /// file stays in their scope.
    /// </summary>
    public static string CodeBehind(string codeBehindPath)
    {
        var path = Path.Combine(Root, codeBehindPath.Replace('/', Path.DirectorySeparatorChar));
        if (!path.EndsWith(".razor.cs", StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected a .razor.cs path.", nameof(codeBehindPath));
        }

        var razor = path[..^".cs".Length];
        return string.Join("\n", new[] { path }.Concat(PartialFilesOf(razor)).Select(File.ReadAllText));
    }

    /// <summary>
    /// The <c>&lt;Name&gt;.*.cs</c> partial files beside <paramref name="razorPath"/>, its <c>.razor.cs</c>
    /// excluded, in ordinal name order.
    /// </summary>
    public static IEnumerable<string> PartialFilesOf(string razorPath)
    {
        var dir = Path.GetDirectoryName(razorPath)!;
        var name = Path.GetFileNameWithoutExtension(razorPath);
        return Directory.EnumerateFiles(dir, name + ".*.cs", SearchOption.TopDirectoryOnly)
            .Where(file => !file.EndsWith(".razor.cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// Every checked-in C# and Razor source file in the client, build output excluded. For the lints
    /// that have to prove something is declared in exactly ONE place, which a <c>.razor</c>-only sweep
    /// can't do — the declaration usually lives in a plain <c>.cs</c>.
    /// </summary>
    public static IEnumerable<string> SourceFiles() => SourceTree.Files(Root, "*.cs", "*.razor");

    /// <summary>Path of <paramref name="file"/> relative to the client root, for readable failures.</summary>
    public static string Relative(string file) => Path.GetRelativePath(Root, file);
}
