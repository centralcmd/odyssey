using System.Text.RegularExpressions;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints for issue #522: the cap is never a client constant (§8), every calendar write names its links (goal 5),
/// no file-derived string is built as markup (SX7), and the photo tiles keep their target size, focus ring and motion
/// rules (AC 11). The behaviour behind them is rendered in <see cref="CalendarAttachmentDialogTests"/>.
/// </summary>
public class CalendarAttachmentSourceLintTests
{
    private static string Code(string relative) =>
        SourceLint.StripComments(File.ReadAllText(Path.Combine(ClientSource.Root, relative)));

    private static string Raw(string relative) => File.ReadAllText(Path.Combine(ClientSource.Root, relative));

    [Fact] // §8 (SX5, O1): no client file holds the link cap as a number, and the fallback holds none
    public void The_link_cap_is_never_a_client_literal()
    {
        var literal = new Regex(@"CalendarEventMaxLinksPerKind\s*(=|\?\?|:)\s*\d");
        var offenders = ClientSource.SourceFiles()
            .Where(f => literal.IsMatch(SourceLint.StripComments(File.ReadAllText(f))))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.True(offenders.Count == 0, "The calendar link cap is read from IImportLimitsCache, never a literal: " + string.Join(", ", offenders));
        Assert.Matches(@"CalendarEventMaxLinksPerKind\s*=\s*null", Code("Services/ImportLimitsCache.cs"));
        Assert.Contains("ImportLimits.GetAsync()", Code("Pages/Calendar/CalendarEventDialog.razor.cs"), StringComparison.Ordinal);
    }

    [Theory] // goal 5 (B1, B2, SX1): every builder takes the links as a required argument
    [InlineData(@"NewCalendarEvent Event\([^)]*AttachmentLinks attachments, LocationLink link\)")]
    [InlineData(@"NewCalendarEvent Retimed\([^)]*AttachmentLinks attachments\)")]
    [InlineData(@"NewRecurrencePattern Pattern\([\s\S]*?AttachmentLinks attachments, LocationLink link\)")]
    public void Every_calendar_builder_takes_the_links(string signature)
    {
        Assert.Matches(new Regex(signature), Code("Pages/Calendar/CalendarWrites.cs"));
    }

    [Fact] // goal 5: the link arrays are set in CalendarWrites alone, so no write can drop or invent them elsewhere
    public void The_link_arrays_are_set_only_in_calendar_writes()
    {
        var assignment = new Regex(@"\b(AttachmentFileIds|PhotoIds)\s*=(?!=)");
        var offenders = ClientSource.SourceFiles()
            .Select(f => (File: ClientSource.Relative(f).Replace(Path.DirectorySeparatorChar, '/'), Text: SourceLint.StripComments(File.ReadAllText(f))))
            .Where(f => f.File.StartsWith("Pages/Calendar/", StringComparison.Ordinal) && f.File != "Pages/Calendar/CalendarWrites.cs")
            .Where(f => assignment.IsMatch(f.Text))
            .Select(f => f.File)
            .ToList();

        Assert.Empty(offenders);
        Assert.DoesNotContain("CalendarWrites.Retimed(existing, newStart, newEnd)", Code("Pages/Calendar/CalendarPage.razor.cs"), StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(Code("Pages/Calendar/CalendarPage.razor.cs"), @"AttachmentLinks\.Keep\)").Count);
    }

    [Theory] // SX7: the attachment surfaces build no markup from a string
    [InlineData("Pages/Attachments/AttachmentLinkFields.razor")]
    [InlineData("Pages/Calendar/CalendarEventLinks.razor")]
    [InlineData("Pages/Calendar/ExportAttachmentsDialog.razor")]
    [InlineData("Pages/Calendar/ImportCalendarDialog.razor")]
    public void No_attachment_surface_renders_a_markup_string(string file)
    {
        Assert.DoesNotMatch(@"MarkupString|innerHTML|AddMarkupContent", Code(file));
    }

