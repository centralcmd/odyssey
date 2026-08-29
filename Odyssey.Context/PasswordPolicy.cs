using Microsoft.AspNetCore.Identity;

namespace Odyssey.Context;

/// <summary>
/// The account password policy for a financial-PII app: the ASP.NET Identity defaults (min length 6,
/// no character-class requirements) raised to a strong baseline — at least 16 characters spanning all
/// four classes.
/// </summary>
/// <remarks>
/// One definition, applied by both <c>Odyssey.Api</c> (the authoritative gate on <c>/register</c> and
/// the change/reset endpoints) and <c>Odyssey.MigrationService</c> (which validates the configured
/// bootstrap administrator's password through <c>UserManager.CreateAsync</c>, issue #290). Splitting it
/// would let the migrations job seed a password the API would refuse, or refuse one it would accept.
/// The client register page mirrors these rules for inline feedback only.
/// </remarks>
public static class PasswordPolicy
{
    public const int RequiredLength = 16;

    public static void Apply(PasswordOptions options)
    {
        options.RequiredLength = RequiredLength;
        options.RequireLowercase = true;
        options.RequireUppercase = true;
        options.RequireDigit = true;
        options.RequireNonAlphanumeric = true;
    }
}
