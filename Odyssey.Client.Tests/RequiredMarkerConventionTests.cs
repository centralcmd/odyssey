using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints for the design system's single obligation marker (Odyssey Design System · "Forms mark
/// required only"): a required field carries a <c>*</c> after its label, and nothing is ever labelled
/// "Optional" — the absence of the asterisk IS optional, and the dialog explains the marker once with
/// its footer legend.
/// </summary>
/// <remarks>
/// The <c>Optional</c> parameter was removed from every <c>Ods*</c> field rather than kept as a no-op,
/// which is why the first lint matters more than it looks: most of those components capture unmatched
/// attributes, so a leftover <c>Optional="true"</c> still compiles — and then either splats a stray
/// attribute onto the DOM or throws when the component is first rendered.
/// </remarks>
public class RequiredMarkerConventionTests
{
    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LineComment = new(@"^\s*//.*$", RegexOptions.Multiline | RegexOptions.Compiled);

    // `Optional="…"`, or the bare boolean form followed by the next attribute / the tag end.
    private static readonly Regex OptionalAttribute =
        new(@"(?<=\s)Optional(?:\s*=\s*""|\s*/?>|\s+[A-Z@][\w-]*\s*=)", RegexOptions.Compiled);

    private static readonly Regex OptionalWording =
        new(@"\b(Placeholder|Help|HelperText|Meta)\s*=\s*""[^""]*\boptional\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RetiredMarkerClass = new(@"\b(odc-field-opt|atm-opt|trm-opt)\b", RegexOptions.Compiled);

    [Fact]
    public void No_component_is_passed_an_Optional_marker()
    {
        var violations = Scan(ClientSource.RazorFiles(), OptionalAttribute, match =>
            "an Optional marker — the parameter is gone; mark the required fields instead");

        Assert.True(violations.Count == 0,
            "Fields marked Optional (the system marks required only):\n" + string.Join('\n', violations));
    }

    [Fact]
    public void No_placeholder_helper_or_divider_meta_says_optional()
    {
        var violations = Scan(ClientSource.RazorFiles(), OptionalWording, match =>
            $"{match.Groups[1].Value} says \"optional\" — show an example or what empty means instead");

        Assert.True(violations.Count == 0,
            "\"Optional\" wording in a form (placeholders show an example, helpers state consequence):\n" +
            string.Join('\n', violations));
    }

    [Fact]
    public void The_retired_optional_marker_classes_are_not_used_or_styled()
    {
        var files = ClientSource.SourceFiles()
            .Concat(Directory.EnumerateFiles(Path.Combine(ClientSource.Root, "wwwroot", "css"), "*.css"))
            .Concat(Directory.EnumerateFiles(ClientSource.Root, "*.razor.css", SearchOption.AllDirectories));

        var violations = Scan(files, RetiredMarkerClass, match => $".{match.Value} is retired");

        Assert.True(violations.Count == 0,
            "Retired \"Optional\" label markers still referenced:\n" + string.Join('\n', violations));
    }

    private static List<string> Scan(IEnumerable<string> files, Regex pattern, Func<Match, string> describe)
    {
        var violations = new List<string>();
        foreach (var file in files.Distinct())
        {
            var raw = File.ReadAllText(file);
            // Blank comments out rather than removing them, so reported line numbers stay true.
            var text = LineComment.Replace(RazorComment.Replace(raw, Blank), Blank);
            foreach (Match match in pattern.Matches(text))
                violations.Add($"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, match.Index)} — {describe(match)}");
        }
        return violations;
    }

    private static string Blank(Match match) => Regex.Replace(match.Value, @"[^\n]", " ");
}
