using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Odyssey.Context;

namespace Odyssey.Api.Identity;

/// <summary>
/// <see cref="UserManager{TUser}"/> with one added responsibility: clearing
/// <see cref="ApplicationUser.MustChangePassword"/> when the user actually sets a new password (issue
/// #406 §5.3).
/// </summary>
/// <remarks>
/// <para>
/// The two paths that legitimately clear the flag — <c>ResetPasswordAsync</c> from the emailed link and
/// <c>ChangePasswordAsync</c> from the gate page — both live inside handlers this codebase does not own
/// (<c>MapIdentityApi</c>) or wants to keep thin. Both, however, go through virtual
/// <see cref="UserManager{TUser}"/> methods, so overriding the framework seam covers every caller
/// present and future, in the same spirit as the existing
/// <c>IUserConfirmation&lt;ApplicationUser&gt;</c> override.
/// </para>
/// <para>
/// <b>Rejected alternative:</b> inferring "the password changed" from a security-stamp comparison. 2FA
/// enrolment, an email change and external-login removal all rotate the stamp too, so the flag would
/// clear without a password ever being set.
/// </para>
/// </remarks>
public sealed class OdysseyUserManager : UserManager<ApplicationUser>
{
    public OdysseyUserManager(
        IUserStore<ApplicationUser> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<ApplicationUser> passwordHasher,
        IEnumerable<IUserValidator<ApplicationUser>> userValidators,
        IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<UserManager<ApplicationUser>> logger)
        : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
    }

    public override async Task<IdentityResult> ResetPasswordAsync(ApplicationUser user, string token, string newPassword)
    {
        var result = await base.ResetPasswordAsync(user, token, newPassword);
        if (result.Succeeded)
        {
            await ClearMustChangePasswordAsync(user);
        }

        return result;
    }

    public override async Task<IdentityResult> ChangePasswordAsync(
        ApplicationUser user, string currentPassword, string newPassword)
    {
        var result = await base.ChangePasswordAsync(user, currentPassword, newPassword);
        if (result.Succeeded)
        {
            await ClearMustChangePasswordAsync(user);
        }

        return result;
    }

    /// <summary>
    /// Persists the cleared flag. Only when it is actually set, so the overwhelmingly common case — an
    /// ordinary self-service password change — costs no extra write. A failure here is logged rather than
    /// thrown: the password has already been changed, so turning this into an error would report a
    /// completed rotation as a failure and invite the user to repeat it.
    /// </summary>
    private async Task ClearMustChangePasswordAsync(ApplicationUser user)
    {
        if (!user.MustChangePassword)
        {
            return;
        }

        user.MustChangePassword = false;

        var result = await UpdateAsync(user);
        if (!result.Succeeded)
        {
            Logger.LogError(
                "Password set for user {UserId}, but clearing MustChangePassword failed: {Errors}.",
                user.Id,
                string.Join(" ", result.Errors.Select(error => error.Description)));
        }
    }
}
