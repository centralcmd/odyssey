namespace Odyssey.Dtos.Internet;

/// <summary>
/// The stored facts a feed's health is derived from (issue #470 §4.2). The values come from both rows of a feed:
/// <see cref="Archived"/> and <see cref="IsActive"/> from <c>NewsFeeds</c>, the rest from <c>NewsFeedFetchStates</c>.
/// </summary>
/// <param name="Archived"><c>ArchivedAtUtc</c> is set.</param>
/// <param name="Quarantined"><c>QuarantinedAtUtc</c> is set.</param>
/// <param name="IsActive">The feed is not paused.</param>
/// <param name="LastErrorKind">The last fetch's error kind; <see cref="NewsFeedErrorKind.Gone"/> after a <c>410</c>.</param>
/// <param name="QuotaReached"><c>QuotaReachedAtUtc</c> is set.</param>
/// <param name="ConsecutiveFailures">Failed fetches since the last success.</param>
/// <param name="HasSucceeded"><c>LastSuccessAtUtc</c> is set: the feed has been fetched successfully at least once.</param>
public readonly record struct NewsFeedHealthFacts(
    bool Archived,
    bool Quarantined,
    bool IsActive,
    NewsFeedErrorKind LastErrorKind,
    bool QuotaReached,
    int ConsecutiveFailures,
    bool HasSucceeded);

/// <summary>
/// The one definition of a feed's health (§4.2, decision 27): every projection and the <c>health</c> list filter call
/// <see cref="Derive"/>, so the filter matches the derived value and never the raw conditions. It lives in
/// <c>Odyssey.Dtos</c>, which has no project references, so the server and the client name one rule.
/// </summary>
public static class NewsFeedHealthRules
{
    /// <summary>The precedence, highest first. A feed has exactly one health value: the first that applies.</summary>
    public static readonly IReadOnlyList<NewsFeedHealth> Precedence =
    [
        NewsFeedHealth.Archived,
        NewsFeedHealth.Quarantined,
        NewsFeedHealth.Paused,
        NewsFeedHealth.Gone,
        NewsFeedHealth.QuotaReached,
        NewsFeedHealth.Failing,
        NewsFeedHealth.Pending,
        NewsFeedHealth.Healthy,
    ];

    /// <summary>
    /// <c>Archived</c> &gt; <c>Quarantined</c> &gt; <c>Paused</c> &gt; <c>Gone</c> &gt; <c>QuotaReached</c> &gt;
    /// <c>Failing</c> &gt; <c>Pending</c> &gt; <c>Healthy</c>.
    /// </summary>
    public static NewsFeedHealth Derive(NewsFeedHealthFacts facts)
    {
        if (facts.Archived)
        {
            return NewsFeedHealth.Archived;
        }

        if (facts.Quarantined)
        {
            return NewsFeedHealth.Quarantined;
        }

        if (!facts.IsActive)
        {
            return NewsFeedHealth.Paused;
        }

        if (facts.LastErrorKind == NewsFeedErrorKind.Gone)
        {
            return NewsFeedHealth.Gone;
        }

        if (facts.QuotaReached)
        {
            return NewsFeedHealth.QuotaReached;
        }

        if (facts.ConsecutiveFailures > 0)
        {
            return NewsFeedHealth.Failing;
        }

        return facts.HasSucceeded ? NewsFeedHealth.Healthy : NewsFeedHealth.Pending;
    }

    /// <summary>
    /// The projected <c>quotaReason</c>: the stored reason exactly when the derived health is
    /// <see cref="NewsFeedHealth.QuotaReached"/>, else <c>null</c> — even while <c>QuotaReachedAtUtc</c> is still set.
    /// </summary>
    public static NewsFeedQuotaReason? ProjectQuotaReason(NewsFeedHealth health, NewsFeedQuotaReason? storedReason) =>
        health == NewsFeedHealth.QuotaReached ? storedReason : null;
}

/// <summary>Named groups of health values (§5.1).</summary>
public static class NewsFeedHealthSets
{
    /// <summary>"Needs attention": the values a user has to act on.</summary>
    public static readonly IReadOnlySet<NewsFeedHealth> NeedsAttention = new HashSet<NewsFeedHealth>
    {
        NewsFeedHealth.Failing,
        NewsFeedHealth.Gone,
        NewsFeedHealth.Quarantined,
        NewsFeedHealth.QuotaReached,
    };
}
