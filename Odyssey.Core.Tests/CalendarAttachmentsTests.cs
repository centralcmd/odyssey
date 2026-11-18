using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Interop;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Calendar event attachments (issue #521) at the unit tier: pass 1's skim and kinds, the spooler's line cap, the link
/// service's rules, the disk check, the series-delete bound and the single-writer source-lint.
/// </summary>
public class CalendarAttachmentsTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ Pass 1 (v3 §B.5)

    [Fact]
    public async Task Pass_one_skims_a_multi_megabyte_inline_attachment_past_the_line_and_component_caps_and_records_its_kind()
    {
        var base64 = Convert.ToBase64String(new byte[6 * 1024 * 1024]);
        var folded = string.Join("\r\n ", Enumerable.Range(0, (base64.Length + 73) / 74).Select(i => base64.Substring(i * 74, Math.Min(74, base64.Length - (i * 74)))));
        var file = "BEGIN:VCALENDAR\r\n"
            + "BEGIN:VEVENT\r\nUID:inline\r\nATTACH;FMTTYPE=application/pdf;ENCODING=BASE64;VALUE=BINARY:" + folded + "\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:photo-ref\r\nATTACH:odyssey-photo:" + Guid.NewGuid() + "\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:uri\r\nATTACH:https://example.com/a.pdf\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(file));
        var scanner = new IcsByteScanner(stream);

        var scanned = new List<ScannedEvent>();
        await foreach (var e in scanner.ReadEventsAsync())
        {
            scanned.Add(e);
        }

        Assert.Equal([true, false, false], scanned.Select(s => s.HasInlineAttachment));
        Assert.True(scanner.AttachmentKinds.HasFileInline);
        Assert.True(scanner.AttachmentKinds.HasPhotoRef);
        Assert.False(scanner.AttachmentKinds.HasFileRef); // https: has no kind (v3 §A.1)
        Assert.False(scanner.AttachmentKinds.HasPhotoInline);
    }

    [Fact]
    public async Task The_spooler_drops_attach_lines_past_its_limit_unparsed_and_counts_them()
    {
        var lines = string.Concat(Enumerable.Range(0, 30).Select(_ => $"ATTACH:odyssey-file:{Guid.NewGuid()}\r\n"));
        using var reader = new StringReader("BEGIN:VEVENT\r\nUID:a\r\n" + lines + "END:VEVENT\r\n");

        using var spool = await IcsAttachmentSpooler.SpoolAsync(reader, new IcsAttachmentSpooler.Options(1024, 1 << 20, MaxAttachLines: 4), default);

        Assert.Equal(26, spool.DroppedAttachLines);
        Assert.Equal(4, Regex.Matches(spool.CalendarText, "ATTACH:").Count);
    }

    [Fact]
    public async Task An_import_with_too_little_free_temporary_disk_for_one_attachment_is_a_507()
    {
        await using var context = TestContextFactory.CreateJournal();
        var calendar = new Odyssey.Context.Calendar { Name = "Work" };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        var service = new CalendarIcsService(
            context, NullLogger<CalendarIcsService>.Instance, new FakeImportExportLimitsLookup(),
            new ClientDateHandlingTests.StubJournalLimits(), new FakeTimeProvider(Now)) { TempFreeSpace = _ => 1024 };
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nDTSTART:20260302T100000\r\nDTEND:20260302T110000\r\nSUMMARY:A\r\n"
            + "ATTACH;FMTTYPE=text/plain;ENCODING=BASE64;VALUE=BINARY:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"))
            + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var refused = await Assert.ThrowsAsync<FileStorageFullException>(() => service.ImportAsync(
            calendar.CalendarId, new MemoryStream(Encoding.UTF8.GetBytes(ics)), Encoding.UTF8.GetByteCount(ics), "text/calendar", "user",
            attachmentClaims: CalendarIcsImportClaims.Trusted));

        Assert.Equal(507, refused.StatusCode);
        Assert.False(await context.CalendarEvents.AnyAsync());
    }

    // ------------------------------------------------------------------ CalendarLinkService

    [Fact]
    public void Links_on_a_generated_occurrence_are_a_422_keyed_on_their_array_but_null_and_empty_pass()
    {
        CalendarLinkService.EnsureNoOccurrenceLinks(null, null);
        CalendarLinkService.EnsureNoOccurrenceLinks([], []);

        var files = Assert.Throws<DomainUnprocessableException>(() => CalendarLinkService.EnsureNoOccurrenceLinks([Guid.NewGuid()], null));
        var photos = Assert.Throws<DomainUnprocessableException>(() => CalendarLinkService.EnsureNoOccurrenceLinks(null, [Guid.NewGuid()]));

        Assert.True(files.Errors!.ContainsKey(CalendarLinkService.FilesField));
        Assert.True(photos.Errors!.ContainsKey(CalendarLinkService.PhotosField));
    }

    [Fact]
    public async Task A_replace_reports_only_what_changed()
    {
        await using var context = TestContextFactory.CreateJournal();
        var eventId = Guid.NewGuid();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var rows = new List<CalendarEventAttachment>
        {
            new() { CalendarEventId = eventId, FileId = a, CreatedAt = Now },
            new() { CalendarEventId = eventId, FileId = b, CreatedAt = Now },
        };
        context.CalendarEventAttachments.AddRange(rows);
        await context.SaveChangesAsync();

        var same = CalendarLinkService.ReplaceEventLinks(context, eventId, [.. rows], [], new CalendarLinkService.ImportReplace([a, b], null), Now);
        var narrowed = CalendarLinkService.ReplaceEventLinks(context, eventId, [.. rows], [], new CalendarLinkService.ImportReplace([a], null), Now);

        Assert.Equal((false, 0), (same.Changed, same.Removed));
        Assert.Equal((true, 1), (narrowed.Changed, narrowed.Removed));
    }

    [Fact]
    public async Task A_detach_copy_keeps_each_links_created_at_and_position()
    {
        await using var context = TestContextFactory.CreateJournal();
        var patternId = Guid.NewGuid();
        var created = new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        context.RecurrencePatternAttachments.Add(new RecurrencePatternAttachment { RecurrencePatternId = patternId, FileId = Guid.NewGuid(), CreatedAt = created });
        context.RecurrencePatternPhotos.Add(new RecurrencePatternPhoto { RecurrencePatternId = patternId, PhotoId = Guid.NewGuid(), Position = 3, CreatedAt = created });
        await context.SaveChangesAsync();
        var rows = Enumerable.Range(0, 3).Select(_ => new Odyssey.Context.CalendarEvent
        {
            CalendarEventId = Guid.NewGuid(), CalendarId = Guid.NewGuid(), Title = "x",
        }).ToList();

        await CalendarLinkService.StageCopyToDetachedAsync(context, patternId, rows, default);
        await context.SaveChangesAsync();

        Assert.Equal(3, await context.CalendarEventAttachments.CountAsync(c => c.CreatedAt == created));
        Assert.Equal(3, await context.CalendarEventPhotos.CountAsync(c => c.Position == 3 && c.CreatedAt == created));
    }

    // ------------------------------------------------------------------ Guards

    [Fact]
    public void The_series_delete_copy_is_bounded_at_two_hundred_thousand_rows()
    {
        // v4 §6: raising either bound re-opens the series-delete copy and AC 5's budget.
        Assert.True(SystemSettingsBounds.RecurrenceMaxGeneratedOccurrencesMax * 2 * SystemSettingsBounds.CalendarEventMaxLinksPerKindMax <= 200_000);
        Assert.InRange(SystemSettingsDefaults.CalendarEventMaxLinksPerKind,
            SystemSettingsBounds.CalendarEventMaxLinksPerKindMin, SystemSettingsBounds.CalendarEventMaxLinksPerKindMax);
    }

    [Fact]
    public void Both_attachment_tables_file_their_content_under_journal_and_the_counter_discovers_them()
    {
        Assert.Equal(StoredFileArea.Journal, FileAreaClassifier.AreaOfReference[typeof(CalendarEventAttachment)]);
        Assert.Equal(StoredFileArea.Journal, FileAreaClassifier.AreaOfReference[typeof(RecurrencePatternAttachment)]);
        using var context = TestContextFactory.CreateJournal();
        var discovered = FileReferenceCounter.Discover(context);
        Assert.Contains(new FileReference(typeof(CalendarEventAttachment), nameof(CalendarEventAttachment.FileId)), discovered);
        Assert.Contains(new FileReference(typeof(RecurrencePatternAttachment), nameof(RecurrencePatternAttachment.FileId)), discovered);
    }

    [Fact]
    public void The_file_reference_guard_names_both_calendar_attachment_tables()
    {
        var source = File.ReadAllText(Path.Combine(SolutionRoot(), "Odyssey.Core", "Finance", "Lookups", "FileReferenceGuard.cs"));
        Assert.Contains("context.CalendarEventAttachments", source, StringComparison.Ordinal);
        Assert.Contains("context.RecurrencePatternAttachments", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Invariant 1 has no database check (§4, SF5): CalendarLinkService is the only production code that adds to or
    /// removes from the four link tables, in the API, the worker and the core alike.
    /// </summary>
    [Fact]
    public void Only_CalendarLinkService_writes_the_four_calendar_link_tables()
    {
        var root = SolutionRoot();
        var writer = new Regex(
            @"\b(CalendarEventAttachments|CalendarEventPhotos|RecurrencePatternAttachments|RecurrencePatternPhotos)\s*\.\s*(Add|AddRange|Remove|RemoveRange|Attach|Update)\s*\(|new\s+(CalendarEventAttachment|CalendarEventPhoto|RecurrencePatternAttachment|RecurrencePatternPhoto)\b",
            RegexOptions.CultureInvariant);
        var scanned = 0;
        var offenders = new List<string>();
        foreach (var project in new[] { "Odyssey.Core", "Odyssey.Api", "Odyssey.Worker" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                scanned++;
                if (Path.GetFileName(path) != "CalendarLinkService.cs" && writer.IsMatch(File.ReadAllText(path)))
                {
                    offenders.Add(Path.GetRelativePath(root, path));
                }
            }
        }

        Assert.True(scanned > 100, "The lint scanned almost nothing: its paths are wrong.");
        Assert.True(offenders.Count == 0, "Write the calendar link tables through CalendarLinkService: " + string.Join(", ", offenders));
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the solution root.");
    }
}
