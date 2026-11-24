using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The in-browser player's trust boundaries (issue #594 §4, security review): the episode title and content type come
/// from third-party feeds, the stream needs the session cookie, and the final save must carry the antiforgery header.
/// </summary>
public class PodcastPlayerSourceLintTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "js", "podcast-player.js"));

    private static string Code => Regex.Replace(Regex.Replace(Script, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"(?m)//.*$", string.Empty);

    [Theory]
    [InlineData("innerHTML")]
    [InlineData("outerHTML")]
    [InlineData("insertAdjacentHTML")]
    [InlineData("document.write")]
    [InlineData("crossorigin")]
    [InlineData("crossOrigin")]
    [InlineData("sendBeacon")]
    public void The_player_script_never_uses(string token) =>
        Assert.DoesNotContain(token, Code, StringComparison.Ordinal);

    [Fact]
    public void The_final_save_is_a_keepalive_put_with_the_token_header() =>
        Assert.Matches(@"method:\s*'PUT',\s*keepalive:\s*true", Code);

    [Fact]
    public void The_source_is_checked_against_the_api_origin_and_the_stream_route() =>
        Assert.Contains("url.origin === apiOrigin && new RegExp(pattern).test(url.pathname)", Code, StringComparison.Ordinal);

    /// <summary>A11y review (2.1.1): Alt/Ctrl/Meta/Shift + a seek key is the browser's (Back/Forward, scroll), never a seek.</summary>
    [Fact]
    public void The_seek_keys_leave_modified_keys_to_the_browser() =>
        Assert.Contains("e.altKey || e.ctrlKey || e.metaKey || e.shiftKey", Code, StringComparison.Ordinal);

    [Fact]
    public void No_podcast_component_renders_markup_from_a_string()
    {
        var files = Directory.GetFiles(Path.Combine(ClientSource.Root, "Pages", "Internet"), "Podcast*")
            .Concat(Directory.GetFiles(Path.Combine(ClientSource.Root, "Components"), "OdsPodcast*"));
        Assert.DoesNotContain(files, f => File.ReadAllText(f).Contains("MarkupString", StringComparison.Ordinal));
    }
}
