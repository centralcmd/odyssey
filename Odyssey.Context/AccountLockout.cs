namespace Odyssey.Context;

/// <summary>
/// Shared helpers for the lockout-based "disabled account" convention. A user is treated as
/// disabled by setting <see cref="ApplicationUser.LockoutEnd"/> to <see cref="DisabledLockoutEnd"/>
/// (effectively permanent), and enabled by clearing it. The same convention backs both the admin
/// enable/disable action and the require-admin-approval registration gate.
/// </summary>
public static class AccountLockout
{
    /// <summary>
    /// Sentinel <c>LockoutEnd</c> marking an account disabled. This is the maximum a MySQL
    /// <c>datetime(6)</c> column can hold (<c>9999-12-31 23:59:59</c>), so it is effectively
    /// permanent — do not use <see cref="System.DateTimeOffset.MaxValue"/>, which overflows it.
    /// </summary>
    public static readonly DateTimeOffset DisabledLockoutEnd = new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);

    /// <summary>An account is enabled when it has no active lockout window.</summary>
    public static bool IsEnabled(DateTimeOffset? lockoutEnd, DateTimeOffset now) =>
        lockoutEnd is null || lockoutEnd <= now;
}
