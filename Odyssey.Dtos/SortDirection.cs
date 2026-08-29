namespace Odyssey.Dtos;

/// <summary>
/// The direction of a server-side list sort (issue #277). Bound case-insensitively from the query
/// string (<c>?sortDir=asc</c> / <c>?sortDir=desc</c>); when absent, the sort field applies its own
/// natural default direction.
/// </summary>
public enum SortDirection
{
    Asc,
    Desc,
}
