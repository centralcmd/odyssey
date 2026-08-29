using System.Text.RegularExpressions;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Covers <see cref="OdsTypeRegistries"/> — the single source of truth for how every domain enum
/// renders: its label, its Material glyph and its category colour.
/// </summary>
/// <remarks>
/// This is the highest-risk kind of code to leave untested, because none of its failure modes are
/// loud. Each <c>…Of</c> lookup is a <c>FirstOrDefault</c> over a hand-maintained list with a
/// fallback, so a new enum member added without a registry entry does not throw, does not fail the
/// build, and does not even render blank — it silently renders as "Other". These tests assert the
/// registries and the enums they mirror are the same set, in both directions.
/// </remarks>
public class OdsTypeRegistriesTests
{
    /// <summary>Every registry paired with the enum it mirrors. Add a row when a registry is added.</summary>
    public static TheoryData<string, Type> RegistryEnumPairs() => new()
    {
        { nameof(OdsTypeRegistries.ContactTypes), typeof(ContactType) },
        { nameof(OdsTypeRegistries.RelationshipTypes), typeof(RelationshipType) },
        { nameof(OdsTypeRegistries.AddressLabels), typeof(AddressLabel) },
        { nameof(OdsTypeRegistries.EmailLabels), typeof(EmailLabel) },
        { nameof(OdsTypeRegistries.PhoneLabels), typeof(PhoneLabel) },
        { nameof(OdsTypeRegistries.AccountFileTypes), typeof(AccountFileType) },
        { nameof(OdsTypeRegistries.TransactionFileTypes), typeof(TransactionFileType) },
        { nameof(OdsTypeRegistries.TaxStatementFileTypes), typeof(TaxStatementFileType) },
        { nameof(OdsTypeRegistries.InsurancePolicyTypes), typeof(InsurancePolicyType) },
        { nameof(OdsTypeRegistries.PolicyFileTypes), typeof(PolicyFileType) },
        { nameof(OdsTypeRegistries.ContractTypes), typeof(ContractType) },
        { nameof(OdsTypeRegistries.ContractFileTypes), typeof(ContractFileType) },
        { nameof(OdsTypeRegistries.BillingIntervals), typeof(BillingInterval) },
        { nameof(OdsTypeRegistries.BudgetCategoryTypes), typeof(BudgetCategoryType) },
    };

    /// <summary>
    /// The defect this exists for: adding an enum member and forgetting the registry entry. The
    /// picker then omits it, and every chip rendering it falls back to "Other" — a silently wrong
    /// screen, not an error.
    /// </summary>
    [Theory]
    [MemberData(nameof(RegistryEnumPairs))]
    public void Every_enum_member_has_a_registry_entry(string registryName, Type enumType)
    {
        var missing = Enum.GetNames(enumType).Except(Registry(registryName).Select(t => t.Key)).ToList();

        Assert.True(missing.Count == 0,
            $"{enumType.Name} members with no {registryName} entry (they render as the fallback): " +
            string.Join(", ", missing));
    }

    /// <summary>The reverse: an entry left behind after its enum member was renamed or removed is
    /// dead weight in the picker that binds to a value the API will reject.</summary>
    [Theory]
    [MemberData(nameof(RegistryEnumPairs))]
    public void No_registry_entry_names_a_missing_enum_member(string registryName, Type enumType)
    {
        var orphaned = Registry(registryName).Select(t => t.Key).Except(Enum.GetNames(enumType)).ToList();

        Assert.True(orphaned.Count == 0,
            $"{registryName} entries with no matching {enumType.Name} member: " + string.Join(", ", orphaned));
    }

