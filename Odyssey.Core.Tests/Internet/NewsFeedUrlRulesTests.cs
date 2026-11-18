using Odyssey.Core.Internet.News;
using Odyssey.Dtos;
using Odyssey.Core.Internet.News.Reading;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// <see cref="NewsFeedUrlRules"/>: the throwing <c>Validate</c> used by feed writes and the non-throwing
/// <c>TryValidate</c> the OPML import calls per entry (issue #508 A9, AC 13) must agree on every rule.
/// </summary>
public sealed class NewsFeedUrlRulesTests
{
    private static readonly IReadOnlyList<NewsPrivateHostGrant> NoGrants = [];

    public static TheoryData<string?> Refused => new()
    {
        null,
        "   ",
        "https://example.com/" + new string('a', NewsLimits.MaxUrlLength),
        "ftp://example.com/feed",
        "/relative/feed",
        "https://user:pass@example.com/feed",
        "https://example.com:8080/feed",
        "http://127.0.0.1/feed",
        "http://10.0.0.1/feed",
        "http://[::1]/feed",
        "http://168.63.129.16/feed",
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void TryValidate_refuses_with_the_same_message_Validate_throws(string? url)
    {
        Assert.False(NewsFeedUrlRules.TryValidate(url, NoGrants, out var address, out var message));
        Assert.Null(address);
        Assert.False(string.IsNullOrEmpty(message));

        var thrown = Assert.Throws<NewsException>(() => NewsFeedUrlRules.Validate(url, NoGrants));
        Assert.Equal(message, thrown.Message);
        if (!string.IsNullOrWhiteSpace(url))
        {
            Assert.DoesNotContain(url.Trim(), message!);
        }
    }

    [Theory]
    [InlineData("https://example.com/feed.xml")]
    [InlineData("  http://Example.COM:80/rss  ")]
    public void TryValidate_accepts_what_Validate_accepts(string url)
    {
        Assert.True(NewsFeedUrlRules.TryValidate(url, NoGrants, out var address, out var message));
        Assert.Null(message);

        var validated = NewsFeedUrlRules.Validate(url, NoGrants);
        Assert.Equal(validated.Url, address!.Url);
        Assert.Equal(validated.Host, address.Host);
        Assert.Equal(validated.UrlKeyHash, address.UrlKeyHash);
        Assert.Equal(url.Trim(), address.Url);
    }

    [Fact]
    public void A_granted_private_host_and_port_pass_both()
    {
        IReadOnlyList<NewsPrivateHostGrant> grants = [new NewsPrivateHostGrant("192.168.1.20", 8080)];

        Assert.True(NewsFeedUrlRules.TryValidate("http://192.168.1.20:8080/feed", grants, out _, out _));
        Assert.Equal("192.168.1.20", NewsFeedUrlRules.Validate("http://192.168.1.20:8080/feed", grants).Host);
    }

    [Fact]
    public void AC13_Ten_thousand_invalid_addresses_are_refused_without_an_exception()
    {
        var firstChance = 0;
        var thread = Environment.CurrentManagedThreadId;
        void Count(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread)
            {
                Interlocked.Increment(ref firstChance);
            }
        }

        var urls = Enumerable.Range(0, 10_000).Select(i => (i % 4) switch
        {
            0 => $"ftp://host{i}.example/feed",
            1 => $"https://u:p@host{i}.example/feed",
            2 => $"http://10.0.{i % 256}.1/feed",
            _ => $"not a url {i}",
        }).ToList();

        AppDomain.CurrentDomain.FirstChanceException += Count;
        int refused;
        try
        {
            refused = urls.Count(u => !NewsFeedUrlRules.TryValidate(u, NoGrants, out _, out _));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }

        Assert.Equal(10_000, refused);
        Assert.Equal(0, firstChance);
    }
}
