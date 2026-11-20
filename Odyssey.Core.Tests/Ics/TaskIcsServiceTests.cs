using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Interop;
using Odyssey.Dtos.Journal;
using Xunit;
using ContextJournalTaskTag = Odyssey.Context.JournalTaskTag;

namespace Odyssey.Core.Tests.Ics;

/// <summary>
/// VTODO/.ics import and export rules (issue #337 and its follow-ups) at the service tier: status and date mapping,
/// skips, UID matching, positions, link resolution and caps, the inline files and their dedupe. The endpoints' claim
/// gates, status codes and headers stay in <c>Odyssey.Api.Tests/TaskIcsApiTests</c>.
/// </summary>
public class TaskIcsServiceTests : IcsServiceTestBase
{
    private static readonly byte[] NoteBytes = Encoding.UTF8.GetBytes("Packing list: tent, stove, map.\n");

    private static readonly byte[] SeededFileContent = [1, 2, 3];

    private static readonly JournalTaskStatus[] AllStatuses =
        [JournalTaskStatus.Backlog, JournalTaskStatus.Doing, JournalTaskStatus.Done, JournalTaskStatus.Archived];

    /// <summary><c>files.read</c> and <c>files.create</c>, as the controller hands them to the service.</summary>
    private sealed record Claims(bool FilesRead = false, bool FilesCreate = false);

    private static readonly Claims ReadWrite = new();
    private static readonly Claims WithFiles = new(FilesRead: true);
    private static readonly Claims WithUploads = new(FilesRead: true, FilesCreate: true);

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Export_EmptyBoard_IsAValidVCalendar_WithNoVTodo()
    {
        var body = await ExportAsync();

        Assert.Contains("BEGIN:VCALENDAR", body);
        Assert.Contains("END:VCALENDAR", body);
        Assert.DoesNotContain("BEGIN:VTODO", body);
    }

    [Fact]
    public async Task Export_FileName_IsTimestampPrefixed()
    {
        string? name = null;
        await InScopeAsync(sp => sp.GetRequiredService<TaskIcsService>().ExportStreamingAsync(
            new(), false, new MemoryStream(), (fileName, _) => name = fileName));

        Assert.Matches(@"^odyssey-tasks-\d{8}-\d{6}Z\.ics$", name!);
    }

    [Fact]
    public async Task Export_Task_EmitsVTodo_WithUidSummaryStatusDueCategoriesAttach()
    {
        var tagId = await SeedTagAsync("Home");
        var fileId = await SeedFileWithContentAsync("spec.pdf", "application/pdf", SeededFileContent);
        var created = await CreateAsync(new NewJournalTask
        {
            Title = "Buy milk",
            Content = "2%",
            Status = JournalTaskStatus.Doing,
            Deadline = new DateOnly(2030, 8, 1),
            TagIds = [tagId],
            AttachmentFileIds = [fileId],
        });

        var body = await ExportAsync(claims: WithFiles);

        Assert.Equal(1, Count(body, "BEGIN:VTODO"));
        Assert.Contains($"UID:{created.ExternalUid}", body);
        Assert.Contains("SUMMARY:Buy milk", body);
        Assert.Contains("STATUS:IN-PROCESS", body);
        // Doing ⇒ DTSTART (DATE-TIME) is present, so DUE must be a DATE-TIME too (RFC 5545 §3.8.2.3).
        Assert.Contains("DTSTART:", body);
        Assert.Contains("DUE:20300801T000000Z", body);
        Assert.DoesNotContain("DUE;VALUE=DATE", body);
        Assert.Contains("CATEGORIES:Home", body);
        // The attached file travels inline — its bytes, name, type, id and hash — not as a bare reference.
        Assert.DoesNotContain($"ATTACH:odyssey-file:{fileId}", body);
        Assert.Contains("ATTACH;FMTTYPE=application/pdf;ENCODING=BASE64;VALUE=BINARY;FILENAME=\"spec.pdf\";SIZE=3;", Unfold(body));
        Assert.Contains($"X-ODYSSEY-FILE-ID={fileId}", Unfold(body));
        Assert.Contains(":" + Convert.ToBase64String(SeededFileContent) + "\r\n", Unfold(body));
        Assert.Contains("PERCENT-COMPLETE:0", body);
    }

    [Fact]
    public async Task Export_DueValueType_MatchesDtstartPresence()
    {
        // Backlog ⇒ no DTSTART ⇒ DUE is a floating VALUE=DATE.
        await CreateAsync(new NewJournalTask { Title = "Backlog due", Deadline = new DateOnly(2030, 9, 9) });
        // Doing ⇒ DTSTART present ⇒ DUE must be a DATE-TIME, never VALUE=DATE (else strict readers reject it).
        await CreateAsync(new NewJournalTask { Title = "Doing due", Status = JournalTaskStatus.Doing, Deadline = new DateOnly(2030, 10, 10) });

        var body = await ExportAsync(new() { Statuses = [JournalTaskStatus.Backlog, JournalTaskStatus.Doing] });

        Assert.Contains("DUE;VALUE=DATE:20300909", body);
        Assert.Contains("DUE:20301010T000000Z", body);
    }

