namespace Odyssey.Dtos.Finance;

/// <summary>
/// Derived, never-stored coverage status of an insurance policy, computed at read time from its
/// renewals against the request's single UTC clock (see issue #175 §5).
/// </summary>
public enum CoverageStatus
{
    /// <summary>The policy has no renewals.</summary>
    NoCoverage = 0,

    /// <summary>A renewal's window contains today and the current renewal ends beyond the expiring-soon window.</summary>
    Active = 1,

    /// <summary>Covered today, but the current renewal ends within the expiring-soon window.</summary>
    ExpiringSoon = 2,

    /// <summary>No renewal contains today and the earliest renewal starts in the future.</summary>
    Upcoming = 3,

    /// <summary>No renewal contains today and the latest renewal ended in the past.</summary>
    Lapsed = 4,

    /// <summary>
    /// The policy has been archived. Terminal lifecycle state that takes precedence over the derived
    /// coverage state (mirrors the Contracts status model) — an archived policy always reads as Archived.
    /// </summary>
    Archived = 5,
}
