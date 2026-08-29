namespace Odyssey.Core.Finance;

/// <summary>
/// Normalizes externally-supplied <see cref="DateTime"/> values to UTC at the service boundary.
/// Client payloads may arrive with <see cref="DateTimeKind.Local"/> or
/// <see cref="DateTimeKind.Unspecified"/>; persisting them unconverted into the UTC
/// <c>datetime(6)</c> columns corrupts range filters and effective-dated resolution by up to a
/// timezone offset. Every service that writes a client-supplied date funnels through here.
/// </summary>
public static class DateTimeNormalization
{
    public static DateTime NormalizeToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
