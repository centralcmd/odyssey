using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The OPML report's <c>news.css</c> rules (issue #509 §9, A6, AC 13, AC 22): bUnit applies no CSS, so the declarations
/// that make a row wrap, a list grow without a scroll region and the tiles stack at phone width are pinned from the
/// stylesheet. Colours are tokens only, which is what the contrast check (AC 23) rests on.
/// </summary>
public class ImportNewsFeedsStylesTests
{
    private static readonly Lazy<string> Css = new(() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "news.css")));

    private static string Block()
    {
        var css = Css.Value;
        var start = css.IndexOf("/* ---------- OPML import and export", StringComparison.Ordinal);
        Assert.True(start >= 0, "news.css has no OPML block.");
        var end = css.IndexOf("/* ----------", start + 1, StringComparison.Ordinal);
        return end < 0 ? css[start..] : css[start..end];
    }

    [Fact] // §9: a URL is never cut by the shared nowrap / ellipsis item
    public void A_report_row_wraps_anywhere()
    {
        Assert.Contains(".cal-imp-skip-item.nw-opml-row span,", Block(), StringComparison.Ordinal);
        Assert.Matches(@"\.cal-imp-skip-item\.nw-opml-row bdi \{[^}]*white-space: normal;[^}]*overflow-wrap: anywhere;", Block());
    }

    [Fact] // A6: no scroll region to reach by keyboard, and `hidden` still hides the collapsed list
    public void A_group_list_has_no_max_height_and_honours_hidden()
    {
        Assert.Contains(".cal-imp-skip-list.nw-opml-list { max-height: none; overflow: visible; }", Block(), StringComparison.Ordinal);
        Assert.Contains(".cal-imp-skip-list.nw-opml-list[hidden] { display: none; }", Block(), StringComparison.Ordinal);
    }

    [Fact] // AC 22: the three tiles stack at phone width
    public void The_tiles_stack_at_phone_width() =>
        Assert.Matches(@"@media \(max-width: \d+px\) \{ \.cal-imp-stats\.nw-opml-stats \{ grid-template-columns: minmax\(0, 1fr\); \} \}", Block());

    [Fact] // §9, AC 23: tokens only
    public void The_block_uses_tokens_only()
    {
        var block = Regex.Replace(Block(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b", block);
        Assert.DoesNotMatch(@"rgba?\(", block);
        Assert.DoesNotMatch(@"(?<![\w(-])\d+px(?!\))", Regex.Replace(block, @"@media[^{]*", string.Empty));
    }
}
