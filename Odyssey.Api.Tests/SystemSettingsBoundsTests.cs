using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Odyssey.Api.SystemSettings;
using Odyssey.Dtos.Application;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// AC 22(a) and (b) — the guard that ties a key's bound pair to every consumer of it (issue #437
/// Goal 4). <strong>Bidirectional and two-ended</strong>, which the earlier shape was not: asserting
/// only the maximum, only from the attribute's side, passes on the very omission it exists to catch —
/// a key that adds the constants and the <c>[Range]</c> but forgets the descriptor, and therefore has
/// no read-path bound at all.
///
/// <para>
/// The fourth consumer, the client catalogue's <c>Min</c>/<c>Max</c>, is guarded from
/// <c>Odyssey.Client.Tests</c>, which is the only project that can see the catalogue.
/// </para>
/// </summary>
public class SystemSettingsBoundsTests
{
    private static IReadOnlyList<PropertyInfo> IntProperties =>
        typeof(SystemSettingsUpdate)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(int?))
            .ToList();

    private static int Constant(string name)
    {
        var field = typeof(SystemSettingsBounds).GetField(name, BindingFlags.Public | BindingFlags.Static);
        Assert.True(field is not null, $"SystemSettingsBounds.{name} does not exist.");
        return (int)field!.GetRawConstantValue()!;
    }

    /// <summary>
    /// (a) Every <c>int?</c> write field has a <c>SystemSettingsBounds</c> pair, and the pair IS its
    /// <c>[Range]</c>. Both ends, so a bound that moves on one side and not the other fails.
    /// </summary>
    [Fact]
    public void Every_int_field_has_a_bound_pair_equal_to_its_range()
    {
        Assert.NotEmpty(IntProperties);

        foreach (var property in IntProperties)
        {
            var range = property.GetCustomAttribute<RangeAttribute>();
            Assert.True(range is not null, $"{property.Name} carries no [Range].");

            Assert.Equal(range!.Minimum, Constant(property.Name + "Min"));
            Assert.Equal(range.Maximum, Constant(property.Name + "Max"));
        }
    }

    /// <summary>
    /// (b) The half that catches the real omission: <strong>every <c>IntSetting</c> descriptor declares
    /// the pair</strong>, and it is the same pair. Without this, a new key can carry the constants and
    /// the attribute while its descriptor silently gets no read-path bound.
    ///
    /// <para>
    /// The descriptor's <c>Min</c>/<c>Max</c> are <c>required</c>, so "declares a pair" is also a
    /// compile error — but that only proves a number is present, not that it is the right one.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_int_descriptor_declares_the_same_bound_pair()
    {
        var intSettings = SystemSettingsRegistry.All.OfType<IntSetting>().ToList();

        // Every int? write field is backed by an IntSetting, and vice versa.
        Assert.Equal(
            IntProperties.Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal),
            intSettings.Select(setting => setting.FieldName).OrderBy(name => name, StringComparer.Ordinal));

        foreach (var setting in intSettings)
        {
            Assert.Equal(Constant(setting.FieldName + "Min"), setting.Min);
            Assert.Equal(Constant(setting.FieldName + "Max"), setting.Max);
            Assert.True(setting.Max > setting.Min,
                $"{setting.Key}: a pair with Max <= Min bounds every value to one number.");
        }
    }

    /// <summary>
    /// The census, so a wrong denominator cannot scope a future fix to a subset of its own defect class.
    /// 38 int keys before issue #437, 41 after, 42 once issue #8 added the SMTP port, 43 once issue #27
    /// added the insurance link cap, 44 once issue #135 added the per-contract term cap, 47 once the
    /// Contracts summary gained its two windows and its next-charge row cap, 48 once issue #166 added
    /// the per-contract smart-tag cap, and 45 once the standalone subscriptions feature was removed
    /// and took its three summary limits with it.
    /// </summary>
    [Fact]
    public void The_int_key_census_is_forty_five()
    {
        Assert.Equal(45, SystemSettingsRegistry.All.OfType<IntSetting>().Count());
        Assert.Equal(45, IntProperties.Count);

        // …and the whole registry equals the persisted key catalogue, which is the check that the
        // per-kind counts are right rather than merely consistent with each other. Issue #8 added four:
        // one int (the SMTP port), one bool (STARTTLS) and two strings (the host and the link origin).
        // Issue #27 added one more int, InsuranceMaxLinksPerPolicy; issue #135 another,
        // ContractMaxTermsPerContract; the Contracts summary windows added three more; issue #166 the
        // per-contract smart-tag cap. Removing the subscriptions feature took three int keys back out.
        Assert.Equal(69, SystemSettingsRegistry.All.Count);
        Assert.Equal(5, SystemSettingsRegistry.All.OfType<BoolSetting>().Count());
        Assert.Equal(8, SystemSettingsRegistry.All.OfType<CapacitySetting>().Count());
        Assert.Equal(10, SystemSettingsRegistry.All.OfType<StringSetting>().Count());
        Assert.Single(SystemSettingsRegistry.All.OfType<DecimalSetting>());
    }

    /// <summary>
    /// Three pairs must ALIAS <see cref="SystemSettingsDefaults"/> rather than restate its literal:
    /// that end is the pinned end of a single-direction key, and a second literal could drift from the
    /// seed. Asserted by value here; the <c>[Range]</c> half of the same pin is asserted by NAME in
    /// <c>TuningSystemSettingsApiTests</c>, and those attributes must not be rewritten to name the
    /// bounds constants instead — that source assertion is the other half of the pin.
    /// </summary>
    [Fact]
    public void The_three_single_direction_ends_are_the_shipped_defaults()
    {
        Assert.Equal(
            SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences,
            SystemSettingsBounds.RecurrenceMaxGeneratedOccurrencesMax);
        Assert.Equal(
            SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry,
            SystemSettingsBounds.ContactVCardMaxRepeatablePropertiesPerEntryMax);
        Assert.Equal(
            SystemSettingsDefaults.EmailMaxTrackedRecipients,
            SystemSettingsBounds.EmailMaxTrackedRecipientsMin);
    }

    /// <summary>
    /// The alias is asserted by NAME too, in the source: a literal that happens to equal the constant
    /// today satisfies the value assertion above and would drift the moment the seed moved.
    ///
    /// <para>
    /// The fourth case is a different kind of pin and belongs on the same assertion (issue #166 §8.3,
    /// AC 21): <c>ContractMaxSmartTagsPerContractMax</c> mirrors <c>ListDefaults.MaxFilterArrayLength</c>
    /// because a contract's smart tags are resolved through <c>GET /api/transactions?tagIds=…</c>,
    /// whose filter carries that <c>[MaxLength]</c> — so a value above it would configure smart tags
    /// the feature's own resolution query rejects with a <c>400</c>. It is pinned by the constant, not
    /// by the number 50, so the two move together if the filter length ever changes.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("RecurrenceMaxGeneratedOccurrencesMax", "SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences")]
    [InlineData("ContactVCardMaxRepeatablePropertiesPerEntryMax", "SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry")]
    [InlineData("EmailMaxTrackedRecipientsMin", "SystemSettingsDefaults.EmailMaxTrackedRecipients")]
    [InlineData("ContractMaxSmartTagsPerContractMax", "ListDefaults.MaxFilterArrayLength")]
    public void The_ends_that_alias_a_shared_constant_name_it_in_source(string constant, string expression)
    {
        var source = File.ReadAllText(
            SolutionFile("Odyssey.Dtos", "SystemSettings", "SystemSettingsBounds.cs"));

        var declaration = System.Text.RegularExpressions.Regex.Match(
            source, @$"public const int {constant}\s*=\s*(?<value>[^;]+);");

        Assert.True(declaration.Success, $"No declaration found for {constant}.");
        Assert.Contains(expression, declaration.Groups["value"].Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #168 — the per-account smart-tag ceiling IS the filter-array cap, joined at all three
    /// ends that have to agree: the bound pair, the constant it names, and the <c>[MaxLength]</c> on
    /// the filter the section actually resolves its tags through.
    ///
    /// <para>
    /// Asserting only the first two would pass if <c>TagIds</c> grew its own literal, which is the
    /// same drift in the other direction: the cap the administrator sets would once again exceed what
    /// one query can carry, and the smart-tag panel would once again 400 past it.
    /// </para>
    ///
    /// <para>
    /// The per-CONTRACT ceiling (issue #166) is asserted here too rather than in a parallel test: the
    /// two keys are bounded by the same filter for the same reason, so one assertion is what keeps
    /// them from being fixed one at a time — which is how the account key spent a release at 1000.
    /// </para>
    /// </summary>
    [Fact]
    public void The_smart_tag_ceiling_is_the_cap_on_the_filter_it_is_resolved_through()
    {
        Assert.Equal(
            ListDefaults.MaxFilterArrayLength,
            SystemSettingsBounds.AccountMaxSmartTagsPerAccountMax);
        Assert.Equal(
            ListDefaults.MaxFilterArrayLength,
            SystemSettingsBounds.ContractMaxSmartTagsPerContractMax);

        var tagIds = typeof(Odyssey.Dtos.Finance.TransactionsQueryParams)
            .GetProperty(nameof(Odyssey.Dtos.Finance.TransactionsQueryParams.TagIds))!;

        var maxLength = tagIds.GetCustomAttribute<MaxLengthAttribute>();
        Assert.True(maxLength is not null,
            "TransactionsQueryParams.TagIds carries no [MaxLength] — the smart-tag ceiling has nothing "
            + "to be bounded by.");
        Assert.Equal(SystemSettingsBounds.AccountMaxSmartTagsPerAccountMax, maxLength!.Length);
        Assert.Equal(SystemSettingsBounds.ContractMaxSmartTagsPerContractMax, maxLength.Length);
    }

    /// <summary>
    /// The same pin by NAME, for the reason the three single-direction ends carry one: a literal that
    /// happens to equal 50 today satisfies the value assertion above and drifts the moment
    /// <see cref="ListDefaults.MaxFilterArrayLength"/> moves — which is exactly how this key came to
    /// accept 1000 against a filter that carries 50.
    /// </summary>
    [Fact]
    public void The_smart_tag_ceiling_names_the_filter_array_constant()
    {
        var source = File.ReadAllText(
            SolutionFile("Odyssey.Dtos", "SystemSettings", "SystemSettingsBounds.cs"));

        var declaration = System.Text.RegularExpressions.Regex.Match(
            source, @"public const int AccountMaxSmartTagsPerAccountMax\s*=\s*(?<value>[^;]+);");

        Assert.True(declaration.Success, "No declaration found for AccountMaxSmartTagsPerAccountMax.");
        Assert.Contains(
            "ListDefaults.MaxFilterArrayLength",
            declaration.Groups["value"].Value,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every descriptor's shipped default lies inside its own pair. A default outside its bound would
    /// be silently clamped on every single read — including on an absent row, which is the one path
    /// that is supposed to be exactly the documented value.
    /// </summary>
    [Fact]
    public void Every_int_default_lies_inside_its_own_pair()
    {
        foreach (var setting in SystemSettingsRegistry.All.OfType<IntSetting>())
        {
            var value = int.Parse(setting.DefaultValue, System.Globalization.CultureInfo.InvariantCulture);

            Assert.InRange(value, setting.Min, setting.Max);
        }
    }

    private static string SolutionFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir, "Odyssey.sln")))
        {
            dir = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }

        Assert.NotNull(dir);
        return System.IO.Path.Combine([dir!, .. parts]);
    }
}
