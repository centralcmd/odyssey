using System.Text.RegularExpressions;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The shared catalogue and the display-icon rule (issue #279 §3, §4, AC 7 and AC 11; issue #345 §3, §4, AC 7, AC 8 and AC 13).
/// </summary>
public class TagIconsTests
{
    [Fact]
    public void Catalogue_keys_are_unique()
    {
        var keys = TagIcons.All.Select(option => option.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Catalogue_labels_are_unique()
    {
        var labels = TagIcons.All.Select(option => option.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Other_stays_last_in_picker_order() =>
        Assert.Equal("more_horiz", TagIcons.All[^1].Key);

    /// <summary>
    /// Issue #345 widened the catalogue for journal, task and photo tags. Every key #279 shipped is
    /// persisted in <c>TransactionTags.Icon</c>, so dropping one would silently reset those tags to the
    /// default — the widening may only add.
    /// </summary>
    [Theory]
    [InlineData("shopping_cart"), InlineData("restaurant"), InlineData("local_cafe"), InlineData("directions_car")]
    [InlineData("local_gas_station"), InlineData("directions_bus"), InlineData("flight"), InlineData("home")]
    [InlineData("bolt"), InlineData("wifi"), InlineData("phone_iphone"), InlineData("medical_services")]
    [InlineData("fitness_center"), InlineData("school"), InlineData("child_care"), InlineData("pets")]
    [InlineData("checkroom"), InlineData("movie"), InlineData("subscriptions"), InlineData("card_giftcard")]
    [InlineData("volunteer_activism"), InlineData("savings"), InlineData("trending_up"), InlineData("account_balance")]
    [InlineData("credit_card"), InlineData("receipt_long"), InlineData("request_quote"), InlineData("payments")]
    [InlineData("work"), InlineData("build"), InlineData("shield"), InlineData("more_horiz")]
    public void Every_key_shipped_by_issue_279_is_still_selectable(string key) =>
        Assert.True(TagIcons.IsKnown(key));

    [Fact]
    public void Catalogue_keys_are_lower_case_ligature_names_within_the_column()
    {
        Assert.NotEmpty(TagIcons.All);
        Assert.All(TagIcons.All, option =>
        {
            Assert.Matches("^[a-z0-9_]{1,64}$", option.Key);
            Assert.True(option.Key.Length <= TagIcons.MaxKeyLength);
            Assert.False(string.IsNullOrWhiteSpace(option.Label));
        });
    }

    [Fact]
    public void Default_is_not_a_selectable_key()
    {
        Assert.Equal("local_offer", TagIcons.Default);
        Assert.DoesNotContain(TagIcons.All, option => option.Key == TagIcons.Default);
        Assert.False(TagIcons.IsKnown(TagIcons.Default));
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
        Assert.Equal(expected, TagIcons.IsKnown(key));

    [Fact]
    public void Normalize_Glyph_and_LabelFor_treat_unknown_as_default()
    {
        Assert.Equal("restaurant", TagIcons.Normalize("restaurant"));
        Assert.Null(TagIcons.Normalize("retired_key"));
        Assert.Null(TagIcons.Normalize(null));

        Assert.Equal("restaurant", TagIcons.Glyph("restaurant"));
        Assert.Equal(TagIcons.Default, TagIcons.Glyph("retired_key"));
        Assert.Equal(TagIcons.Default, TagIcons.Glyph(null));

        Assert.Equal("Dining", TagIcons.LabelFor("restaurant"));
        Assert.Equal("Default", TagIcons.LabelFor(null));
        Assert.Equal("Default", TagIcons.LabelFor("retired_key"));
    }

    [Fact]
    public void Order_is_by_name_case_insensitive_then_by_id()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var ordered = TagIcons.Order(
        [
            Tag("cherry"), Tag("Banana"), Tag("apple"), Tag("DUP", id: second), Tag("dup", id: first),
        ]);

        Assert.Equal(["apple", "Banana", "cherry", "dup", "DUP"], ordered.Select(tag => tag.Name));
    }

    [Fact]
    public void Order_returns_a_new_list_and_tolerates_null()
    {
        var source = new List<TagIconCandidate> { Tag("b"), Tag("a") };
        var ordered = TagIcons.Order(source);

        Assert.NotSame(source, ordered);
        Assert.Equal("b", source[0].Name);
        Assert.Empty(TagIcons.Order(null));
    }

    [Fact]
    public void Order_puts_a_nameless_candidate_last_by_id()
    {
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var ordered = TagIcons.Order([new TagIconCandidate(high, null, null), Tag("zebra"), new TagIconCandidate(low, null, "palette")]);

        Assert.Equal([null, null], ordered.Skip(1).Select(tag => tag.Name));
        Assert.Equal([low, high], ordered.Skip(1).Select(tag => tag.Id));
        Assert.Equal("zebra", ordered[0].Name);
    }

    [Fact]
    public void A_nameless_candidate_contributes_no_icon_ahead_of_a_named_one() =>
        Assert.Equal("beach_access",
            TagIcons.Resolve([new TagIconCandidate(Guid.NewGuid(), null, "palette"), Tag("Trip", "beach_access")]));

    [Fact]
    public void The_shape_overloads_sort_and_resolve_any_tag_type_through_its_candidate()
    {
        var tags = new List<ExistingTransactionTag>
        {
            new() { TransactionTagId = Guid.NewGuid(), Name = "Food", Archived = null, Icon = "restaurant" },
            new() { TransactionTagId = Guid.NewGuid(), Name = "bills", Archived = null, Icon = "receipt_long" },
        };

        Assert.Equal(["bills", "Food"], TagIcons.Order(tags, tag => tag.AsIconCandidate()).Select(tag => tag.Name));
        Assert.Equal("receipt_long", TagIcons.Resolve(tags, tag => tag.AsIconCandidate()));
        Assert.Equal(TagIcons.Default, TagIcons.Resolve<ExistingTransactionTag>(null, tag => tag.AsIconCandidate()));
    }

    public static TheoryData<string, TagIconCandidate[], string> ResolveTruthTable => new()
    {
        { "none", [], "local_offer" },
        { "one tag, no icon", [Tag("Food")], "local_offer" },
        { "one tag, icon", [Tag("Food", "restaurant")], "restaurant" },
        { "first WITH an icon", [Tag("Food", "restaurant"), Tag("Bills")], "restaurant" },
        { "first by name", [Tag("Food", "restaurant"), Tag("Bills", "receipt_long")], "receipt_long" },
        { "unknown key skipped", [Tag("bills", "receipt_long"), Tag("Apple", "retired_key")], "receipt_long" },
        { "archived counts", [Tag("Fuel", "local_gas_station"), Tag("Auto", "directions_car")], "directions_car" },
        { "journal: first by name", [Tag("Trip", "beach_access"), Tag("Art", "palette")], "palette" },
        { "journal: first WITH an icon", [Tag("Trip", "beach_access"), Tag("Art")], "beach_access" },
    };

    [Theory]
    [MemberData(nameof(ResolveTruthTable))]
    public void Resolve_follows_the_truth_table(string _, TagIconCandidate[] tags, string expected) =>
        Assert.Equal(expected, TagIcons.Resolve(tags));

    [Fact]
    public void Resolve_of_null_is_the_default() =>
        Assert.Equal(TagIcons.Default, TagIcons.Resolve(null));

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
        var attribute = new TagIconAttribute();
        Assert.Equal(valid, attribute.IsValid(value));
        Assert.Equal(TagIcons.InvalidIconMessage, attribute.FormatErrorMessage("Icon"));
    }

    /// <summary>
    /// A too-long value is the <c>[StringLength]</c> beside it to report, with the same message, so the
    /// response carries one <c>errors.Icon</c> entry rather than two identical ones.
    /// </summary>
    [Fact]
    public void Attribute_leaves_an_over_length_value_to_StringLength() =>
        Assert.True(new TagIconAttribute().IsValid(new string('a', TagIcons.MaxKeyLength + 1)));

    /// <summary>
    /// The resolution and ordering rules have ONE implementation (issue #279 AC 11, issue #345 AC 13).
    /// <c>DisplayIcon</c> is assigned in exactly two files — the Mapster registration for transactions and
    /// <c>TagIconProjection</c> for journal entries, tasks and photos — and both go through
    /// <see cref="TagIcons"/>. A third assignment, or a hand-rolled sort of any record's tag collection
    /// anywhere in <c>Odyssey.Core</c>, would be a re-implementation that could drift.
    /// </summary>
    [Fact]
    public void Only_the_two_projections_compute_the_display_icon()
    {
        var root = Path.Combine(SolutionRoot(), "Odyssey.Core");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        // A path-scoped lint whose scan set silently empties keeps passing after a move.
        Assert.True(files.Count > 50, $"Scan set unexpectedly small ({files.Count} files) under {root}.");

        var assigners = files
            .Where(path => Regex.IsMatch(StripComments(File.ReadAllText(path)), @"\bDisplayIcon\s*=[^=>]"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["MapsterConfig.cs", "TagIconProjection.cs"], assigners);

        foreach (var name in assigners)
        {
            var code = StripComments(File.ReadAllText(files.Single(path => Path.GetFileName(path) == name)));
            Assert.Contains("TagIcons.Resolve(", code);
            Assert.Contains("TagIcons.Order(", code);
        }

        var mapster = StripComments(File.ReadAllText(files.Single(path => Path.GetFileName(path) == "MapsterConfig.cs")));
        Assert.Contains("TagIcons.Normalize(", mapster);

        var tagSorters = files
            .Where(path => Regex.IsMatch(StripComments(File.ReadAllText(path)),
                @"\b(TransactionTags|EntryTags|ItemTags|Tags)\s*\.\s*OrderBy(Descending)?\s*\("))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(tagSorters);
    }

    /// <summary>
    /// Issue #345 AC 13: the catalogue was renamed from its transaction-only name when four tag types
    /// came to share it. No type, reference or comment under the old names survives in shipped code —
    /// a second catalogue under the old name is exactly the drift the rename exists to prevent.
    /// (Applied migrations and their tests are history and keep the names they were written with.)
    /// </summary>
    [Fact]
    public void No_transaction_only_catalogue_name_survives()
    {
        var root = SolutionRoot();
        var files = new[] { "Odyssey.Dtos", "Odyssey.Core", "Odyssey.Context", "Odyssey.Api", "Odyssey.ApiClient", "Odyssey.Client" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".css", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .ToList();

        Assert.True(files.Count > 200, $"Scan set unexpectedly small ({files.Count} files) under {root}.");

        var survivors = files
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"\bTransactionTagIcon(s|Option|Attribute)?\b"))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();
        Assert.Empty(survivors);
    }

    private static TagIconCandidate Tag(string name, string? icon = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), name, icon);

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
