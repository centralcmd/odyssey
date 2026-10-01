namespace Odyssey.Dtos.Journal;

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

[Flags]
public enum DaysOfWeekFlags
{
    None = 0,
    Monday = 1 << 0,
    Tuesday = 1 << 1,
    Wednesday = 1 << 2,
    Thursday = 1 << 3,
    Friday = 1 << 4,
    Saturday = 1 << 5,
    Sunday = 1 << 6,
}

public static class DaysOfWeekFlagsExtensions
{
    /// <summary>Every defined day bit; any bit outside this mask names no weekday.</summary>
    public const DaysOfWeekFlags AllDays = DaysOfWeekFlags.Monday | DaysOfWeekFlags.Tuesday
        | DaysOfWeekFlags.Wednesday | DaysOfWeekFlags.Thursday | DaysOfWeekFlags.Friday
        | DaysOfWeekFlags.Saturday | DaysOfWeekFlags.Sunday;

    public static bool HasOnlyDefinedDays(this DaysOfWeekFlags flags) => (flags & ~AllDays) == 0;
}
