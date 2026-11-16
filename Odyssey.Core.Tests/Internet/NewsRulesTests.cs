using System.ComponentModel.DataAnnotations;
using Odyssey.Context;
using Odyssey.Core.Internet.News;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The shared news rules of issue #470 that have no I/O: feed health and its precedence (AC 79), the quota and
/// daily-cap predicates (§6.3), the derived image-size ceiling (AC 61, 63), the private-host allow-list's shape and
/// the request DTOs' own validation (§8.1).
/// </summary>
public class NewsRulesTests
{
    private static readonly NewsFeedHealthFacts Healthy =
        new(Archived: false, Quarantined: false, IsActive: true, NewsFeedErrorKind.None, QuotaReached: false, ConsecutiveFailures: 0, HasSucceeded: true);

    // ── Health (§4.2, decision 27) ───────────────────────────────────────────────────────────────

    private static NewsFeedHealthFacts With(NewsFeedHealth condition, NewsFeedHealthFacts facts) => condition switch
    {
        NewsFeedHealth.Archived => facts with { Archived = true },
        NewsFeedHealth.Quarantined => facts with { Quarantined = true },
        NewsFeedHealth.Paused => facts with { IsActive = false },
        NewsFeedHealth.Gone => facts with { LastErrorKind = NewsFeedErrorKind.Gone, ConsecutiveFailures = 1 },
        NewsFeedHealth.QuotaReached => facts with { QuotaReached = true },
        NewsFeedHealth.Failing => facts with { ConsecutiveFailures = facts.ConsecutiveFailures + 2 },
        NewsFeedHealth.Pending => facts with { HasSucceeded = false },
        _ => facts,
    };

    public static TheoryData<NewsFeedHealth, NewsFeedHealth> Pairs()
    {
        var data = new TheoryData<NewsFeedHealth, NewsFeedHealth>();
        var order = NewsFeedHealthRules.Precedence;
        for (var higher = 0; higher < order.Count - 1; higher++)
        {
            for (var lower = higher + 1; lower < order.Count - 1; lower++)
            {
                data.Add(order[higher], order[lower]);
            }
        }

        return data;
    }

    /// <summary>AC 79: each pair in the precedence resolves to the higher value.</summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void Each_pair_of_conditions_resolves_to_the_higher_value(NewsFeedHealth higher, NewsFeedHealth lower) =>
        Assert.Equal(higher, NewsFeedHealthRules.Derive(With(lower, With(higher, Healthy))));

    [Fact]
    public void The_precedence_names_every_health_value_once_in_the_spec_order()
    {
        Assert.Equal(
            [NewsFeedHealth.Archived, NewsFeedHealth.Quarantined, NewsFeedHealth.Paused, NewsFeedHealth.Gone,
             NewsFeedHealth.QuotaReached, NewsFeedHealth.Failing, NewsFeedHealth.Pending, NewsFeedHealth.Healthy],
            NewsFeedHealthRules.Precedence);
        Assert.Equal(Enum.GetValues<NewsFeedHealth>().Where(h => h != NewsFeedHealth.Unknown).Order(), NewsFeedHealthRules.Precedence.Order());
    }

    [Fact]
    public void A_feed_with_no_condition_is_healthy_and_one_never_fetched_is_pending()
    {
        Assert.Equal(NewsFeedHealth.Healthy, NewsFeedHealthRules.Derive(Healthy));
        Assert.Equal(NewsFeedHealth.Pending, NewsFeedHealthRules.Derive(Healthy with { HasSucceeded = false }));
    }

    /// <summary>AC 79: archived, quarantined and at quota reports Archived with no quota reason.</summary>
    [Fact]
    public void The_quota_reason_is_projected_only_with_QuotaReached_health()
    {
        var facts = Healthy with { Archived = true, Quarantined = true, QuotaReached = true };
        var health = NewsFeedHealthRules.Derive(facts);

        Assert.Equal(NewsFeedHealth.Archived, health);
        Assert.Null(NewsFeedHealthRules.ProjectQuotaReason(health, NewsFeedQuotaReason.TextQuota));
        Assert.Equal(NewsFeedQuotaReason.DailyCap,
            NewsFeedHealthRules.ProjectQuotaReason(NewsFeedHealthRules.Derive(Healthy with { QuotaReached = true }), NewsFeedQuotaReason.DailyCap));
    }

