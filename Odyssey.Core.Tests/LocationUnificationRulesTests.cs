using System.Text.RegularExpressions;
using Odyssey.Core.Locations;
using Odyssey.Core.Locations.Conversion;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The pure rules of issue #380 (unified locations): the shared text cleaner, the address key, name
/// composition and suffixing, link-like detection, the exact-letter match and the conversion's in-memory
/// index. The SQL driver around them is covered by <c>Odyssey.IntegrationTests</c> against MariaDB.
/// </summary>
public partial class LocationUnificationRulesTests
{
    // ── LocationTextCleaner: the single implementation (§4, V7-1) ────────────

    [Fact]
    public void Core_LocationText_delegates_to_the_shared_cleaner()
    {
        const string hostile = " \u202EÅl\u0000 \t";
        Assert.Equal(LocationTextCleaner.Clean(hostile), Odyssey.Core.Journal.LocationText.Clean(hostile));
        Assert.Equal("Ål", LocationTextCleaner.Clean(hostile));
    }

    /// <summary>
    /// A source-lint: exactly one file in the solution implements the location text cleaning — the one
    /// that strips the bidi controls — and it is <c>LocationTextCleaner</c> in <c>Odyssey.Dtos</c>. A second
    /// copy would drift from the one the address key and the WASM client use.
    /// </summary>
    [Fact]
    public void Exactly_one_location_text_cleaning_implementation_exists()
    {
        var root = SolutionRoot();
        var implementations = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path) && !path.Contains(".Tests", StringComparison.Ordinal))
            .Select(path => (Path: path, Code: StripComments(File.ReadAllText(path))))
            // A cleaner composes to NFC and strips controls and bidi overrides. TermTextValue rejects the same
            // characters rather than removing them and normalizes nothing, so it is not a copy.
            .Where(file => BidiRangeCheck().IsMatch(file.Code)
                && file.Code.Contains("NormalizationForm.FormC", StringComparison.Ordinal))
            .Select(file => file.Path)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        Assert.NotEmpty(implementations);
        Assert.Equal(["Odyssey.Dtos/Journal/LocationTextCleaner.cs"], implementations);

        var coreDelegate = StripComments(File.ReadAllText(Path.Combine(root, "Odyssey.Core", "Journal", "LocationText.cs")));
        Assert.Contains("LocationTextCleaner.Clean(", coreDelegate, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalizationForm", coreDelegate, StringComparison.Ordinal);
    }

    // ── LocationAddressKey (§4) ──────────────────────────────────────────────

    [Fact]
    public void Normalize_is_positional_upper_cased_and_unit_separator_joined()
    {
        var normalized = LocationAddressKey.Normalize("no", "0150", "oslo", "storgata  1", null, " ");

        Assert.Equal("NO\u001F0150\u001FOSLO\u001FSTORGATA 1\u001F\u001F", normalized);
    }

    [Fact]
    public void Case_and_whitespace_variants_share_a_key()
    {
        var a = LocationAddressKey.Compute("NO", "0150", "Oslo", "Storgata 1", null, null);
        var b = LocationAddressKey.Compute("no", " 0150", "OSLO", "storgata\t  1 ", "", "  ");

        Assert.NotNull(a);
        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a!);
    }

    [Fact]
    public void Letters_are_never_folded()
    {
        Assert.NotEqual(
            LocationAddressKey.Compute("NO", null, "Åsen", "Vei 1", null, null),
            LocationAddressKey.Compute("NO", null, "Asen", "Vei 1", null, null));
        Assert.NotEqual(
            LocationAddressKey.Compute("NO", null, "Ørsta", "Vei 1", null, null),
            LocationAddressKey.Compute("NO", null, "Orsta", "Vei 1", null, null));
    }

    [Fact]
    public void A_line_shift_and_a_missing_country_are_different_places()
    {
        var original = LocationAddressKey.Compute("NO", null, "Oslo", "Storgata 1", "Bakgården", null);

        Assert.NotEqual(original, LocationAddressKey.Compute("NO", null, "Oslo", "Bakgården", "Storgata 1", null));
        Assert.NotEqual(original, LocationAddressKey.Compute(null, null, "Oslo", "Storgata 1", "Bakgården", null));
    }

    [Fact]
    public void A_value_cannot_forge_the_separator()
    {
        // U+001F is a control character, so cleaning removes it before the fields are joined.
        Assert.Equal(
            LocationAddressKey.Compute("NO", null, "Oslo", "AB", null, null),
            LocationAddressKey.Compute("NO", null, "Oslo", "A\u001FB", null, null));
        Assert.NotEqual(
            LocationAddressKey.Compute("NO", null, "Oslo", "A", "B", null),
            LocationAddressKey.Compute("NO", null, "Oslo", "A\u001FB", null, null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", "\u202E")]
    public void No_line_and_no_city_has_no_key(string? line1, string? city) =>
        Assert.Null(LocationAddressKey.Compute("NO", "0150", city, line1, "Line two", "Region"));

    [Fact]
    public void Control_and_bidi_characters_do_not_change_the_key()
    {
        Assert.Equal(
            LocationAddressKey.Compute("NO", null, "Oslo", "Storgata 1", null, null),
            LocationAddressKey.Compute("NO", null, "Os\u202Elo", "Storgata\u0007 1", null, null));
    }

    // ── LocationRules address widths ─────────────────────────────────────────

    [Fact]
    public void ContactMethodLimits_names_the_location_widths()
    {
        Assert.Equal(LocationRules.AddressLineMaxLength, ContactMethodLimits.AddressLineMaxLength);
        Assert.Equal(LocationRules.CityMaxLength, ContactMethodLimits.CityMaxLength);
        Assert.Equal(LocationRules.PostalCodeMaxLength, ContactMethodLimits.PostalCodeMaxLength);
        Assert.Equal(LocationRules.RegionMaxLength, ContactMethodLimits.RegionMaxLength);
        Assert.Equal(LocationRules.CountryCodeLength, ContactMethodLimits.CountryCodeLength);
    }

    [Fact]
    public void LocationSource_appends_the_four_derived_sources_without_reusing_an_ordinal()
    {
        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7],
            Enum.GetValues<LocationSource>().Select(source => (int)source).Order());
        Assert.Equal(4, (int)LocationSource.Converted);
        Assert.Equal(5, (int)LocationSource.ContactAddress);
        Assert.Equal(6, (int)LocationSource.PropertyAddress);
        Assert.Equal(7, (int)LocationSource.Imported);
    }

    // ── Name composition and suffixing (§4) ──────────────────────────────────

    [Fact]
    public void ComposeAddress_joins_the_non_empty_parts_in_the_documented_order()
    {
        Assert.Equal(
            "Storgata 1 Bakgården, 0150 Oslo, Viken, NO",
            LocationNaming.ComposeAddress("Storgata 1", "Bakgården", "0150", "Oslo", "Viken", "no"));
        Assert.Equal("Oslo, NO", LocationNaming.ComposeAddress(null, " ", null, "Oslo", null, "NO"));
        Assert.Equal("NO", LocationNaming.ComposeAddress(null, null, null, null, null, "NO"));
        Assert.Null(LocationNaming.ComposeAddress(null, "", "  ", null, null, null));
    }

    [Fact]
    public void Candidates_truncate_the_base_so_the_suffix_fits()
    {
        var longName = new string('x', 140);

        Assert.Equal(100, LocationNaming.Candidate(longName, 1).Length);
        var second = LocationNaming.Candidate(longName, 2);
        Assert.Equal(100, second.Length);
        Assert.EndsWith(" (2)", second, StringComparison.Ordinal);
        Assert.EndsWith(" (12)", LocationNaming.Candidate(longName, 12), StringComparison.Ordinal);
        Assert.Equal(100, LocationNaming.Candidate(longName, 12).Length);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var value = new string('a', 99) + "\U0001F600";

        var cut = LocationNaming.Truncate(value, 100);

        Assert.Equal(new string('a', 99), cut);
    }

    [Fact]
    public void Allocate_finds_the_next_free_suffix_past_six()
    {
        var taken = new HashSet<string>(LocationNaming.CollationComparer) { "Cabin" };
        for (var i = 2; i <= 7; i++)
        {
            taken.Add($"Cabin ({i})");
        }

        Assert.Equal("Cabin (8)", LocationNaming.Allocate("Cabin", taken.Contains));
    }

    [Fact]
    public void Allocation_follows_the_collation_so_Asen_beside_Åsen_is_suffixed()
    {
        var taken = new HashSet<string>(LocationNaming.CollationComparer) { "Åsen" };

        Assert.Contains("asen", taken);
        Assert.Equal("Asen (2)", LocationNaming.Allocate("Asen", taken.Contains));
    }

    [Fact]
    public void Long_strings_sharing_a_prefix_get_distinct_names()
    {
        var prefix = new string('p', 100);
        var taken = new HashSet<string>(LocationNaming.CollationComparer);

        var first = LocationNaming.Allocate(prefix + " one", taken.Contains);
        taken.Add(first);
        var second = LocationNaming.Allocate(prefix + " two", taken.Contains);

        Assert.Equal(prefix, first);
        Assert.Equal(new string('p', 96) + " (2)", second);
    }

    // ── Exact-letter match (§7.1.9) ──────────────────────────────────────────

    [Fact]
    public void Exact_letter_match_folds_case_and_normalization_but_never_letters()
    {
        Assert.True(LocationNaming.IsExactLetterMatch("åsen", "ÅSEN"));
        Assert.True(LocationNaming.IsExactLetterMatch("A\u030Asen", "Åsen")); // decomposed vs composed
        Assert.False(LocationNaming.IsExactLetterMatch("Åsen", "Asen"));
        Assert.False(LocationNaming.IsExactLetterMatch("Øvre", "Ovre"));
    }

    // ── Link-like values (§11) ───────────────────────────────────────────────

    [Theory]
    [InlineData("https://meet.example.com/abc")]
    [InlineData("teams://join")]
    [InlineData("www.example.com")]
    [InlineData("WWW.Example.com")]
    [InlineData("meet.google.com/abc-defg-hij")]
    [InlineData("Join at zoom.us/j/123456789 please")]
    [InlineData("whereby.com")]
    [InlineData("whereby.com/room")]
    [InlineData("Zoom, passcode 1234")]
    [InlineData("Password: hunter2")]
    [InlineData("pwd=abc")]
    [InlineData("PIN 4455")]
    [InlineData("token abc")]
    [InlineData("Meeting ID 123 456 789")]
    [InlineData("access code 99")]
    [InlineData("Conference ID: 12")]
    [InlineData("Dial-in +47 22 00 00 00")]
    [InlineData("Teams, passord 1234")]
    [InlineData("kode 1234")]
    [InlineData("Møte-ID 123")]
    public void Link_like_values_are_recognised(string value) => Assert.True(LinkLikeText.IsLinkLike(value));

    [Theory]
    [InlineData("Home")]
    [InlineData("Camping")]
    [InlineData("Shopping")]
    [InlineData("Spinneriet")]
    [InlineData("Koden")]
    [InlineData("Pinnebergveien 3")]
    [InlineData("St. Olavs plass")]
    [InlineData("Whitby, North Yorkshire")]
    [InlineData("Åsen")]
    [InlineData("A | B")]
    public void Place_names_containing_a_keyword_as_a_substring_convert(string value) =>
        Assert.False(LinkLikeText.IsLinkLike(value));

    // ── The conversion's in-memory index (§11, L1) ───────────────────────────

    [Fact]
    public void The_index_matches_names_exactly_on_letters_and_falls_back_to_address_text()
    {
        var index = new LocationIndex();
        var åsen = Guid.NewGuid();
        var asen = Guid.NewGuid();
        index.Add(åsen, "Åsen", null, null);
        index.Add(asen, "Asen (2)", "Asen", null);

        Assert.Equal(åsen, index.FindByText("åsen"));
        Assert.Equal(asen, index.FindByText("ASEN"));
        Assert.Null(index.FindByText("Asen (3)"));
        Assert.True(index.IsNameTaken("asen"));
        Assert.Equal("Asen (3)", index.AllocateName("Asen"));
    }

    [Fact]
    public void The_index_follows_a_merge_forwarder()
    {
        var index = new LocationIndex();
        var survivor = Guid.NewGuid();
        var forwarder = Guid.NewGuid();
        index.Add(survivor, "Survivor", null, "key-b");
        index.Add(forwarder, "Old", null, "key-a", mergedInto: survivor);

        Assert.Equal(survivor, index.FindByKey("key-a"));
        Assert.Equal(survivor, index.FindByText("Old"));
    }

    [Fact]
    public void A_rolled_back_batch_takes_its_locations_back_out()
    {
        var index = new LocationIndex();
        index.Add(Guid.NewGuid(), "Kept", null, "kept");
        index.Commit();

        index.Add(Guid.NewGuid(), "Gone", "Gone text", "gone");
        index.Rollback();

        Assert.Equal(1, index.Count);
        Assert.Null(index.FindByKey("gone"));
        Assert.Null(index.FindByText("Gone text"));
        Assert.False(index.IsNameTaken("Gone"));
        Assert.NotNull(index.FindByKey("kept"));
    }

    [Fact]
    public void A_reserved_name_survives_a_rollback()
    {
        var index = new LocationIndex();
        index.ReserveName("Refused");
        index.Rollback();

        Assert.True(index.IsNameTaken("Refused"));
        Assert.Equal("Refused (2)", index.AllocateName("Refused"));
    }

    [GeneratedRegex(@"'(\\u202[Aa]|\u202A)'")]
    private static partial Regex BidiRangeCheck();

    private static string StripComments(string code) =>
        Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"//.*?$", string.Empty, RegexOptions.Multiline);

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

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
