using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// Live single-key reads over <see cref="OdysseyContext.SystemSettings"/> — never cached, for the
/// settings where a stale value would be a security gap rather than a cosmetic one.
///
/// <para>
/// Used by the authentication-perimeter fields (issue #349): registration/sign-in volume is far below
/// the threshold the Insurance-field cache exists to protect, and a stale read there would let a
/// disabled gate keep admitting users. Extended by issue #421 Wave 2 to the transactional-email
/// settings, for the same reason in a different shape — the per-recipient throttle is a security
/// control on the anonymous mail path, so lowering a limit under active abuse must bind on the very
/// next send rather than after a cache TTL.
/// </para>
///
/// <para>
/// Consumers are <see cref="OdysseyContext"/>'s own <c>ApplyNewUserDefaults</c> choke point and the
/// <c>Odyssey.Api</c> sites that already depend on this context directly (<c>SmtpEmailSender</c>, the
/// <c>IUserConfirmation&lt;ApplicationUser&gt;</c> sign-in seam) — they need no
/// <c>ISystemSettingsLookup</c> cross-domain indirection.
/// </para>
///
/// <para>
/// <strong>Every getter fails safe, including on a corrupt value.</strong> This is stricter than it
/// looks and it is deliberate: the previous version claimed to "fail closed if the row is ever
/// missing/unreadable" but only handled <em>missing</em> — <c>bool.Parse</c> on a corrupt row threw,
/// and because <c>SmtpEmailSender</c> catches everything from the throttle and sends anyway (a dropped
/// password reset is a lockout), a single malformed row would have turned the throttle from a limiter
/// into a no-op. Now an unparseable value logs nothing here but resolves to the caller's documented
/// default, so the control degrades instead of disappearing.
/// </para>
/// </summary>
public static class SystemSettingsReader
{
    public static bool GetBool(OdysseyContext context, string key, bool defaultValue) =>
        Parse(ReadValue(context, key), defaultValue, bool.TryParse);

    public static async Task<bool> GetBoolAsync(
        OdysseyContext context, string key, bool defaultValue, CancellationToken cancellationToken = default) =>
        Parse(await ReadValueAsync(context, key, cancellationToken), defaultValue, bool.TryParse);

    /// <summary>An invariant-culture integer, or <paramref name="defaultValue"/> if missing or unparseable.</summary>
    public static async Task<int> GetIntAsync(
        OdysseyContext context, string key, int defaultValue, CancellationToken cancellationToken = default) =>
        Parse(await ReadValueAsync(context, key, cancellationToken), defaultValue, TryParseInvariantInt);

    /// <summary>
    /// A non-blank string, or <paramref name="defaultValue"/>. Blank counts as absent: a string setting
    /// that reached the store empty is unusable, and the caller's default is the safer value.
    /// </summary>
    public static async Task<string> GetStringAsync(
        OdysseyContext context, string key, string defaultValue, CancellationToken cancellationToken = default)
    {
        var value = await ReadValueAsync(context, key, cancellationToken);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static string? ReadValue(OdysseyContext context, string key) =>
        context.SystemSettings.AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .FirstOrDefault();

    private static Task<string?> ReadValueAsync(
        OdysseyContext context, string key, CancellationToken cancellationToken) =>
        context.SystemSettings.AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

    private delegate bool TryParse<T>(string value, out T result);

    private static T Parse<T>(string? value, T defaultValue, TryParse<T> tryParse) =>
        value is not null && tryParse(value, out var parsed) ? parsed : defaultValue;

    private static bool TryParseInvariantInt(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
}