    [Fact]
    public async Task Export_HidesArchivedByDefault_IncludesWhenRequested()
    {
        var archived = await CreateAsync(new NewJournalTask { Title = "Old", Status = JournalTaskStatus.Archived });

        Assert.DoesNotContain(archived.ExternalUid, await ExportAsync());

        var shown = await ExportAsync(new() { Statuses = [JournalTaskStatus.Archived] });
        Assert.Contains(archived.ExternalUid, shown);
        Assert.Contains("STATUS:CANCELLED", shown);
    }

    [Fact]
    public async Task Export_ByIds_ReturnsOnlyThoseTasks()
    {
        var a = await CreateAsync(new NewJournalTask { Title = "Keep" });
        var b = await CreateAsync(new NewJournalTask { Title = "Drop" });

        var body = await ExportAsync(new() { Ids = [a.JournalTaskId] });

        Assert.Equal(1, Count(body, "BEGIN:VTODO"));
        Assert.Contains(a.ExternalUid, body);
        Assert.DoesNotContain(b.ExternalUid, body);
    }

    [Fact]
    public async Task Export_ByIds_WithAllStatuses_ExportsArchivedTask()
    {
        var archived = await CreateAsync(new NewJournalTask { Title = "Archived one", Status = JournalTaskStatus.Archived });

        // The row menu pairs ids with all statuses so an archived task still exports.
        var body = await ExportAsync(new() { Statuses = AllStatuses, Ids = [archived.JournalTaskId] });

        Assert.Equal(1, Count(body, "BEGIN:VTODO"));
        Assert.Contains(archived.ExternalUid, body);
        Assert.Contains("STATUS:CANCELLED", body);
    }

    [Fact]
    public async Task Export_WithAttachedFile_WithoutFilesRead_IsForbidden_AndSoIsTheSummary()
    {
        var fileId = await SeedFileWithContentAsync("note.txt", "text/plain", NoteBytes);
        await CreateAsync(new NewJournalTask { Title = "With file", AttachmentFileIds = [fileId] });

        await Assert.ThrowsAsync<DomainForbiddenException>(() => ExportAsync());
        await Assert.ThrowsAsync<DomainForbiddenException>(() => SummarizeAsync(new()));
    }

    [Fact]
    public async Task Summary_CountsTasksAndAttachedFiles_ForAllFilteredAndSingle()
    {
        var fileId = await SeedFileWithContentAsync("note.txt", "text/plain", NoteBytes);
        var withFile = await CreateAsync(new NewJournalTask { Title = "Alpha with file", AttachmentFileIds = [fileId] });
        await CreateAsync(new NewJournalTask { Title = "Beta" });

        var all = await SummarizeAsync(new() { Statuses = AllStatuses }, WithFiles);
        var filtered = await SummarizeAsync(new() { Search = "Beta" }, WithFiles);
        var single = await SummarizeAsync(
            new() { Statuses = [JournalTaskStatus.Archived, JournalTaskStatus.Backlog], Ids = [withFile.JournalTaskId] }, WithFiles);

        Assert.Equal((2, 1, (long)NoteBytes.Length), (all.TaskCount, all.AttachmentCount, all.AttachmentBytes));
        Assert.Equal((1, 0), (filtered.TaskCount, filtered.AttachmentCount));
        Assert.Equal((1, 1), (single.TaskCount, single.AttachmentCount));
    }

    [Fact]
    public async Task Export_SingleAndFiltered_CarryTheFileInline()
    {
        var fileId = await SeedFileWithContentAsync("note.txt", "text/plain", NoteBytes);
        var task = await CreateAsync(new NewJournalTask { Title = "Camping", AttachmentFileIds = [fileId] });

        var single = Unfold(await ExportAsync(new() { Statuses = [JournalTaskStatus.Backlog], Ids = [task.JournalTaskId] }, WithFiles));
        var filtered = Unfold(await ExportAsync(new() { Search = "Camp" }, WithFiles));

        foreach (var body in new[] { single, filtered })
        {
            Assert.Contains(";ENCODING=BASE64;VALUE=BINARY;FILENAME=\"note.txt\";", body);
            Assert.Contains($"X-ODYSSEY-SHA256={Sha256(NoteBytes)}:" + Convert.ToBase64String(NoteBytes), body);
        }
    }

    [Fact]
    public async Task Export_OverTheByteCap_IsRefusedBeforeAnyByte()
    {
        Limits.Value = Limits.Value with { TaskIcsMaxExportBytes = 1024L * 1024 };
        var big = new byte[1024 * 1024]; // 1 MB of content is ~1.4 MB once base64-encoded and folded
        var fileId = await SeedFileWithContentAsync("big.pdf", "application/pdf", big);
        await CreateAsync(new NewJournalTask { Title = "Big", AttachmentFileIds = [fileId] });

        var summary = await Assert.ThrowsAsync<DomainValidationException>(() => SummarizeAsync(new(), WithFiles));
        var output = new MemoryStream();
        var named = false;
        await Assert.ThrowsAsync<DomainValidationException>(() => InScopeAsync(sp =>
            sp.GetRequiredService<TaskIcsService>().ExportStreamingAsync(new(), true, output, (_, _) => named = true)));

        Assert.Contains("1 MB export limit", summary.Message);
        Assert.False(named);
        Assert.Equal(0, output.Length);
    }

