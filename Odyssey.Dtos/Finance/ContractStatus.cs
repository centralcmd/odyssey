namespace Odyssey.Dtos.Finance;

/// <summary>
/// Derived, never stored (issue #174 §6). Computed per request from the contract's dates and archive
/// flag: <c>Archived</c> wins. For a one-off (completion date set): <c>Upcoming</c> until the completion
/// date, else <c>Active</c>. For a term: <c>Upcoming</c> (start in the future); then <c>Expired</c> (end
/// in the past); otherwise <c>Active</c>.
/// </summary>
public enum ContractStatus
{
    Active = 0,
    Upcoming = 1,
    Expired = 2,
    Archived = 3,
}
