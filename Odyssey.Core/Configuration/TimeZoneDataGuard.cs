using Odyssey.Dtos;

namespace Odyssey.Core.Configuration;

/// <summary>
/// The startup assertion behind location time zones (issue #350 §11, N3/N10). The alpine runtime image
/// ships no time-zone database unless <c>tzdata</c> is installed, and without one every IANA id fails to
/// resolve — so every location write would be a <c>400</c> on <c>timeZoneId</c> and every geocoding
/// candidate would come back with no zone. Like the API's <c>GlobalizationGuard</c>, the failure is invisible
/// on a developer machine and in CI, so a loud refusal to start is the only signal that reaches the
/// container where it happens.
/// </summary>
/// <remarks>
/// A separate guard rather than an extension of <c>GlobalizationGuard</c>, whose doc, message and
/// tests are ICU-specific. It checks canary ids through the same <see cref="IanaTimeZones.IsValid"/> the
/// write path uses, rather than enumerating the host's zones, which is host-dependent.
/// <para>
/// Shared by every host that resolves a zone: the API (location writes, geocoding) and <c>Odyssey.Worker</c>
/// (the weather job's site-local days), so each image's <c>apk add tzdata</c> is asserted where it ships.
/// </para>
/// </remarks>
public static class TimeZoneDataGuard
{
    public static readonly IReadOnlyList<string> Canaries = ["Europe/Oslo", "Europe/Kyiv", "America/New_York", "Etc/UTC"];

    public static void EnsureTimeZoneDataAvailable() => EnsureTimeZoneDataAvailable(IanaTimeZones.IsValid);

    public static void EnsureTimeZoneDataAvailable(Func<string, bool> resolves)
    {
        var missing = Canaries.Where(id => !resolves(id)).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The time-zone database is unavailable: {string.Join(", ", missing)} did not resolve. Install the "
            + "tzdata package in the runtime image (the apk add line in the API and worker Dockerfiles) — without it "
            + "every location write is rejected on its time zone and no weather day can be composed.");
    }
}
