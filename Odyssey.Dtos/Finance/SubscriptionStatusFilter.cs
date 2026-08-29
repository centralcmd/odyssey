namespace Odyssey.Dtos.Finance;

/// <summary>
/// The single derived lifecycle status a subscription reports, used as the list-filter vocabulary
/// (issue #293). Computed per request with a fixed precedence — <c>Archived</c> wins, then
/// <c>Ended</c> (its <c>EndDate</c> has lapsed on/before today), then <c>Paused</c>, otherwise
/// <c>Active</c>. Only <c>Paused</c>/<c>Archived</c> are stored flags; <c>Ended</c> and <c>Active</c>
/// are derived. Mirrors the design system's status filter and "By status" breakdown.
/// </summary>
public enum SubscriptionStatusFilter
{
    Active = 0,
    Paused = 1,
    Ended = 2,
    Archived = 3,
}
