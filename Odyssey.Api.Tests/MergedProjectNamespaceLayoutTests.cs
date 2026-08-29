using Odyssey.Context;
using Odyssey.Dtos;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Guards the layout of the three merged projects. The four former <c>Odyssey.&lt;Module&gt;.Dtos</c>
/// projects are now folders under <c>Odyssey.Dtos</c>; <c>Odyssey.Finance</c> /
/// <c>Odyssey.Journal</c> / <c>Odyssey.Shared</c> are now folders under <c>Odyssey.Core</c>; and
/// <c>Odyssey.Application.Context</c> is now folded into <c>Odyssey.Context</c>, whose
/// <c>Authorization/</c>, <c>Legal/</c> and <c>Secrets/</c> folders sit under that root. In all three,
/// a module namespace is *nested inside* the shared root rather than sitting beside it.
///
/// That nesting changes name resolution: C# searches the innermost enclosing namespace first, so a
/// type declared at the root is visible from every module file with no <c>using</c> at all. Where a
/// module declares its own type of the same name, the module copy silently wins by proximity — which
/// is correct for the one deliberate case below, but would hide an accidental duplicate that the
/// separate projects used to surface as a build error.
/// </summary>
public class MergedProjectNamespaceLayoutTests
{
    private const string DtosRoot = "Odyssey.Dtos";
    private const string CoreRoot = "Odyssey.Core";
    private const string ContextRoot = "Odyssey.Context";

    private static Assembly AssemblyFor(string root) => root switch
    {
        DtosRoot => typeof(Sex).Assembly,
        CoreRoot => typeof(Odyssey.Core.DomainException).Assembly,
        ContextRoot => typeof(OdysseyContext).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, "Unknown merged project root."),
    };

    private static readonly string[] Roots = [DtosRoot, CoreRoot, ContextRoot];

    public static TheoryData<string> MergedRoots => [.. Roots];

    /// <summary>
    /// Every type the shadowing rule can reach — <see cref="Assembly.GetTypes"/>, deliberately not
    /// <c>GetExportedTypes</c>. C# name resolution does not filter by accessibility, so an
    /// <c>internal</c> type shadows a root type exactly as a public one does; scanning only exported
    /// types guarded a narrower surface than the rule it enforces. Confirmed by mutation during review
    /// of the PR that added this file: an <c>internal DomainNotFoundException</c> added to
    /// <c>Odyssey.Core.Journal</c> silently rebound every unqualified <c>throw</c> in that module to a
    /// type not deriving from <see cref="DomainException"/> — turning 404s into 500s — while the build
    /// and this test both stayed green.
    ///
    /// Three kinds of type are filtered out, none of which the rule can speak about. A <b>nested</b> type
    /// is reached as <c>Outer.Inner</c>, so it cannot shadow a namespace-level name. A type whose name
    /// contains <c>&lt;</c> has an <b>unspeakable</b> name that no C# source can declare — closure
    /// classes, <c>&lt;PrivateImplementationDetails&gt;</c>, the synthesised <c>&lt;&gt;y__InlineArray</c>
    /// types, and the <c>[GeneratedRegex]</c> source generator's output all land here. And Roslyn's
    /// synthesised <c>EmbeddedAttribute</c>/<c>NullableAttribute</c> pair have speakable names but are
    /// marked <see cref="CompilerGeneratedAttribute"/>, so they are excluded by that instead.
    ///
    /// Note the filter is deliberately <em>not</em> "exclude generated code": a source generator that
    /// emitted a speakable type into a module namespace would shadow exactly like hand-written source,
    /// and this test should catch it.
    /// </summary>
    private static IEnumerable<Type> ShadowableTypes(Assembly assembly) =>
        assembly.GetTypes()
            .Where(type => !type.IsNested)
            .Where(type => !type.Name.Contains('<', StringComparison.Ordinal))
            .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false));

    /// <summary>
    /// Simple names deliberately declared both at a root and inside one of its module namespaces.
    /// Adding an entry here is a design decision, not a formality — state why the two must not merge.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedShadows = new(StringComparer.Ordinal)
    {
        // Issue #316 §6: the identity-side profile sex is deliberately separate from the contact sex,
        // with ordinals aligned (Male = 1, Female = 2) so the two never conflate when both persist as
        // int. Merging them would join a user's own profile to the contact record vocabulary.
        ["Sex"] = "Odyssey.Dtos.Application",
    };

    [Theory]
    [MemberData(nameof(MergedRoots))]
    public void EveryTypeLivesUnderItsProjectRoot(string root)
    {
        var assembly = AssemblyFor(root);

        var strays = ShadowableTypes(assembly)
            .Where(type => (type.Namespace ?? string.Empty) != root
                && !(type.Namespace ?? string.Empty).StartsWith(root + ".", StringComparison.Ordinal))
            .Select(type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            strays.Count == 0,
            $"Types in {assembly.GetName().Name} must be namespaced under {root} so the folder " +
            $"layout and the namespaces agree. Found: {string.Join(", ", strays)}");
    }

    [Theory]
    [MemberData(nameof(MergedRoots))]
    public void NoModuleTypeAccidentallyShadowsARootType(string root)
    {
        var assembly = AssemblyFor(root);

        var rootNames = ShadowableTypes(assembly)
            .Where(type => type.Namespace == root)
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var shadows = ShadowableTypes(assembly)
            .Where(type => type.Namespace?.StartsWith(root + ".", StringComparison.Ordinal) == true)
            .Where(type => rootNames.Contains(type.Name))
            .Where(type => !(AllowedShadows.TryGetValue(type.Name, out var ns) && ns == type.Namespace))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            shadows.Count == 0,
            $"These module types share a simple name with a type in {root}. Because the module " +
            "namespace is nested inside the root, unqualified uses inside that module bind to the " +
            "module copy silently rather than failing to compile. Either rename one, or add it to " +
            $"{nameof(AllowedShadows)} with the reason the two must stay distinct. Found: " +
            string.Join(", ", shadows));
    }

    [Fact]
    public void TheAllowedShadowsAreStillRealShadows()
    {
        // Keeps the allow-list from outliving the duplicate it excuses.
        foreach (var (name, moduleNamespace) in AllowedShadows)
        {
            var root = Roots.Single(candidate =>
                moduleNamespace.StartsWith(candidate + ".", StringComparison.Ordinal));
            var types = ShadowableTypes(AssemblyFor(root)).ToList();

            Assert.True(
                types.Any(t => t.Namespace == root && t.Name == name),
                $"{nameof(AllowedShadows)} still excuses '{name}', but no type by that name is " +
                $"declared in {root} any more. Remove the entry.");

            Assert.True(
                types.Any(t => t.Namespace == moduleNamespace && t.Name == name),
                $"{nameof(AllowedShadows)} still excuses '{name}', but no type by that name is " +
                $"declared in {moduleNamespace} any more. Remove the entry.");
        }
    }
}
