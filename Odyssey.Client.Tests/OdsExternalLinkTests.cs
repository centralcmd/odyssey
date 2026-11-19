using Bunit;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="OdsExternalLink"/> (issue #473 §4.1, §6.2, AC 10): a link only for a safe http/https URL,
/// with the new-tab name and <c>rel</c>; plain text and Copy for anything else; the URL in an ltr
/// <c>bdi</c>; the punycode host as its own token; Copy's result announced by the component itself.
/// </summary>
public class OdsExternalLinkTests
{
    private const string Module = "./js/external-link.js";

    private static IRenderedComponent<OdsExternalLink> Render(BunitContext ctx, string? url, bool copyable = true,
        List<bool>? copies = null) =>
        ctx.Render<OdsExternalLink>(p => p
            .Add(c => c.Url, url)
            .Add(c => c.Copyable, copyable)
            .Add(c => c.OnCopy, (bool ok) => copies?.Add(ok)));

    [Fact]
    public void An_https_url_renders_a_new_tab_link_named_with_the_new_tab_note()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, "https://example.com/news/1?x=y");

        var link = cut.Find("a.odc-extlink-open");
        Assert.Equal("https://example.com/news/1?x=y", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
        Assert.Equal("no-referrer", link.GetAttribute("referrerpolicy"));
        Assert.Equal("Open original (opens in a new tab)", OdsInfiniteListRemoteTests.SpokenText(link));
        Assert.Equal("example.com", cut.Find(".odc-extlink-host bdi").TextContent);

        var url = cut.Find("bdi.odc-extlink-url");
        Assert.Equal("ltr", url.GetAttribute("dir"));
        Assert.Equal("https://example.com/news/1?x=y", url.TextContent);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>hi</b>")]
    [InlineData("https://user@example.com/")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://@example.com/")]
    [InlineData("/relative/path")]
    [InlineData("//example.com/x")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    public void Anything_else_is_plain_text_with_copy_and_no_href(string url)
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, url);

        Assert.Empty(cut.FindAll("a"));
        Assert.Empty(cut.FindAll("[href]"));
        Assert.Equal(url, cut.Find("bdi.odc-extlink-url").TextContent);
        Assert.Equal("Not a web link", cut.Find(".odc-extlink-host").TextContent);
        Assert.Equal("Copy link", cut.Find("button.odc-extlink-copy").GetAttribute("aria-label"));
        Assert.Null(OdsExternalLink.Target(url));
    }

    [Fact]
    public void An_idn_host_shows_its_punycode_token()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, "https://bücher.example/a");

        Assert.Equal("xn--bcher-kva.example", cut.Find(".odc-extlink-host bdi").TextContent);
        Assert.Equal("https://bücher.example/a", cut.Find("bdi.odc-extlink-url").TextContent);
    }

    [Fact]
    public void Markup_in_a_url_is_rendered_as_text()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = Render(ctx, "https://example.com/<script>x</script>");

        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("<script>", cut.Find("bdi.odc-extlink-url").TextContent);
    }

    [Fact]
    public void An_empty_url_renders_a_dash()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        Assert.Equal("—", Render(ctx, null).Find(".odc-extlink.empty").TextContent);
    }

    [Fact]
    public void Copyable_false_omits_the_copy_button()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        Assert.Empty(Render(ctx, "https://example.com", copyable: false).FindAll("button"));
    }

    [Theory]
    [InlineData(true, "Link copied", false)]
    [InlineData(false, "Couldn’t copy. Select the text instead.", true)]
    public void Copy_announces_its_result_in_the_components_own_status_line(bool ok, string message, bool fail)
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var module = ctx.JSInterop.SetupModule(Module);
        module.Setup<bool>("copy", _ => true).SetResult(ok);
        var copies = new List<bool>();
        var cut = Render(ctx, "https://example.com", copies: copies);

        var status = cut.Find(".odc-extlink-status");
        Assert.Equal("status", status.GetAttribute("role"));
        Assert.Equal("polite", status.GetAttribute("aria-live"));
        Assert.Equal(string.Empty, status.TextContent);

        cut.Find("button.odc-extlink-copy").Click();

        cut.WaitForAssertion(() => Assert.Equal(message, cut.Find(".odc-extlink-status").TextContent));
        Assert.Equal(fail, cut.Find(".odc-extlink-status").ClassList.Contains("fail"));
        Assert.Equal([ok], copies);
        Assert.Equal("https://example.com", module.Invocations["copy"].Single().Arguments[0]);
    }

    [Fact]
    public void A_copy_that_throws_reports_failure()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var module = ctx.JSInterop.SetupModule(Module);
        module.Setup<bool>("copy", _ => true).SetException(new Microsoft.JSInterop.JSException("blocked"));
        var cut = Render(ctx, "https://example.com");

        cut.Find("button.odc-extlink-copy").Click();

        cut.WaitForAssertion(() => Assert.Equal(OdsExternalLink.CopyFailedText, cut.Find(".odc-extlink-status").TextContent));
    }

    [Fact]
    public void Without_actions_a_safe_url_is_itself_the_link_and_the_host_stays()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = ctx.Render<OdsExternalLink>(p => p.Add(c => c.Url, "https://example.com/a").Add(c => c.Actions, false));

        var link = cut.Find("a.odc-extlink-urllink");
        Assert.Equal("https://example.com/a", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
        Assert.Equal("no-referrer", link.GetAttribute("referrerpolicy"));
        Assert.Equal("ltr", link.QuerySelector("bdi")!.GetAttribute("dir"));
        Assert.Equal("https://example.com/a (opens in a new tab)", OdsInfiniteListRemoteTests.SpokenText(link));
        Assert.Equal("example.com", cut.Find(".odc-extlink-host bdi").TextContent);
        Assert.Empty(cut.FindAll(".odc-extlink-actions, a.odc-extlink-open, button.odc-extlink-copy, [role=status]"));
    }

    [Fact]
    public void Without_actions_an_unsafe_url_stays_text_with_no_href()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = ctx.Render<OdsExternalLink>(p => p.Add(c => c.Url, "javascript:alert(1)").Add(c => c.Actions, false));

        Assert.Empty(cut.FindAll("a, [href]"));
        Assert.Equal("javascript:alert(1)", cut.Find("bdi.odc-extlink-url").TextContent);
        Assert.Equal("Not a web link", cut.Find(".odc-extlink-host").TextContent);
    }
}
