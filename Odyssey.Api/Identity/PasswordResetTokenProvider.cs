using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Odyssey.Api.Identity;

/// <summary>
/// Options for the password-reset-only token provider (issue #405). Identity's shipped
/// <see cref="DataProtectionTokenProviderOptions"/> is consumed as a <em>non-named</em>
/// <c>IOptions&lt;T&gt;</c>, so configuring it directly retunes the shared <c>"Default"</c> provider —
/// which also issues email-confirmation tokens. Cutting the reset lifespan that way would silently cut
/// confirmation links from a day to an hour too, which onboarding depends on.
/// <para>
/// Deriving an options type and a provider type gives password reset its own
/// <see cref="DataProtectionTokenProviderOptions.Name"/> (and therefore its own data-protection
/// purpose) and its own lifespan, leaving the default provider untouched.
/// </para>
/// </summary>
public sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    /// <summary>The <c>IdentityOptions.Tokens.ProviderMap</c> key this provider is registered under.</summary>
    public const string ProviderName = "PasswordReset";

    public PasswordResetTokenProviderOptions()
    {
        Name = ProviderName;
        TokenLifespan = TimeSpan.FromHours(1);
    }
}

/// <summary>
/// A <see cref="DataProtectorTokenProvider{TUser}"/> bound to
/// <see cref="PasswordResetTokenProviderOptions"/> instead of the shared default, so
/// <c>IdentityOptions.Tokens.PasswordResetTokenProvider</c> can point at a provider with a
/// short lifespan of its own.
/// </summary>
public sealed class PasswordResetTokenProvider<TUser>(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<PasswordResetTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<TUser>> logger)
    : DataProtectorTokenProvider<TUser>(dataProtectionProvider, options, logger)
    where TUser : class;
