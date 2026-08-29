using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lint guard against a specific, recurring Razor mistake: <c>Value="_field"</c> (no <c>@</c>)
/// on a <c>string</c>-typed component parameter binds the parameter to the literal text
/// <c>"_field"</c>, not the C# field — Razor only treats a bare attribute as a C# expression for
/// non-string parameter types (bool, enum, DateTime, …). For <c>string</c> parameters you need
/// <c>Value="@_field"</c> or <c>@bind-Value</c>. This has shipped at least four times (Insurance
/// policy type, Subscription billing interval, Contract type, Calendar view/export-scope segmented
/// controls) — always invisible in code review because the file still compiles and the "wrong" value
/// only shows up as a placeholder at runtime. No bUnit needed: this is a pure source-text check.
/// </summary>
public class RazorStringBindingTests
{
    // Matches a bare identifier field reference ("_type") or a dotted member-access expression on a
    // lowercase-started identifier ("ctx.File.IssuedBy") — the two shapes every real instance of this
    // bug has taken. Deliberately narrow: a plain prose literal ("Choose a type…", "Optional") never
    // matches either shape, so this can't flag a legitimate placeholder/label string.
    private static readonly Regex FieldLikeValue = new(
        @"^(_[A-Za-z]\w*|[a-z]\w*(\.[A-Za-z]\w*)+)$", RegexOptions.Compiled);

    // <ComponentName ...unquoted-attr-run... Param="value" ...> — attribute order within the tag is
    // unconstrained, so this only anchors the component's opening tag name and then finds each
    // Param="value" pair independently within the tag's full span (below), not adjacent to the name.
    private static readonly Regex OpeningTag = new(
        @"<([A-Z]\w*)((?:\s+[^<>]*?)?)(?:/?>)", RegexOptions.Compiled | RegexOptions.Singleline);

    // The negative lookbehind excludes both "@Value=" (already an expression) and "@bind-Value="
    // (the hyphen before "Value" would otherwise let the hyphenated @bind-X directive's tail match
    // as if it were its own independent attribute).
    private static readonly Regex BareAttribute = new(
        @"(?<![@\w-])([A-Za-z]\w*)=""([^""@][^""]*)""", RegexOptions.Compiled);

    [Fact]
    public void No_bare_field_reference_bound_to_a_string_typed_component_parameter()
    {
        var clientRoot = FindClientRoot();
        var stringParams = CollectStringParameters(Path.Combine(clientRoot, "Components"));

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(clientRoot, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match tag in OpeningTag.Matches(text))
            {
                var component = tag.Groups[1].Value;
                if (!stringParams.TryGetValue(component, out var paramNames))
                    continue;

                foreach (Match attr in BareAttribute.Matches(tag.Groups[2].Value))
                {
                    var paramName = attr.Groups[1].Value;
                    var value = attr.Groups[2].Value;
                    if (paramNames.Contains(paramName) && FieldLikeValue.IsMatch(value))
                    {
                        var line = text[..tag.Index].Count(c => c == '\n') + 1;
                        violations.Add($"{Path.GetRelativePath(clientRoot, file)}:{line} — " +
                                       $"<{component} {paramName}=\"{value}\"> needs @ (\"@{value}\") to bind the field instead of the literal text \"{value}\"");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Found string-parameter bindings missing '@' (binds the literal text, not the field):\n" +
            string.Join('\n', violations));
    }

    /// <summary>Component name → the names of its <c>string</c>/<c>string?</c>-typed [Parameter] properties.</summary>
    private static Dictionary<string, HashSet<string>> CollectStringParameters(string componentsDir)
    {
        var map = new Dictionary<string, HashSet<string>>();
        var paramRegex = new Regex(
            @"\[Parameter[^\]]*\]\s*public\s+string\??\s+(\w+)\s*\{\s*get;\s*set;\s*\}",
            RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(componentsDir, "*.razor", SearchOption.AllDirectories))
        {
            var component = Path.GetFileNameWithoutExtension(file);
            var text = File.ReadAllText(file);
            var names = new HashSet<string>();
            foreach (Match m in paramRegex.Matches(text))
                names.Add(m.Groups[1].Value);

            if (names.Count > 0)
                map[component] = names;
        }

        return map;
    }

    private static string FindClientRoot() => ClientSource.Root;
}
