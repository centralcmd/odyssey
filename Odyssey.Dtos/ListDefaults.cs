namespace Odyssey.Dtos;

/// <summary>
/// Shared numeric bounds for the list-query contract (issue #277), referenced by the server clamp
/// helpers, the <see cref="QueryParams{TSortBy}"/> defaults, and the client so the limits live in one place.
/// </summary>
public static class ListDefaults
{
    /// <summary>Hard upper bound for <c>limit</c>. Set high (issue #277 modification) so a caller can pull the whole result set in one window while the pager UI is deferred.</summary>
    public const int MaxLimit = 99999;

    /// <summary>Default <c>limit</c> used when a request omits it.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Maximum accepted length of a <c>search</c> term (longer is truncated, not rejected).</summary>
    public const int MaxSearchLength = 200;

    /// <summary>
    /// Max length of any array filter (types, statuses, ids, ...) on a list query. Referenced by the
    /// <c>*QueryParams</c> data annotations so an over-cap filter array is rejected 400 by
    /// <c>[ApiController]</c> model validation rather than tripping ASP.NET's
    /// <c>MvcOptions.MaxModelBindingCollectionSize</c> limit and surfacing as a 500.
    /// </summary>
    public const int MaxFilterArrayLength = 50;
}
