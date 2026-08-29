using Microsoft.AspNetCore.Identity;

namespace Odyssey.Context;

public class ApplicationUser : IdentityUser
{
    /// <summary>
    /// Set by an admin-initiated password reset (issue #406); cleared by a successful
    /// <c>ResetPasswordAsync</c> or <c>ChangePasswordAsync</c> (see <c>OdysseyUserManager</c>).
    /// <para>
    /// Generating a reset token does not invalidate the current password — that only happens when the
    /// reset completes — so immediately after an admin triggers a reset the old (possibly compromised)
    /// password still authenticates. While this flag is set, <c>PasswordChangeRequiredMiddleware</c>
    /// refuses every authenticated endpoint except the handful needed to escape the state.
    /// </para>
    /// </summary>
    public bool MustChangePassword { get; set; }
}
