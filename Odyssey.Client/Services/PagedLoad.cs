using Odyssey.ApiClient;
using Odyssey.Dtos;

namespace Odyssey.Client.Services;

/// <summary>
/// Outcome of a paged list fetch (issue #277). Distinguishes <b>Success</b> (which may carry an
/// empty <see cref="PagedResult{T}.Items"/> — the Empty state) from <b>Failure</b> (the Error
/// state), so a page never conflates "no matches" with "load failed".
/// </summary>
/// <remarks>
/// Page-facing shape only. Build it with <c>ApiInteropExtensions.PagedOrToast</c> rather than by
/// hand, so the failure toast and this state can't drift apart — see that method's remarks.
/// </remarks>
public sealed record PagedLoad<T>
{
    public PagedResult<T>? Result { get; init; }

    /// <summary>True when the fetch succeeded (even if it matched zero rows).</summary>
    public bool IsSuccess => Result is not null;

    public IReadOnlyList<T> Items => Result?.Items ?? [];
    public int TotalCount => Result?.TotalCount ?? 0;
    public int Offset => Result?.Offset ?? 0;
    public int Limit => Result?.Limit ?? 0;

    public static PagedLoad<T> Success(PagedResult<T> result) => new() { Result = result };
    public static PagedLoad<T> Failure() => new();

    /// <summary>Adapts a transport result from <see cref="IOdysseyApi"/> into the page-facing shape.</summary>
    public static PagedLoad<T> From(ApiResult<PagedResult<T>> result) =>
        result is { IsSuccess: true, Value: { } page } ? Success(page) : Failure();
}
