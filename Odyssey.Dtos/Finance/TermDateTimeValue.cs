namespace Odyssey.Dtos.Finance;

/// <summary>
/// The range a <see cref="TermValueUnit.DateTime"/> term's value must lie in (issue #192 §8 rule 6),
/// shared by the server's write path and the client's dialog. Both ends are UTC instants.
/// </summary>
public static class TermDateTimeValue
{
    /// <summary>The earliest storable instant.</summary>
    public static readonly DateTime Min = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The latest storable instant.</summary>
    public static readonly DateTime Max = new(2200, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    /// <summary>Whether a UTC instant lies within <see cref="Min"/>…<see cref="Max"/>.</summary>
    public static bool IsInRange(DateTime utc) => utc >= Min && utc <= Max;
}
