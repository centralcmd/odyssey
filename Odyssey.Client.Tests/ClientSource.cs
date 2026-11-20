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

    /// <summary>Every <c>.razor</c> and <c>.razor.cs</c> file under the given client-relative directories.</summary>
    public static IEnumerable<string> RazorFilesIn(params string[] relativeDirs) =>
        relativeDirs.SelectMany(dir => SourceTree.Files(Path.Combine(Root, dir), "*.razor", "*.razor.cs"));

    /// <summary>
    /// Every checked-in C# and Razor source file in the client, build output excluded. For the lints
    /// that have to prove something is declared in exactly ONE place, which a <c>.razor</c>-only sweep
    /// can't do — the declaration usually lives in a plain <c>.cs</c>.
    /// </summary>
    public static IEnumerable<string> SourceFiles() => SourceTree.Files(Root, "*.cs", "*.razor");

    /// <summary>Path of <paramref name="file"/> relative to the client root, for readable failures.</summary>
    public static string Relative(string file) => Path.GetRelativePath(Root, file);
}
