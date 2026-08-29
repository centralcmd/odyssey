using Odyssey.ApiClient;
using Odyssey.Client.Components;

namespace Odyssey.Client.Services;

/// <summary>
/// Design-system glue for <see cref="PagedQuery"/>. The builder itself is deliberately free of any
/// component-layer types so it can move into a shared client library; the <see cref="OdsTableSort"/>
/// overload that the pages actually call lives here instead.
/// </summary>
public static class PagedQueryOdsExtensions
{
    /// <summary>Append <c>sortBy</c>/<c>sortDir</c> from a resolved <see cref="OdsTableSort"/>.</summary>
    public static PagedQuery Sort(this PagedQuery query, OdsTableSort? sort) =>
        sort is null ? query : query.Sort(sort.Key, sort.Dir == OdsSortDirection.Asc);
}
