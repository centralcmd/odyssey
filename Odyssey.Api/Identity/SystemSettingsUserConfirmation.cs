using Microsoft.AspNetCore.Identity;
using Odyssey.Context;
using Odyssey.Dtos;

namespace Odyssey.Api.Identity;

/// <summary>
/// The sign-in-side half of the <c>EmailRequireConfirmation</c> perimeter field (issue #349 §5).
/// <c>SignInManager&lt;TUser&gt;</c> resolves <see cref="IUserConfirmation{TUser}"/> fresh per
/// instance and only consults it when <c>IdentityOptions.SignIn.RequireConfirmedAccount</c> is
/// <see langword="true"/> — which <c>Program.cs</c> now pins unconditionally so this seam is always
/// the live decision point in both directions (on <em>and</em> off), rather than a toggle that only
/// ever tightens.
///
/// When the setting is off, every account reads as confirmed for sign-in purposes regardless of its
/// real <see cref="ApplicationUser.EmailConfirmed"/> flag; when it's on, the real flag is authoritative.
/// This is a live read (no cache) so a toggle takes effect on the very next sign-in attempt.
/// </summary>
public sealed class SystemSettingsUserConfirmation(OdysseyContext context) : IUserConfirmation<ApplicationUser>
{
    public async Task<bool> IsConfirmedAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        var requireConfirmation = await SystemSettingsReader.GetBoolAsync(
            context, SystemSettingsKeys.EmailRequireConfirmation, SystemSettingsDefaults.EmailRequireConfirmation);

        return !requireConfirmation || user.EmailConfirmed;
    }
}
