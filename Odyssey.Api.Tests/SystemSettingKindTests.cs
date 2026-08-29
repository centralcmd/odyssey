using System.Globalization;
using Odyssey.Api.SystemSettings;
using Odyssey.Dtos.Application;
using Odyssey.Core;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The two value kinds issue #421 Wave 0b adds — <c>StringSetting</c> and <c>DecimalSetting</c>.
///
/// <para>
/// Neither has a registry entry yet: the first string settings are the AI-analysis processor
/// disclosure and the first decimal is the auto-link threshold, all in Wave 1. So these exercise the
/// kinds directly, which is also the only way to cover the branches a real entry would not reach (a
/// per-descriptor <c>Validator</c>, an over-length value) until then.
/// </para>
/// </summary>
public class SystemSettingKindTests
{
    private static StringSetting Text(int maxLength = 128, Func<string, string?>? validator = null) => new()
    {
        Key = "TestString",
        FieldName = nameof(SystemSettingsUpdate.RequireTwoFactor), // any real name; only its text is used
        RequiredClaim = PermissionClaims.SystemSettingsUpdate,
        DefaultValue = "seed",
        MaxLength = maxLength,
        Validator = validator,
        Read = _ => Captured,
        Write = (_, _) => { },
    };

    private static DecimalSetting Number(decimal seed = 0.60m) => new()
    {
        Key = "TestDecimal",
        FieldName = nameof(SystemSettingsUpdate.InsuranceExpiringSoonWindowDays),
        RequiredClaim = PermissionClaims.SystemSettingsUpdate,
        DefaultValue = seed.ToString(DecimalSetting.StorageFormat, CultureInfo.InvariantCulture),
        Read = _ => CapturedDecimal,
        Write = (_, _) => { },
    };

    // The kinds read from the request object; these stand in for the property a real entry would name.
    /// <summary>
    /// The runtime ceilings every descriptor's <c>Validate</c> now receives (issue #421 Wave 4). None
    /// of the string/decimal kinds exercised here consults it, but it is a required argument, so the
    /// tests build one from the shipped file-storage defaults.
    /// </summary>
    private static RequestCapCeilings Ceilings =>
        new(Microsoft.Extensions.Options.Options.Create(new Odyssey.Core.Finance.FileStorageOptions()));

    private static string? Captured;
    private static decimal? CapturedDecimal;

    private static DomainValidationException AssertRejects(StringSetting setting, string value)
    {
        Captured = value;
        return Assert.Throws<DomainValidationException>(() => setting.Validate(new SystemSettingsUpdate(), Ceilings));
    }

    // ── StringSetting ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_string_setting_rejects_an_empty_or_whitespace_value(string value)
    {
        var exception = AssertRejects(Text(), value);

        Assert.Contains("must not be empty", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("carriage\rreturn")]
    [InlineData("null\0byte")]
    public void A_string_setting_rejects_control_characters(string value)
    {
        // CR/LF in a value that reaches a mail header is injection; the rest have no legitimate use in
        // a single-line setting. Rejected for every string kind rather than only the risky ones.
        var exception = AssertRejects(Text(), value);

        Assert.Contains("control characters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_string_setting_rejects_a_value_over_its_length_bound()
    {
        var exception = AssertRejects(Text(maxLength: 8), new string('x', 9));

        Assert.Contains("8 characters or fewer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_string_settings_length_bound_applies_after_trimming()
    {
        // "        x        " is 17 characters raw but 1 trimmed, so it must pass an 8-char bound —
        // otherwise leading whitespace could reject a legitimate value.
        Captured = "        x        ";
        var setting = Text(maxLength: 8);

        Assert.Null(Record.Exception(() => setting.Validate(new SystemSettingsUpdate(), Ceilings)));
    }

    [Fact]
    public void A_string_setting_stores_its_value_trimmed()
    {
        // Without this, " x" and "x" are distinct stored values and a GET-then-PUT of unchanged data
        // stops being a no-op — a property ImportExportSettingsApiTests asserts for every field.
        Captured = "  https://example.test/policy  ";

        Assert.Equal("https://example.test/policy", Text().Format(new SystemSettingsUpdate()));
    }

    [Fact]
    public void A_string_settings_own_validator_runs_last_and_sees_the_trimmed_value()
    {
        string? seen = null;
        var setting = Text(validator: value => { seen = value; return "not acceptable"; });

        var exception = AssertRejects(setting, "  padded  ");

        Assert.Equal("padded", seen);
        Assert.Contains("not acceptable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_string_setting_names_its_field_in_the_errors_dictionary()
    {
        // This is what lets the page render the message on the offending row instead of only in a
        // toast: GlobalExceptionHandler copies Errors into the problem-details `errors` extension.
        var setting = Text();
        var exception = AssertRejects(setting, "");

        Assert.NotNull(exception.Errors);
        Assert.Contains(setting.FieldName, exception.Errors!.Keys);
        Assert.Equal(exception.Message, exception.Errors[setting.FieldName][0]);
    }

    // ── DecimalSetting ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_decimal_setting_round_trips_through_the_read_dto()
    {
        CapturedDecimal = 0.6m;
        var setting = Number();
        var stored = setting.Format(new SystemSettingsUpdate());

        Assert.Equal("0.6", stored);
        Assert.Equal(0.6m, decimal.Parse(stored, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// AC 11. A bare <c>ToString()</c> under a comma-decimal culture writes <c>0,6</c>, which then
    /// throws on read — so this is a correctness requirement, not a style preference. Both directions
    /// pin invariant culture; this asserts it by running the whole round trip under de-DE.
    /// </summary>
    [Fact]
    public void A_decimal_setting_round_trips_unchanged_under_a_comma_decimal_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            CapturedDecimal = 0.6m;
            var setting = Number();
            var stored = setting.Format(new SystemSettingsUpdate());

            Assert.Equal("0.6", stored);
            Assert.DoesNotContain(",", stored, StringComparison.Ordinal);

            // The OUTCOME, not the absence of an exception (issue #437 AC 34). Project became
            // non-throwing, and a value-returning lambda still binds to Record.Exception's
            // Func<object?> overload via boxing — so the old assertion kept COMPILING while it could
            // never fail again. Green and wrong, which is the more dangerous of the two failure modes.
            var dto = new SystemSettingsDto();
            Assert.Equal(ProjectionOutcome.Ok, setting.Project(stored, dto));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_decimal_setting_stores_a_canonical_form()
    {
        // 0.60 and 0.6 must not become two distinct stored values, or the GET-then-PUT no-op breaks
        // for a field nobody edited.
        CapturedDecimal = 0.60m;

        Assert.Equal("0.6", Number().Format(new SystemSettingsUpdate()));
    }

    [Fact]
    public void A_present_value_is_decided_by_the_object_for_both_new_kinds()
    {
        // The same rule the other kinds follow: null means "leave unchanged", and presence is never
        // inferred from the value itself.
        Captured = null;
        CapturedDecimal = null;

        Assert.False(Text().IsPresent(new SystemSettingsUpdate()));
        Assert.False(Number().IsPresent(new SystemSettingsUpdate()));

        Captured = "x";
        CapturedDecimal = 0m; // zero is a VALUE, not an absence

        Assert.True(Text().IsPresent(new SystemSettingsUpdate()));
        Assert.True(Number().IsPresent(new SystemSettingsUpdate()));
    }
}
