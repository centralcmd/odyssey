using Odyssey.Client.Auth;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Behaviour of the shared client-side password gate (issue #405). The source-lints in
/// <see cref="PasswordSurfaceSourceTests"/> check that every surface reads its rules from here and
/// that <see cref="PasswordPolicy.MinLength"/> still equals the server's <c>RequiredLength</c>; what
/// they cannot check is whether the rules actually evaluate correctly. They gate the submit button on
/// three pages, so a wrong boundary either blocks a valid password or waves an invalid one through to
/// a server rejection the user was told wouldn't happen.
/// </summary>
public class PasswordPolicyTests
{
    // A password that satisfies every rule, used as the base for the one-rule-short cases below.
    private const string Valid = "Odyssey!Demo1Pass";

    [Fact]
    public void TheRuleSet_IsTheFiveRulesInDisplayOrder()
    {
        var keys = PasswordPolicy.Rules(Valid).Select(rule => rule.Key);

        Assert.Equal(["len", "upper", "lower", "digit", "sym"], keys);
    }

    [Fact]
    public void TheLengthRule_NamesTheMinimumItEnforces()
    {
        // The label is generated from MinLength rather than written out, which is what stops the
        // /account page's old "At least 6 characters" defect from being reintroduced by hand.
        var length = Assert.Single(PasswordPolicy.Rules("x"), rule => rule.Key == "len");

        Assert.Equal($"At least {PasswordPolicy.MinLength} characters", length.Label);
    }

    // ── The length boundary ──────────────────────────────────────────────────

    [Fact]
    public void ExactlyTheMinimumLength_MeetsTheLengthRule()
    {
        Assert.True(IsMet("len", new string('a', PasswordPolicy.MinLength)));
    }

    [Fact]
    public void OneCharacterShortOfTheMinimum_FailsTheLengthRule()
    {
        Assert.False(IsMet("len", new string('a', PasswordPolicy.MinLength - 1)));
    }

    // ── One rule short ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("odyssey!demo1pass", "upper")]   // no uppercase
    [InlineData("ODYSSEY!DEMO1PASS", "lower")]   // no lowercase
    [InlineData("Odyssey!DemoPass", "digit")]    // no digit
    [InlineData("OdysseyDemo1Passw", "sym")]     // no symbol
    public void APasswordMissingASingleRule_FailsThatRuleAlone(string candidate, string expectedUnmet)
    {
        var unmet = PasswordPolicy.Rules(candidate).Where(rule => !rule.Met).Select(rule => rule.Key);

        Assert.Equal([expectedUnmet], unmet);
        Assert.False(PasswordPolicy.IsSatisfied(candidate));
    }

    [Fact]
    public void APasswordMeetingEveryRule_IsSatisfied()
    {
        Assert.All(PasswordPolicy.Rules(Valid), rule => Assert.True(rule.Met));
        Assert.True(PasswordPolicy.IsSatisfied(Valid));
    }

    [Fact]
    public void FourOfFiveRulesMet_IsStillNotSatisfied()
    {
        // The submit button is driven by IsSatisfied, so "nearly there" must not open the gate.
        var candidate = new string('a', PasswordPolicy.MinLength) + "1!";

        Assert.Equal(4, PasswordPolicy.Rules(candidate).Count(rule => rule.Met));
        Assert.False(PasswordPolicy.IsSatisfied(candidate));
    }

    // ── Empty and null ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoCandidateAtAll_MeetsNothingAndDoesNotThrow(string? candidate)
    {
        // Null is the state the pages start in before the field is bound, and it reaches Rules() on
        // the very first render — an unguarded implementation would throw during page load.
        var rules = PasswordPolicy.Rules(candidate);

        Assert.All(rules, rule => Assert.False(rule.Met));
        Assert.False(PasswordPolicy.IsSatisfied(candidate));
    }

    [Fact]
    public void EveryRule_CarriesALabelForTheChecklistToRender()
    {
        Assert.All(
            PasswordPolicy.Rules(null),
            rule =>
            {
                Assert.False(string.IsNullOrWhiteSpace(rule.Key));
                Assert.False(string.IsNullOrWhiteSpace(rule.Label));
            });
    }

    // ── Characters the rules have to classify correctly ──────────────────────

    [Theory]
    [InlineData(" ")]   // whitespace is not a letter or digit, so it counts as a symbol
    [InlineData("_")]
    [InlineData("é")]   // a letter, not a symbol — IsLetterOrDigit is Unicode-aware
    public void SymbolClassification_FollowsIsLetterOrDigit(string character)
    {
        var expected = !char.IsLetterOrDigit(character[0]);

        Assert.Equal(expected, IsMet("sym", new string('a', PasswordPolicy.MinLength) + character));
    }

    private static bool IsMet(string key, string? candidate) =>
        PasswordPolicy.Rules(candidate).Single(rule => rule.Key == key).Met;
}
