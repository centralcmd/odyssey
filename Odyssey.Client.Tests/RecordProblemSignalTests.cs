using Bunit;
using MudBlazor;
using Moq;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The shared row half of the problem/signal system — <see cref="OdsProblemFlag"/> — and the shared
/// header-rollup → record jump, <see cref="OdsRecordReveal"/>. Both replaced copies on the Accounts,
/// Contracts, Properties and Tax statements pages, so the source lints at the end keep a fifth copy
/// from growing back.
/// </summary>
public class RecordProblemSignalTests
{
    static RecordProblemSignalTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    // ── OdsProblemFlag ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(OdsSeverity.Warning, "warning", "warning_amber")]
    [InlineData(OdsSeverity.Error, "error", "error_outline")]
    [InlineData(OdsSeverity.Info, "info", "info")]
    public void The_flag_takes_its_tone_and_glyph_from_the_severity(OdsSeverity severity, string tone, string glyph)
    {
        using var ctx = new BunitContext();

        var flag = ctx.Render<OdsProblemFlag>(p => p
            .Add(f => f.Severity, severity)
            .AddChildContent("No rate"));

        var chip = flag.Find("span.acct-flag");
        Assert.Contains(tone, chip.ClassList);
        Assert.Equal(glyph, chip.QuerySelector(".material-icons")!.TextContent);
    }

    /// <summary>
    /// The label carries the meaning and the glyph is decorative, so severity is never conveyed by
    /// colour or icon alone (WCAG 1.4.1) and the glyph's ligature text is not read out.
    /// </summary>
    [Fact]
    public void The_flag_reads_as_its_label_with_a_decorative_glyph()
    {
        using var ctx = new BunitContext();

        var flag = ctx.Render<OdsProblemFlag>(p => p.AddChildContent("No rate"));

        Assert.Equal("true", flag.Find(".material-icons").GetAttribute("aria-hidden"));
        Assert.EndsWith("No rate", flag.Find("span.acct-flag").TextContent.Trim(), StringComparison.Ordinal);
    }

    // ── OdsRecordReveal ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_flash_ends_when_it_is_still_the_one_showing()
    {
        var ended = false;

        await OdsRecordReveal.EndFlashAsync(() => true, () => ended = true);

        Assert.True(ended);
    }

    /// <summary>
    /// A second jump started while the first was flashing owns the ring now; the first jump's timer
    /// must not clear it.
    /// </summary>
    [Fact]
    public async Task A_superseded_flash_leaves_the_newer_one_alone()
    {
        var ended = false;

        await OdsRecordReveal.EndFlashAsync(() => false, () => ended = true);

        Assert.False(ended);
    }

    [Fact]
    public async Task The_scroll_targets_the_record_smoothly()
    {
        var scroll = new Mock<IScrollManager>();

        await OdsRecordReveal.ScrollIntoViewAsync(scroll.Object, "#con-1");

        scroll.Verify(s => s.ScrollIntoViewAsync("#con-1", ScrollBehavior.Smooth), Times.Once);
    }

    /// <summary>The scroll is best-effort: the record is already expanded, so a failure is not an error.</summary>
    [Fact]
    public async Task A_failed_scroll_is_swallowed()
    {
        var scroll = new Mock<IScrollManager>();
        scroll.Setup(s => s.ScrollIntoViewAsync(It.IsAny<string>(), It.IsAny<ScrollBehavior>()))
            .ThrowsAsync(new InvalidOperationException("no element"));

        await OdsRecordReveal.ScrollIntoViewAsync(scroll.Object, "#con-1");
    }

    // ── One copy each ────────────────────────────────────────────────────────

    /// <summary>The flag's markup lives in OdsProblemFlag alone; a page renders the component.</summary>
    [Fact]
    public void No_page_hand_rolls_the_problem_flag()
    {
        var offenders = ClientSource.RazorFiles()
            .Where(f => !f.EndsWith("OdsProblemFlag.razor", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("class=\"acct-flag", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "Use <OdsProblemFlag> instead of a raw .acct-flag span: " + string.Join(", ", offenders));
    }

    /// <summary>The record-list pages jump through OdsRecordReveal rather than scrolling by hand.</summary>
    [Fact]
    public void No_finance_page_scrolls_to_a_record_by_hand()
    {
        var finance = Path.Combine(ClientSource.Root, "Pages", "Finance");
        var offenders = Directory.EnumerateFiles(finance, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("ScrollManager.ScrollIntoViewAsync(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "Use OdsRecordReveal.ScrollIntoViewAsync: " + string.Join(", ", offenders));
    }
}
