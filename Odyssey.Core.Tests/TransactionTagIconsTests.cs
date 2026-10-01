using System.Text.RegularExpressions;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The shared catalogue and the display-icon rule (issue #279 §3, §4, AC 7 and AC 11).
/// </summary>
public class TransactionTagIconsTests
{
    [Fact]
    public void Catalogue_keys_are_unique()
    {
        var keys = TransactionTagIcons.All.Select(option => option.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Catalogue_keys_are_lower_case_ligature_names_within_the_column()
    {
        Assert.NotEmpty(TransactionTagIcons.All);
        Assert.All(TransactionTagIcons.All, option =>
        {
            Assert.Matches("^[a-z0-9_]{1,64}$", option.Key);
            Assert.True(option.Key.Length <= TransactionTagIcons.MaxKeyLength);
            Assert.False(string.IsNullOrWhiteSpace(option.Label));
        });
    }

    [Fact]
    public void Default_is_not_a_selectable_key()
    {
        Assert.Equal("local_offer", TransactionTagIcons.Default);
        Assert.DoesNotContain(TransactionTagIcons.All, option => option.Key == TransactionTagIcons.Default);
        Assert.False(TransactionTagIcons.IsKnown(TransactionTagIcons.Default));
    }

    [Theory]
    [InlineData("shopping_cart", true)]
    [InlineData("Shopping_Cart", false)]
    [InlineData("SHOPPING_CART", false)]
    [InlineData("local_offer", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("<svg>", false)]
    [InlineData("not_an_icon", false)]
    [InlineData(null, false)]
    public void IsKnown_is_ordinal_and_case_sensitive(string? key, bool expected) =>
        Assert.Equal(expected, TransactionTagIcons.IsKnown(key));

    [Fact]
    public void Normalize_Glyph_and_LabelFor_treat_unknown_as_default()
    {
        Assert.Equal("restaurant", TransactionTagIcons.Normalize("restaurant"));
        Assert.Null(TransactionTagIcons.Normalize("retired_key"));
        Assert.Null(TransactionTagIcons.Normalize(null));

        Assert.Equal("restaurant", TransactionTagIcons.Glyph("restaurant"));
        Assert.Equal(TransactionTagIcons.Default, TransactionTagIcons.Glyph("retired_key"));
        Assert.Equal(TransactionTagIcons.Default, TransactionTagIcons.Glyph(null));

        Assert.Equal("Dining", TransactionTagIcons.LabelFor("restaurant"));
        Assert.Equal("Default", TransactionTagIcons.LabelFor(null));
        Assert.Equal("Default", TransactionTagIcons.LabelFor("retired_key"));
    }

    [Fact]
    public void Order_is_by_name_case_insensitive_then_by_id()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var ordered = TransactionTagIcons.Order(
        [
            Tag("cherry"), Tag("Banana"), Tag("apple"), Tag("DUP", id: second), Tag("dup", id: first),
        ]);

        Assert.Equal(["apple", "Banana", "cherry", "dup", "DUP"], ordered.Select(tag => tag.Name));
    }

    [Fact]
    public void Order_returns_a_new_list_and_tolerates_null()
    {
        var source = new List<ExistingTransactionTag> { Tag("b"), Tag("a") };
        var ordered = TransactionTagIcons.Order(source);

        Assert.NotSame(source, ordered);
        Assert.Equal("b", source[0].Name);
        Assert.Empty(TransactionTagIcons.Order(null));
    }

    public static TheoryData<string, ExistingTransactionTag[], string> ResolveTruthTable => new()
    {
        { "none", [], "local_offer" },
        { "one tag, no icon", [Tag("Food")], "local_offer" },
        { "one tag, icon", [Tag("Food", "restaurant")], "restaurant" },
        { "first WITH an icon", [Tag("Food", "restaurant"), Tag("Bills")], "restaurant" },
        { "first by name", [Tag("Food", "restaurant"), Tag("Bills", "receipt_long")], "receipt_long" },
        { "unknown key skipped", [Tag("bills", "receipt_long"), Tag("Apple", "retired_key")], "receipt_long" },
        { "archived counts", [Tag("Fuel", "local_gas_station"), Tag("Auto", "directions_car", archived: true)], "directions_car" },
    };

    [Theory]
    [MemberData(nameof(ResolveTruthTable))]
    public void Resolve_follows_the_truth_table(string _, ExistingTransactionTag[] tags, string expected) =>
        Assert.Equal(expected, TransactionTagIcons.Resolve(tags));

    [Fact]
    public void Resolve_of_null_is_the_default() =>
        Assert.Equal(TransactionTagIcons.Default, TransactionTagIcons.Resolve(null));

    [Theory]
    [InlineData(null, true)]
    [InlineData("shopping_cart", true)]
    [InlineData("local_offer", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("Shopping_Cart", false)]
    [InlineData("<svg>", false)]
    public void Attribute_accepts_null_or_a_catalogue_key(string? value, bool valid)
    {
        var attribute = new TransactionTagIconAttribute();
        Assert.Equal(valid, attribute.IsValid(value));
        Assert.Equal(TransactionTagIcons.InvalidIconMessage, attribute.FormatErrorMessage("Icon"));
    }

    /// <summary>
    /// A too-long value is the <c>[StringLength]</c> beside it to report, with the same message, so the
    /// response carries one <c>errors.Icon</c> entry rather than two identical ones.
    /// </summary>
    [Fact]
    public void Attribute_leaves_an_over_length_value_to_StringLength() =>
        Assert.True(new TransactionTagIconAttribute().IsValid(new string('a', TransactionTagIcons.MaxKeyLength + 1)));

    /// <summary>
    /// AC 11: the resolution and ordering rules have ONE implementation. Every <c>ExistingTransaction</c>
    /// producer goes through the Mapster registration, which calls <see cref="TransactionTagIcons"/>;
    /// a second assignment of <c>DisplayIcon</c> or a hand-rolled name sort of a transaction's tags
    /// anywhere in <c>Odyssey.Core</c> would be a re-implementation that could drift.
    /// </summary>
    [Fact]
    public void Only_the_Mapster_registration_computes_the_display_icon()
    {
        var root = Path.Combine(SolutionRoot(), "Odyssey.Core");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        // A path-scoped lint whose scan set silently empties keeps passing after a move.
        Assert.True(files.Count > 50, $"Scan set unexpectedly small ({files.Count} files) under {root}.");

        var assigners = files
            .Where(path => Regex.IsMatch(StripComments(File.ReadAllText(path)), @"\.DisplayIcon\s*=[^=]"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["MapsterConfig.cs"], assigners);

        var mapster = StripComments(File.ReadAllText(files.Single(path => Path.GetFileName(path) == "MapsterConfig.cs")));
        Assert.Contains("TransactionTagIcons.Resolve(", mapster);
        Assert.Contains("TransactionTagIcons.Order(", mapster);
        Assert.Contains("TransactionTagIcons.Normalize(", mapster);

        var tagSorters = files
            .Where(path => Regex.IsMatch(StripComments(File.ReadAllText(path)),
                @"TransactionTags\s*\.\s*OrderBy(Descending)?\s*\("))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(tagSorters);
    }

    private static ExistingTransactionTag Tag(string name, string? icon = null, bool archived = false, Guid? id = null) => new()
    {
        TransactionTagId = id ?? Guid.NewGuid(),
        Name = name,
        Archived = archived ? DateTime.UtcNow : null,
        Icon = icon,
    };

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

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the solution root from the test binary.");
    }
}
