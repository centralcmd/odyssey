namespace Odyssey.Dtos.Finance;

/// <summary>
/// List-filter status for accounts, derived at query time from the <c>Archived</c>/<c>Closed</c> date
/// columns: <see cref="Open"/> (neither set), <see cref="Closed"/> (closed but not archived) and
/// <see cref="Archived"/>. Distinct from <see cref="ArchivalStatus"/> because accounts add a Closed state.
/// </summary>
public enum AccountStatus
{
    Open,
    Closed,
    Archived,
}