    [Fact] // design system 1795477868-c9a2: the Photos field is the shared tile in a dense 96px grid, with no page-local tile CSS
    public void The_photo_field_draws_the_shared_tile_not_its_own()
    {
        var markup = Code("Pages/Attachments/AttachmentLinkFields.razor");
        Assert.Matches(@"<OdsPhotoGrid\b[^>]*Dense=""true""[^>]*MinTile=""96""", markup);
        Assert.Contains("<OdsPhotoTile", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<OdsPhotoImage", markup, StringComparison.Ordinal);

        var css = Raw("Pages/Attachments/AttachmentLinkFields.razor.css");
        Assert.DoesNotMatch(@"\.jef-photo", css);
        Assert.DoesNotMatch(@"\.odc-phototile", css);
    }

    [Theory] // review 1: every class the export step renders is styled, with the design system's values
    [InlineData(@"\.cal-exp-att-body\s*\{\s*display: flex; flex-direction: column; gap: 12px; \}")]
    [InlineData(@"\.cal-exp-att-check\s*\{\s*display: flex; flex-direction: column; gap: 4px; \}")]
    [InlineData(@"\.cal-exp-att-check\.is-disabled \.odc-check\s*\{\s*opacity: 0\.6; cursor: not-allowed; \}")]
    [InlineData(@"\.cal-exp-att-help\s*\{\s*margin: 0 0 0 28px;")]
    public void The_export_step_classes_are_styled(string rule)
    {
        Assert.Matches(rule, Raw("wwwroot/css/odyssey-components.css"));
    }

    [Fact] // review 2: the event dialog's footer wraps at a narrow width instead of clipping its actions
    public void The_event_dialog_footer_wraps()
    {
        Assert.Contains("Class=\"cal-event-dialog\"", Code("Pages/Calendar/CalendarEventDialog.razor"), StringComparison.Ordinal);
        Assert.Matches(@"\.mud-dialog\.cal-event-dialog \.mud-dialog-actions\.ods-modal-foot\s*\{[^}]*flex-wrap: wrap",
            Raw("wwwroot/css/odyssey-components.css"));
    }

    [Fact] // AC 11: reduced motion — the skeleton pulses only when motion is welcome, and the photo image has no motion of its own
    public void Reduced_motion_is_honoured()
    {
        var css = Raw("Pages/Attachments/AttachmentLinkFields.razor.css");
        var outside = Regex.Replace(css, @"@media \(prefers-reduced-motion: no-preference\)\s*\{[^{}]*\{[^}]*\}\s*\}", string.Empty);
        Assert.DoesNotMatch(@"animation\s*:", Regex.Replace(outside, @"@keyframes[^{]*\{[^{}]*\{[^}]*\}\s*\}", string.Empty));
        Assert.Matches(@"@media \(prefers-reduced-motion: no-preference\)\s*\{\s*\.alf-skel\s*\{\s*animation", css);

        var photoImage = Regex.Match(Raw("wwwroot/css/odyssey-components.css"), @"\.odc-photoimg\s*\{([^}]*)\}").Groups[1].Value;
        Assert.DoesNotMatch("transition|animation", photoImage);
    }

    [Fact] // AC 13: the journal's fields are the shared component, with the journal's own sentences
    public void The_journal_uses_the_shared_fields()
    {
        var journal = Code("Pages/Journal/JournalEntryFields.razor");
        Assert.Contains("<AttachmentLinkFields", journal, StringComparison.Ordinal);
        Assert.DoesNotContain("<AttachPhotosDialog", journal, StringComparison.Ordinal);
        Assert.DoesNotContain("StageAttachmentAsync", journal, StringComparison.Ordinal);
        Assert.Contains("Area=\"Odyssey.Dtos.FileUploadArea.Journal\"", Code("Pages/Attachments/AttachmentLinkFields.razor"), StringComparison.Ordinal);
    }
}
