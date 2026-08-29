namespace Odyssey.Client.Auth;

/// <summary>
/// The single client-side mirror of the server's password gate (issue #405). Every surface that
/// shows or checks password requirements — <c>Register</c>, <c>/account</c>'s change-password
/// section, and <c>/reset-password</c> — reads the rules from here and renders them through
/// <c>OdsPasswordRules</c>, so the displayed rules cannot drift from one another. Before this
/// existed they already had: the account page advertised "at least 6 characters" (Identity's
/// default, not this app's policy) while registration required 16.
/// </summary>
/// <remarks>
/// <c>IdentityOptions.Password</c> in <c>Odyssey.Api/Program.cs</c> remains the authoritative gate;
/// this is a UX aid that lets the user see why a password is rejected before submitting.
/// <c>PasswordSurfaceSourceTests.TheClientMinimumLength_EqualsTheServersRequiredLength</c> parses both
/// files and fails the build if the two disagree; <c>PasswordPolicyTests</c> covers the rules themselves.
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>Minimum length. Kept equal to <c>IdentityOptions.Password.RequiredLength</c>.</summary>
    public const int MinLength = 16;

    /// <summary>The rule set evaluated against a candidate password, in display order.</summary>
    public static IReadOnlyList<PasswordRule> Rules(string? candidate)
    {
        var value = candidate ?? string.Empty;
        return
        [
            new("len", $"At least {MinLength} characters", value.Length >= MinLength),
            new("upper", "An uppercase letter", value.Any(char.IsUpper)),
            new("lower", "A lowercase letter", value.Any(char.IsLower)),
            new("digit", "A number", value.Any(char.IsDigit)),
            new("sym", "A symbol (!@#$…)", value.Any(character => !char.IsLetterOrDigit(character))),
        ];
    }

    /// <summary>
    /// True when every rule is met. Hosts drive their submit button's disabled state from this — the
    /// same source that renders the ticks, so the button and the checklist can never disagree.
    /// </summary>
    public static bool IsSatisfied(string? candidate) => Rules(candidate).All(rule => rule.Met);
}

/// <summary>One password requirement and whether the candidate currently satisfies it.</summary>
/// <param name="Key">Stable key for list rendering.</param>
/// <param name="Label">Human-readable requirement, e.g. "At least 16 characters".</param>
/// <param name="Met">Whether the candidate satisfies this rule.</param>
public sealed record PasswordRule(string Key, string Label, bool Met);
