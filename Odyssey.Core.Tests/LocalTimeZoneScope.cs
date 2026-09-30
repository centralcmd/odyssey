using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// <see cref="TimeZoneInfo.Local"/> is process-global, so a test that pins it must not run beside
/// anything else that converts a <see cref="DateTimeKind.Local"/> value.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class LocalTimeZoneCollection
{
    public const string Name = "local-time-zone";
}

/// <summary>
/// Pins <see cref="TimeZoneInfo.Local"/> to Asia/Tokyo (UTC+9 at every date the tests use) for the
/// lifetime of the scope, so a <see cref="DateTimeKind.Local"/> value converts by a known, non-zero offset rather
/// than by the machine's zone — under UTC every Local case is a no-op conversion and cannot tell a
/// correct normalization from a missing one. It relies on the Unix <c>TZ</c> lookup, so the test is
/// skipped where the pin does not take.
/// </summary>
internal sealed class LocalTimeZoneScope : IDisposable
{
    private static readonly TimeSpan TokyoOffset = TimeSpan.FromHours(9);

    private readonly string? previous;

    private LocalTimeZoneScope()
    {
        previous = Environment.GetEnvironmentVariable("TZ");
        Environment.SetEnvironmentVariable("TZ", "Asia/Tokyo");
        TimeZoneInfo.ClearCachedData();
    }

    public static LocalTimeZoneScope UseTokyo()
    {
        var scope = new LocalTimeZoneScope();
        // Probed at the dates the tests use, not via SupportsDaylightSavingTime: Tokyo's tz history
        // carries a 1948–1951 DST rule, which that flag reports even though no test date is near it.
        var pinned = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc)) == TokyoOffset
            && TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)) == TokyoOffset;
        if (!pinned)
        {
            scope.Dispose();
            Skip.If(true, "TimeZoneInfo.Local could not be pinned to Asia/Tokyo on this platform.");
        }
        return scope;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TZ", previous);
        TimeZoneInfo.ClearCachedData();
    }
}