    [Fact]
    public void Needs_attention_is_failing_gone_quarantined_and_quota_reached()
    {
        Assert.Equal(
            [NewsFeedHealth.Failing, NewsFeedHealth.Gone, NewsFeedHealth.Quarantined, NewsFeedHealth.QuotaReached],
            NewsFeedHealthSets.NeedsAttention.Order());
    }

    // ── Quotas and caps (§6.3, §11.1) ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, 1L, false)]
    [InlineData(1023L, 1024L, false)]
    [InlineData(1024L, 1024L, true)]
    [InlineData(2048L, 1024L, true)]
    public void The_text_quota_is_reached_at_its_bytes(long stored, long quota, bool reached) =>
        Assert.Equal(reached, NewsTextQuota.IsReached(stored, quota));

    [Fact]
    public void The_text_quota_bytes_are_the_setting_in_GiB() =>
        Assert.Equal(20L * 1024 * 1024 * 1024, NewsTextQuota.QuotaBytes(SystemSettingsDefaults.NewsTextStoreMaxGiB));

    /// <summary>§6.3: yesterday's count can never keep the cap set.</summary>
    [Fact]
    public void The_daily_cap_counts_only_today()
    {
        var today = new DateOnly(2026, 11, 16);
        var feed = new NewsFeedFetchState { DailyQuotaDate = today, ArticlesToday = 500 };

        Assert.True(NewsDailyCap.IsReached(feed, today, 500));
        Assert.False(NewsDailyCap.IsReached(feed, today, 501));
        Assert.False(NewsDailyCap.IsReached(feed, today.AddDays(1), 500));
        Assert.False(NewsDailyCap.IsReached(new NewsFeedFetchState { ArticlesToday = 900 }, today, 500));
    }

    /// <summary>AC 61, 63: the ceiling is MaxFileSizeBytes / 1024, never below the setting's floor.</summary>
    [Theory]
    [InlineData(64L * 1024 * 1024, 65536)]
    [InlineData(5L * 1024 * 1024, 5120)]
    [InlineData(64L * 1024, 64)]
    [InlineData(10L * 1024, 64)]
    [InlineData(0L, 64)]
    public void The_image_size_ceiling_is_the_file_limit_in_KiB_with_the_setting_floor(long maxFileSizeBytes, int expected)
    {
        Assert.Equal(expected, NewsImageSizeCeiling.Kb(maxFileSizeBytes));
        Assert.True(NewsImageSizeCeiling.Kb(maxFileSizeBytes) >= SystemSettingsBounds.NewsImageMaxSizeKbMin);
    }

    // ── The private-host allow-list (§7.9) ───────────────────────────────────────────────────────

    [Fact]
    public void An_empty_allow_list_is_valid_and_grants_nothing()
    {
        Assert.Null(NewsPrivateHostAllowListRule.Validate(""));
        Assert.Empty(NewsPrivateHostAllowListRule.Parse(""));
        Assert.Empty(NewsPrivateHostAllowListRule.Parse(null));
    }

    [Fact]
    public void Entries_are_canonicalised_deduplicated_and_keep_their_ports()
    {
        const string value = " NAS.lan , 192.168.1.20:8080, [FD00::1]:8443, nas.lan., 10.0.0.1 ";

        Assert.Null(NewsPrivateHostAllowListRule.Validate(value));
        Assert.Equal("nas.lan, 192.168.1.20:8080, [fd00::1]:8443, 10.0.0.1", NewsPrivateHostAllowListRule.Canonicalize(value));
        Assert.Equal(
            [new NewsPrivateHostGrant("nas.lan", null), new NewsPrivateHostGrant("192.168.1.20", 8080),
             new NewsPrivateHostGrant("fd00::1", 8443), new NewsPrivateHostGrant("10.0.0.1", null)],
            NewsPrivateHostAllowListRule.Parse(value));
    }

