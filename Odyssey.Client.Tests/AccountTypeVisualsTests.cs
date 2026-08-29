using System.Text.RegularExpressions;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Covers <see cref="AccountTypeVisuals"/> — the label, glyph, asset/liability grouping and colour
/// tokens behind every account badge.
/// </summary>
/// <remarks>
/// Same silent-failure class as <see cref="OdsTypeRegistriesTests"/>: each accessor is a
/// <c>switch</c> with a catch-all arm, so an unmapped account type compiles, renders, and quietly
/// shows up as "Unknown" with a grey question mark. Grouping is worse than cosmetic — a liability
/// that falls through to the <c>_ =&gt; Asset</c> default is counted on the wrong side of the
/// balance in the accounts summary.
/// </remarks>
public class AccountTypeVisualsTests
{
    private static readonly AccountType[] AllTypes = Enum.GetValues<AccountType>();

    public static TheoryData<AccountType> Selectable() => new(AccountTypeVisuals.Selectable);

    [Fact]
    public void Selectable_is_every_type_except_Unknown()
    {
        Assert.Equal(AllTypes.Where(t => t != AccountType.Unknown), AccountTypeVisuals.Selectable);
    }

    /// <summary>
    /// The two group lists back the "Assets" / "Liabilities" sections of the type picker and the
    /// summary totals. A type in neither is unpickable; a type in both is double-counted.
    /// </summary>
    [Fact]
    public void Assets_and_Liabilities_partition_the_selectable_types()
    {
        Assert.Equal(
            AccountTypeVisuals.Selectable,
            AccountTypeVisuals.Assets.Concat(AccountTypeVisuals.Liabilities).OrderBy(t => (int)t));
        Assert.Empty(AccountTypeVisuals.Assets.Intersect(AccountTypeVisuals.Liabilities));
    }

    /// <summary>
    /// Pinned explicitly rather than derived from <see cref="AccountTypeVisuals.Group"/>, because
    /// <c>Group</c>'s catch-all arm means a new debt type added to the enum but not to the liability
    /// list is treated as an asset — the failure this test exists to catch.
    /// </summary>
    [Fact]
    public void The_debt_types_are_liabilities_and_everything_else_is_an_asset()
    {
        Assert.Equal(
            [
                AccountType.CreditCard, AccountType.Mortgage, AccountType.StudentLoan,
                AccountType.PersonalLoan, AccountType.CarLoan, AccountType.TaxDebt,
                AccountType.OtherLiability,
            ],
            AccountTypeVisuals.Liabilities);

        Assert.Equal(
            [
                AccountType.Cash, AccountType.CheckingAccount, AccountType.SavingsAccount,
                AccountType.InvestmentAccount, AccountType.PensionAccount, AccountType.Property,
                AccountType.Vehicle, AccountType.OtherAsset,
            ],
            AccountTypeVisuals.Assets);
    }

    [Theory]
    [MemberData(nameof(Selectable))]
    public void Every_selectable_type_has_its_own_label_and_glyph(AccountType type)
    {
        Assert.NotEqual("Unknown", AccountTypeVisuals.Label(type));
        Assert.NotEqual("help", AccountTypeVisuals.MaterialIcon(type));
        Assert.Matches("^[a-z0-9_]+$", AccountTypeVisuals.MaterialIcon(type));
    }

    /// <summary>Two types sharing a colour makes them indistinguishable in the list; the glyphs may
    /// legitimately repeat (Vehicle and CarLoan both use <c>directions_car</c>), the colours may not.</summary>
    [Fact]
    public void No_two_selectable_types_share_a_colour()
    {
        var colors = AccountTypeVisuals.Selectable.Select(AccountTypeVisuals.FgColor).ToList();

        Assert.Equal(colors.Count, colors.Distinct().Count());
    }

    [Fact]
    public void Labels_are_unique_so_the_picker_has_no_ambiguous_rows()
    {
        var labels = AccountTypeVisuals.Selectable.Select(AccountTypeVisuals.Label).ToList();

        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    /// <summary>
    /// The badge is a glyph on a tint of its own hue, so the background token is always the
    /// foreground token plus <c>-soft</c>. Pairing a glyph with another type's tint is a colour bug
    /// that only shows on screen.
    /// </summary>
    [Theory]
    [MemberData(nameof(Selectable))]
    public void The_badge_background_is_the_soft_variant_of_its_glyph_colour(AccountType type)
    {
        var fg = AccountTypeVisuals.FgColor(type);

        Assert.Equal(fg.Replace(")", "-soft)"), AccountTypeVisuals.BgColor(type));
    }

    /// <summary>
    /// An accessor returning a CSS variable that no stylesheet declares yields an *empty* computed
    /// value — an invisible glyph on a transparent badge — with nothing in the build or the console
    /// to say so. This is the one check that ties the C# registry to the stylesheet it depends on.
    /// </summary>
    [Theory]
    [MemberData(nameof(Selectable))]
    public void Every_colour_token_is_declared_in_the_stylesheets(AccountType type)
    {
        var declared = DeclaredCssVariables();

        Assert.Contains(VariableName(AccountTypeVisuals.FgColor(type)), declared);
        Assert.Contains(VariableName(AccountTypeVisuals.BgColor(type)), declared);
    }

    /// <summary>
    /// <see cref="AccountType.Unknown"/> is the enum's zero value, so it is what an unset or
    /// unrecognised account type deserializes to. It must render as a neutral placeholder rather
    /// than borrowing a real type's identity.
    /// </summary>
    [Fact]
    public void Unknown_renders_as_a_neutral_placeholder()
    {
        Assert.Equal("Unknown", AccountTypeVisuals.Label(AccountType.Unknown));
        Assert.Equal("help", AccountTypeVisuals.MaterialIcon(AccountType.Unknown));
        Assert.Equal("var(--mud-palette-text-secondary)", AccountTypeVisuals.FgColor(AccountType.Unknown));
        Assert.Equal("var(--mud-palette-action-disabled-background)", AccountTypeVisuals.BgColor(AccountType.Unknown));
    }

    /// <summary>A value outside the enum — a row written by a newer build — must not throw.</summary>
    [Fact]
    public void A_value_outside_the_enum_falls_back_to_the_Unknown_treatment()
    {
        var stray = (AccountType)99;

        Assert.Equal("Unknown", AccountTypeVisuals.Label(stray));
        Assert.Equal("help", AccountTypeVisuals.MaterialIcon(stray));
        Assert.Equal(AccountGroup.Asset, AccountTypeVisuals.Group(stray));
    }

    private static string VariableName(string cssValue)
    {
        var match = Regex.Match(cssValue, @"^var\((--[a-z0-9-]+)\)$");
        Assert.True(match.Success, $"Expected a bare CSS variable reference, got '{cssValue}'");
        return match.Groups[1].Value;
    }

    /// <summary>Every custom property declared anywhere in the client's stylesheets.</summary>
    private static IReadOnlySet<string> DeclaredCssVariables()
    {
        var cssRoot = Path.Combine(ClientSource.Root, "wwwroot", "css");

        return Directory.EnumerateFiles(cssRoot, "*.css", SearchOption.AllDirectories)
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"(--[a-z0-9-]+)\s*:")
                .Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
    }
}
