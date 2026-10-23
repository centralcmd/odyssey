using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Geocoding;

/// <summary>
/// The search cache and its single-flight table (issue #350 §3 Search cache, N2). A singleton holding a
/// <b>dedicated</b> <see cref="MemoryCache"/> with a 500-entry size limit — the application's shared
/// <c>IMemoryCache</c> has no size limit and other consumers rely on that, so it is not used.
/// </summary>
/// <remarks>
/// Keys are <c>(region, SHA-256 of the normalized query)</c>, so the query text itself is never held as a
/// key. Only complete results are cached, for 10 minutes; a partial or failed search is not, so a
/// transient provider hiccup is not served for 10 minutes. Concurrent identical searches share one
/// provider call, which runs on its own token — one cancelled caller never fails the others (N12).
/// </remarks>
public sealed class LocationSearchCache : IDisposable
{
    public const int SizeLimit = 500;

    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = SizeLimit });
    private readonly ConcurrentDictionary<string, Lazy<Task<LocationSearchResponse>>> flights = new(StringComparer.Ordinal);

    public bool TryGet(string key, out LocationSearchResponse? response) => cache.TryGetValue(key, out response);

    public void Set(string key, LocationSearchResponse response) =>
        cache.Set(key, response, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Ttl });

    /// <summary>Joins the in-flight call for <paramref name="key"/>, or starts one.</summary>
    public Task<LocationSearchResponse> Join(string key, Func<Task<LocationSearchResponse>> start)
    {
        var flight = flights.GetOrAdd(key, k => new Lazy<Task<LocationSearchResponse>>(() => RunAndForget(k, start)));
        return flight.Value;
    }

    private async Task<LocationSearchResponse> RunAndForget(string key, Func<Task<LocationSearchResponse>> start)
    {
        try
        {
            return await start();
        }
        finally
        {
            flights.TryRemove(key, out _);
        }
    }

    public void Dispose() => cache.Dispose();
}
