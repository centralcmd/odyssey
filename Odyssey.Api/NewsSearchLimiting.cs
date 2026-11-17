using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Odyssey.Api;

/// <summary>
/// Bound from <c>RateLimiting:NewsSearch</c> (issue #470 §8.4): at most 4 searches at once across the deployment, at
/// most 2 of them one user's, and 30 per user per minute. Deploy-time configuration for the reason
/// <see cref="LocationSearchRateLimiting"/> gives.
/// </summary>
public sealed class NewsSearchLimitOptions
{
    public const string SectionName = "RateLimiting:NewsSearch";

    [Range(1, 1000)]
    public int GlobalConcurrency { get; set; } = 4;

    [Range(1, 1000)]
    public int PerUserConcurrency { get; set; } = 2;

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 30;

    [Range(1, 86400)]
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>
/// The full-text search limiter (§8.4), the <see cref="LocationSearchGlobalLimiter"/> pattern widened to the three
/// limits a <c>FULLTEXT</c> query needs. Acquired inside the article list only when a search is present, so an ordinary
/// list never spends from it. The per-user window is checked before either concurrency slot is taken, so a refused
/// request holds nothing; a granted request holds its two slots until the lease is disposed.
/// </summary>
public sealed class NewsSearchLimiter(IOptions<NewsSearchLimitOptions> options, TimeProvider time)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, int> running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime WindowStart, int Count)> windows = new(StringComparer.Ordinal);
    private int runningTotal;

    /// <summary>A lease to dispose when the search finishes, or <c>null</c> with the seconds to wait.</summary>
    public IDisposable? TryAcquire(string userId, out int retryAfterSeconds)
    {
        var limits = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var window = TimeSpan.FromSeconds(limits.WindowSeconds);
        lock (gate)
        {
            var (start, count) = windows.GetValueOrDefault(userId, (now, 0));
            if (now - start >= window)
            {
                (start, count) = (now, 0);
            }

            if (count >= limits.PermitLimit)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((start + window - now).TotalSeconds));
                return null;
            }

            var mine = running.GetValueOrDefault(userId);
            if (runningTotal >= limits.GlobalConcurrency || mine >= limits.PerUserConcurrency)
            {
                retryAfterSeconds = 1;
                return null;
            }

            windows[userId] = (start, count + 1);
            running[userId] = mine + 1;
            runningTotal++;
            PruneWindows(now, window);
        }

        retryAfterSeconds = 0;
        return new Lease(this, userId);
    }

    private void Release(string userId)
    {
        lock (gate)
        {
            runningTotal--;
            if (running.TryGetValue(userId, out var mine) && mine <= 1)
            {
                running.Remove(userId);
            }
            else if (mine > 1)
            {
                running[userId] = mine - 1;
            }
        }
    }

    // Bounded by the number of users who searched within one window; expired entries go on the next grant.
    private void PruneWindows(DateTime now, TimeSpan window)
    {
        if (windows.Count < 256)
        {
            return;
        }

        foreach (var key in windows.Where(w => now - w.Value.WindowStart >= window).Select(w => w.Key).ToList())
        {
            windows.Remove(key);
        }
    }

    private sealed class Lease(NewsSearchLimiter owner, string userId) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Release(userId);
            }
        }
    }
}

public static class NewsSearchLimiting
{
    public static IServiceCollection AddNewsSearchLimiter(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NewsSearchLimitOptions>()
            .Bind(configuration.GetSection(NewsSearchLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<NewsSearchLimiter>();
        return services;
    }
}
