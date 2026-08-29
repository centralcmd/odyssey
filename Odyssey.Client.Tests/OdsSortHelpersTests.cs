using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Unit coverage for <see cref="OdsSortHelpers"/> — the shared sorting authority behind the per-page
/// "Sort by" control (issue #275): natural default directions, typed labels, the allowlist-validated
/// persisted-state resolve, and the stable client-side ordering the hand-rolled list pages use.
/// </summary>
public class OdsSortHelpersTests
{
    private sealed record Row(string Id, string? Name, int Order);

    private static readonly IReadOnlyList<OdsSortField<Row>> Fields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = r => r.Name },
        new() { Key = "order", Label = "Order", Type = OdsSortType.Number, SortValue = r => r.Order },
        new() { Key = "novalue", Label = "No value", Type = OdsSortType.Text }, // SortValue omitted
    ];

    // ── DefaultDir ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(OdsSortType.Text, OdsSortDirection.Asc)]
    [InlineData(OdsSortType.Status, OdsSortDirection.Asc)]
    [InlineData(OdsSortType.Number, OdsSortDirection.Desc)]
    [InlineData(OdsSortType.Date, OdsSortDirection.Desc)]
    public void DefaultDir_maps_each_type(OdsSortType type, OdsSortDirection expected) =>
        Assert.Equal(expected, OdsSortHelpers.DefaultDir(type));

    [Fact]
    public void DefaultDir_field_override_wins_over_type_default()
    {
        var field = new OdsSortField<Row> { Key = "k", Label = "K", Type = OdsSortType.Text, DefaultDir = OdsSortDirection.Desc };
        Assert.Equal(OdsSortDirection.Desc, OdsSortHelpers.DefaultDir(field));
    }

    [Fact]
    public void DefaultDir_field_falls_back_to_type_when_no_override()
    {
        var field = new OdsSortField<Row> { Key = "k", Label = "K", Type = OdsSortType.Number };
        Assert.Equal(OdsSortDirection.Desc, OdsSortHelpers.DefaultDir(field));
    }

    // ── DirLabel (§4.4 — a typo ships silently, so lock all eight) ──────────────
    [Theory]
    [InlineData(OdsSortType.Text, OdsSortDirection.Asc, "A → Z")]
    [InlineData(OdsSortType.Text, OdsSortDirection.Desc, "Z → A")]
    [InlineData(OdsSortType.Number, OdsSortDirection.Asc, "Low → High")]
    [InlineData(OdsSortType.Number, OdsSortDirection.Desc, "High → Low")]
    [InlineData(OdsSortType.Date, OdsSortDirection.Asc, "Oldest first")]
    [InlineData(OdsSortType.Date, OdsSortDirection.Desc, "Newest first")]
    [InlineData(OdsSortType.Status, OdsSortDirection.Asc, "Defined order")]
    [InlineData(OdsSortType.Status, OdsSortDirection.Desc, "Reversed")]
    public void DirLabel_returns_typed_label(OdsSortType type, OdsSortDirection dir, string expected) =>
        Assert.Equal(expected, OdsSortHelpers.DirLabel(type, dir));

    // ── Resolve — persisted key is untrusted (user-prefs), so it's allowlist-validated ──
    [Fact]
    public void Resolve_null_or_empty_key_returns_fallback()
    {
        var fallback = new OdsTableSort("name", OdsSortDirection.Asc);
        Assert.Equal(fallback, OdsSortHelpers.Resolve(Fields, null, OdsSortDirection.Desc, fallback));
        Assert.Equal(fallback, OdsSortHelpers.Resolve(Fields, "", OdsSortDirection.Desc, fallback));
    }

    [Fact]
    public void Resolve_unknown_key_returns_fallback()
    {
        var fallback = new OdsTableSort("name", OdsSortDirection.Asc);
        Assert.Equal(fallback, OdsSortHelpers.Resolve(Fields, "not_a_key", OdsSortDirection.Asc, fallback));
    }

    [Fact]
    public void Resolve_known_key_null_dir_uses_field_default()
    {
        var fallback = new OdsTableSort("name", OdsSortDirection.Asc);
        // "order" is Number → natural default Desc
        Assert.Equal(new OdsTableSort("order", OdsSortDirection.Desc),
            OdsSortHelpers.Resolve(Fields, "order", null, fallback));
    }

    [Fact]
    public void Resolve_known_key_explicit_dir_is_honored()
    {
        var fallback = new OdsTableSort("name", OdsSortDirection.Asc);
        Assert.Equal(new OdsTableSort("order", OdsSortDirection.Asc),
            OdsSortHelpers.Resolve(Fields, "order", OdsSortDirection.Asc, fallback));
    }

    [Fact]
    public void Resolve_out_of_range_dir_falls_back_to_field_default()
    {
        var fallback = new OdsTableSort("name", OdsSortDirection.Asc);
        var corrupt = (OdsSortDirection)99; // a tampered/corrupt persisted int
        Assert.Equal(new OdsTableSort("order", OdsSortDirection.Desc),
            OdsSortHelpers.Resolve(Fields, "order", corrupt, fallback));
    }

    // ── SortRows — ordering, empties-last, tiebreak, guards ─────────────────────
    [Fact]
    public void SortRows_orders_by_value_ascending()
    {
        var rows = new[] { new Row("1", "Charlie", 0), new Row("2", "Alice", 0), new Row("3", "Bob", 0) };
        var sorted = OdsSortHelpers.SortRows(rows, Fields, new OdsTableSort("name", OdsSortDirection.Asc), r => r.Id);
        Assert.Equal(["Alice", "Bob", "Charlie"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void SortRows_descending_reverses_non_empty_order()
    {
        var rows = new[] { new Row("1", "Alice", 0), new Row("2", "Charlie", 0), new Row("3", "Bob", 0) };
        var sorted = OdsSortHelpers.SortRows(rows, Fields, new OdsTableSort("name", OdsSortDirection.Desc), r => r.Id);
        Assert.Equal(["Charlie", "Bob", "Alice"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void SortRows_null_and_empty_sort_last_ascending()
    {
        var rows = new[] { new Row("1", null, 0), new Row("2", "Alice", 0), new Row("3", "", 0), new Row("4", "Bob", 0) };
        var sorted = OdsSortHelpers.SortRows(rows, Fields, new OdsTableSort("name", OdsSortDirection.Asc), r => r.Id);
        // Non-empty ascending first (Alice, Bob), then the two empties (null + "") last.
        Assert.Equal(["Alice", "Bob"], sorted.Take(2).Select(r => r.Name));
        Assert.All(sorted.Skip(2), r => Assert.True(string.IsNullOrEmpty(r.Name)));
    }

    [Fact]
    public void SortRows_null_and_empty_sort_last_descending_too()
    {
        var rows = new[] { new Row("1", null, 0), new Row("2", "Alice", 0), new Row("3", "Bob", 0) };
        var sorted = OdsSortHelpers.SortRows(rows, Fields, new OdsTableSort("name", OdsSortDirection.Desc), r => r.Id);
        // Descending among non-empties, empty STILL last (not flipped to first).
        Assert.Equal(["Bob", "Alice"], sorted.Take(2).Select(r => r.Name));
        Assert.Null(sorted[^1].Name);
    }

    [Fact]
    public void SortRows_ties_break_on_id_then_input_order()
    {
        // All share the same sort value → order resolves on the record id (ordinal).
        var rows = new[] { new Row("c", "same", 0), new Row("a", "same", 0), new Row("b", "same", 0) };
        var sorted = OdsSortHelpers.SortRows(rows, Fields, new OdsTableSort("name", OdsSortDirection.Asc), r => r.Id);
        Assert.Equal(["a", "b", "c"], sorted.Select(r => r.Id));
    }

    [Fact]
    public void SortRows_null_sort_returns_input_unchanged()
    {
        var rows = new[] { new Row("1", "Charlie", 0), new Row("2", "Alice", 0) };
        var sorted = OdsSortHelpers.SortRows(rows, Fields, null, r => r.Id);
        Assert.Equal(["Charlie", "Alice"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void SortRows_field_without_sortvalue_returns_input_unchanged()
    {
        var rows = new[] { new Row("1", "Charlie", 0), new Row("2", "Alice", 0) };
        var sorted = OdsSortHelpers.SortRows(rows, Fields, new OdsTableSort("novalue", OdsSortDirection.Asc), r => r.Id);
        Assert.Equal(["Charlie", "Alice"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void SortRows_unknown_key_returns_input_unchanged()
    {
        var rows = new[] { new Row("1", "Charlie", 0), new Row("2", "Alice", 0) };
        var sorted = OdsSortHelpers.SortRows(rows, Fields, new OdsTableSort("ghost", OdsSortDirection.Asc), r => r.Id);
        Assert.Equal(["Charlie", "Alice"], sorted.Select(r => r.Name));
    }

    // ── ColumnChangeDir — the OdsRecordTable header-click column-change rule (§8.4, issue #282) ──
    private static OdsRecordColumn<Row> Column(OdsSortType type, OdsSortDirection? defaultDir = null) =>
        new() { Key = "k", SortType = type, SortDefaultDir = defaultDir, Sortable = true };

    [Theory]
    [InlineData(OdsSortType.Text, OdsSortDirection.Asc)]
    [InlineData(OdsSortType.Status, OdsSortDirection.Asc)]
    [InlineData(OdsSortType.Number, OdsSortDirection.Desc)]
    [InlineData(OdsSortType.Date, OdsSortDirection.Desc)]
    public void ColumnChangeDir_uses_the_columns_type_default(OdsSortType type, OdsSortDirection expected) =>
        Assert.Equal(expected, OdsSortHelpers.ColumnChangeDir(Column(type), current: null, keepDir: false));

    [Fact]
    public void ColumnChangeDir_column_default_override_wins_over_type()
    {
        // A Number column would naturally be Desc, but the explicit override forces Asc.
        var column = Column(OdsSortType.Number, OdsSortDirection.Asc);
        Assert.Equal(OdsSortDirection.Asc, OdsSortHelpers.ColumnChangeDir(column, current: null, keepDir: false));
    }

    [Fact]
    public void ColumnChangeDir_null_column_falls_back_to_asc()
    {
        // No column matched the clicked key — defensive Asc, never the type default.
        Assert.Equal(OdsSortDirection.Asc,
            OdsSortHelpers.ColumnChangeDir<Row>(column: null, current: null, keepDir: false));
    }

    [Fact]
    public void ColumnChangeDir_keepDir_preserves_current_direction_over_column_default()
    {
        // KeepDirOnColumnChange: carry the current Desc across the switch even though a Text column defaults Asc.
        var current = new OdsTableSort("name", OdsSortDirection.Desc);
        Assert.Equal(OdsSortDirection.Desc,
            OdsSortHelpers.ColumnChangeDir(Column(OdsSortType.Text), current, keepDir: true));
    }

    [Fact]
    public void ColumnChangeDir_keepDir_with_no_current_uses_column_default()
    {
        // keepDir is set but there is no prior sort to preserve → fall through to the column default.
        Assert.Equal(OdsSortDirection.Desc,
            OdsSortHelpers.ColumnChangeDir(Column(OdsSortType.Number), current: null, keepDir: true));
    }
}
