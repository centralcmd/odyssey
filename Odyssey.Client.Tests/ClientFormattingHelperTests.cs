using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The small shared helpers this client routes its repeated code through: <see cref="OdsDates"/>,
/// <see cref="FileDownloads"/> and the <see cref="OdsMenuItem.CopyId"/> factory.
/// </summary>
public class ClientFormattingHelperTests
{
    // ── OdsDates ──────────────────────────────────────────────────────────────

    [Fact]
    public void Long_pads_the_day_and_LongUnpadded_does_not()
    {
        var date = new DateTime(2026, 4, 2, 9, 30, 0);
        Assert.Equal("Apr 02, 2026", OdsDates.Long(date));
        Assert.Equal("Apr 2, 2026", OdsDates.LongUnpadded(date));
    }

    [Fact]
    public void The_DateOnly_overloads_read_the_same()
    {
        var date = new DateOnly(2026, 11, 5);
        Assert.Equal("Nov 05, 2026", OdsDates.Long(date));
        Assert.Equal("Nov 5, 2026", OdsDates.LongUnpadded(date));
    }

    /// <summary>The dates sit inside English copy, so a browser culture must not localise the month.</summary>
    [Theory]
    [InlineData("nb-NO")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public void The_format_ignores_the_current_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try
        {
            Assert.Equal("May 17, 2026", OdsDates.Long(new DateTime(2026, 5, 17)));
            Assert.Equal("May 17, 2026", OdsDates.LongUnpadded(new DateOnly(2026, 5, 17)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ── FileDownloads ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("application/pdf", "image/png", "application/pdf")]   // the response's own type wins
    [InlineData(null, "image/png", "image/png")]                        // then the stored metadata's
    [InlineData(null, null, FileDownloads.FallbackContentType)]         // then the fallback
    [InlineData(" ", null, FileDownloads.FallbackContentType)]          // a blank type is no type
    public async Task SaveFileAsync_picks_the_content_type_in_order(string? responseType, string? knownType, string expected)
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var bytes = new byte[] { 1, 2, 3 };

        await ctx.JSInterop.JSRuntime.SaveFileAsync(new ApiFile(bytes, "server.bin", responseType), "deed.pdf", knownType);

        var call = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "downloadFileFromBytes");
        Assert.Same(bytes, call.Arguments[0]);
        Assert.Equal("deed.pdf", call.Arguments[1]);
        Assert.Equal(expected, call.Arguments[2]);
    }

    // ── OdsMenuItem.CopyId ────────────────────────────────────────────────────

    [Fact]
    public async Task CopyId_builds_the_design_systems_row_and_keeps_the_callers_action()
    {
        var clicked = false;
        var onClick = EventCallback.Factory.Create(this, () => clicked = true);

        var item = OdsMenuItem.CopyId(onClick, "Copy ID of Hytta");

        Assert.Equal("Copy ID", item.Label);
        Assert.Equal("fingerprint", item.Icon);
        Assert.Equal("content_copy", item.TrailingIcon);
        Assert.Equal("Copy ID of Hytta", item.AriaLabel);
        Assert.False(item.Danger);
        await item.OnClick.InvokeAsync();
        Assert.True(clicked);
    }

    [Fact]
    public void CopyId_without_an_aria_label_leaves_the_visible_label_as_the_name() =>
        Assert.Null(OdsMenuItem.CopyId(EventCallback.Empty).AriaLabel);
}
