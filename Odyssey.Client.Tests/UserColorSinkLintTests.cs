using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Keeps every tag and calendar colour on the one validated path to a style (issues #392 §7.5, #394 §8):
/// the <c>UserColor</c> helper and the typed <c>UserColor</c> / <c>UserColorDot</c> parameters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope: tag and calendar code only</b> (#394 [PO-2]). A file is scanned when it is under
/// <c>Pages/Calendar/</c>, is one of the four tag pages, names a tag or calendar type or component, or is
/// <c>OdsAvatar</c> / <c>OdsChip</c>. Charts, registries and other code-constant colours are never scanned.
/// </para>
/// <para>
/// <b>Mechanism:</b> a match by NAME — a member access <c>.Color</c> / <c>.DisplayColor</c> or a
/// <c>ColorOf(</c> call — inside a <c>style</c>, <c>Style</c>, <c>Bg</c> or <c>Fg</c> attribute value or an
/// interpolated string, unless the same expression goes through <c>UserColor.</c>. Within the scoped files
/// any such access counts as user data, so no type resolution is needed. Dataflow is followed one
/// assignment hop: a local assigned from a matched expression is itself user data. Longer chains are out
/// of scope.
/// </para>
/// </remarks>
public class UserColorSinkLintTests
{
    private static readonly string[] ScopeNames =
    [
        "ExistingTransactionTag", "ExistingJournalTag", "ExistingJournalTaskTag", "ExistingPhotoTag",
        "ExistingCalendar", "JournalTagChips", "OdsTagChips", "OdsTagAdmin", "OdsTagColorPicker",
        "OdsCalendarSwatches", "UserColor",
    ];

    private static readonly string[] TagPages =
    [
        "TransactionTagsPage.razor", "JournalTagsPage.razor", "TaskTagsPage.razor", "PhotoTagsPage.razor",
    ];

    /// <summary>The files whose every <c>style</c> expression must come from <c>UserColor</c>.</summary>
    private static readonly string[] StrictStyleFiles =
    [
        "OdsTagColorPicker.razor", "ManageCalendarsDialog.razor",
    ];

    [Fact]
    public void No_tag_or_calendar_colour_reaches_a_style_except_through_UserColor()
    {
        var files = ScopedFiles().ToList();
        Assert.True(files.Count >= 20, $"Scan set unexpectedly small ({files.Count}).");
        Assert.Contains(files, path => path.EndsWith("OdsAvatar.razor", StringComparison.Ordinal));
        Assert.Contains(files, path => path.EndsWith("ManageCalendarsDialog.razor", StringComparison.Ordinal));

        var violations = files
            .SelectMany(path => Violations(File.ReadAllText(path)).Select(v => $"{Path.GetRelativePath(ClientSource.Root, path)}: {v}"))
            .ToList();

        Assert.True(violations.Count == 0, "A user colour reaches a style without UserColor:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void Chart_and_registry_files_are_out_of_scope() =>
        Assert.DoesNotContain(ScopedFiles(), path =>
            path.Contains("Chart", StringComparison.Ordinal) || path.EndsWith("OdsTypeRegistries.cs", StringComparison.Ordinal));

    [Theory]
    [InlineData("<span style=\"@($\"background:{tag.Color}\")\"></span>")]
    [InlineData("<OdsAvatar Bg=\"@t.Color\" />")]
    [InlineData("<OdsChip Fg=\"@ColorOf(t)\" />")]
    [InlineData("private string S => $\"--x:{row.DisplayColor}\";")]
    [InlineData("var c = tag.Color;\n<span style=\"@($\"color:{c}\")\"></span>")]
    [InlineData("new OdsOption(id, name) { Icon = \"circle\", IconColor = c.Color }")]
    public void Each_seeded_sink_shape_is_caught(string code) =>
        Assert.NotEmpty(Violations(code));

    [Theory]
    [InlineData("<span style=\"@UserColor.Background(calendar.Color, x)\"></span>")]
    [InlineData("<OdsAvatar UserColor=\"@ColorOf(t)\" />")]
    [InlineData("<OdsChip UserColorDot=\"@tag.Color\" />")]
    [InlineData("private string? D => UserColor.Dot(tag.Color);")]
    [InlineData("new OdsOption(id, name) { Icon = \"circle\", IconColor = UserColor.Canonical(c.Color) }")]
    public void The_validated_path_is_not_flagged(string code) =>
        Assert.Empty(Violations(code));

    [Fact]
    public void A_raw_chart_colour_is_not_reported_because_charts_are_never_scanned() =>
        Assert.DoesNotContain(ScopedFiles(), path => path.Contains($"{Path.DirectorySeparatorChar}Charts{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// In the picker, the swatch chooser and the calendar dialog, every <c>style</c> expression is a
    /// <c>UserColor</c> call or a member whose body is one.
    /// </summary>
    [Fact]
    public void Every_style_in_the_strict_files_comes_from_UserColor()
    {
        var problems = new List<string>();
        foreach (var name in StrictStyleFiles)
        {
            var path = Directory.EnumerateFiles(ClientSource.Root, name, SearchOption.AllDirectories).Single(p => !IsBuildOutput(p));
            var markup = File.ReadAllText(path);
            // Definitions live in the code-behind or the @code block, never in the markup's attributes.
            var codeBlock = markup.IndexOf("@code", StringComparison.Ordinal) is var at and >= 0 ? markup[at..] : string.Empty;
            var code = codeBlock + (File.Exists(path + ".cs") ? File.ReadAllText(path + ".cs") : string.Empty);
            foreach (Match match in Regex.Matches(markup, "\\bstyle=\"@([^\"]+)\""))
            {
                var expression = match.Groups[1].Value;
                if (expression.Contains("UserColor.", StringComparison.Ordinal))
                    continue;

                var member = Regex.Match(expression, @"^\(?([A-Za-z_]\w*)").Groups[1].Value;
                var definition = Regex.Match(code, $@"\b(?:private|public|internal|protected)\b[^;=(]*\b{Regex.Escape(member)}\s*(?:\([^)]*\))?\s*=>[^;]*;", RegexOptions.Singleline);
                var bindsFromPattern = Regex.IsMatch(markup, $@"is\s*\{{\s*\}}\s*{Regex.Escape(member)}\b")
                    && Regex.IsMatch(markup, $@"(UserColor\.\w+|CaptionDot|CellStyle)\b[^\n]*\bis\s*\{{\s*\}}\s*{Regex.Escape(member)}\b");
                if (!(definition.Success && definition.Value.Contains("UserColor.", StringComparison.Ordinal)) && !bindsFromPattern)
                    problems.Add($"{name}: style=\"@{expression}\"");
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    internal static IEnumerable<string> Violations(string code)
    {
        var tokens = new List<string> { @"\.Color\b", @"\.DisplayColor\b", @"\bColorOf\s*\(" };

        // One assignment hop: a local assigned from a user-colour expression (not through UserColor).
        foreach (Match local in Regex.Matches(code, @"\bvar\s+([A-Za-z_]\w*)\s*=\s*([^;]+);"))
        {
            var value = local.Groups[2].Value;
            if (tokens.Any(t => Regex.IsMatch(value, t)) && !value.Contains("UserColor.", StringComparison.Ordinal))
                tokens.Add($@"\b{Regex.Escape(local.Groups[1].Value)}\b");
        }

        // OdsGlyphInk paints code-constant registry colours (issue #164) — never a tag or calendar colour.
        bool IsUserData(string text) => tokens.Any(t => Regex.IsMatch(text, t))
            && !text.Contains("UserColor.", StringComparison.Ordinal)
            && !text.Contains("OdsGlyphInk.", StringComparison.Ordinal);

        foreach (Match attribute in Regex.Matches(code, "\\b(style|Style|Bg|Fg)=\"((?:[^\"\\\\]|\\\\.|\"[^\"]*\")*?)\"(?=[\\s/>])"))
        {
            if (IsUserData(attribute.Groups[2].Value))
                yield return $"{attribute.Groups[1].Value}=\"{attribute.Groups[2].Value}\"";
        }

        // An option's glyph ink is written into a style by OdsGlyphInk, so the property is a sink too.
        foreach (Match ink in Regex.Matches(code, @"\bIconColor\s*=\s*([^,}\n;]+)"))
        {
            if (IsUserData(ink.Groups[1].Value))
                yield return ink.Value.Trim();
        }

        foreach (Match interpolation in Regex.Matches(code, "\\$\"(?:[^\"\\\\]|\\\\.)*\""))
        {
            foreach (Match hole in Regex.Matches(interpolation.Value, @"\{([^{}]+)\}"))
            {
                if (IsUserData(hole.Groups[1].Value) && !IsInsideUserColorCall(code, interpolation.Index))
                    yield return interpolation.Value;
            }
        }
    }

    private static bool IsInsideUserColorCall(string code, int index)
    {
        var lineStart = code.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        return code[lineStart..index].Contains("UserColor.", StringComparison.Ordinal);
    }

    private static IEnumerable<string> ScopedFiles() =>
        Directory.EnumerateFiles(ClientSource.Root, "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(ClientSource.Root, "*.razor.cs", SearchOption.AllDirectories))
            .Where(path => !IsBuildOutput(path))
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                if (path.Contains($"{Path.DirectorySeparatorChar}Pages{Path.DirectorySeparatorChar}Calendar{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || TagPages.Contains(name)
                    || name.StartsWith("OdsAvatar.", StringComparison.Ordinal)
                    || name.StartsWith("OdsChip.", StringComparison.Ordinal))
                    return true;

                var text = File.ReadAllText(path);
                return ScopeNames.Any(scope => Regex.IsMatch(text, $@"\b{scope}\b"));
            });

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
