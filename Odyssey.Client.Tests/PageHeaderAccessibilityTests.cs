using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #216: the <see cref="PageHeader"/> problem rollup's accessibility — severity as text with the
/// ligature hidden, the disclosure state on every region toggle, a separator between the toggle's
/// label and its count, and (as a CSS source-lint) the text-safe foregrounds and the focus ring.
/// </summary>
/// <remarks>
/// The markup half renders, because each rule is a property of the output. The colour half cannot be
/// rendered — bUnit computes no styles — so it pins the declarations that produce the ratios, the
/// <c>SettingAdvisoryTests</c> pattern.
/// </remarks>
public class PageHeaderAccessibilityTests
{
    static PageHeaderAccessibilityTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static PageHeaderProblem Problem(PageHeaderSeverity severity, string message) => new()
    {
        Severity = severity,
        Message = message,
        ViewLabel = "Accounts",
        OnView = EventCallback.Factory.Create(new object(), () => { }),
    };

    private static IRenderedComponent<PageHeader> Render(BunitContext ctx, params PageHeaderProblem[] problems) =>
        ctx.Render<PageHeader>(p => p
            .Add(h => h.Title, "Dashboard")
            .Add(h => h.Problems, [.. problems])
            .Add(h => h.ProblemsLabel, "Net worth issues"));

    // ── Defect 2 — severity as text, ligature hidden ──────────────────────────────────────────

    [Theory]
    [InlineData(PageHeaderSeverity.Error, "Error")]
    [InlineData(PageHeaderSeverity.Warning, "Warning")]
    [InlineData(PageHeaderSeverity.Information, "Information")]
    public void EachRow_PrefixesItsSeverityAsText_AndHidesTheLigature(PageHeaderSeverity severity, string text)
    {
        using var ctx = NewContext();
        var header = Render(ctx, Problem(severity, "Something to know."));

        var row = header.Find(".signal-panel .alert");
        Assert.Equal("true", row.QuerySelector(".alert-icon")!.GetAttribute("aria-hidden"));
        var prefix = row.QuerySelector(".alert-body > .sr-only")!;
        Assert.Equal($"{text}: ", prefix.TextContent);
    }

    // ── Defect 3 — disclosure state ───────────────────────────────────────────────────────────

    [Fact]
    public void TheProblemsToggle_ExposesAriaExpanded_AndControlsItsRegion()
    {
        using var ctx = NewContext();
        var header = Render(ctx, Problem(PageHeaderSeverity.Warning, "Understated."));

        var toggle = header.Find("button.ph-signal-btn");
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
        var controls = toggle.GetAttribute("aria-controls");
        Assert.False(string.IsNullOrEmpty(controls));
        Assert.Equal(controls, header.Find(".ph-region").Id);

        toggle.Click();

        var closed = header.Find("button.ph-signal-btn");
        Assert.Equal("false", closed.GetAttribute("aria-expanded"));
        Assert.Empty(header.FindAll(".ph-region"));
        // The region is gone, so nothing may point at it.
        Assert.False(closed.HasAttribute("aria-controls"));

        closed.Click();

        var reopened = header.Find("button.ph-signal-btn");
        Assert.Equal(reopened.GetAttribute("aria-controls"), header.Find(".ph-region").Id);
    }

    [Fact]
    public void TwoHeaders_NeverShareARegionId()
    {
        using var ctx = NewContext();
        // Both open by default, so both carry aria-controls.
        var first = Render(ctx, Problem(PageHeaderSeverity.Warning, "One."));
        var second = Render(ctx, Problem(PageHeaderSeverity.Warning, "Two."));

        Assert.NotEqual(
            first.Find("button.ph-signal-btn").GetAttribute("aria-controls"),
            second.Find("button.ph-signal-btn").GetAttribute("aria-controls"));
    }