    [Theory]
    [InlineData("http://nas.lan")]
    [InlineData("nas.lan/feed")]
    [InlineData("user@nas.lan")]
    [InlineData("nas lan")]
    [InlineData("fd00::1")]
    [InlineData("[fd00::1")]
    [InlineData("[10.0.0.1]")]
    [InlineData("nas.lan:0")]
    [InlineData("nas.lan:65536")]
    [InlineData("nas.lan:")]
    [InlineData("nas.lan:80a")]
    [InlineData("10.1")]
    [InlineData("167772161")]
    [InlineData("-nas.lan")]
    [InlineData("nas..lan")]
    [InlineData("nas.lan,,router.lan")]
    [InlineData("nås.lan")]
    public void A_malformed_entry_is_refused_and_a_stored_one_grants_nothing(string value)
    {
        Assert.NotNull(NewsPrivateHostAllowListRule.Validate(value));
        Assert.Null(NewsPrivateHostAllowListRule.Canonicalize(value));
        // A degraded read must never open an address: an unparseable stored value is read as empty.
        Assert.Empty(NewsPrivateHostAllowListRule.Parse(value));
    }

    [Fact]
    public void The_list_is_bounded_by_entries_and_by_length()
    {
        var thirtyTwo = string.Join(", ", Enumerable.Range(1, NewsPrivateHostAllowListRule.MaxEntries).Select(i => $"h{i}.lan"));
        var thirtyThree = thirtyTwo + ", h33.lan";
        var tooLong = string.Join(",", Enumerable.Range(1, 17).Select(i => new string('a', 63) + "." + new string('b', 63) + i + ".lan"));

        Assert.Null(NewsPrivateHostAllowListRule.Validate(thirtyTwo));
        Assert.NotNull(NewsPrivateHostAllowListRule.Validate(thirtyThree));
        Assert.True(tooLong.Length > NewsPrivateHostAllowListRule.MaxLength);
        Assert.NotNull(NewsPrivateHostAllowListRule.Validate(tooLong));
    }

    // ── Request DTOs (§8.1) ──────────────────────────────────────────────────────────────────────

    private static List<ValidationResult> Validate(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results;
    }

    /// <summary>AC 72, 73: duplicates collapse before the 50-id cap; 51 distinct ids are refused.</summary>
    [Fact]
    public void Filter_ids_are_deduplicated_before_their_cap()
    {
        var one = Guid.NewGuid();
        var fifty = Enumerable.Range(0, NewsLimits.MaxFilterIds).Select(_ => Guid.NewGuid()).ToArray();

        Assert.Empty(Validate(new NewsArticlesQueryParams { FeedIds = [.. Enumerable.Repeat(one, 80)] }));
        Assert.Empty(Validate(new NewsArticlesQueryParams { TagIds = fifty }));
        Assert.Contains(Validate(new NewsArticlesQueryParams { TagIds = [.. fifty, Guid.NewGuid()] }),
            r => r.MemberNames.Contains(nameof(NewsArticlesQueryParams.TagIds)));
        Assert.Contains(Validate(new MarkNewsRead { AsOfUtc = DateTimeOffset.UtcNow, FeedIds = [.. fifty, Guid.NewGuid()] }),
            r => r.MemberNames.Contains(nameof(MarkNewsRead.FeedIds)));
        Assert.Equal([one], NewsFilterIds.Normalize([one, one, one]));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(NewsLimits.MaxPageSize, true)]
    [InlineData(NewsLimits.MaxPageSize + 1, false)]
    public void The_list_page_size_is_bounded_by_the_news_maximum(int limit, bool valid)
    {
        Assert.Equal(valid, Validate(new NewsArticlesQueryParams { Limit = limit }).Count == 0);
        Assert.Equal(valid, Validate(new NewsFeedsQueryParams { Limit = limit }).Count == 0);
    }

