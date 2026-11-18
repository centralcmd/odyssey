namespace Odyssey.Dtos.Journal;

/// <summary>
/// The body of an occurrence <c>DELETE</c> that keyed a legacy series first (issue #519 v5 §3). The ordinary delete
/// answers <c>204</c> with no body; this is returned with <c>200</c> only when keying detached rows.
/// </summary>
public sealed record CalendarEventDeleteResult
{
    /// <summary>The deleted row had become a standalone event while its series was keyed, and was deleted on its own.</summary>
    public bool Detached { get; set; }

    /// <summary>Rows keying detached to standalone events.</summary>
    public int DetachedCount { get; set; }
}
