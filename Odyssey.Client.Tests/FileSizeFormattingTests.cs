using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Covers the two byte-size formatters — <see cref="ByteSize"/> (the files table and export chips)
/// and <see cref="OdsFileUploadDefaults.FmtSize"/> (the upload list) — plus the upload kind guess.
/// </summary>
/// <remarks>
/// They round differently on purpose (<c>ByteSize</c> drops a redundant decimal and goes up to GB;
/// <c>FmtSize</c> keeps one decimal below 10 KB and stops at MB), so the boundaries are worth
/// pinning: a unit shift at exactly 1024 is the classic off-by-one, and it renders as "1024 KB"
/// rather than crashing.
/// </remarks>
public class FileSizeFormattingTests
{
    // ── ByteSize ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024 * 1024, "1 MB")]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    [InlineData(1024L * 1024 * 1024 * 5, "5 GB")]
    public void ByteSize_scales_to_the_largest_unit_that_keeps_the_number_readable(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    /// <summary>GB is the last unit, so a terabyte-scale file keeps counting in GB rather than
    /// walking off the end of the unit table.</summary>
    [Fact]
    public void ByteSize_stops_scaling_at_GB()
    {
        Assert.Equal("1024 GB", ByteSize.Format(1024L * 1024 * 1024 * 1024));
    }

    /// <summary>A missing or zero size is rendered by the caller (as "—" or nothing at all), so the
    /// formatter returns empty rather than "0 B".</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ByteSize_returns_empty_for_a_non_positive_size(long bytes)
    {
        Assert.Equal(string.Empty, ByteSize.Format(bytes));
    }

    /// <summary>
    /// The decimal separator is pinned to invariant. Without that a Norwegian locale renders
    /// "1,5 KB", which reads as one-and-a-half thousand to an English-speaking user and disagrees
    /// with every other size on the page formatted from a different code path.
    /// </summary>
    [Fact]
    public void ByteSize_uses_an_invariant_decimal_separator()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nb-NO");
            Assert.Equal("1.5 KB", ByteSize.Format(1536));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── OdsFileUploadDefaults.FmtSize ────────────────────────────────────────

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(5120, "5.0 KB")]
    [InlineData(10240, "10 KB")]           // at 10 KB the decimal is dropped
    [InlineData(1024 * 1024 - 1, "1024 KB")]
    [InlineData(1024 * 1024, "1.0 MB")]
    [InlineData(1024 * 1024 * 3 / 2, "1.5 MB")]
    public void FmtSize_keeps_one_decimal_only_where_it_carries_information(long bytes, string expected)
    {
        Assert.Equal(expected, OdsFileUploadDefaults.FmtSize(bytes));
    }

    /// <summary>An unknown size is an em dash, not "0 B" — a pending row has no size yet.</summary>
    [Fact]
    public void FmtSize_renders_an_unknown_size_as_a_dash()
    {
        Assert.Equal("—", OdsFileUploadDefaults.FmtSize(null));
    }

    // ── OdsFileUploadDefaults.GuessKind ──────────────────────────────────────

    [Theory]
    [InlineData("scan.jpg", "Receipt")]
    [InlineData("scan.JPEG", "Receipt")]      // extension match is case-insensitive
    [InlineData("photo.heic", "Receipt")]
    [InlineData("statement.pdf", "Statement")]
    [InlineData("notes.txt", "Document")]
    [InlineData("no-extension", "Document")]
    [InlineData("archive.tar.gz", "Document")] // only the final extension counts
    [InlineData("", "Document")]
    public void GuessKind_maps_an_extension_to_a_default_file_kind(string name, string expected)
    {
        Assert.Equal(expected, OdsFileUploadDefaults.GuessKind(name));
    }

    /// <summary>Every kind <c>GuessKind</c> can return must exist in the registry the picker binds
    /// to, or a freshly picked file lands on a kind the dropdown cannot display.</summary>
    [Fact]
    public void Every_guessed_kind_exists_in_the_default_registry()
    {
        var keys = OdsFileUploadDefaults.Kinds.Select(k => k.Key).ToHashSet();

        foreach (var name in new[] { "a.jpg", "a.pdf", "a.txt" })
            Assert.Contains(OdsFileUploadDefaults.GuessKind(name), keys);
    }

    [Fact]
    public void The_default_kind_registry_is_fully_populated_with_unique_keys()
    {
        var kinds = OdsFileUploadDefaults.Kinds;

        Assert.Equal(kinds.Count, kinds.Select(k => k.Key).Distinct().Count());
        Assert.All(kinds, kind =>
        {
            Assert.False(string.IsNullOrWhiteSpace(kind.Label));
            Assert.Matches("^[a-z0-9_]+$", kind.Icon);
            Assert.StartsWith("oklch(", kind.Color);
            Assert.StartsWith("oklch(", kind.Soft);
        });
    }
}