    /// <summary>AC 68: a cursor and an offset together are a 400 keyed cursor; Relevance needs a search.</summary>
    [Fact]
    public void A_cursor_excludes_an_offset_and_relevance_needs_a_search()
    {
        Assert.Contains(Validate(new NewsArticlesQueryParams { Cursor = "abc", Offset = 50 }),
            r => r.MemberNames.Contains(nameof(NewsArticlesQueryParams.Cursor)));
        Assert.Empty(Validate(new NewsArticlesQueryParams { Cursor = "abc" }));
        Assert.Contains(Validate(new NewsArticlesQueryParams { SortBy = NewsArticleSortBy.Relevance }),
            r => r.MemberNames.Contains(nameof(NewsArticlesQueryParams.SortBy)));
        Assert.Empty(Validate(new NewsArticlesQueryParams { SortBy = NewsArticleSortBy.Relevance, Search = "climate" }));
        Assert.NotEmpty(Validate(new NewsArticlesQueryParams { Cursor = new string('x', NewsLimits.MaxCursorLength + 1) }));
    }

    [Fact]
    public void The_required_nullable_members_refuse_an_omitted_value()
    {
        Assert.Contains(Validate(new UpdateNewsFeed { Url = "https://example.com/feed", DownloadImages = true }),
            r => r.MemberNames.Contains(nameof(UpdateNewsFeed.IsActive)));
        Assert.Contains(Validate(new UpdateNewsFeed { Url = "https://example.com/feed", DownloadImages = true, IsActive = true }),
            r => r.MemberNames.Contains(nameof(UpdateNewsFeed.Version)));
        Assert.Empty(Validate(new UpdateNewsFeed { Url = "https://example.com/feed", DownloadImages = true, IsActive = true, Version = Guid.NewGuid() }));
        Assert.NotEmpty(Validate(new NewsArticleReadState()));
        Assert.NotEmpty(Validate(new MarkNewsRead()));
        Assert.NotEmpty(Validate(new NewsArticlesReadStateBulk { ArticleIds = [], IsRead = true }));
        Assert.NotEmpty(Validate(new NewsArticlesReadStateBulk
        {
            ArticleIds = [.. Enumerable.Range(0, NewsLimits.MaxBulkReadStateIds + 1).Select(_ => Guid.NewGuid())], IsRead = true,
        }));
    }

    [Theory]
    [InlineData(NewsLimits.MinFetchIntervalMinutes - 1, false)]
    [InlineData(NewsLimits.MinFetchIntervalMinutes, true)]
    [InlineData(NewsLimits.MaxFetchIntervalMinutes, true)]
    [InlineData(NewsLimits.MaxFetchIntervalMinutes + 1, false)]
    public void A_feed_interval_is_bounded_by_the_shared_limits(int minutes, bool valid) =>
        Assert.Equal(valid, Validate(new NewNewsFeed { Url = "https://example.com/feed", FetchIntervalMinutes = minutes }).Count == 0);

    [Fact]
    public void Feed_and_tag_writes_bound_their_strings_and_tag_lists()
    {
        Assert.NotEmpty(Validate(new NewNewsFeed { Url = new string('a', NewsLimits.MaxUrlLength + 1) }));
        Assert.NotEmpty(Validate(new NewNewsFeed { Url = "https://example.com", CustomTitle = new string('t', NewsLimits.MaxCustomTitleLength + 1) }));
        Assert.NotEmpty(Validate(new NewNewsFeed
        {
            Url = "https://example.com", TagIds = [.. Enumerable.Range(0, NewsLimits.MaxTagIdsPerRequest + 1).Select(_ => Guid.NewGuid())],
        }));
        Assert.NotEmpty(Validate(new NewNewsTag { Name = "" }));
        Assert.NotEmpty(Validate(new NewNewsTag { Name = new string('n', NewsLimits.MaxTagNameLength + 1) }));
        Assert.NotEmpty(Validate(new NewNewsTag { Name = "World", Color = "#12345Z" }));
        Assert.NotEmpty(Validate(new NewNewsTag { Name = "World", Icon = "<svg>" }));
        Assert.Empty(Validate(new NewNewsTag { Name = "World", Color = "#123456" }));
    }
}
