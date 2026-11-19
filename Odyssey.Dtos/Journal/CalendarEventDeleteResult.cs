namespace Odyssey.Dtos.Journal;

/// <summary>
/// The body of every successful <c>DELETE /api/calendar-events/{id}</c> (issue #519 v5 §3): what keying a legacy series
/// first detached — <c>false</c> and <c>0</c> on an ordinary delete, so the endpoint has one response shape.
/// </summary>
public sealed record CalendarEventDeleteResult
{
    /// <summary>The deleted row had become a standalone event while its series was keyed, and was deleted on its own.</summary>
    public bool Detached { get; set; }

    /// <summary>Rows keying detached to standalone events.</summary>
    public int DetachedCount { get; set; }
}
