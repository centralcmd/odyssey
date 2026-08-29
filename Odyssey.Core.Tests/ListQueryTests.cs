using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Unit coverage for the pure <see cref="ListQuery"/> helpers that back the server-side list contract
/// (issue #277): LIKE escaping, offset/limit clamping, search normalisation, filter parsing, and the
/// in-memory <see cref="ListQuery.ToPagedResult{T}"/> window.
/// </summary>
public class ListQueryTests
{
    // ── EscapeLike ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("50%", "50\\%")]           // percent → escaped so it matches literally, not as a wildcard
    [InlineData("a_b", "a\\_b")]           // underscore → escaped
    [InlineData("c\\d", "c\\\\d")]         // backslash → doubled (must be escaped first)
    [InlineData("%_\\", "\\%\\_\\\\")]     // all three, combined
    [InlineData("plain", "plain")]         // nothing to escape
    public void EscapeLike_escapes_metacharacters(string input, string expected) =>
        Assert.Equal(expected, ListQuery.EscapeLike(input));

    [Fact]
    public void EscapeLike_escapes_backslash_before_wildcards()
    {
        // If '%' were escaped before '\', the escaping backslash would itself get doubled. Order matters.
        Assert.Equal("\\\\\\%", ListQuery.EscapeLike("\\%"));
    }

    [Fact]
    public void ContainsPattern_wraps_the_escaped_term_in_wildcards()
    {
        Assert.Equal("%50\\%%", ListQuery.ContainsPattern("50%"));
    }

    // ── ClampLimit ────────────────────────────────────────────────────────────

    [Fact]
    public void ClampLimit_defaults_when_absent() =>
        Assert.Equal(ListDefaults.DefaultLimit, ListQuery.ClampLimit(null));

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(30, 30)]
    [InlineData(100_000, ListDefaults.MaxLimit)]
    [InlineData(ListDefaults.MaxLimit, ListDefaults.MaxLimit)]
    public void ClampLimit_clamps_to_bounds(int input, int expected) =>
        Assert.Equal(expected, ListQuery.ClampLimit(input));

    // ── NormalizeSearch ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeSearch_blank_is_null(string? input) =>
        Assert.Null(ListQuery.NormalizeSearch(input));

    [Fact]
    public void NormalizeSearch_trims() =>
        Assert.Equal("abc", ListQuery.NormalizeSearch("  abc  "));

    [Fact]
    public void NormalizeSearch_caps_length_at_the_max()
    {
        var normalized = ListQuery.NormalizeSearch(new string('x', ListDefaults.MaxSearchLength + 50));
        Assert.Equal(ListDefaults.MaxSearchLength, normalized!.Length);
    }

    // ── Ascending ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SortDirection.Asc, true, true)]
    [InlineData(SortDirection.Asc, false, true)]
    [InlineData(SortDirection.Desc, true, false)]
    [InlineData(SortDirection.Desc, false, false)]
    [InlineData(null, true, true)]   // absent → the field's natural default
    [InlineData(null, false, false)] // absent → the field's natural default
    public void Ascending_parses_or_defaults(SortDirection? sortDir, bool naturalDefault, bool expected) =>
        Assert.Equal(expected, ListQuery.Ascending(sortDir, naturalDefault));

    // ── ResolveWindow ─────────────────────────────────────────────────────────

    [Fact]
    public void ResolveWindow_clamps_negative_offset_to_zero()
    {
        var (offset, limit) = ListQuery.ResolveWindow(offset: -3, limit: 5);
        Assert.Equal(0, offset);
        Assert.Equal(5, limit);
    }

    [Fact]
    public void ResolveWindow_passes_a_large_offset_through()
    {
        // Offset past the end is valid — it just yields an empty window (no clamp-to-last).
        var (offset, _) = ListQuery.ResolveWindow(offset: 100, limit: 5);
        Assert.Equal(100, offset);
    }

    [Fact]
    public void ResolveWindow_clamps_oversized_limit()
    {
        var (_, limit) = ListQuery.ResolveWindow(offset: 0, limit: 1_000_000);
        Assert.Equal(ListDefaults.MaxLimit, limit);
    }

    // ── ToPagedResult (in-memory) ─────────────────────────────────────────────

    [Fact]
    public void ToPagedResult_slices_the_requested_window()
    {
        var rows = Enumerable.Range(1, 12).ToList();
        var result = ListQuery.ToPagedResult(rows, offset: 5, limit: 5);

        Assert.Equal(12, result.TotalCount);
        Assert.Equal(5, result.Offset);
        Assert.Equal(5, result.Limit);
        Assert.Equal([6, 7, 8, 9, 10], result.Items);
    }

    [Fact]
    public void ToPagedResult_returns_the_tail_when_the_window_overruns()
    {
        var rows = Enumerable.Range(1, 12).ToList();
        var result = ListQuery.ToPagedResult(rows, offset: 10, limit: 5);

        Assert.Equal(10, result.Offset);
        Assert.Equal([11, 12], result.Items);
    }
}
