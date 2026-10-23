using Odyssey.Dtos;

namespace Odyssey.Api;

/// <summary>
/// The startup assertion behind location time zones (issue #350 §11, N3/N10). The alpine runtime image
/// ships no time-zone database unless <c>tzdata</c> is installed, and without one every IANA id fails to
/// resolve — so every location write would be a <c>400</c> on <c>timeZoneId</c> and every geocoding
/// candidate would come back with no zone. Like <see cref="GlobalizationGuard"/>, the failure is invisible
/// on a developer machine and in CI, so a loud refusal to start is the only signal that reaches the
/// container where it happens.
/// </summary>
/// <remarks>
/// A separate guard rather than an extension of <see cref="GlobalizationGuard"/>, whose doc, message and
/// tests are ICU-specific. It checks canary ids through the same <see cref="IanaTimeZones.IsValid"/> the
/// write path uses, rather than enumerating the host's zones, which is host-dependent.
/// </remarks>
internal static class TimeZoneDataGuard
{
    internal static readonly string[] Canaries = ["Europe/Oslo", "Europe/Kyiv", "America/New_York", "Etc/UTC"];

    public static void EnsureTimeZoneDataAvailable() => EnsureTimeZoneDataAvailable(IanaTimeZones.IsValid);

    internal static void EnsureTimeZoneDataAvailable(Func<string, bool> resolves)
    {
        var missing = Canaries.Where(id => !resolves(id)).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The time-zone database is unavailable: {string.Join(", ", missing)} did not resolve. Install the "
            + "tzdata package in the runtime image (the API Dockerfile's apk add line) — without it every location "
            + "write is rejected on its time zone.");
    }
}
