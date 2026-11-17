using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The news reader's small shared pieces (issue #473 §4.1, §4.2): <see cref="NewsFeedHealthChip"/>
/// (§6.4), <see cref="OdsRelativeTime"/> (AC 27) and the <see cref="OdsSortSelect{TRow}"/> field menu
/// (AC 61).
/// </summary>
public class NewsSharedComponentTests
{
    private static string ComponentsCss() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));

    // ── NewsFeedHealthChip ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(NewsFeedHealth.Pending, "Pending first fetch", null)]
    [InlineData(NewsFeedHealth.Healthy, "Healthy", "income")]
    [InlineData(NewsFeedHealth.Failing, "Failing", "warning")]
    [InlineData(NewsFeedHealth.Gone, "Gone", "error")]
    [InlineData(NewsFeedHealth.Paused, "Paused", "outline")]
    [InlineData(NewsFeedHealth.Archived, "Archived", "outline")]
    [InlineData(NewsFeedHealth.Quarantined, "Quarantined", "error")]
    [InlineData(NewsFeedHealth.QuotaReached, "Storage limit reached", "warning")]
    public void The_health_chip_carries_its_meaning_in_text_with_a_hidden_glyph(NewsFeedHealth health, string label, string? tone)
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var chip = ctx.Render<NewsFeedHealthChip>(p => p.Add(c => c.Health, health)).Find(".odc-chip");

        Assert.Equal(label, OdsInfiniteListRemoteTests.SpokenText(chip));
        Assert.Equal("true", chip.QuerySelector(".material-icons")!.GetAttribute("aria-hidden"));
        var classes = chip.ClassList.ToHashSet();
        Assert.Contains("odc-newshealth", classes);
        if (tone is not null)
            Assert.Contains(tone, classes);
        Assert.DoesNotContain("info", classes);   // Tide and Sea stay brand-only
    }

    [Fact]
    public void An_unknown_health_renders_neutral_under_its_own_name()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var chip = ctx.Render<NewsFeedHealthChip>(p => p.Add(c => c.Health, NewsFeedHealth.Unknown)).Find(".odc-chip");

        Assert.Equal("Unknown", OdsInfiniteListRemoteTests.SpokenText(chip));
        Assert.Contains("outline", chip.ClassList);
        Assert.True(OdsNewsFeedHealth.Meta((NewsFeedHealth)200).Unknown);
    }

    [Fact]
    public void Every_known_health_has_a_registry_row()
    {
        foreach (var health in Enum.GetValues<NewsFeedHealth>().Where(h => h != NewsFeedHealth.Unknown))
        {
            Assert.False(OdsNewsFeedHealth.Meta(health).Unknown, health.ToString());
            Assert.Contains(health, OdsNewsFeedHealth.Order);
        }
    }

    [Fact]
    public void NeedsAttention_is_the_shared_set()
    {
        var attention = Enum.GetValues<NewsFeedHealth>().Where(OdsNewsFeedHealth.NeedsAttention).ToHashSet();

        Assert.True(attention.SetEquals(NewsFeedHealthSets.NeedsAttention));
        Assert.Equal(
            new HashSet<NewsFeedHealth> { NewsFeedHealth.Failing, NewsFeedHealth.Gone, NewsFeedHealth.Quarantined, NewsFeedHealth.QuotaReached },
            attention);
    }

    [Fact]
    public void The_health_chip_can_be_described_by_its_reason_line()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var chip = ctx.Render<NewsFeedHealthChip>(p => p
            .Add(c => c.Health, NewsFeedHealth.QuotaReached)
            .Add(c => c.DescribedBy, "feed-1-quota")).Find(".odc-chip");

        Assert.Equal("feed-1-quota", chip.GetAttribute("aria-describedby"));
    }

    // ── OdsRelativeTime ──────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static BunitContext TimeContext(FakeTimeProvider clock)
    {
        var ctx = NestedInteractiveControlTests.NewContext();
        ctx.Services.AddSingleton<TimeProvider>(clock);
        ctx.Services.AddScoped<RelativeTimeTicker>();
        return ctx;
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(-5, "just now")]
    [InlineData(1, "1 min ago")]
    [InlineData(12, "12 min ago")]
    [InlineData(59, "59 min ago")]
    [InlineData(60, "1 h ago")]
    [InlineData(23 * 60 + 59, "23 h ago")]
    [InlineData(24 * 60, "yesterday")]
    [InlineData(3 * 24 * 60, "3 days ago")]
    public void The_relative_wording_follows_the_kit(int minutesAgo, string expected) =>
        Assert.Equal(expected, OdsRelativeTime.Relative(Now, Now.AddMinutes(-minutesAgo)));

    [Fact]
    public void A_relative_time_is_a_time_element_with_the_absolute_date_as_text()
    {
        var clock = new FakeTimeProvider(Now);
        using var ctx = TimeContext(clock);
        var value = Now.AddMinutes(-12);

        var time = ctx.Render<OdsRelativeTime>(p => p.Add(c => c.Value, value)).Find("time");

        Assert.Equal("2026-11-17T11:48:00Z", time.GetAttribute("datetime"));
        Assert.StartsWith("12 min ago", time.TextContent);
        var absolute = time.QuerySelector(".odc-sr-only")!;
        Assert.Equal($", {OdsRelativeTime.Absolute(value)}", absolute.TextContent);
        Assert.Contains("2026", absolute.TextContent);
        Assert.False(time.HasAttribute("title"));   // never only a tooltip
        Assert.Null(time.Closest("[aria-live]"));
    }

    [Fact]
    public void Every_relative_time_shares_one_ticker_that_refreshes_them_each_minute()
    {
        var clock = new FakeTimeProvider(Now);
        using var ctx = TimeContext(clock);
        var first = ctx.Render<OdsRelativeTime>(p => p.Add(c => c.Value, Now.AddMinutes(-1)));
        var second = ctx.Render<OdsRelativeTime>(p => p.Add(c => c.Value, Now.AddMinutes(-2)));

        var ticker = ctx.Services.GetRequiredService<RelativeTimeTicker>();
        Assert.Equal(2, ticker.Subscribers);

        clock.Advance(RelativeTimeTicker.Interval);

        first.WaitForAssertion(() => Assert.StartsWith("2 min ago", first.Find("time").TextContent));
        second.WaitForAssertion(() => Assert.StartsWith("3 min ago", second.Find("time").TextContent));
    }

    [Fact]
    public void A_disposed_relative_time_leaves_the_ticker()
    {
        var clock = new FakeTimeProvider(Now);
        using var ctx = TimeContext(clock);
        var cut = ctx.Render<OdsRelativeTime>(p => p.Add(c => c.Value, Now));
        var ticker = ctx.Services.GetRequiredService<RelativeTimeTicker>();

        cut.Instance.Dispose();

        Assert.Equal(0, ticker.Subscribers);
    }

    // ── OdsSortSelect field menu (AC 61) ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_sort_field_menu_opens_at_the_fields_width()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var pop = ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<OdsSortSelect<object>>(p => p.Add(c => c.Fields, (IReadOnlyList<OdsSortField<object>>)
        [
            new OdsSortField<object> { Key = "published", Label = "Published", Type = OdsSortType.Date },
            new OdsSortField<object> { Key = "relevance", Label = "Relevance to the search, best first", Type = OdsSortType.Number },
        ]));

        await cut.Find("button.odc-sortsel-trigger").ClickAsync(new());

        var paper = pop.Find(".mud-popover.odc-sortsel-pop");
        Assert.Contains("mud-popover-relative-width", paper.ClassName);
    }

    [Fact]
    public void Long_sort_field_labels_wrap_rather_than_clip()
    {
        var css = ComponentsCss();
        var rule = System.Text.RegularExpressions.Regex.Match(css,
            @"\.odc-sortsel-pop \.odc-typesel-lab\s*\{([^}]*)\}").Groups[1].Value;

        Assert.Contains("white-space: normal", rule);
        Assert.Contains("text-overflow: clip", rule);
        Assert.Contains("overflow-wrap: anywhere", rule);
    }
}
