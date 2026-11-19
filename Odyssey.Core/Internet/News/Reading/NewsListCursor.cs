using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Reading;

/// <summary>
/// The article list's normalised filter set (issue #470 §5.2): what a cursor is bound to. Ids are de-duplicated and
/// sorted, the search trimmed and the dates in UTC; the page size is deliberately not part of it, so a client may change
/// <c>limit</c> mid-scroll.
/// </summary>
public sealed record NewsArticleFilter(
    IReadOnlyList<Guid> FeedIds,
    IReadOnlyList<Guid> TagIds,
    IReadOnlyList<NewsArticleReadStatus> Statuses,
    string? Search,
    DateTime? FromUtc,
    DateTime? ToUtc,
    bool IncludeArchivedFeeds,
    NewsArticleSortBy SortBy)
{
    /// <summary>The SHA-256 of the canonical form, truncated to 16 bytes.</summary>
    public byte[] Hash()
    {
        var canonical = new StringBuilder()
            .Append("f:").AppendJoin(',', FeedIds.Order().Select(id => id.ToString("N")))
            .Append("|t:").AppendJoin(',', TagIds.Order().Select(id => id.ToString("N")))
            .Append("|r:").Append(NewsReadStatusFilter.HashToken(Statuses))
            .Append("|s:").Append(Search ?? string.Empty)
            .Append("|from:").Append(FromUtc?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "-")
            .Append("|to:").Append(ToUtc?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "-")
            .Append("|a:").Append(IncludeArchivedFeeds ? '1' : '0')
            .Append("|o:").Append((int)SortBy)
            .ToString();
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical))[..NewsListCursor.FilterHashLength];
    }
}

/// <summary>Where a page continues: after a <c>(SortAtUtc, Id)</c> key, or (<c>Relevance</c>) at an offset.</summary>
public readonly record struct NewsCursorPosition(DateTime SortAtUtc, Guid Id, int Offset, DateTime AsOfUtc);

/// <summary>
/// The protected list cursor (§5.2, §7.17): authenticated encryption under the Data Protection purpose
/// <see cref="Purpose"/>, carrying only the sort key, the id (or the next offset), the first page's <c>asOfUtc</c> and a
/// hash of the filter set it was issued for. A tampered or foreign cursor, one sent with other filters and one that no
/// longer decrypts (a revoked or rotated-out key) are all <c>400</c> keyed <c>cursor</c>; its value is never echoed.
/// </summary>
public sealed class NewsListCursor(IDataProtectionProvider protection)
{
    public const string Purpose = "Odyssey.News.ListCursor.v1";

    internal const int FilterHashLength = 16;

    private const byte Version = 1;

    // version, sortAt ticks, id, offset, asOf ticks, filter hash.
    private const int PayloadLength = 1 + 8 + 16 + 4 + 8 + FilterHashLength;

    private readonly IDataProtector protector = protection.CreateProtector(Purpose);

    public string Issue(NewsArticleFilter filter, NewsCursorPosition position)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var payload = new byte[PayloadLength];
        var span = payload.AsSpan();
        span[0] = Version;
        BinaryPrimitives.WriteInt64LittleEndian(span[1..], position.SortAtUtc.Ticks);
        position.Id.TryWriteBytes(span[9..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[25..], position.Offset);
        BinaryPrimitives.WriteInt64LittleEndian(span[29..], position.AsOfUtc.Ticks);
        filter.Hash().CopyTo(span[37..]);
        return Base64Url.EncodeToString(protector.Protect(payload));
    }

    /// <summary>The position a cursor names, or a <c>400</c> keyed <c>cursor</c>.</summary>
    public NewsCursorPosition Read(string cursor, NewsArticleFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        byte[] payload;
        try
        {
            payload = protector.Unprotect(Base64Url.DecodeFromChars(cursor));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw NewsErrors.CursorInvalid();
        }

        if (payload.Length != PayloadLength || payload[0] != Version
            || !CryptographicOperations.FixedTimeEquals(payload.AsSpan(37), filter.Hash()))
        {
            throw NewsErrors.CursorInvalid();
        }

        var span = payload.AsSpan();
        var sortTicks = BinaryPrimitives.ReadInt64LittleEndian(span[1..]);
        var asOfTicks = BinaryPrimitives.ReadInt64LittleEndian(span[29..]);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(span[25..]);
        if (sortTicks is < 0 || sortTicks > DateTime.MaxValue.Ticks || asOfTicks is < 0 || asOfTicks > DateTime.MaxValue.Ticks || offset < 0)
        {
            throw NewsErrors.CursorInvalid();
        }

        return new NewsCursorPosition(
            new DateTime(sortTicks, DateTimeKind.Utc), new Guid(span.Slice(9, 16)), offset, new DateTime(asOfTicks, DateTimeKind.Utc));
    }
}
