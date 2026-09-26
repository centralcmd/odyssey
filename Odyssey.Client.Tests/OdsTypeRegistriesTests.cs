using System.Globalization;
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
        { nameof(OdsTypeRegistries.AddressLabels), typeof(AddressLabel) },
        { nameof(OdsTypeRegistries.EmailLabels), typeof(EmailLabel) },
        { nameof(OdsTypeRegistries.PhoneLabels), typeof(PhoneLabel) },
        { nameof(OdsTypeRegistries.AccountFileTypes), typeof(AccountFileType) },
        { nameof(OdsTypeRegistries.TransactionFileTypes), typeof(TransactionFileType) },
        { nameof(OdsTypeRegistries.TaxStatementFileTypes), typeof(TaxStatementFileType) },
        { nameof(OdsTypeRegistries.ContractTypes), typeof(ContractType) },
        { nameof(OdsTypeRegistries.ContractFileTypes), typeof(ContractFileType) },
        { nameof(OdsTypeRegistries.ContractPartyRoles), typeof(ContractPartyRole) },
        { nameof(OdsTypeRegistries.ContractEventTypes), typeof(ContractEventType) },
        { nameof(OdsTypeRegistries.BudgetCategoryTypes), typeof(BudgetCategoryType) },
        { nameof(OdsTypeRegistries.PropertyFileTypes), typeof(PropertyFileType) },
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
        AssertResolvesEveryMember<ContractType>(OdsTypeRegistries.ContractTypeOf);
        AssertResolvesEveryMember<ContractFileType>(OdsTypeRegistries.ContractFileTypeOf);
        AssertResolvesEveryMember<ContractEventType>(OdsTypeRegistries.ContractEventTypeOf);
        AssertResolvesEveryMember<AccountFileType>(OdsTypeRegistries.AccountFileTypeOf);
        AssertResolvesEveryMember<TransactionFileType>(OdsTypeRegistries.TransactionFileTypeOf);
        AssertResolvesEveryMember<TaxStatementFileType>(OdsTypeRegistries.TaxStatementFileTypeOf);
        AssertResolvesEveryMember<PropertyFileType>(OdsTypeRegistries.PropertyFileTypeOf);
    }

    [Fact]
    public void Every_string_keyed_lookup_resolves_each_enum_member_to_its_own_entry()
    {
        AssertResolvesEveryKey<ContactType>(OdsTypeRegistries.ContactTypeOf);
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
        Assert.Equal("Other", OdsTypeRegistries.ContractTypeOf((ContractType)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.ContractFileTypeOf((ContractFileType)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.ContractEventTypeOf((ContractEventType)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.AccountFileTypeOf((AccountFileType)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.TransactionFileTypeOf((TransactionFileType)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.TaxStatementFileTypeOf((TaxStatementFileType)99).Key);
        Assert.Equal("Other", OdsTypeRegistries.PropertyFileTypeOf((PropertyFileType)99).Key);
    }

    /// <summary>
    /// <c>PropertyFileTypes</c> (issue #210) reads with <c>Other</c> LAST although it is the enum's zero
    /// member — the AccountFileType shape. <c>PropertyFileTypeOf</c>'s fallback is positional
    /// (<c>[^1]</c>), so a reorder that ends the list with anything else would render every unknown
    /// ordinal as that member; and a picker opening on <c>Deed</c> by default would be the silent claim
    /// the guess rule exists to avoid.
    /// </summary>
    [Fact]
    public void PropertyFileTypes_reads_with_Other_last_although_it_is_ordinal_zero()
    {
        Assert.Equal(
            ["Deed", "PurchaseAgreement", "Valuation", "Inspection", "Registration", "Insurance", "Warranty",
             "Receipt", "Maintenance", "Tax", "Drawing", "Other"],
            OdsTypeRegistries.PropertyFileTypes.Select(t => t.Key).ToList());

        Assert.Equal(0, (int)PropertyFileType.Other);
        Assert.Equal("Other", OdsTypeRegistries.PropertyFileTypeOf(default).Key);
    }

    /// <summary>
    /// <c>ContractTypes</c> reads in its own order, not ordinal order (issues #157, #187): <c>Loan</c>
    /// (8) sits after <c>Purchase</c>, <c>Deposit</c> (9) beside its mirror <c>Loan</c>, and
    /// <c>Other</c> (3) last — the trailing entry being <c>ContractTypeOf</c>'s documented fallback, so
    /// appending <c>Deposit</c> at the end of the list would have made every unknown type render as a
    /// deposit.
    /// </summary>
    [Fact]
    public void ContractTypes_reads_Deposit_after_Loan_and_Other_last()
    {
        Assert.Equal(
            ["Employment", "Service", "Rental", "Insurance", "Subscription", "Purchase", "Loan", "Deposit",
             "Membership", "Other"],
            OdsTypeRegistries.ContractTypes.Select(t => t.Key).ToList());

        Assert.NotEqual(ContractType.Other, Enum.GetValues<ContractType>().Max());
    }

    /// <summary>
    /// Issue #187 — the contract-type and party-role registries agree with the design system's
    /// <c>CONTRACT_TYPES</c> / <c>CONTRACT_PARTY_ROLES</c> exports on every key, label, glyph, colour,
    /// ordinal and the reading order. The internal-consistency tests above stay green while a hand-copied
    /// row drifts from its source; this is what catches the drift, the same way
    /// <see cref="ContractEventTypes_agrees_with_the_design_systems_registry"/> does for events. Issue #210
    /// adds <c>PROPERTY_FILE_TYPES</c>, whose reading order (Other last, ordinal 0) likewise differs from
    /// its ordinal order.
    /// </summary>
    [Theory]
    [InlineData(nameof(OdsTypeRegistries.ContractTypes), "ContractTypeSelect.jsx", typeof(ContractType))]
    [InlineData(nameof(OdsTypeRegistries.ContractPartyRoles), "ContractPartyRoleSelect.jsx", typeof(ContractPartyRole))]
    [InlineData(nameof(OdsTypeRegistries.PropertyFileTypes), "PropertyFileTypeSelect.jsx", typeof(PropertyFileType))]
    public void Select_backed_registries_agree_with_the_design_system(
        string registryName, string dsFile, Type enumType)
    {
        var path = ClientSource.Sibling(Path.Combine("Odyssey Design System", "components", dsFile));
        Assert.True(File.Exists(path), $"The design system's registry is missing at {path}.");

        var declared = Regex.Matches(
                File.ReadAllText(path),
                @"\{\s*key:\s*'(?<key>\w+)',\s*label:\s*'(?<label>[^']*)',\s*enumValue:\s*(?<ordinal>\d+),"
                + @"\s*icon:\s*'(?<icon>\w+)',\s*color:\s*'(?<color>[^']*)',\s*soft:\s*'(?<soft>[^']*)'")
            .Select(m => (
                Key: m.Groups["key"].Value,
                Label: m.Groups["label"].Value,
                Ordinal: int.Parse(m.Groups["ordinal"].Value, CultureInfo.InvariantCulture),
                Icon: m.Groups["icon"].Value,
                Color: m.Groups["color"].Value,
                Soft: m.Groups["soft"].Value))
            .ToList();

        var registry = Registry(registryName);

        Assert.Equal(declared.Select(d => d.Key), registry.Select(t => t.Key));

        foreach (var (entry, ds) in registry.Zip(declared))
        {
            Assert.Equal(ds.Label, entry.Label);
            Assert.Equal(ds.Icon, entry.Icon);
            Assert.Equal(ds.Color, entry.Color);
            Assert.Equal(ds.Soft, entry.Soft);
            Assert.Equal(ds.Ordinal, Convert.ToInt32(Enum.Parse(enumType, entry.Key), CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// <c>ContractEventTypes</c> reads in a different order from the one it is stored in (issue #154).
    /// <c>Other</c> keeps ordinal 8 while the nine automation members take 9–17, so the registry's
    /// reading order and the enum's ordinal order have parted company — the same split
    /// <c>ContractTypes</c> already carries.
    /// </summary>
    /// <remarks>
    /// <b><c>Other</c> must stay LAST in the registry.</b> <c>ContractEventTypeOf</c> documents the
    /// trailing entry as its fallback for an ordinal this build does not know, so a reorder that ends
    /// the list with something else would silently render every unknown event as that member instead —
    /// plausible, specific and wrong. The assertion is deliberately made on both halves at once: that
    /// <c>Other</c> is last, and that its ordinal is NOT.
    /// </remarks>
    [Fact]
    public void ContractEventTypes_reads_with_Other_last_although_its_ordinal_is_not()
    {
        var keys = OdsTypeRegistries.ContractEventTypes.Select(t => t.Key).ToList();

        Assert.Equal("Other", keys[^1]);
        Assert.Equal(8, (int)ContractEventType.Other);
        Assert.NotEqual(
            ContractEventType.Other,
            Enum.GetValues<ContractEventType>().Max());

        // The nine automation members read AFTER the original eight and BEFORE Other.
        var firstAutomation = keys.IndexOf("Paused");
        Assert.Equal(keys.IndexOf("EmailSent") + 1, firstAutomation);
        Assert.Equal(keys.Count - 1, keys.IndexOf("PartyRemoved") + 1);
    }

    /// <summary>
    /// Issue #155 AC 19 — the design system and this registry agree on all eighteen keys, labels,
    /// glyphs, colours <b>and reading order</b>. The design system is the source of truth for all
    /// five; this is the only thing that catches the two drifting apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The AC says "pinned by <c>OdsTypeRegistriesTests</c>", and until this test the file pinned only
    /// the C# side's <em>internal</em> consistency — that <c>Other</c> is last and every enum member
    /// has an entry. Both would stay green while the DS renamed a label, re-hued a glyph or reordered
    /// the list, which is exactly the drift the AC names and the class of defect this whole file
    /// exists for: a wrong mapping never throws and never fails a build, it is just silently wrong on
    /// screen.
    /// </para>
    /// <para>
    /// <c>ContractEventTypes</c> is the only registry with this cross-file check today. That is a
    /// repo-wide gap rather than a rule — the others are equally exposed — but a test that covers one
    /// registry is worth more than a note saying all of them should have one.
    /// </para>
    /// </remarks>
    [Fact]
    public void ContractEventTypes_agrees_with_the_design_systems_registry()
    {
        var path = ClientSource.Sibling(
            Path.Combine("Odyssey Design System", "ui_kits", "web", "contract-events-data.js"));
        Assert.True(File.Exists(path), $"The design system's registry is missing at {path}.");

        // One object literal per member, in source order — which IS the reading order on both sides.
        var declared = Regex.Matches(
                File.ReadAllText(path),
                @"\{\s*key:\s*'(?<key>\w+)',\s*label:\s*'(?<label>[^']*)',\s*enumValue:\s*(?<ordinal>\d+),"
                + @"\s*icon:\s*'(?<icon>\w+)',\s*color:\s*'(?<color>[^']*)',\s*soft:\s*'(?<soft>[^']*)'")
            .Select(m => (
                Key: m.Groups["key"].Value,
                Label: m.Groups["label"].Value.Replace("\u2019", "'", StringComparison.Ordinal),
                Ordinal: int.Parse(m.Groups["ordinal"].Value, CultureInfo.InvariantCulture),
                Icon: m.Groups["icon"].Value,
                Color: m.Groups["color"].Value,
                Soft: m.Groups["soft"].Value))
            .ToList();

        Assert.Equal(
            OdsTypeRegistries.ContractEventTypes.Count,
            declared.Count);

        // Reading order, member for member.
        Assert.Equal(
            declared.Select(d => d.Key),
            OdsTypeRegistries.ContractEventTypes.Select(t => t.Key));

        foreach (var (entry, ds) in OdsTypeRegistries.ContractEventTypes.Zip(declared))
        {
            Assert.Equal(ds.Label, entry.Label);
            Assert.Equal(ds.Icon, entry.Icon);
            Assert.Equal(ds.Color, entry.Color);
            Assert.Equal(ds.Soft, entry.Soft);

            // The ordinal is the wire contract, and the DS carries it explicitly precisely because
            // reading order no longer implies it.
            Assert.Equal(ds.Ordinal, (int)Enum.Parse<ContractEventType>(entry.Key));
        }
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

    /// <summary>
    /// The keyed <c>Other</c> fallback (issue #47 §9, criterion 19). This test fails against the old
    /// positional <c>[^1]</c> implementation now that the organization members are appended AFTER
    /// <c>Other</c> — that fallback would render an undefined ordinal as <c>Branch</c> / <c>Claims</c>
    /// / <c>Direct</c>: plausible, specific and wrong.
    /// </summary>
    [Fact]
    public void An_unknown_contact_label_key_falls_back_to_Other()
    {
        Assert.Equal("Other", OdsTypeRegistries.AddressLabelOf(null).Key);
        Assert.Equal("Other", OdsTypeRegistries.AddressLabelOf("Nonexistent").Key);
        Assert.Equal("Other", OdsTypeRegistries.EmailLabelOf("Nonexistent").Key);
        Assert.Equal("Other", OdsTypeRegistries.PhoneLabelOf("Nonexistent").Key);
        Assert.Equal("Other", OdsTypeRegistries.EmailLabelOf("Mobile").Key);   // Mobile is phone-only
        Assert.Equal("Other", OdsTypeRegistries.AddressLabelOf("Switchboard").Key); // phone-only
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
        // No *LabelOptions rows: the three unfiltered label unions were removed by issue #47 §3 in
        // favour of the per-contact-type projections, which LabelsFor_offers_exactly_the_scoped_set covers.
        { "AccountFileOptions", OdsTypeRegistries.AccountFileOptions, OdsTypeRegistries.AccountFileTypes },
        { "TransactionFileOptions", OdsTypeRegistries.TransactionFileOptions, OdsTypeRegistries.TransactionFileTypes },
        { "TaxStatementFileOptions", OdsTypeRegistries.TaxStatementFileOptions, OdsTypeRegistries.TaxStatementFileTypes },
        { "ContractOptions", OdsTypeRegistries.ContractOptions, OdsTypeRegistries.ContractTypes },
        { "ContractFileOptions", OdsTypeRegistries.ContractFileOptions, OdsTypeRegistries.ContractFileTypes },
        { "ContractPartyRoleOptions", OdsTypeRegistries.ContractPartyRoleOptions, OdsTypeRegistries.ContractPartyRoles },
        { "PropertyFileOptions", OdsTypeRegistries.PropertyFileOptions, OdsTypeRegistries.PropertyFileTypes },
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

    // ── The budget category's DIRECTION projection (issue #159 / #162) ───────

    /// <summary>
    /// <c>BudgetCategoryDirections</c> is the same two values shaped as a money field's LEAD, and it is
    /// DERIVED from <see cref="OdsTypeRegistries.BudgetCategoryTypes"/> rather than written out again —
    /// so the lead, the dialog's copy and the stored enum cannot disagree. It deliberately does not
    /// appear in <see cref="RegistryEnumPairs"/>: it is a list of <c>OdsDirectionOption</c>, not
    /// <c>OdsTypeOption</c>, so the reflection helper there would throw on it.
    /// </summary>
    [Fact]
    public void The_budget_direction_lead_mirrors_the_category_registry()
    {
        Assert.Equal(
            OdsTypeRegistries.BudgetCategoryTypes.Select(t => t.Key),
            OdsTypeRegistries.BudgetCategoryDirections.Select(o => o.Value));
        Assert.Equal(
            OdsTypeRegistries.BudgetCategoryTypes.Select(t => t.Label),
            OdsTypeRegistries.BudgetCategoryDirections.Select(o => o.Label));
    }

    /// <summary>
    /// <b>The lead carries a WORD and no glyph.</b> A directional arrow beside a figure reads as that
    /// figure rising or falling — against value rather than against the budget — which is the same
    /// reason <c>TermDirectionVisuals</c> carries none. <c>OdsMoneyField</c> prefers <c>Icon</c> over
    /// <c>Short</c>, so an icon slipped in here would silently replace the word everywhere.
    /// </summary>
    [Fact]
    public void The_budget_direction_lead_is_a_word_with_no_icon()
    {
        var lead = OdsTypeRegistries.BudgetCategoryDirections;

        Assert.Equal(2, lead.Count);
        Assert.All(lead, option =>
        {
            Assert.Null(option.Icon);
            Assert.False(string.IsNullOrWhiteSpace(option.Short));
        });
        Assert.Equal(["out", "in"], lead.Select(o => o.Short));
    }

    /// <summary>
    /// <b>Tone comes from the DIRECTION, never from the value.</b> This is the trap
    /// <c>docs/frontend-mudblazor-gotchas.md</c> names: a lead tinted from <c>Value</c> would emit
    /// <c>tone-Expense</c>, which matches no CSS rule, leaving the figure the wrong colour with
    /// nothing failing. Expense is coral, Income mint — the finance semantics, never a brand hue.
    /// </summary>
    [Fact]
    public void Each_budget_direction_carries_its_own_finance_tone()
    {
        Assert.Equal(["expense", "income"], OdsTypeRegistries.BudgetCategoryDirections.Select(o => o.Tone));

        Assert.Equal("expense", OdsTypeRegistries.BudgetCategoryDirectionOf(BudgetCategoryType.Expense).Tone);
        Assert.Equal("income", OdsTypeRegistries.BudgetCategoryDirectionOf(BudgetCategoryType.Income).Tone);
        Assert.Equal("var(--finance-expense)", OdsTypeRegistries.BudgetCategoryDirectionOf(BudgetCategoryType.Expense).Color);
        Assert.Equal("var(--finance-income)", OdsTypeRegistries.BudgetCategoryDirectionOf(BudgetCategoryType.Income).Color);
    }

    /// <summary>Each side says what it MEANS, for the field's helper line — and the two differ.</summary>
    [Fact]
    public void Each_budget_direction_names_what_it_means()
    {
        var expense = OdsTypeRegistries.BudgetCategoryDirectionOf(BudgetCategoryType.Expense).Sentence;
        var income = OdsTypeRegistries.BudgetCategoryDirectionOf(BudgetCategoryType.Income).Sentence;

        Assert.Contains("out of the budget", expense, StringComparison.Ordinal);
        Assert.Contains("into the budget", income, StringComparison.Ordinal);
        Assert.NotEqual(expense, income);
    }

    /// <summary>
    /// The lead hands back a STRING, so the dialog parses it to get the enum it stores. Anything
    /// unrecognised resolves to <see cref="BudgetCategoryType.Expense"/> — the enum's zero member and
    /// the same fallback <c>BudgetCategoryTypeOf</c> documents — rather than throwing on a value from
    /// an older build.
    /// </summary>
    [Theory]
    [InlineData("Expense", BudgetCategoryType.Expense)]
    [InlineData("Income", BudgetCategoryType.Income)]
    [InlineData("Sideways", BudgetCategoryType.Expense)]
    [InlineData("99", BudgetCategoryType.Expense)]
    [InlineData("", BudgetCategoryType.Expense)]
    [InlineData(null, BudgetCategoryType.Expense)]
    public void An_unrecognised_budget_direction_resolves_to_the_default(string? value, BudgetCategoryType expected) =>
        Assert.Equal(expected, OdsTypeRegistries.BudgetCategoryTypeFrom(value));

    /// <summary>
    /// The round trip the dialog actually performs: the lead's value parses back to the enum whose
    /// direction produced it. A mismatch here would store the opposite direction from the one shown.
    /// </summary>
    [Fact]
    public void Every_budget_lead_value_round_trips_to_its_own_enum_member()
    {
        foreach (var value in Enum.GetValues<BudgetCategoryType>())
        {
            var option = OdsTypeRegistries.BudgetCategoryDirections.Single(o => o.Value == value.ToString());
            Assert.Equal(value, OdsTypeRegistries.BudgetCategoryTypeFrom(option.Value));
        }
    }

    // ── The object-role flag (issue #169) ────────────────────────────────────

    /// <summary>
    /// Exactly three party roles name what the agreement is ABOUT rather than a side of it. The set
    /// is pinned because both the tile's mark and the tile ORDER read it: a role that silently gained
    /// or lost the flag would re-sort the parties section with nothing else changing.
    /// </summary>
    [Fact]
    public void Exactly_the_three_object_roles_carry_the_object_flag()
    {
        Assert.Equal(
            [nameof(ContractPartyRole.Object), nameof(ContractPartyRole.Property), nameof(ContractPartyRole.Collateral)],
            OdsTypeRegistries.ContractPartyRoles.Where(r => r.IsObject).Select(r => r.Key).ToList());

        Assert.All(
            Enum.GetValues<ContractPartyRole>(),
            role => Assert.Equal(
                role is ContractPartyRole.Object or ContractPartyRole.Property or ContractPartyRole.Collateral,
                OdsTypeRegistries.IsObjectRole(role)));
    }

    /// <summary>
    /// A role this build cannot name is not an object role. The helper reads the registry, so an
    /// ordinal newer than this client has no entry and must answer <see langword="false"/> rather
    /// than throwing or being guessed into the group it would lead the section from.
    /// </summary>
    [Fact]
    public void An_unknown_role_ordinal_is_not_an_object_role() =>
        Assert.False(OdsTypeRegistries.IsObjectRole((ContractPartyRole)int.MaxValue));

    /// <summary>
    /// No OTHER registry sets the flag. It rides on the shared option type, so a copy-pasted row in an
    /// unrelated registry would set it silently — and nothing in that registry's own surface would
    /// show it.
    /// </summary>
    [Theory]
    [MemberData(nameof(RegistryEnumPairs))]
    public void Only_the_party_role_registry_flags_object_members(string registryName, Type enumType)
    {
        _ = enumType;
        if (registryName == nameof(OdsTypeRegistries.ContractPartyRoles))
        {
            return;
        }

        Assert.DoesNotContain(Registry(registryName), option => option.IsObject);
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
