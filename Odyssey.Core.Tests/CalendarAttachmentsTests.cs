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

    // ------------------------------------------------------------------ One head reader for both passes (PR #529 S1)

    [Theory]
    [InlineData("ATTACH;X-ODYSSEY-KIND=\"PHOTO\";ENCODING=BASE64", true, true)]
    [InlineData("ATTACH;x-odyssey-kind=photo;encoding=base64", true, true)]
    [InlineData("ATTACH; X-ODYSSEY-KIND = PHOTO ;ENCODING= \"BASE64\" ", true, true)]
    [InlineData("ATTACH;FILENAME=\"a;X-ODYSSEY-KIND=PHOTO\";ENCODING=BASE64", true, false)]
    [InlineData("ATTACH;FILENAME=\"b;ENCODING=BASE64\";X-ODYSSEY-KIND=PHOTO", false, true)]
    [InlineData("ATTACH;FMTTYPE=text/plain", false, false)]
    public async Task Pass_one_and_the_spooler_read_every_head_the_same(string head, bool inline, bool photo)
    {
        var parsed = IcsAttachHead.Parse(head);
        Assert.Equal((inline, photo), (parsed.IsInline, parsed.IsInline && parsed.IsPhoto || (!parsed.IsInline && parsed.IsPhoto)));

        var file = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\n" + head + ":" + (inline ? "aGVsbG8=" : "odyssey-file:x") + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(file));
        var scanner = new IcsByteScanner(stream);
        await foreach (var _ in scanner.ReadEventsAsync())
        {
        }

        Assert.Equal(inline && photo, scanner.AttachmentKinds.HasPhotoInline);
        Assert.Equal(inline && !photo, scanner.AttachmentKinds.HasFileInline);

        using var reader = new StringReader(file);
        using var spool = await IcsAttachmentSpooler.SpoolAsync(reader, new IcsAttachmentSpooler.Options(1024, 1 << 20), default);
        Assert.Equal(inline ? 1 : 0, spool.Count);
        Assert.All(spool.Attachments, a => Assert.Equal(photo, a.IsPhoto));
    }

    [Fact]
    public async Task The_importer_refuses_inline_content_and_references_without_their_claims_at_the_point_of_use()
    {
        await using var context = TestContextFactory.CreateJournal();
        var importer = new CalendarAttachmentImporter(
            context, new FileValidationService(), null, null, "user", 1024, Path.GetTempPath(), _ => long.MaxValue,
            NullLogger.Instance) { Claims = new CalendarIcsImportClaims(FilesRead: true, FilesCreate: true, PhotosRead: false, PhotosCreate: false) };
        var photo = new SpooledAttachment { Reference = "r", IsPhoto = true, Problem = IcsAttachmentSpooler.DamagedProblem };

        var inline = await Assert.ThrowsAsync<CalendarAttachmentsException>(() =>
            importer.ResolveInlineAsync(photo, () => Task.FromResult(false), _ => { }, default));
        var reference = CalendarAttachmentImporter.Reference(
            CalendarLinkKind.Photo, Guid.NewGuid().ToString(), new string('a', 64), "3", _ => { });
        var linked = await Assert.ThrowsAsync<CalendarAttachmentsException>(() => importer.ResolveReferencesAsync([reference], _ => { }, default));

        Assert.Equal(CalendarAttachmentsException.ImportForbiddenCode, inline.Code);
        Assert.Contains("photos.read", inline.MissingClaims);
        Assert.Equal(["photos.read"], linked.MissingClaims);
    }

    [Theory]
    [InlineData(null, "3")]
    [InlineData("abc", null)]
    [InlineData(null, null)]
    public void A_reference_needs_its_id_hash_and_size_together(string? hash, string? size)
    {
        var skipped = new List<string>();
        var slot = CalendarAttachmentImporter.Reference(
            CalendarLinkKind.File, Guid.NewGuid().ToString(), hash is null ? null : new string('a', 64), size, skipped.Add);

        Assert.Equal(CalendarAttachSlotState.Skipped, slot.State);
        Assert.Equal([CalendarAttachmentImporter.InvalidReferenceReason], skipped);
    }

    // ------------------------------------------------------------------ Nested attachments (PR #529 S2)

    [Fact]
    public async Task An_inline_attachment_in_a_valarm_is_never_skimmed_so_a_large_one_hits_the_caps()
    {
        var base64 = Convert.ToBase64String(new byte[2 * 1024 * 1024]);
        var folded = string.Join("\r\n ", Enumerable.Range(0, (base64.Length + 73) / 74).Select(i => base64.Substring(i * 74, Math.Min(74, base64.Length - (i * 74)))));
        var file = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:alarm\r\nBEGIN:VALARM\r\nACTION:AUDIO\r\n"
            + "ATTACH;ENCODING=BASE64;VALUE=BINARY:" + folded + "\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(file));

        var refused = await Assert.ThrowsAsync<DomainValidationException>(async () =>
        {
            await foreach (var _ in new IcsByteScanner(stream).ReadEventsAsync())
            {
            }
        });

        Assert.Equal(IcsComponentSplitter.TooLargeMessage, refused.Message);
    }

    [Fact]
    public async Task A_small_inline_attachment_in_a_valarm_is_never_decoded_and_the_event_still_imports()
    {
        await using var context = TestContextFactory.CreateJournal();
        var calendar = new Odyssey.Context.Calendar { Name = "Work" };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        var service = new CalendarIcsService(
            context, NullLogger<CalendarIcsService>.Instance, new FakeImportExportLimitsLookup(),
            new ClientDateHandlingTests.StubJournalLimits(), new FakeTimeProvider(Now)) { TempFreeSpace = _ => 0 };
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nDTSTART:20260302T100000\r\nDTEND:20260302T110000\r\nSUMMARY:A\r\n"
            + "BEGIN:VALARM\r\nACTION:AUDIO\r\nTRIGGER:-PT5M\r\nATTACH;ENCODING=BASE64;VALUE=BINARY:aGVsbG8=\r\nEND:VALARM\r\n"
            + "END:VEVENT\r\nEND:VCALENDAR\r\n";

        // No claim at all, and no free disk: the nested value is no attachment of the event's, so neither is consulted.
        var result = await service.ImportAsync(
            calendar.CalendarId, new MemoryStream(Encoding.UTF8.GetBytes(ics)), Encoding.UTF8.GetByteCount(ics), "text/calendar", "user");

        Assert.Equal((1, 0, 0), (result.ImportedCount, result.StoredFileCount, service.AttachmentsDecoded));
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
    public void The_counter_discovers_both_attachment_tables()
    {
        using var context = TestContextFactory.CreateJournal();
        var discovered = FileReferenceCounter.Discover(context);
        Assert.Contains(new FileReference(typeof(CalendarEventAttachment), nameof(CalendarEventAttachment.FileId)), discovered);
        Assert.Contains(new FileReference(typeof(RecurrencePatternAttachment), nameof(RecurrencePatternAttachment.FileId)), discovered);
    }

    /// <summary>
    /// Invariant 1 has no database check (§4, SF5): CalendarLinkService is the only production code that adds to or
    /// removes from the four link tables, in the API, the worker and the core alike.
    /// </summary>
    [Fact]
    public void Only_CalendarLinkService_writes_the_four_calendar_link_tables()
    {
        var root = SolutionRoot();
        // The DbSets' writers, the generic Set<T>() route to them, and any construction of a link row. A context-level
        // Remove(row) is caught where the row comes from: a row can only be had from these sets or by construction.
        var writer = new Regex(
            @"\b(CalendarEventAttachments|CalendarEventPhotos|RecurrencePatternAttachments|RecurrencePatternPhotos)\s*\.\s*(Add|AddAsync|AddRange|AddRangeAsync|Remove|RemoveRange|Attach|AttachRange|Update|UpdateRange|Entry)\s*\("
            + @"|new\s+(CalendarEventAttachment|CalendarEventPhoto|RecurrencePatternAttachment|RecurrencePatternPhoto)\b"
            + @"|Set\s*<\s*(Odyssey\.Context\.)?(CalendarEventAttachment|CalendarEventPhoto|RecurrencePatternAttachment|RecurrencePatternPhoto)\s*>",
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
