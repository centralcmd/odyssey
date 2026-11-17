using System.Net;
using Bunit;
using Microsoft.Extensions.Time.Testing;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>NewsArticleBody</c> (issue #473 §3.2.6, AC 8) and <c>NewsStoreGate</c> (§4.1, §7.2, §7.3): paragraphs and line
/// breaks as encoded text, collapsed blank runs, the 50-then-200 chunking with focus on the first new paragraph; and
/// the store gate's classification and Retry-After wait.
/// </summary>
public class NewsArticleBodyTests
{
    [Fact]
    public void Paragraphs_split_on_blank_lines_and_single_breaks_become_br()
    {
        Assert.Equal([["One", "line two"], ["Three"]], NewsArticleBody.Split("One\r\nline two\n\n\n\n  \nThree\n"));
        Assert.Empty(NewsArticleBody.Split("  \n\n "));
        Assert.Empty(NewsArticleBody.Split(null));
    }

    [Fact]
    public async Task It_renders_encoded_paragraphs_with_br_and_lang()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<NewsArticleBody>(p => p.Add(b => b.Body, "<b>Bold</b>\nnext\n\nSecond").Add(b => b.Lang, "nb"));

        Assert.Equal("nb", cut.Find(".nw-body").GetAttribute("lang"));
        Assert.Equal(2, cut.FindAll("p").Count);
        Assert.Single(cut.FindAll("p br"));
        Assert.Empty(cut.FindAll("b"));
        Assert.Contains("<b>Bold</b>", cut.Find("p").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_maximum_length_body_renders_the_first_chunk_then_the_next_per_press_and_focuses_the_first_new_paragraph()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // A body at the API's cap, one short paragraph per blank-line-separated block.
        var paragraph = "Lorem ipsum dolor sit amet.";
        var count = NewsLimits.MaxBodyLength / (paragraph.Length + 2);
        var body = string.Join("\n\n", Enumerable.Repeat(paragraph, count));
        Assert.True(body.Length <= NewsLimits.MaxBodyLength);

        var cut = ctx.Render<NewsArticleBody>(p => p.Add(b => b.Body, body).Add(b => b.IdPrefix, "t"));

        Assert.Equal(NewsArticleBody.FirstChunk, cut.FindAll("p").Count);
        var more = count - NewsArticleBody.FirstChunk;
        Assert.Contains($"Show the rest ({more:N0} more paragraphs)", cut.Find("button").TextContent, StringComparison.Ordinal);

        cut.Find("button").Click();

        Assert.Equal(NewsArticleBody.FirstChunk + NewsArticleBody.NextChunk, cut.FindAll("p").Count);
        var first = cut.Find($"#t-p{NewsArticleBody.FirstChunk}");
        Assert.Equal("-1", first.GetAttribute("tabindex"));
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById" && (string?)i.Arguments[0] == $"t-p{NewsArticleBody.FirstChunk}");
        Assert.Null(cut.Find("#t-p0").GetAttribute("tabindex"));
    }

    [Fact]
    public async Task One_paragraph_left_says_paragraph()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var body = string.Join("\n\n", Enumerable.Range(0, NewsArticleBody.FirstChunk + 1).Select(i => $"P{i}"));

        var cut = ctx.Render<NewsArticleBody>(p => p.Add(b => b.Body, body));

        Assert.Contains("Show the rest (1 more paragraph)", cut.Find("button").TextContent, StringComparison.Ordinal);
    }

    // ── NewsStoreGate ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_gate_classifies_storage_answers_and_waits_out_their_retry_after()
    {
        var time = new FakeTimeProvider(NewsPageTestContext.Now);
        var gate = new NewsStoreGate(time);

        Assert.Equal(NewsStoreOutcome.Ok, gate.Observe(NewsPageTestContext.Ok(new NewsArticleDetail())));
        Assert.Equal(NewsStoreOutcome.Failed, gate.Observe(NewsPageTestContext.Fail<NewsArticleDetail>(HttpStatusCode.InternalServerError)));
        Assert.Null(gate.Remaining);
        Assert.Equal(NewsStoreOutcome.Failed, gate.Observe(NewsPageTestContext.Busy<NewsArticleDetail>()));
        Assert.Null(gate.Remaining);

        Assert.Equal(NewsStoreOutcome.StorageUnavailable,
            gate.Observe(NewsPageTestContext.Fail<NewsArticleDetail>(HttpStatusCode.ServiceUnavailable, NewsErrorCodes.StorageUnavailable, TimeSpan.FromSeconds(25))));
        Assert.Equal(25, gate.RemainingSeconds);
        var wait = gate.WaitAsync();
        Assert.False(wait.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(25));
        await wait;
        Assert.Null(gate.Remaining);

        gate.Observe(NewsPageTestContext.Fail<NewsArticleDetail>(HttpStatusCode.ServiceUnavailable, NewsErrorCodes.StorageUnavailable));
        Assert.Equal((int)NewsStoreGate.DefaultWait.TotalSeconds, gate.RemainingSeconds);
    }
}
