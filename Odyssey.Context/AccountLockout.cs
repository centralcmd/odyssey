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

    /// <summary>
    /// Whether an account was disabled <b>by an administrator</b> — the sentinel, and only the
    /// sentinel (issue #94 §10.6).
    ///
    /// <para>
    /// <b>This is not <see cref="IsEnabled"/> inverted, and substituting one for the other is a
    /// security defect rather than a tidy-up.</b> Odyssey has real <i>transient</i> lockouts
    /// (<c>lockoutOnFailure: true</c>, five attempts / five minutes), and <c>IsEnabled</c> answers
    /// <i>sign-in eligibility</i>, which those affect. The profile-picture read path <c>404</c>s an
    /// administratively disabled subject precisely because that is indistinguishable from "they removed
    /// their picture" — a deliberate removal never reverts. A transient lockout reverts in about five
    /// minutes, so keying the same <c>404</c> off <c>IsEnabled</c> would let any
    /// <c>profile-images.read</c> holder, Guest included, poll a known id, observe
    /// <c>200 → 404 → 200</c>, and learn that account is under a failed-login lockout — a
    /// password-spray confirmation channel outside the login endpoint and its rate limiter.
    /// </para>
    ///
    /// <para>
    /// <b>For materialised entities only.</b> EF Core translates no arbitrary static method call, so
    /// calling this inside a <c>Where</c>/<c>Select</c> over <c>AspNetUsers</c> throws "The LINQ
    /// expression could not be translated" at runtime on the first request, having compiled clean. A
    /// query-side check compares against <see cref="DisabledLockoutEnd"/> inline — still naming this
    /// constant rather than a literal.
    /// </para>
    /// </summary>
    public static bool IsAdministrativelyDisabled(DateTimeOffset? lockoutEnd) =>
        lockoutEnd == DisabledLockoutEnd;
}