    [Fact]
    public void TheOverviewSearchAndInfoToggles_ExposeTheirStateToo()
    {
        using var ctx = NewContext();
        RenderFragment content = builder => builder.AddContent(0, "region");
        var header = ctx.Render<PageHeader>(p => p
            .Add(h => h.Title, "Accounts")
            .Add(h => h.OverviewContent, content)
            .Add(h => h.SearchContent, content)
            .Add(h => h.InfoContent, content));

        var toggles = header.FindAll("button[aria-expanded]");
        Assert.Equal(3, toggles.Count);
        Assert.All(toggles, toggle =>
        {
            Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
            // Closed: no region in the DOM, so no aria-controls.
            Assert.False(toggle.HasAttribute("aria-controls"));
        });

        foreach (var index in Enumerable.Range(0, 3))
        {
            header.FindAll("button[aria-expanded]")[index].Click();
        }

        var regionIds = header.FindAll(".ph-region").Select(region => region.Id).ToList();
        Assert.Equal(3, regionIds.Count);
        Assert.All(header.FindAll("button[aria-expanded]"), toggle =>
        {
            Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
            Assert.Contains(toggle.GetAttribute("aria-controls"), regionIds);
        });
    }

    // ── Defect 6 — the accessible name separates label and count ──────────────────────────────

    [Fact]
    public void TheToggleText_SeparatesTheLabelFromTheCount()
    {
        using var ctx = NewContext();
        var header = Render(ctx,
            Problem(PageHeaderSeverity.Warning, "One."),
            Problem(PageHeaderSeverity.Information, "Two."));

        var text = Regex.Replace(header.Find("button.ph-signal-btn").TextContent, @"\s+", " ").Trim();
        Assert.EndsWith("Net worth issues, 2", text, StringComparison.Ordinal);
    }

    // ── Defects 1, 4, 5 — the CSS that produces the ratios ────────────────────────────────────

    private static string AppCss() => File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "app.css"));

    private static string HeaderCss() => File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "PageHeader.razor.css"));

    private static string Rule(string css, string selector)
    {
        var match = Regex.Match(css, Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(match.Success, $"No rule for '{selector}'.");
        return match.Groups["body"].Value;
    }

    [Theory]
    [InlineData(".alert.info", "--info-text")]
    [InlineData(".alert.warning", "--warning-text")]
    public void AlertRows_UseTheTextSafeTokenAsTheirForeground(string selector, string token)
    {
        var body = Rule(AppCss(), selector);

        Assert.Matches(@"(^|[;\s])color:\s*var\(" + Regex.Escape(token) + @"\)", body);
    }

    [Fact]
    public void TheAlertAction_HasTheTokenisedFocusRing()
    {
        var body = Rule(AppCss(), ".alert .alert-fix:focus-visible");

        Assert.Contains("var(--focus-ring-width)", body, StringComparison.Ordinal);
        Assert.Contains("var(--focus-ring", body, StringComparison.Ordinal);
        Assert.Contains("var(--focus-ring-offset)", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".ph-signal-btn.mud-button-outlined-info", "color", "--info-text")]
    [InlineData(".ph-signal-btn.mud-button-outlined-warning", "color", "--warning-text")]
    [InlineData(".ph-signal-btn.mud-button-filled-info:active", "background-color", "--info-text")]
    [InlineData(".ph-signal-btn.mud-button-filled-info:active", "color", "--mud-palette-surface")]
    [InlineData(".ph-signal-btn.mud-button-filled-warning:active", "background-color", "--warning-text")]
    [InlineData(".ph-signal-btn.mud-button-filled-warning:active", "color", "--mud-palette-surface")]
    [InlineData(".ph-signal-btn.mud-button-filled-info    .signal-count > span", "color", "--info-text")]
    [InlineData(".ph-signal-btn.mud-button-filled-warning .signal-count > span", "color", "--warning-text")]
    [InlineData(".ph-signal-btn.mud-button-filled-error   .signal-count > span", "color", "--mud-palette-error")]
    public void TheSignalToggle_UsesTheTextSafeTokens(string selector, string property, string token)
    {
        var body = Rule(HeaderCss(), selector);

        Assert.Matches(@"(^|[;\s])" + Regex.Escape(property) + @":\s*var\(" + Regex.Escape(token) + @"\)", body);
    }

    [Fact]
    public void EveryFilledSeverityState_IsCovered_BecauseMudBlazorResetsTheBackgroundOnEach()
    {
        var css = HeaderCss();
        foreach (var severity in new[] { "info", "warning" })
        {
            foreach (var state in new[] { "", ":hover", ":focus-visible", ":active" })
            {
                Assert.Contains($".ph-signal-btn.mud-button-filled-{severity}{state}", css, StringComparison.Ordinal);
            }
        }
    }
}