    [Theory]
    [MemberData(nameof(RegistryEnumPairs))]
    public void Registry_keys_are_unique(string registryName, Type enumType)
    {
        _ = enumType;
        var keys = Registry(registryName).Select(t => t.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    /// <summary>
    /// A blank glyph renders as the raw ligature text inside the badge, and a blank colour drops the
    /// badge to the browser default — both visible, neither detectable at compile time.
    /// </summary>
    [Theory]
    [MemberData(nameof(RegistryEnumPairs))]
    public void Every_registry_entry_is_fully_populated(string registryName, Type enumType)
    {
        _ = enumType;

        foreach (var option in Registry(registryName))
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Label), $"{registryName}.{option.Key} has no label");
            Assert.Matches("^[a-z0-9_]+$", option.Icon);   // Material Icons ligature
            Assert.StartsWith("oklch(", option.Color);
            Assert.StartsWith("oklch(", option.Soft);
        }
    }

    /// <summary>
    /// The soft tint is the glyph colour at 16% alpha — that pairing is what keeps a badge legible in
    /// both themes. A hand-edited hue that updates one and not the other gives a glyph on a tint of a
    /// different colour, which reads as a rendering bug rather than a typo.
    /// </summary>
    [Theory]
    [MemberData(nameof(RegistryEnumPairs))]
    public void Every_soft_tint_is_its_glyph_colour_at_low_alpha(string registryName, Type enumType)
    {
        _ = enumType;

        foreach (var option in Registry(registryName))
        {
            var expected = Regex.Replace(option.Color, @"\)$", " / 0.16)");
            Assert.Equal(expected, option.Soft);
        }
    }

    // ── The typed lookups ────────────────────────────────────────────────────

    [Fact]
    public void Every_typed_lookup_resolves_each_enum_member_to_its_own_entry()
    {
        AssertResolvesEveryMember<BudgetCategoryType>(OdsTypeRegistries.BudgetCategoryTypeOf);
        AssertResolvesEveryMember<InsurancePolicyType>(OdsTypeRegistries.InsurancePolicyTypeOf);
        AssertResolvesEveryMember<BillingInterval>(OdsTypeRegistries.BillingIntervalOf);
        AssertResolvesEveryMember<ContractType>(OdsTypeRegistries.ContractTypeOf);
        AssertResolvesEveryMember<ContractFileType>(OdsTypeRegistries.ContractFileTypeOf);
        AssertResolvesEveryMember<PolicyFileType>(OdsTypeRegistries.PolicyFileTypeOf);
        AssertResolvesEveryMember<AccountFileType>(OdsTypeRegistries.AccountFileTypeOf);
        AssertResolvesEveryMember<TransactionFileType>(OdsTypeRegistries.TransactionFileTypeOf);
        AssertResolvesEveryMember<TaxStatementFileType>(OdsTypeRegistries.TaxStatementFileTypeOf);
    }

    [Fact]
    public void Every_string_keyed_lookup_resolves_each_enum_member_to_its_own_entry()
    {
        AssertResolvesEveryKey<ContactType>(OdsTypeRegistries.ContactTypeOf);
        AssertResolvesEveryKey<RelationshipType>(OdsTypeRegistries.RelationshipTypeOf);
        AssertResolvesEveryKey<AddressLabel>(OdsTypeRegistries.AddressLabelOf);
        AssertResolvesEveryKey<EmailLabel>(OdsTypeRegistries.EmailLabelOf);
        AssertResolvesEveryKey<PhoneLabel>(OdsTypeRegistries.PhoneLabelOf);
    }

    /// <summary>
    /// A value outside the enum reaches these from persisted data written by an older build, so the
    /// documented fallback is the contract — and it is deliberately not uniform: the budget lookup
    /// falls back to the *first* entry and the billing lookup to Monthly, while the rest fall back to
    /// their trailing "Other". Getting one wrong is invisible until a stale row renders.
    /// </summary>
    [Fact]
    public void An_unknown_enum_value_falls_back_to_the_documented_entry()
    {
        Assert.Equal("Expense", OdsTypeRegistries.BudgetCategoryTypeOf((BudgetCategoryType)99).Key);
        Assert.Equal("Monthly", OdsTypeRegistries.BillingIntervalOf((BillingInterval)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.InsurancePolicyTypeOf((InsurancePolicyType)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.ContractTypeOf((ContractType)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.ContractFileTypeOf((ContractFileType)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.PolicyFileTypeOf((PolicyFileType)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.AccountFileTypeOf((AccountFileType)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.TransactionFileTypeOf((TransactionFileType)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.TaxStatementFileTypeOf((TaxStatementFileType)99).Key);
    }

    /// <summary>A contact whose type is missing renders as an organisation, not as a person —
    /// the safer default for a record with no stated kind.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Merchant")]     // one of the pre-#325 values folded into Organization
    public void An_unknown_contact_type_key_falls_back_to_Organization(string? key)
    {
        Assert.Equal("Organization", OdsTypeRegistries.ContactTypeOf(key).Key);
    }

    [Fact]
    public void An_unknown_contact_label_key_falls_back_to_Other()
    {
        Assert.Equal("Other", OdsTypeRegistries.RelationshipTypeOf("Nope").Key);
        Assert.Equal("Other", OdsTypeRegistries.AddressLabelOf(null).Key);
        Assert.Equal("Other", OdsTypeRegistries.EmailLabelOf("Mobile").Key);   // Mobile is phone-only
        Assert.Equal("Other", OdsTypeRegistries.PhoneLabelOf("Billing").Key);  // Billing is address-only
    }

    // ── Option projections ───────────────────────────────────────────────────

    /// <summary>
    /// <c>ToOptions</c> is what every picker binds to, so a projection that drops the glyph or the
    /// colour strips the visual language from all of them at once.
    /// </summary>
    [Fact]
    public void ToOptions_preserves_order_and_carries_each_entry_glyph_and_colour()
    {
        var options = OdsTypeRegistries.ToOptions(OdsTypeRegistries.ContractTypes);

        Assert.Equal(OdsTypeRegistries.ContractTypes.Count, options.Count);
        Assert.Equal(
            OdsTypeRegistries.ContractTypes.Select(t => (t.Key, t.Label, t.Icon, t.Color)),
            options.Select(o => (o.Value, o.Label, o.Icon!, o.IconColor!)));
    }

    /// <summary>The pre-built lists are cached projections; each must still match its registry.</summary>
    public static TheoryData<string, IReadOnlyList<OdsOption>, IReadOnlyList<OdsTypeOption>> PrebuiltOptions() => new()
    {
        { "ContactOptions", OdsTypeRegistries.ContactOptions, OdsTypeRegistries.ContactTypes },
        { "RelationshipOptions", OdsTypeRegistries.RelationshipOptions, OdsTypeRegistries.RelationshipTypes },
        { "AddressLabelOptions", OdsTypeRegistries.AddressLabelOptions, OdsTypeRegistries.AddressLabels },
        { "EmailLabelOptions", OdsTypeRegistries.EmailLabelOptions, OdsTypeRegistries.EmailLabels },
        { "PhoneLabelOptions", OdsTypeRegistries.PhoneLabelOptions, OdsTypeRegistries.PhoneLabels },
        { "AccountFileOptions", OdsTypeRegistries.AccountFileOptions, OdsTypeRegistries.AccountFileTypes },
        { "TransactionFileOptions", OdsTypeRegistries.TransactionFileOptions, OdsTypeRegistries.TransactionFileTypes },
        { "TaxStatementFileOptions", OdsTypeRegistries.TaxStatementFileOptions, OdsTypeRegistries.TaxStatementFileTypes },
        { "InsurancePolicyOptions", OdsTypeRegistries.InsurancePolicyOptions, OdsTypeRegistries.InsurancePolicyTypes },
        { "PolicyFileOptions", OdsTypeRegistries.PolicyFileOptions, OdsTypeRegistries.PolicyFileTypes },
        { "ContractOptions", OdsTypeRegistries.ContractOptions, OdsTypeRegistries.ContractTypes },
        { "ContractFileOptions", OdsTypeRegistries.ContractFileOptions, OdsTypeRegistries.ContractFileTypes },
        { "BillingIntervalOptions", OdsTypeRegistries.BillingIntervalOptions, OdsTypeRegistries.BillingIntervals },
    };

    [Theory]
    [MemberData(nameof(PrebuiltOptions))]
    public void Each_prebuilt_option_list_mirrors_its_registry(
        string name, IReadOnlyList<OdsOption> options, IReadOnlyList<OdsTypeOption> registry)
    {
        Assert.Equal(registry.Select(t => t.Key), options.Select(o => o.Value));
        Assert.Equal(registry.Select(t => t.Label), options.Select(o => o.Label));
        Assert.True(options.All(o => !string.IsNullOrEmpty(o.Icon)), $"{name} lost its glyphs");
    }

    /// <summary>The Sex options are hand-written rather than projected, so nothing else pins them
    /// to the <see cref="Sex"/> enum they bind to.</summary>
    [Fact]
    public void The_sex_options_match_the_Sex_enum()
    {
        Assert.Equal(Enum.GetNames<Sex>(), OdsTypeRegistries.SexOptions.Select(o => o.Value));
    }

    /// <summary>
    /// <c>BillingIntervals</c> documents its order as the enum's numeric order, because the
    /// subscriptions list sorts by "Frequency" against it. A reordered registry would silently
    /// reorder that column.
    /// </summary>
    [Fact]
    public void Billing_intervals_are_in_enum_numeric_order()
    {
        Assert.Equal(
            Enum.GetValues<BillingInterval>().OrderBy(v => (int)v).Select(v => v.ToString()),
            OdsTypeRegistries.BillingIntervals.Select(t => t.Key));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<OdsTypeOption> Registry(string name) =>
        (IReadOnlyList<OdsTypeOption>)typeof(OdsTypeRegistries).GetField(name)!.GetValue(null)!;

    private static void AssertResolvesEveryMember<TEnum>(Func<TEnum, OdsTypeOption> lookup) where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
            Assert.Equal(value.ToString(), lookup(value).Key);
    }

    private static void AssertResolvesEveryKey<TEnum>(Func<string?, OdsTypeOption> lookup) where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
            Assert.Equal(value.ToString(), lookup(value.ToString()).Key);
    }
}
