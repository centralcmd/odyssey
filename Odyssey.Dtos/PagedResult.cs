namespace Odyssey.Dtos;

/// <summary>
/// Shared response envelope for server-side list endpoints (issue #277). Carries a window of items
/// (<see cref="Items"/>) plus the offset/limit that produced it and the total number of matching
/// records the caller is authorized to see (<see cref="TotalCount"/>, computed after claim-scoping,
/// search and filters, before the window slice).
/// </summary>
/// <remarks>
/// A read-only response envelope, never bound to a Blazor form, so it uses <c>init</c> accessors rather
/// than the project's form-DTO <c>get; set;</c> convention.
/// </remarks>
public sealed record PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>0-based index of the first returned item within the full matching set.</summary>
    public int Offset { get; init; }

    /// <summary>Maximum number of items requested for this window.</summary>
    public int Limit { get; init; }

    public int TotalCount { get; init; }
}
