using Odyssey.Context;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Dtos.Internet;
using Xunit;
using S = Odyssey.Dtos.Internet.NewsArticleReadStatus;

namespace Odyssey.Core.Tests.Internet;

/// <summary>The article list's Status filter: normalisation, the cursor token and the updated-since-read test.</summary>
public sealed class NewsReadStatusFilterTests
{
    public static TheoryData<bool?, S[], S[]> Sets => new()
    {
        { null, [], [] },
        { false, [], [S.Unread] },
        { true, [], [S.Read] },
        { null, [S.Updated], [S.Updated] },
        { null, [S.Read, S.Updated], [S.Read] },          // Updated is a subset of Read
        { null, [S.Unread, S.Read], [] },                 // everything
        { null, [S.Unread, S.Read, S.Updated], [] },
        { null, [S.Updated, S.Unread], [S.Unread, S.Updated] },
        { null, [S.Unknown], [] },
    };

    [Theory]
    [MemberData(nameof(Sets))]
    public void Normalize_collapses_to_the_canonical_set(bool? isRead, S[] statuses, S[] expected) =>
        Assert.Equal(expected, NewsReadStatusFilter.Normalize(isRead, statuses));

    [Fact]
    public void The_single_member_tokens_match_the_older_isRead_tokens()
    {
        Assert.Equal("-", NewsReadStatusFilter.HashToken([]));
        Assert.Equal("0", NewsReadStatusFilter.HashToken([S.Unread]));
        Assert.Equal("1", NewsReadStatusFilter.HashToken([S.Read]));
        Assert.Equal("s2", NewsReadStatusFilter.HashToken([S.Updated]));
        Assert.Equal("s0,2", NewsReadStatusFilter.HashToken([S.Unread, S.Updated]));
    }

    [Theory]
    [InlineData(true, 60, 0, true)]     // changed an hour after it was read
    [InlineData(true, -60, 0, false)]   // changed before it was read
    [InlineData(true, 0, 0, false)]     // changed at the very moment it was read: not after, so not Updated
    [InlineData(false, 60, 0, false)]   // unread
    [InlineData(true, null, 0, false)]  // never changed
    public void Updated_since_read_needs_read_and_a_later_change(bool isRead, int? changedOffsetMinutes, int readOffsetMinutes, bool expected)
    {
        var read = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var article = new NewsArticle
        {
            IsRead = isRead,
            ReadAtUtc = isRead ? read.AddMinutes(readOffsetMinutes) : null,
            ContentChangedAtUtc = changedOffsetMinutes is { } m ? read.AddMinutes(m) : null,
        };

        Assert.Equal(expected, NewsReadStatusFilter.UpdatedSinceRead(article));
        Assert.Equal(expected, NewsReadStatusFilter.IsUpdatedSinceRead.Compile()(article));
        Assert.Equal(expected ? 1 : 0, NewsReadStatusFilter.Apply(new[] { article }.AsQueryable(), [S.Updated]).Count());
    }

    [Fact]
    public void Unread_or_updated_returns_unread_and_updated_but_not_plain_read()
    {
        var read = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var unread = new NewsArticle { Title = "unread", IsRead = false };
        var updated = new NewsArticle { Title = "updated", IsRead = true, ReadAtUtc = read, ContentChangedAtUtc = read.AddMinutes(5) };
        var plainRead = new NewsArticle { Title = "read", IsRead = true, ReadAtUtc = read, ContentChangedAtUtc = read.AddMinutes(-5) };
        var sameInstant = new NewsArticle { Title = "same", IsRead = true, ReadAtUtc = read, ContentChangedAtUtc = read };

        var statuses = NewsReadStatusFilter.Normalize(null, [S.Unread, S.Updated]);
        var titles = NewsReadStatusFilter.Apply(new[] { unread, updated, plainRead, sameInstant }.AsQueryable(), statuses)
            .Select(a => a.Title).ToList();

        Assert.Equal(["unread", "updated"], titles);
    }
}
