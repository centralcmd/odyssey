using System.Net.Http.Json;
using Odyssey.Dtos;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Test helpers for the server-side list contract (issue #277): list endpoints now return a
/// <see cref="PagedResult{T}"/> envelope instead of a bare array. <see cref="GetPagedItemsAsync{T}"/>
/// GETs that envelope and returns its items as a list, so existing assertions over the list shape
/// stay unchanged.
/// </summary>
public static class PagedTestExtensions
{
    public static async Task<List<T>> GetPagedItemsAsync<T>(this HttpClient client, string url)
    {
        var page = await client.GetFromJsonAsync<PagedResult<T>>(url);
        return page is null ? [] : [.. page.Items];
    }
}
