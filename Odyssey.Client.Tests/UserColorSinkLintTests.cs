using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Keeps every tag and calendar colour on the one validated path to a style (issues #392 §7.5, #394 §8):
/// the <c>UserColor</c> helper and the typed <c>UserColor</c> parameters.
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
        "ExistingCalendar", "OdsTagChips", "OdsTagChip", "OdsTagAdmin", "OdsTagColorPicker",
        "OdsCalendarSwatches", "UserColor",
        // Album icon colours (issue #406): an album colour is user data on the same footing as a tag's.
        "PhotoAlbumSummary", "ExistingPhotoAlbum", "AlbumVisuals",
        // Calendar icon colours: CalendarVisuals is the calendar twin of AlbumVisuals.
        "CalendarVisuals",
    ];

    /// <summary>The album surfaces that must stay in the scan set (issue #406 AC 9).</summary>
    private static readonly string[] AlbumFiles =
    [
        "AlbumsPage.razor", "AlbumFormDialog.razor", "AddToAlbumDialog.razor", "PhotoLibraryOverview.razor", "PhotosCard.razor.cs",
    ];

    /// <summary>The only client files that may read an album's <c>Color</c> outside an <c>AlbumVisuals</c> call (issue #406 §4).</summary>
    private static readonly string[] AlbumColorReaders = ["AlbumVisuals.cs", "AlbumFormDialog.razor.cs", "AlbumWrites.cs"];

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
        foreach (var album in AlbumFiles)
        {
            Assert.Contains(files, path => Path.GetFileName(path) == album);
        }

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
    [InlineData("new OdsOption(id, name) { Icon = AlbumVisuals.Glyph(a.Icon), IconColor = a.Color }")]
    [InlineData("<span style=\"color:@a.Color\"></span>")]
    [InlineData("<span style=\"@AlbumVisuals.GlyphStyle(a.Color, f);background:@a.Color\"></span>")]
    public void Each_seeded_sink_shape_is_caught(string code) =>
        Assert.NotEmpty(Violations(code));

    [Theory]
    [InlineData("<span style=\"@UserColor.Background(calendar.Color, x)\"></span>")]
    [InlineData("<OdsAvatar UserColor=\"@ColorOf(t)\" />")]
    [InlineData("<OdsChip UserColor=\"@tag.Color\" />")]
    [InlineData("private string? D => UserColor.Dot(tag.Color);")]
    [InlineData("private string? C => UserColor.Chip(tag.Color);")]
    [InlineData("new OdsOption(id, name) { Icon = \"circle\", IconColor = UserColor.Canonical(c.Color) }")]
    [InlineData("<span style=\"@AlbumVisuals.GlyphStyle(a.Color, AlbumVisuals.SurfaceFallback)\"></span>")]
    [InlineData("new OdsOption(id, name) { IconColor = AlbumVisuals.Ink(a.Color, AlbumVisuals.OverviewFallback) }")]
    public void The_validated_path_is_not_flagged(string code) =>
        Assert.Empty(Violations(code));

    [Fact]
    public void A_raw_chart_colour_is_not_reported_because_charts_are_never_scanned() =>
        Assert.DoesNotContain(ScopedFiles(), path => path.Contains($"{Path.DirectorySeparatorChar}Charts{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// <c>CalendarVisuals</c> is the calendar's one door from a colour to a style. Every colour it reads
    /// (any <c>x.Color</c>, whatever the variable is called) goes through <c>UserColor</c> on that line, and
    /// its one style member, <c>GlyphStyle</c>, composes only <c>Ink</c>, which is a <c>UserColor.Glyph</c>.
    /// The strict-file check below trusts <c>CalendarVisuals.GlyphStyle(</c> on the strength of this.
    /// </summary>
    [Fact]
    public void Calendar_visuals_route_every_colour_through_UserColor()
    {
        var path = Directory.EnumerateFiles(ClientSource.Root, "CalendarVisuals.cs", SearchOption.AllDirectories).Single(p => !IsBuildOutput(p));
        var source = File.ReadAllText(path);

        var reads = Regex.Matches(source, @"\b\w+\.Color\b");
        Assert.NotEmpty(reads);
        foreach (Match read in reads)
        {
            var line = source[source.LastIndexOf('\n', read.Index)..source.IndexOf('\n', read.Index)];
            Assert.Contains("UserColor.", line, StringComparison.Ordinal);
        }

        var ink = Regex.Match(source, @"public static string\? Ink\([^)]*\)\s*=>[^;]*;", RegexOptions.Singleline);
        Assert.True(ink.Success, "CalendarVisuals.Ink not found");
        Assert.Contains("UserColor.Glyph(", ink.Value, StringComparison.Ordinal);

        var glyphStyle = Regex.Match(source, @"public static string GlyphStyle\([^)]*\)\s*=>\s*(?<body>[^;]*);", RegexOptions.Singleline);
        Assert.True(glyphStyle.Success, "CalendarVisuals.GlyphStyle not found");
        Assert.Equal("$\"color:{Ink(calendar)}\"", glyphStyle.Groups["body"].Value.Trim());
    }

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
                // CalendarVisuals.GlyphStyle is the calendar's one style door, and it routes through UserColor
                // (Calendar_visuals_route_every_colour_through_UserColor pins that). Nothing else in it is trusted.
                if (expression.Contains("UserColor.", StringComparison.Ordinal) || expression.StartsWith("CalendarVisuals.GlyphStyle(", StringComparison.Ordinal))
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
        // AlbumVisuals routes an album colour through UserColor.Glyph (pinned below), so a colour passed INTO an
        // AlbumVisuals call is on the validated path. Only the call's own arguments are exempt: the call is cut
        // out before the check, so a second, raw colour beside it in the same attribute is still caught.
        bool IsUserData(string text)
        {
            var rest = WithoutAlbumVisualsCalls(text);
            return tokens.Any(t => Regex.IsMatch(rest, t))
                && !rest.Contains("UserColor.", StringComparison.Ordinal)
                && !rest.Contains("OdsGlyphInk.", StringComparison.Ordinal);
        }

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

    /// <summary>AlbumVisuals is only exempt because it re-validates through UserColor; pin that.</summary>
    [Fact]
    public void AlbumVisuals_routes_every_album_colour_through_UserColor()
    {
        var source = File.ReadAllText(SourceFile("AlbumVisuals.cs"));
        Assert.Matches(@"\bInk\([^)]*\)\s*=>\s*UserColor\.Glyph\(", source);
        Assert.Matches(@"\bGlyphStyle\([^)]*\)\s*=>[^;]*\bInk\(", source);
    }

    /// <summary>
    /// <c>AlbumVisuals</c> is the only path from an album's <c>Color</c> to ink (issue #406 §4): in every
    /// other client file an album's colour (<c>a.Color</c>, <c>album.Color</c>, or a local copied from one)
    /// is read only as an argument of an <c>AlbumVisuals.</c> call. The form (picker seeding) and the save
    /// mapper read it without rendering it.
    /// </summary>
    [Fact]
    public void Only_AlbumVisuals_turns_an_album_colour_into_ink()
    {
        var files = Directory.EnumerateFiles(ClientSource.Root, "*.*", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path) && (path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal)))
            .Where(path => !AlbumColorReaders.Contains(Path.GetFileName(path)))
            .ToList();
        Assert.Contains(files, path => Path.GetFileName(path) == "AlbumsPage.razor");

        var problems = files.SelectMany(path => AlbumColorReads(File.ReadAllText(path)).Select(line => $"{Path.GetRelativePath(ClientSource.Root, path)}: {line}")).ToList();
        Assert.True(problems.Count == 0, "An album colour is read outside AlbumVisuals:\n" + string.Join('\n', problems));
    }

    [Theory]
    [InlineData("var c = album.Color;")]
    [InlineData("IconColor = a.Color,")]
    [InlineData("var c = a.Color; var s = AlbumVisuals.Ink(c);")]
    [InlineData("var c = _openAlbum.Color;")]
    [InlineData("IconColor = coverAlbum.Color,")]
    public void Each_seeded_album_colour_read_is_caught(string code) => Assert.NotEmpty(AlbumColorReads(code));

    [Fact]
    public void An_album_colour_passed_to_AlbumVisuals_is_not_flagged() =>
        Assert.Empty(AlbumColorReads("IconColor = AlbumVisuals.Ink(a.Color, AlbumVisuals.OverviewFallback),"));

    private static IEnumerable<string> AlbumColorReads(string code)
    {
        foreach (var line in code.Split('\n'))
        {
            // Any receiver named for an album (album, coverAlbum, _openAlbum, albums[i]…) or the conventional `a`.
            foreach (Match read in Regex.Matches(line, @"\b(?:a|\w*[Aa]lbum\w*(?:\[[^\]]*\])?)\.Color\b"))
            {
                var before = line[..read.Index];
                var insideAlbumVisuals = Regex.IsMatch(before, @"\bAlbumVisuals\.\w+\([^)]*$");
                if (!insideAlbumVisuals)
                    yield return line.Trim();
            }
        }
    }

    /// <summary>The text with every <c>AlbumVisuals.X(…)</c> call (arguments included, one nesting level) removed.</summary>
    internal static string WithoutAlbumVisualsCalls(string text) =>
        // A sink regex can cut an argument list at its first comma, leaving an unclosed call: strip that tail too.
        Regex.Replace(Regex.Replace(text, @"\bAlbumVisuals\.\w+\((?:[^()]|\([^()]*\))*\)", "AlbumVisualsCall"),
            @"\bAlbumVisuals\.\w+\([^()]*$", "AlbumVisualsCall");

    private static string SourceFile(string name) =>
        Directory.EnumerateFiles(ClientSource.Root, name, SearchOption.AllDirectories).Single(path => !IsBuildOutput(path));

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
