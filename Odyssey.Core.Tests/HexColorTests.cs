using System.Text.RegularExpressions;
using Odyssey.Core;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.TestShared;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The shared colour rule, palette and display resolver (issue #392). <see cref="HexColor"/> is the only
/// hex check in the solution; these pin its behaviour independently of any <c>[StringLength]</c> in front
/// of it, because the service guard and the read paths have none.
/// </summary>
public class HexColorTests
{
    [Theory]
    [MemberData(nameof(HexColorNegativeCorpus.TheoryData), MemberType = typeof(HexColorNegativeCorpus))]
    public void Every_corpus_value_is_rejected_everywhere_the_rule_runs(string value)
    {
        Assert.False(HexColor.IsValid(value));
        Assert.Null(HexColor.Normalize(value));
        Assert.False(new HexColorAttribute().IsValid(value) && value.Length <= HexColor.Length);
        var ex = Assert.Throws<DomainValidationException>(() => TagColorGuard.EnsureValid(value));
        Assert.Equal(HexColor.InvalidMessage, ex.Message);
    }

    [Theory]
    [InlineData("#15803d", "#15803D")]
    [InlineData("#15803D", "#15803D")]
    [InlineData("#abcdef", "#ABCDEF")]
    [InlineData("#000000", "#000000")]
    public void A_valid_value_normalises_to_upper_case(string value, string expected)
    {
        Assert.True(HexColor.IsValid(value));
        Assert.Equal(expected, HexColor.Normalize(value));
    }

    [Fact]
    public void Null_means_no_colour_and_is_accepted_but_never_normalised_into_one()
    {
        Assert.False(HexColor.IsValid(null));
        Assert.Null(HexColor.Normalize(null));
        Assert.True(new HexColorAttribute().IsValid(null));
        TagColorGuard.EnsureValid(null);
    }

    [Fact]
    public void An_over_long_value_is_left_to_StringLength_so_one_error_is_reported() =>
        Assert.True(new HexColorAttribute().IsValid("#FFFFFFFF"));

    /// <summary>
    /// <see cref="HexColorAttribute"/> leaves an over-long value to <c>[StringLength]</c>, so it is only
    /// safe beside one: every property that carries it must carry <c>[StringLength(HexColor.Length)]</c> too.
    /// </summary>
    [Fact]
    public void Every_HexColor_property_is_also_length_bounded()
    {
        var properties = typeof(HexColor).Assembly.GetTypes()
            .SelectMany(type => type.GetProperties())
            .Where(property => property.IsDefined(typeof(HexColorAttribute), inherit: false))
            .ToList();
        Assert.True(properties.Count >= 7, $"Scan set unexpectedly small ({properties.Count}).");

        var unbounded = properties
            .Where(property => property.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.StringLengthAttribute), false)
                .Cast<System.ComponentModel.DataAnnotations.StringLengthAttribute>()
                .All(length => length.MaximumLength != HexColor.Length))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .ToList();

        Assert.Empty(unbounded);
    }

    [Fact]
    public void An_empty_string_is_rejected_rather_than_treated_as_no_colour() =>
        Assert.False(new HexColorAttribute().IsValid(string.Empty));

    [Fact]
    public void Every_palette_swatch_foreground_is_what_the_formula_computes()
    {
        Assert.Equal(8, ColorPalette.All.Count);
        foreach (var swatch in ColorPalette.All)
        {
            Assert.True(HexColor.IsValid(swatch.Hex), swatch.Key);
            Assert.Equal(swatch.Hex, HexColor.Normalize(swatch.Hex));
            Assert.Equal(HexColor.Foreground(swatch.Hex), swatch.Foreground);
        }

        Assert.Equal(ColorPalette.CalendarDefault, ColorPalette.All[0].Hex);
        Assert.Equal(ColorPalette.All.Count, ColorPalette.All.Select(swatch => swatch.Key).Distinct().Count());
    }

    [Fact]
    public void Foreground_picks_the_higher_contrast_candidate()
    {
        Assert.Equal(HexColor.LightForeground, HexColor.Foreground("#000000"));
        Assert.Equal(HexColor.DarkForeground, HexColor.Foreground("#FFFFFF"));

        var random = new Random(392);
        for (var i = 0; i < 1000; i++)
        {
            var hex = $"#{random.Next(0x1000000):X6}";
            var chosen = HexColor.Foreground(hex);
            var other = chosen == HexColor.LightForeground ? HexColor.DarkForeground : HexColor.LightForeground;
            var chosenRatio = HexColor.ContrastRatio(hex, chosen);
            Assert.True(chosenRatio >= HexColor.ContrastRatio(hex, other), hex);
            // Always enough for a non-text glyph (3:1); the worst case is about 4.27:1.
            Assert.True(chosenRatio >= 4.2, $"{hex} → {chosenRatio:F2}");
        }
    }

    [Fact]
    public void Foreground_refuses_an_invalid_value() =>
        Assert.Throws<ArgumentException>(() => HexColor.Foreground("red"));

    [Theory]
    [InlineData("#0369A1", true)]
    [InlineData("#0369a1", true)]
    [InlineData("#123456", false)]
    [InlineData(null, false)]
    public void Palette_membership_is_case_insensitive(string? hex, bool expected) =>
        Assert.Equal(expected, ColorPalette.Contains(hex));

    [Theory]
    [InlineData("#0369a1", "#0369A1")]
    [InlineData("#B23B3B", "#B23B3B")]
    [InlineData("red;x:y", ColorPalette.CalendarDefault)]
    [InlineData("#123456", "#123456")]
    [InlineData("#c2185b", "#C2185B")]
    [InlineData("#12345", ColorPalette.CalendarDefault)]
    [InlineData(null, ColorPalette.CalendarDefault)]
    public void A_stored_calendar_colour_reads_as_any_valid_hex_or_the_default(string? stored, string expected) =>
        Assert.Equal(expected, JournalMapsterConfig.CalendarColorOnRead(stored));

    [Fact]
    public void The_display_colour_comes_from_the_tag_that_wins_the_icon()
    {
        var display = TagIcons.ResolveDisplay(
        [
            new TagIconCandidate(Guid.NewGuid(), "Business", null, "#15803D"),
            new TagIconCandidate(Guid.NewGuid(), "Groceries", "shopping_cart", "#b23b3b"),
            new TagIconCandidate(Guid.NewGuid(), "Travel", "flight", "#0369A1"),
        ]);

        Assert.Equal("shopping_cart", display.Icon);
        Assert.Equal("#B23B3B", display.Color);
    }

    [Fact]
    public void A_winning_tag_without_a_colour_gives_no_colour_even_when_another_tag_has_one()
    {
        var display = TagIcons.ResolveDisplay(
        [
            new TagIconCandidate(Guid.NewGuid(), "Alpha", "home", null),
            new TagIconCandidate(Guid.NewGuid(), "Beta", "work", "#15803D"),
        ]);

        Assert.Equal(("home", (string?)null), (display.Icon, display.Color));
    }

    [Fact]
    public void Tags_without_icons_give_the_default_icon_and_no_colour()
    {
        var display = TagIcons.ResolveDisplay([new TagIconCandidate(Guid.NewGuid(), "Alpha", null, "#15803D")]);

        Assert.Equal((TagIcons.Default, (string?)null), (display.Icon, display.Color));
    }

    [Theory]
    [MemberData(nameof(HexColorNegativeCorpus.TheoryData), MemberType = typeof(HexColorNegativeCorpus))]
    public void A_corrupt_colour_on_the_winning_tag_resolves_to_no_colour(string stored)
    {
        var record = TagIconProjection.Apply(new Record(),
            [new TagIconCandidate(Guid.NewGuid(), "Alpha", "home", stored)]);

        Assert.Equal("home", record.DisplayIcon);
        Assert.Null(record.DisplayColor);
    }

    /// <summary>
    /// <c>DisplayColor</c> is assigned in exactly the two projections that assign <c>DisplayIcon</c>, both
    /// through <c>ResolveDisplay</c> (issue #392 AC 6c), so no path can hand a raw column to a client.
    /// </summary>
    [Fact]
    public void Only_the_two_projections_assign_the_display_colour()
    {
        var files = CoreFiles();
        var assigners = files
            .Where(path => Regex.IsMatch(StripComments(File.ReadAllText(path)), @"\bDisplayColor\s*=[^=>]"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["MapsterConfig.cs", "TagIconProjection.cs"], assigners);
    }

    /// <summary>
    /// <see cref="HexColor.IsValid"/> is the one hex check (issue #392 AC 6c): no regex over a hex colour and
    /// no second validator in the server projects or the client.
    /// </summary>
    [Fact]
    public void There_is_one_hex_colour_implementation()
    {
        var root = SolutionRoot();
        var files = new[] { "Odyssey.Dtos", "Odyssey.Core", "Odyssey.Api", "Odyssey.Client" }
            .SelectMany(project => SourceFiles(Path.Combine(root, project), "*.cs", "*.razor"))
            .ToList();
        Assert.True(files.Count > 200, $"Scan set unexpectedly small ({files.Count}).");

        var offenders = files
            // HexColor.cs is the implementation. OdsGlyphInk checks the CSS shape of code-constant
            // registry colours (contract and property types), never a user-entered one — outside this
            // feature's scope, which is tag and calendar colours only.
            .Where(path => Path.GetFileName(path) is not ("HexColor.cs" or "OdsGlyphInk.cs"))
            .Where(path => Regex.IsMatch(StripComments(File.ReadAllText(path)), @"\[0-9A-Fa-f\]\{6\}|\[0-9a-fA-F\]\{6\}|\[A-Fa-f0-9\]\{6\}|\\p\{XDigit\}|IsAsciiHexDigit|IsHexDigit"))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The calendar palette is declared once (issue #392 AC 13): four or more of its hexes together in one
    /// file outside the declaration is a second copy. Theme ramps share values by design and are exempt.
    /// </summary>
    [Fact]
    public void The_palette_is_declared_once()
    {
        var root = SolutionRoot();
        var exempt = new[] { "ColorPalette.cs", "OdysseyTheme.cs" };
        var files = new[] { "Odyssey.Dtos", "Odyssey.Core", "Odyssey.Api", "Odyssey.Client", "Odyssey.Context", "Odyssey.TestData" }
            .SelectMany(project => SourceFiles(Path.Combine(root, project), "*.cs", "*.razor"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(path => !exempt.Contains(Path.GetFileName(path)))
            .ToList();

        var copies = files
            .Where(path =>
            {
                var code = File.ReadAllText(path);
                return ColorPalette.All.Count(swatch => code.Contains(swatch.Hex, StringComparison.OrdinalIgnoreCase)) >= 4;
            })
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.Empty(copies);
    }

    private sealed class Record : ITagIconRecord
    {
        public List<Guid> TagIds { get; set; } = [];
        public string DisplayIcon { get; set; } = TagIcons.Default;
        public string? DisplayColor { get; set; }
    }

    private static List<string> CoreFiles()
    {
        var files = SourceFiles(Path.Combine(SolutionRoot(), "Odyssey.Core"), "*.cs").ToList();
        Assert.True(files.Count > 50, $"Scan set unexpectedly small ({files.Count}).");
        return files;
    }

    private static IEnumerable<string> SourceFiles(string root, params string[] patterns) =>
        patterns.SelectMany(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string StripComments(string code) =>
        Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"//.*?$", string.Empty, RegexOptions.Multiline);

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Solution root not found.");
    }
}
