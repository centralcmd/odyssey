using Bunit;
using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>OdsRecordSection</c> — one band inside a record card body: the section divider, an optional
/// refused-write notice, and either the section's own view or the muted empty line
/// (Odyssey Design System · components/RecordSection).
/// </summary>
/// <remarks>
/// The two behaviours worth pinning are the ones a call site no longer re-decides: that
/// <c>Empty</c> swaps the view for the line rather than rendering both, and that the notice's tone
/// drives both its class and its default icon. The tone matters beyond styling — <c>Warning</c> is
/// for a REVERSIBLE state the reader can undo, neutral for a standing limit like a cap — so a tone
/// that silently stopped reaching the markup would misdescribe which of the two a refusal is.
/// </remarks>
public class OdsRecordSectionTests
{
    private static IRenderedComponent<OdsRecordSection> Render(
        Action<ComponentParameterCollectionBuilder<OdsRecordSection>> configure)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx.Render(configure);
    }

    [Fact]
    public void A_labelled_section_renders_its_divider_with_the_meta_note()
    {
        var cut = Render(p => p
            .Add(s => s.Label, "Documents")
            .Add(s => s.Meta, "2 files")
            .Add(s => s.ChildContent, "<table></table>"));

        Assert.Equal("Documents", cut.Find(".odc-sectiondivider-l").TextContent.Trim());
        Assert.Equal("2 files", cut.Find(".odc-sectiondivider-meta").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll("table"));
    }

    /// <summary>
    /// The divider label carries the heading level so a record body is navigable by section, and the
    /// default sits under the record card's own level-2 trigger.
    /// </summary>
    [Fact]
    public void The_divider_label_is_a_level_three_heading_by_default()
    {
        var cut = Render(p => p.Add(s => s.Label, "Terms"));

        var label = cut.Find(".odc-sectiondivider-l");
        Assert.Equal("heading", label.GetAttribute("role"));
        Assert.Equal("3", label.GetAttribute("aria-level"));
    }

    [Fact]
    public void A_section_with_no_label_renders_no_divider()
    {
        var cut = Render(p => p.Add(s => s.ChildContent, "<p>body</p>"));

        Assert.Empty(cut.FindAll(".odc-sectiondivider"));
        Assert.NotEmpty(cut.FindAll("p"));
    }

    /// <summary>
    /// <c>Empty</c> SWAPS the view for the line; it does not render both. A call site passing both
    /// (the ordinary case — the children are built unconditionally) must not leak an empty table
    /// frame in under the sentence that says there is nothing in it.
    /// </summary>
    [Fact]
    public void Empty_replaces_the_view_with_the_line()
    {
        var cut = Render(p => p
            .Add(s => s.Label, "Files")
            .Add(s => s.Empty, true)
            .Add(s => s.EmptyText, "No files attached to this account yet.")
            .Add(s => s.ChildContent, "<table id=\"rows\"></table>"));

        Assert.Equal(
            "No files attached to this account yet.",
            cut.Find(".odc-empty.line").TextContent.Trim());
        Assert.Empty(cut.FindAll("#rows"));
    }

    [Fact]
    public void A_non_empty_section_renders_its_view_and_no_line()
    {
        var cut = Render(p => p
            .Add(s => s.Label, "Files")
            .Add(s => s.Empty, false)
            .Add(s => s.EmptyText, "No files attached to this account yet.")
            .Add(s => s.ChildContent, "<table id=\"rows\"></table>"));

        Assert.NotEmpty(cut.FindAll("#rows"));
        Assert.Empty(cut.FindAll(".odc-empty.line"));
    }

    [Theory]
    [InlineData(OdsEmptyLineAlign.Center, OdsSize.Lg, "odc-empty line center pad-lg")]
    [InlineData(OdsEmptyLineAlign.Start, OdsSize.Md, "odc-empty line")]
    public void The_empty_line_forwards_its_align_and_pad(
        OdsEmptyLineAlign align, OdsSize pad, string expected)
    {
        var cut = Render(p => p
            .Add(s => s.Empty, true)
            .Add(s => s.EmptyText, "Nothing here.")
            .Add(s => s.EmptyAlign, align)
            .Add(s => s.EmptyPad, pad));

        Assert.Equal(expected, cut.Find(".odc-empty").ClassName);
    }

    // ── The notice band ──────────────────────────────────────────────────────

    [Fact]
    public void No_notice_renders_when_none_is_given()
    {
        var cut = Render(p => p.Add(s => s.Label, "Terms"));

        Assert.Empty(cut.FindAll(".odc-recordsection-notice"));
        Assert.Empty(cut.FindAll("[role='note']"));
    }

    /// <summary>
    /// Neutral is the default tone — a standing limit such as a cap, which the reader cannot undo.
    /// Its icon says so, and the warning modifier class must NOT be on it.
    /// </summary>
    [Fact]
    public void A_default_toned_notice_is_neutral_with_the_info_icon()
    {
        var cut = Render(p => p
            .Add(s => s.Label, "Terms")
            .Add(s => s.Notice, "This contract has reached its limit of 500 terms."));

        var notice = cut.Find(".odc-recordsection-notice");
        Assert.Equal("odc-recordsection-notice", notice.ClassName);
        Assert.Equal("note", notice.GetAttribute("role"));
        Assert.Equal("info", notice.QuerySelector(".material-icons")!.TextContent.Trim());
        Assert.Contains("limit of 500 terms", notice.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_warning_toned_notice_carries_the_modifier_and_its_own_icon()
    {
        var cut = Render(p => p
            .Add(s => s.Label, "Terms")
            .Add(s => s.Notice, "This record is archived.")
            .Add(s => s.NoticeTone, OdsRecordSectionTone.Warning));

        var notice = cut.Find(".odc-recordsection-notice");
        Assert.Contains("warning", notice.ClassName, StringComparison.Ordinal);
        Assert.Equal("inventory_2", notice.QuerySelector(".material-icons")!.TextContent.Trim());
    }

    /// <summary>
    /// The icon is decorative: the sentence carries the whole message, so the state never rides on a
    /// glyph or a colour alone (WCAG 1.4.1) and the glyph's own ligature text must not be announced.
    /// </summary>
    [Fact]
    public void The_notice_icon_is_hidden_from_assistive_technology()
    {
        var cut = Render(p => p
            .Add(s => s.Notice, "This record is archived.")
            .Add(s => s.NoticeTone, OdsRecordSectionTone.Warning));

        Assert.Equal("true", cut.Find(".odc-recordsection-notice .material-icons").GetAttribute("aria-hidden"));
    }

    [Fact]
    public void An_explicit_notice_icon_overrides_the_tone_default()
    {
        var cut = Render(p => p
            .Add(s => s.Notice, "At the cap.")
            .Add(s => s.NoticeIconName, "production_quantity_limits"));

        Assert.Equal(
            "production_quantity_limits",
            cut.Find(".odc-recordsection-notice .material-icons").TextContent.Trim());
    }

    /// <summary>A notice is about the SECTION, so it renders beside an empty one too.</summary>
    [Fact]
    public void A_notice_renders_alongside_the_empty_line()
    {
        var cut = Render(p => p
            .Add(s => s.Label, "Terms")
            .Add(s => s.Empty, true)
            .Add(s => s.EmptyText, "No terms yet.")
            .Add(s => s.Notice, "At the cap."));

        Assert.NotEmpty(cut.FindAll(".odc-recordsection-notice"));
        Assert.NotEmpty(cut.FindAll(".odc-empty.line"));
    }

    [Fact]
    public void Id_and_class_reach_the_root_section_element()
    {
        var cut = Render(p => p
            .Add(s => s.Id, "acct-files")
            .Add(s => s.Class, "acct-section"));

        var root = cut.Find("section");
        Assert.Equal("acct-files", root.Id);
        Assert.Equal("odc-recordsection acct-section", root.ClassName);
    }
}