    // The pre-flight check judges attached files exactly, but task text only as it is written: text alone over the cap
    // must end the export with an exception (the controller then aborts the connection) rather than finish short.
    [Fact]
    public async Task Export_ThatGrowsPastTheByteCapWhileStreaming_Throws_AfterTheHeadersAreNamed()
    {
        Limits.Value = Limits.Value with { TaskIcsMaxExportBytes = 1024L * 1024 };
        await WithContextAsync(async context =>
        {
            context.JournalTasks.AddRange(Enumerable.Range(0, 400).Select(i => new JournalTask
            {
                ExternalUid = $"bulk-{i}", Title = $"Bulk {i}", Content = new string('x', 4000), Position = i,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            }));
            await context.SaveChangesAsync();
        });

        Assert.Equal(400, (await SummarizeAsync(new())).TaskCount); // nothing to judge up front
        var named = false;
        await Assert.ThrowsAsync<IcsExportSizeExceededException>(() => InScopeAsync(sp =>
            sp.GetRequiredService<TaskIcsService>().ExportStreamingAsync(new(), false, new MemoryStream(), (_, _) => named = true)));
        Assert.True(named);
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Import_HandAuthoredVTodos_CreatesTasks_WithVerbatimUid()
    {
        var result = await ImportAsync(Vcalendar(
            Vtodo("urn:uuid:aaaa1111-0000-0000-0000-000000000001", "SUMMARY:One", "STATUS:NEEDS-ACTION"),
            Vtodo("urn:uuid:aaaa1111-0000-0000-0000-000000000002", "SUMMARY:Two", "STATUS:IN-PROCESS")));

        Assert.Equal((2, 0), (result.ImportedCount, result.UpdatedCount));
        Assert.Empty(result.Skipped);
        var rows = await WithContextAsync(c => c.JournalTasks.ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ExternalUid == "urn:uuid:aaaa1111-0000-0000-0000-000000000001");
    }

    [Fact]
    public async Task Import_ThirdPartyMinimalVTodo_ImportsAsNewTask()
    {
        // A file another app produced: unfamiliar STATUS casing/whitespace, no CATEGORIES/ATTACH.
        var result = await ImportAsync(Vcalendar(Vtodo("external-123", "SUMMARY:From another app", "STATUS: in-process ")));

        Assert.Equal(1, result.ImportedCount);
        Assert.Empty(result.Skipped);
        var row = await SingleTaskAsync();
        Assert.Equal("external-123", row.ExternalUid);
        Assert.NotNull(row.StartedAt); // " in-process " → Doing
    }

    [Fact]
    public async Task Import_ExportedFile_IsIdempotent_UpdatesInPlace()
    {
        await CreateAsync(new NewJournalTask { Title = "Alpha" });
        await CreateAsync(new NewJournalTask { Title = "Beta", Status = JournalTaskStatus.Doing });

        var result = await ImportAsync(await ExportAsync());

        Assert.Equal((0, 2), (result.ImportedCount, result.UpdatedCount));
        Assert.Equal(2, await WithContextAsync(c => c.JournalTasks.CountAsync())); // no duplicates
    }

    [Fact]
    public async Task Import_UpdateInPlace_PreservesCreatedByStampsUpdatedBy()
    {
        var created = await CreateAsync(new NewJournalTask { Title = "Original" }, userId: "creator");

        var result = await ImportAsync(Vcalendar(Vtodo(created.ExternalUid, "SUMMARY:Renamed", "STATUS:COMPLETED")));

        Assert.Equal((0, 1), (result.ImportedCount, result.UpdatedCount));
        var row = await SingleTaskAsync();
        Assert.Equal("Renamed", row.Title);
        Assert.Equal("creator", row.CreatedByUserId);
        Assert.Equal(ActorUserId, row.UpdatedByUserId);
        Assert.NotNull(row.CompletedAt);
    }

    [Theory]
    [InlineData("NEEDS-ACTION")]
    [InlineData("IN-PROCESS")]
    [InlineData("COMPLETED")]
    [InlineData("CANCELLED")]
    public async Task Import_StatusMapping_DerivesTimestamps_IgnoringRawDtstartCompleted(string status)
    {
        // DTSTART/COMPLETED deliberately disagree with STATUS — the derived timestamps must follow STATUS.
        var result = await ImportAsync(Vcalendar(Vtodo("s-" + status, "SUMMARY:S", $"STATUS:{status}",
            "DTSTART:20200101T090000Z", "COMPLETED:20200102T090000Z")));
        Assert.Equal(1, result.ImportedCount);

        var row = await SingleTaskAsync();
        switch (status)
        {
            case "NEEDS-ACTION":
                Assert.Equal((null, null, null), (row.StartedAt, row.CompletedAt, row.Archived));
                break;
            case "IN-PROCESS":
                Assert.NotNull(row.StartedAt);
                Assert.Equal((null, null), (row.CompletedAt, row.Archived));
                break;
            case "COMPLETED":
                Assert.NotNull(row.CompletedAt);
                Assert.Null(row.Archived);
                break;
            case "CANCELLED":
                Assert.NotNull(row.Archived);
                break;
        }
    }

    [Fact]
    public async Task Import_UnknownStatus_DefaultsToBacklog()
    {
        var result = await ImportAsync(Vcalendar(Vtodo("u1", "SUMMARY:Weird", "STATUS:SOMETHING-ELSE")));

        Assert.Equal(1, result.ImportedCount);
        var row = await SingleTaskAsync();
        Assert.Equal((null, null, null), (row.StartedAt, row.CompletedAt, row.Archived));
    }

    [Fact]
    public async Task Import_Due_MapsToDeadlineDateComponent()
    {
        await ImportAsync(Vcalendar(Vtodo("d1", "SUMMARY:Deadline", "DUE;VALUE=DATE:20301231")));

        Assert.Equal(new DateOnly(2030, 12, 31), (await SingleTaskAsync()).Deadline);
    }

    [Fact]
    public async Task Import_MatchedCategory_LinksTag_UnmatchedIsSkippedAndCounted()
    {
        var tagId = await SeedTagAsync("Errands");

        var result = await ImportAsync(Vcalendar(Vtodo("c1", "SUMMARY:Shop", "CATEGORIES:errands,Nonexistent")));

        Assert.Equal((1, 1), (result.ImportedCount, result.SkippedTagLinkCount));
        Assert.Equal(tagId, Assert.Single((await SingleTaskAsync()).ItemTags).JournalTaskTagId);
    }

    /// <summary>
    /// The defect fix (issue #434 §9-A): this service enforced a hardcoded 50 while the administrator's
    /// <c>JournalTaskMaxLinksPerKind</c> was honoured on the create/update path; it also names the remainder.
    /// </summary>
    [Fact]
    public async Task Import_TagLinks_HonourTheConfiguredCap_AndReportTheRemainder()
    {
        JournalLimits.Value = JournalLimits.Value with { JournalTaskMaxLinksPerKind = 2 };
        var names = Enumerable.Range(0, 6).Select(i => $"captag{i}").ToList();
        foreach (var name in names)
        {
            await SeedTagAsync(name);
        }

        var result = await ImportAsync(Vcalendar(Vtodo("cap-cfg", "SUMMARY:Six tags", "CATEGORIES:" + string.Join(",", names))));

        Assert.Equal((1, 4), (result.ImportedCount, result.SkippedTagLinkCount));
        var capped = Assert.Single(result.Skipped, group => group.Reason.Contains("per-task cap"));
        Assert.Equal(4, capped.Count);
        Assert.Contains("2", capped.Reason);
        Assert.Equal(2, (await SingleTaskAsync()).ItemTags.Count);
    }

    [Fact]
    public async Task Import_ArchivedTagCategory_IsResolved()
    {
        // The export writes an archived tag's name like any other, so the import must match it — otherwise re-importing
        // an export silently strips archived tags from the tasks it updates.
        var tagId = await SeedTagAsync("Retired", archived: true);

        var result = await ImportAsync(Vcalendar(Vtodo("c2", "SUMMARY:Task", "CATEGORIES:retired")));

        Assert.Equal((1, 0), (result.ImportedCount, result.SkippedTagLinkCount));
        Assert.Equal(tagId, Assert.Single((await SingleTaskAsync()).ItemTags).JournalTaskTagId);
    }

    [Fact]
    public async Task ReImport_OfOwnExport_KeepsArchivedTagLink()
    {
        var activeId = await SeedTagAsync("Home");
        var archivedId = await SeedTagAsync("Retired");
        await CreateAsync(new NewJournalTask { Title = "Tagged", TagIds = [activeId, archivedId] });
        await WithContextAsync(async context =>
        {
            // A tag archived after it was linked: the link stays, and must survive the round-trip.
            (await context.JournalTaskTags.SingleAsync(t => t.JournalTaskTagId == archivedId)).Archived = DateTime.UtcNow;
            await context.SaveChangesAsync();
        });

        var result = await ImportAsync(await ExportAsync());

        Assert.Equal((1, 0), (result.UpdatedCount, result.SkippedTagLinkCount));
        Assert.Equal(new[] { activeId, archivedId }.Order(), (await SingleTaskAsync()).ItemTags.Select(l => l.JournalTaskTagId).Order());
    }

    [Fact]
    public async Task Import_DuplicateTagName_PrefersTheActiveTag()
    {
        await SeedTagAsync("Errand", archived: true);
        var activeId = await SeedTagAsync("errand");

        var result = await ImportAsync(Vcalendar(Vtodo("c3", "SUMMARY:Task", "CATEGORIES:ERRAND")));

        Assert.Equal(0, result.SkippedTagLinkCount);
        Assert.Equal(activeId, Assert.Single((await SingleTaskAsync()).ItemTags).JournalTaskTagId);
    }

    [Fact]
    public async Task Import_ResolvableAttachment_WithFilesRead_LinksIt()
    {
        var fileId = await SeedFileWithContentAsync("doc.pdf", "application/pdf", SeededFileContent);

        var result = await ImportAsync(Vcalendar(Vtodo("a1", "SUMMARY:With file", $"ATTACH:odyssey-file:{fileId}")), WithFiles);

        Assert.Equal((1, 0), (result.ImportedCount, result.SkippedAttachmentCount));
        Assert.Equal(fileId, Assert.Single((await SingleTaskAsync()).Attachments).FileId);
    }

    [Fact]
    public async Task Import_UnresolvableAttachment_IsSkippedButTaskImports()
    {
        var result = await ImportAsync(Vcalendar(Vtodo("a2", "SUMMARY:Ghost file", $"ATTACH:odyssey-file:{Guid.NewGuid()}")), WithFiles);

        Assert.Equal((1, 1), (result.ImportedCount, result.SkippedAttachmentCount));
        Assert.Empty((await SingleTaskAsync()).Attachments);
    }

    [Fact]
    public async Task Import_Attachment_WithoutFilesRead_IsSkipped()
    {
        var fileId = await SeedFileWithContentAsync("doc.pdf", "application/pdf", SeededFileContent);

        var result = await ImportAsync(Vcalendar(Vtodo("a3", "SUMMARY:Gated", $"ATTACH:odyssey-file:{fileId}")));

        Assert.Equal((1, 1), (result.ImportedCount, result.SkippedAttachmentCount));
        Assert.Empty((await SingleTaskAsync()).Attachments);
    }

    /// <summary>
    /// Re-importing a task without <c>files.read</c> skips its attachment references and leaves the links it already
    /// has alone, as the journal-entry import does (§9, N1) — it used to replace them with the empty set (issue #287 M8).
    /// </summary>
    [Fact]
    public async Task Reimport_WithoutFilesRead_KeepsTheExistingAttachments()
    {
        var fileId = await SeedFileWithContentAsync("doc.pdf", "application/pdf", SeededFileContent);
        var ics = Vcalendar(Vtodo("keep-1", "SUMMARY:Has a file", $"ATTACH:odyssey-file:{fileId}"));
        await ImportAsync(ics);
        await WithContextAsync(async context =>
        {
            var task = await context.JournalTasks.SingleAsync();
            context.Add(new JournalTaskAttachment { JournalTaskId = task.JournalTaskId, FileId = fileId, CreatedAt = DateTime.UtcNow });
            await context.SaveChangesAsync();
        });

        var result = await ImportAsync(ics);

        Assert.Equal((1, 1), (result.UpdatedCount, result.SkippedAttachmentCount));
        Assert.Equal(fileId, Assert.Single((await SingleTaskAsync()).Attachments).FileId);
    }

    [Fact]
    public async Task Import_NonOdysseyAttachmentScheme_IsIgnoredNotCounted()
    {
        var result = await ImportAsync(Vcalendar(Vtodo("a4", "SUMMARY:External link", "ATTACH:https://example.com/file.pdf")), WithFiles);

        Assert.Equal((1, 0), (result.ImportedCount, result.SkippedAttachmentCount));
    }

    [Fact]
    public async Task Import_RecurringVTodo_IsSkippedWithReason()
    {
        var result = await ImportAsync(Vcalendar(Vtodo("r1", "SUMMARY:Repeats", "STATUS:NEEDS-ACTION", "RRULE:FREQ=WEEKLY;COUNT=5")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal("Recurring VTODO not supported", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_MissingSummary_IsSkippedWithSample()
    {
        var result = await ImportAsync(Vcalendar(Vtodo("n1", "STATUS:NEEDS-ACTION")));

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("SUMMARY", group.Reason);
        Assert.Contains("(untitled)", group.SampleTitles);
    }

    [Fact]
    public async Task Import_TitleTooLong_IsSkippedNotTruncated()
    {
        var result = await ImportAsync(Vcalendar(Vtodo("l1", $"SUMMARY:{new string('x', 201)}")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("Title", Assert.Single(result.Skipped).Reason);
        Assert.Equal(0, await WithContextAsync(c => c.JournalTasks.CountAsync()));
    }

    [Fact]
    public async Task Import_DescriptionTooLong_IsSkipped()
    {
        var result = await ImportAsync(Vcalendar(Vtodo("d-long", "SUMMARY:Big", $"DESCRIPTION:{new string('d', 4097)}")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("Description", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_DuplicateUidWithinFile_LastWriteWins_NoDuplicate()
    {
        var result = await ImportAsync(Vcalendar(Vtodo("dupe", "SUMMARY:First"), Vtodo("dupe", "SUMMARY:Second")));

        Assert.Equal("Second", (await SingleTaskAsync()).Title); // exactly one row
        Assert.Equal(1, result.ImportedCount);
    }

    [Fact]
    public async Task Import_TooManyVTodos_IsRefusedWhole_NothingImported()
    {
        // The shipped default is unlimited (the task surface moves whole boards), so an administrator's cap is pinned.
        const int cap = 5;
        Limits.Value = Limits.Value with { TaskIcsMaxImportTasks = cap };

        var refused = await Assert.ThrowsAsync<DomainValidationException>(() =>
            ImportAsync(Vcalendar([.. Enumerable.Range(0, cap + 1).Select(i => Vtodo($"u{i}", $"SUMMARY:T{i}"))])));

        Assert.Contains($"more than {cap} to-do items", refused.Message);
        Assert.Equal(0, await WithContextAsync(c => c.JournalTasks.CountAsync()));
    }

    [Fact]
    public async Task Import_MalformedFile_IsRefused()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync("this is not an ics file"));
    }

    [Fact]
    public async Task Import_WrongContentType_IsRefused()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(Vcalendar(Vtodo("x2", "SUMMARY:x")), contentType: "application/json"));
    }

    [Fact]
    public async Task Import_OctetStreamContentType_IsAccepted()
    {
        var result = await ImportAsync(Vcalendar(Vtodo("o1", "SUMMARY:Octet")), contentType: "application/octet-stream");

        Assert.Equal(1, result.ImportedCount);
    }

    [Fact]
    public async Task Import_OnlyVEvents_IsNoOp_NotError()
    {
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:e1\r\nDTSTART:20300101T090000Z\r\nSUMMARY:Event\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = await ImportAsync(ics);

        Assert.Equal((0, 0), (result.ImportedCount, result.UpdatedCount));
    }

    // ------------------------------------------------------------------ the task's own ExternalUid (create/update path)

    [Fact]
    public async Task Create_WithoutExternalUid_GeneratesUrnUuid()
    {
        Assert.StartsWith("urn:uuid:", (await CreateAsync(new NewJournalTask { Title = "Auto" })).ExternalUid);
    }

    [Fact]
    public async Task Create_WithSuppliedExternalUid_StoresItVerbatim()
    {
        Assert.Equal("my-own-id", (await CreateAsync(new NewJournalTask { Title = "Given", ExternalUid = "my-own-id" })).ExternalUid);
    }

    [Fact]
    public async Task Create_WithExternalUidAlreadyUsed_IsRefused()
    {
        await CreateAsync(new NewJournalTask { Title = "First", ExternalUid = "shared-id" });

        await Assert.ThrowsAsync<DomainValidationException>(() => CreateAsync(new NewJournalTask { Title = "Second", ExternalUid = "shared-id" }));
    }

    [Fact]
    public async Task Update_WithNewExternalUid_StoresItVerbatim()
    {
        var created = await CreateAsync(new NewJournalTask { Title = "Task" });

        var updated = await UpdateAsync(created.JournalTaskId, new UpdateJournalTask { Title = "Task", ExternalUid = "reassigned-id" });

        Assert.Equal("reassigned-id", updated!.ExternalUid);
    }

    [Fact]
    public async Task Update_WithExternalUidUsedByAnotherTask_IsRefused()
    {
        await CreateAsync(new NewJournalTask { Title = "A", ExternalUid = "uid-a" });
        var b = await CreateAsync(new NewJournalTask { Title = "B", ExternalUid = "uid-b" });

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            UpdateAsync(b.JournalTaskId, new UpdateJournalTask { Title = "B", ExternalUid = "uid-a" }));
    }

    // ------------------------------------------------------------------ import position (AC #14) + link diffing

    [Fact]
    public async Task Import_NewTasks_AppendToEndOfColumn()
    {
        var a = await CreateAsync(new NewJournalTask { Title = "A" }); // Backlog pos 0
        var b = await CreateAsync(new NewJournalTask { Title = "B" }); // Backlog pos 1
        Assert.Equal((0, 1), (a.Position, b.Position));

        var result = await ImportAsync(Vcalendar(
            Vtodo("new-1", "SUMMARY:New1", "STATUS:NEEDS-ACTION"),
            Vtodo("new-2", "SUMMARY:New2", "STATUS:NEEDS-ACTION")));
        Assert.Equal(2, result.ImportedCount);

        Assert.Equal(2, (await TaskAsync(t => t.ExternalUid == "new-1")).Position);
        Assert.Equal(3, (await TaskAsync(t => t.ExternalUid == "new-2")).Position);
    }

    [Fact]
    public async Task Import_StatusChangeUpdate_AppendsToNewColumn_LeavesOthersUndisturbed()
    {
        var a = await CreateAsync(new NewJournalTask { Title = "A" }); // Backlog pos 0
        var b = await CreateAsync(new NewJournalTask { Title = "B" }); // Backlog pos 1

        // Move A to Doing via import; B's position must be undisturbed (AC #14).
        var result = await ImportAsync(Vcalendar(Vtodo(a.ExternalUid, "SUMMARY:A", "STATUS:IN-PROCESS")));
        Assert.Equal(1, result.UpdatedCount);

        var movedA = await TaskAsync(t => t.JournalTaskId == a.JournalTaskId);
        Assert.NotNull(movedA.StartedAt);  // now Doing
        Assert.Equal(0, movedA.Position);  // appended to the (empty) Doing column
        Assert.Equal(1, (await TaskAsync(t => t.JournalTaskId == b.JournalTaskId)).Position); // untouched
    }

    [Fact]
    public async Task Import_UnchangedStatusUpdate_LeavesPositionAndTimestampsUntouched()
    {
        await CreateAsync(new NewJournalTask { Title = "A" });         // Backlog pos 0
        var b = await CreateAsync(new NewJournalTask { Title = "B" }); // Backlog pos 1

        // Re-import B unchanged (still Backlog): position must not be recomputed, nor timestamps re-stamped.
        await ImportAsync(Vcalendar(Vtodo(b.ExternalUid, "SUMMARY:B", "STATUS:NEEDS-ACTION")));

        var keptB = await TaskAsync(t => t.JournalTaskId == b.JournalTaskId);
        Assert.Equal((1, null, null), (keptB.Position, keptB.StartedAt, keptB.CompletedAt));
    }

    [Fact]
    public async Task Import_Update_RemovesTagAndAttachmentLinksNotInFile()
    {
        var alpha = await SeedTagAsync("Alpha");
        var beta = await SeedTagAsync("Beta");
        var fileId = await SeedFileWithContentAsync("doc.pdf", "application/pdf", SeededFileContent);
        var created = await CreateAsync(new NewJournalTask { Title = "Linked", TagIds = [alpha, beta], AttachmentFileIds = [fileId] });

        // Re-import the same task naming only Alpha and no ATTACH: Beta + the attachment must be diffed off.
        var result = await ImportAsync(Vcalendar(Vtodo(created.ExternalUid, "SUMMARY:Linked", "CATEGORIES:Alpha")), WithFiles);
        Assert.Equal(1, result.UpdatedCount);

        var row = await TaskAsync(t => t.JournalTaskId == created.JournalTaskId);
        Assert.Equal(alpha, Assert.Single(row.ItemTags).JournalTaskTagId);
        Assert.Empty(row.Attachments);
    }

    // ------------------------------------------------------------------ attached files travel inline

    [Fact]
    public async Task RoundTrip_SameInstance_ReusesTheFile_NoDuplicate()
    {
        var fileId = await SeedFileWithContentAsync("note.txt", "text/plain", NoteBytes);
        await CreateAsync(new NewJournalTask { Title = "Camping", AttachmentFileIds = [fileId] });

        var result = await ImportAsync(await ExportAsync(claims: WithUploads), WithUploads);

        Assert.Equal((1, 0, 1, 0), (result.UpdatedCount, result.StoredFileCount, result.ReusedFileCount, result.SkippedAttachmentCount));
        Assert.Single(await FilesAsync());
        Assert.Equal(fileId, Assert.Single((await SingleTaskAsync()).Attachments).FileId);
    }

    [Fact]
    public async Task RoundTrip_IntoAnotherInstance_StoresTheFile_WithItsNameTypeAndBytes()
    {
        var fileId = await SeedFileWithContentAsync("note.txt", "text/plain", NoteBytes);
        await CreateAsync(new NewJournalTask { Title = "Camping", AttachmentFileIds = [fileId] });
        var exported = await ExportAsync(claims: WithFiles);

        // A second instance of the class is a second database and file volume: another installation.
        var target = new TaskIcsServiceTests();
        try
        {
            var result = await target.ImportAsync(exported, WithUploads);

            Assert.Equal((1, 1, 0), (result.ImportedCount, result.StoredFileCount, result.ReusedFileCount));
            var stored = Assert.Single(await target.FilesAsync());
            Assert.Equal(("note.txt", "text/plain", Sha256(NoteBytes)), (stored.FileName, stored.ContentType, stored.Sha256Hash));
            Assert.NotNull(stored.JournalStoredObjectId); // on disk, in the Journal area — not a legacy blob
            Assert.Equal(NoteBytes, await target.ContentAsync(stored.Id));

            // Importing the same file again matches what the first import stored: still one file.
            var again = await target.ImportAsync(exported, WithUploads);
            Assert.Equal((1, 0, 1), (again.UpdatedCount, again.StoredFileCount, again.ReusedFileCount));
            Assert.Single(await target.FilesAsync());
        }
        finally
        {
            await target.DisposeAsync();
        }
    }

    [Fact]
    public async Task Import_InlineFile_MatchingStoredContent_IsReusedByHash()
    {
        var existingId = await SeedFileWithContentAsync("existing.txt", "text/plain", NoteBytes);

        // No X-ODYSSEY-FILE-ID: a file from elsewhere whose content is already stored.
        var result = await ImportAsync(Vcalendar(Vtodo("h1", "SUMMARY:Hash match", InlineAttach(NoteBytes, "renamed.txt", "text/plain"))), WithUploads);

        Assert.Equal((0, 1), (result.StoredFileCount, result.ReusedFileCount));
        Assert.Equal(existingId, Assert.Single((await SingleTaskAsync()).Attachments).FileId);
    }

    [Fact]
    public async Task Import_SameInlineFileOnTwoTasks_IsStoredOnce()
    {
        var attach = InlineAttach(NoteBytes, "note.txt", "text/plain");

        var result = await ImportAsync(Vcalendar(Vtodo("d1", "SUMMARY:One", attach), Vtodo("d2", "SUMMARY:Two", attach)), WithUploads);

        Assert.Equal((2, 1, 1), (result.ImportedCount, result.StoredFileCount, result.ReusedFileCount));
        var file = Assert.Single(await FilesAsync());
        Assert.All(await WithContextAsync(c => c.JournalTasks.Include(t => t.Attachments).ToListAsync()),
            task => Assert.Equal(file.Id, Assert.Single(task.Attachments).FileId));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Import_InlineFile_WithoutBothFileClaims_IsForbidden_NothingImported(bool filesRead, bool filesCreate)
    {
        await Assert.ThrowsAsync<DomainForbiddenException>(() => ImportAsync(
            Vcalendar(Vtodo("f1", "SUMMARY:Gated", InlineAttach(NoteBytes, "note.txt", "text/plain"))), new Claims(filesRead, filesCreate)));

        Assert.Equal(0, await WithContextAsync(c => c.JournalTasks.CountAsync()));
        Assert.Empty(await FilesAsync());
    }

    [Fact]
    public async Task Import_InlineFile_Refusals_SkipTheFileButKeepTheTask()
    {
        var wrongHash = new string('0', 64);

        var result = await ImportAsync(Vcalendar(
            Vtodo("r1", "SUMMARY:Damaged", "ATTACH;FMTTYPE=text/plain;ENCODING=BASE64;VALUE=BINARY:!!!not base64!!!"),
            Vtodo("r2", "SUMMARY:Checksum", InlineAttach(NoteBytes, "note.txt", "text/plain", $";X-ODYSSEY-SHA256={wrongHash}")),
            Vtodo("r3", "SUMMARY:Executable", InlineAttach([0x4D, 0x5A, 0x90, 0x00], "tool.exe", "application/x-msdownload")),
            Vtodo("r4", "SUMMARY:Mislabelled", InlineAttach(NoteBytes, "fake.png", "image/png"))), WithUploads);

        Assert.Equal((4, 4, 0), (result.ImportedCount, result.SkippedAttachmentCount, result.StoredFileCount));
        Assert.Empty(await FilesAsync());
        // Each case is pinned to its own task through the group's sample titles, so one refusal silently turning into
        // another (or into an accepted file) fails here.
        string[] SamplesFor(string reasonStart) =>
            [.. Assert.Single(result.Skipped, g => g.Reason.StartsWith(reasonStart, StringComparison.Ordinal)).SampleTitles.Order()];
        Assert.Equal(["Damaged"], SamplesFor("Attached file is damaged"));
        Assert.Equal(["Checksum"], SamplesFor("Attached file does not match its checksum"));
        Assert.Equal(["Executable", "Mislabelled"], SamplesFor("Attached file is of a type uploads do not accept"));
    }

    [Fact]
    public async Task Import_NewInlineFile_OverTheAdminUploadCap_IsSkipped_ButAMatchingStoredFileIsStillReused()
    {
        UploadLimits.Megabytes = 1;
        var big = Encoding.UTF8.GetBytes(new string('a', 1536 * 1024)); // 1.5 MB, over the 1 MB upload cap
        var stored = Encoding.UTF8.GetBytes(new string('b', 1536 * 1024));
        var storedId = await SeedFileWithContentAsync("stored.txt", "text/plain", stored);

        var result = await ImportAsync(Vcalendar(
            Vtodo("u1", "SUMMARY:New big", InlineAttach(big, "big.txt", "text/plain")),
            Vtodo("u2", "SUMMARY:Known big", InlineAttach(stored, "stored.txt", "text/plain"))), WithUploads);

        Assert.Equal((2, 0, 1, 1), (result.ImportedCount, result.StoredFileCount, result.ReusedFileCount, result.SkippedAttachmentCount));
        var group = Assert.Single(result.Skipped);
        Assert.Contains("1 MB upload limit", group.Reason);
        Assert.Equal(["New big"], group.SampleTitles);
        Assert.Equal(storedId, Assert.Single(await FilesAsync()).Id);
    }

    [Fact]
    public async Task Import_LargerThanTheImportCap_IsRefusedWhole()
    {
        Limits.Value = Limits.Value with { TaskIcsMaxImportBytes = 1024L * 1024 };

        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(
            Vcalendar(Vtodo("b1", "SUMMARY:Big", InlineAttach(new byte[1024 * 1024], "big.txt", "text/plain"))), WithUploads));

        Assert.Contains("1 MB limit", refused.Message);
        Assert.Empty(await FilesAsync());
        Assert.Equal(0, await WithContextAsync(c => c.JournalTasks.CountAsync()));
    }

    // ------------------------------------------------------------------ helpers

    private static string Vcalendar(params string[] todos) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(todos) + "END:VCALENDAR\r\n";

    private static string Vtodo(string uid, params string[] lines) =>
        "BEGIN:VTODO\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VTODO\r\n";

    private Task<Guid> SeedTagAsync(string name, bool archived = false) => WithContextAsync(async context =>
    {
        var id = Guid.NewGuid();
        context.JournalTaskTags.Add(new ContextJournalTaskTag { JournalTaskTagId = id, Name = name, Archived = archived ? DateTime.UtcNow : null });
        await context.SaveChangesAsync();
        return id;
    });

    private Task<ExistingJournalTask> CreateAsync(NewJournalTask request, string userId = ActorUserId) =>
        InScopeAsync(sp => sp.GetRequiredService<JournalTaskService>().Create(request, userId));

    private Task<ExistingJournalTask?> UpdateAsync(Guid id, UpdateJournalTask request) =>
        InScopeAsync(sp => sp.GetRequiredService<JournalTaskService>().Update(id, request, ActorUserId));

    private async Task<TaskIcsImportResult> ImportAsync(string ics, Claims? claims = null, string contentType = "text/calendar")
    {
        claims ??= ReadWrite;
        await using var stream = Utf8(ics);
        return await InScopeAsync(sp => sp.GetRequiredService<TaskIcsService>().ImportAsync(
            stream, stream.Length, contentType, ActorUserId, claims.FilesRead, claims.FilesCreate));
    }

    private async Task<string> ExportAsync(JournalTasksQueryParams? query = null, Claims? claims = null)
    {
        var output = new MemoryStream();
        await InScopeAsync(sp => sp.GetRequiredService<TaskIcsService>().ExportStreamingAsync(
            query ?? new(), (claims ?? ReadWrite).FilesRead, output, (_, _) => { }));
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private Task<TaskIcsExportSummary> SummarizeAsync(JournalTasksQueryParams query, Claims? claims = null) =>
        InScopeAsync(sp => sp.GetRequiredService<TaskIcsService>().SummarizeExportAsync(query, (claims ?? ReadWrite).FilesRead));

    private Task<JournalTask> SingleTaskAsync() => TaskAsync(_ => true);

    private Task<JournalTask> TaskAsync(System.Linq.Expressions.Expression<Func<JournalTask, bool>> predicate) =>
        WithContextAsync(c => c.JournalTasks.AsNoTracking().Include(t => t.ItemTags).Include(t => t.Attachments).SingleAsync(predicate));

    private Task<byte[]> ContentAsync(Guid fileId) => InScopeAsync(async sp =>
        (await sp.GetRequiredService<StoredFileContentSource>().ReadAllAsync(fileId, long.MaxValue, default))!.Value.Bytes!);
}
