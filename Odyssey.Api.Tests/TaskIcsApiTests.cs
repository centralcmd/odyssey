using Odyssey.Context;
using Odyssey.Testing.FileStorage;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using ContextJournalTaskTag = Odyssey.Context.JournalTaskTag;

namespace Odyssey.Api.Tests;

/// <summary>VTODO/.ics task import/export endpoints (issue #337).</summary>
public class TaskIcsApiTests(TaskIcsApiTests.Host host) : SharedApiHostTests<TaskIcsApiTests.Host>(host)
{
    private const string ActorUserId = "task-ics-actor-id";
    private const string Path = "/api/tasks";
    private const string IcsPath = "/api/tasks/ics";

    private static readonly string[] ReadOnly = [PermissionClaims.TasksRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.TasksRead, PermissionClaims.TasksCreate, PermissionClaims.TasksUpdate,
    ];

    private static readonly string[] ReadWriteWithFiles =
    [
        PermissionClaims.TasksRead, PermissionClaims.TasksCreate,
        PermissionClaims.TasksUpdate, PermissionClaims.FilesRead,
    ];

    private static readonly string[] ReadWriteWithFileUploads =
    [
        PermissionClaims.TasksRead, PermissionClaims.TasksCreate,
        PermissionClaims.TasksUpdate, PermissionClaims.FilesRead, PermissionClaims.FilesCreate,
    ];

    private static readonly byte[] NoteBytes = Encoding.UTF8.GetBytes("Packing list: tent, stove, map.\n");

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Export_EmptyBoard_ReturnsValidVCalendar_WithNoSniff()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadOnly);

        var response = await client.GetAsync(IcsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.TryGetValues("X-Content-Type-Options", out var v) ? string.Join("", v) : null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VCALENDAR", body);
        Assert.Contains("END:VCALENDAR", body);
        Assert.DoesNotContain("BEGIN:VTODO", body);
    }

    [Fact]
    public async Task Export_FileName_IsTimestampPrefixed()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadOnly);

        var response = await client.GetAsync(IcsPath);

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(fileName);
        Assert.Matches(@"^odyssey-tasks-\d{8}-\d{6}Z\.ics$", fileName!);
    }

    [Fact]
    public async Task Export_WithoutReadClaim_ReturnsForbidden()
    {
        var factory = Factory;
        using var client = factory.ClientFor([]);

        var response = await client.GetAsync(IcsPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Export_Task_EmitsVTodo_WithUidSummaryStatusDueCategoriesAttach()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFiles);
        var tagId = await SeedTagAsync(factory, "Home");
        var fileId = await SeedFileAsync(factory, "spec.pdf", "application/pdf");

        var created = await CreateAsync(client, new NewJournalTask
        {
            Title = "Buy milk",
            Content = "2%",
            Status = JournalTaskStatus.Doing,
            Deadline = new DateOnly(2030, 8, 1),
            TagIds = [tagId],
            AttachmentFileIds = [fileId],
        });

        var body = await (await client.GetAsync(IcsPath)).Content.ReadAsStringAsync();

        Assert.Equal(1, CountOccurrences(body, "BEGIN:VTODO"));
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
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        // Backlog ⇒ no DTSTART ⇒ DUE is a floating VALUE=DATE.
        await CreateAsync(client, new NewJournalTask { Title = "Backlog due", Deadline = new DateOnly(2030, 9, 9) });
        // Doing ⇒ DTSTART present ⇒ DUE must be a DATE-TIME, never VALUE=DATE (else strict readers reject it).
        await CreateAsync(client, new NewJournalTask { Title = "Doing due", Status = JournalTaskStatus.Doing, Deadline = new DateOnly(2030, 10, 10) });

        var body = await (await client.GetAsync($"{IcsPath}?statuses=Backlog&statuses=Doing")).Content.ReadAsStringAsync();

        Assert.Contains("DUE;VALUE=DATE:20300909", body);
        Assert.Contains("DUE:20301010T000000Z", body);
    }

    [Fact]
    public async Task Export_HidesArchivedByDefault_IncludesWhenRequested()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var archived = await CreateAsync(client, new NewJournalTask { Title = "Old", Status = JournalTaskStatus.Archived });

        var hidden = await (await client.GetAsync(IcsPath)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(archived.ExternalUid, hidden);

        var shown = await (await client.GetAsync($"{IcsPath}?statuses=Archived")).Content.ReadAsStringAsync();
        Assert.Contains(archived.ExternalUid, shown);
        Assert.Contains("STATUS:CANCELLED", shown);
    }

    [Fact]
    public async Task Export_ByIds_ReturnsOnlyThoseTasks()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var a = await CreateAsync(client, new NewJournalTask { Title = "Keep" });
        var b = await CreateAsync(client, new NewJournalTask { Title = "Drop" });

        var body = await (await client.GetAsync($"{IcsPath}?ids={a.JournalTaskId}")).Content.ReadAsStringAsync();

        Assert.Equal(1, CountOccurrences(body, "BEGIN:VTODO"));
        Assert.Contains(a.ExternalUid, body);
        Assert.DoesNotContain(b.ExternalUid, body);
    }

    [Fact]
    public async Task Export_ByIds_WithAllStatuses_ExportsArchivedTask()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var archived = await CreateAsync(client, new NewJournalTask { Title = "Archived one", Status = JournalTaskStatus.Archived });

        // The row menu pairs ids with all statuses so an archived task still exports.
        var url = $"{IcsPath}?statuses=Backlog&statuses=Doing&statuses=Done&statuses=Archived&ids={archived.JournalTaskId}";
        var body = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        Assert.Equal(1, CountOccurrences(body, "BEGIN:VTODO"));
        Assert.Contains(archived.ExternalUid, body);
        Assert.Contains("STATUS:CANCELLED", body);
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Import_HandAuthoredVTodos_CreatesTasks_WithVerbatimUid()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(
            Vtodo("urn:uuid:aaaa1111-0000-0000-0000-000000000001", "SUMMARY:One", "STATUS:NEEDS-ACTION"),
            Vtodo("urn:uuid:aaaa1111-0000-0000-0000-000000000002", "SUMMARY:Two", "STATUS:IN-PROCESS"));

        var result = await ImportAsync(client, ics);

        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(result.Skipped);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var rows = await ctx.JournalTasks.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ExternalUid == "urn:uuid:aaaa1111-0000-0000-0000-000000000001");
    }

    [Fact]
    public async Task Import_ThirdPartyMinimalVTodo_ImportsAsNewTask()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        // A file another app produced: unfamiliar STATUS casing/whitespace, no CATEGORIES/ATTACH.
        var ics = Vcalendar(Vtodo("external-123", "SUMMARY:From another app", "STATUS: in-process "));

        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Empty(result.Skipped);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.SingleAsync();
        Assert.Equal("external-123", row.ExternalUid);
        Assert.NotNull(row.StartedAt); // " in-process " → Doing
    }

    [Fact]
    public async Task Import_ExportedFile_IsIdempotent_UpdatesInPlace()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await CreateAsync(client, new NewJournalTask { Title = "Alpha" });
        await CreateAsync(client, new NewJournalTask { Title = "Beta", Status = JournalTaskStatus.Doing });

        var exported = await (await client.GetAsync(IcsPath)).Content.ReadAsStringAsync();
        var result = await ImportAsync(client, exported);

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(2, result.UpdatedCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(2, await ctx.JournalTasks.CountAsync()); // no duplicates
    }

    [Fact]
    public async Task Import_UpdateInPlace_PreservesCreatedByStampsUpdatedBy()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var created = await CreateAsync(client, new NewJournalTask { Title = "Original" });

        var ics = Vcalendar(Vtodo(created.ExternalUid, "SUMMARY:Renamed", "STATUS:COMPLETED"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(1, result.UpdatedCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.SingleAsync();
        Assert.Equal("Renamed", row.Title);
        Assert.Equal(ActorUserId, row.CreatedByUserId);
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
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        // DTSTART/COMPLETED deliberately disagree with STATUS — the derived timestamps must follow STATUS.
        var ics = Vcalendar(Vtodo("s-" + status, "SUMMARY:S", $"STATUS:{status}",
            "DTSTART:20200101T090000Z", "COMPLETED:20200102T090000Z"));

        var result = await ImportAsync(client, ics);
        Assert.Equal(1, result.ImportedCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.SingleAsync();

        switch (status)
        {
            case "NEEDS-ACTION":
                Assert.Null(row.StartedAt);
                Assert.Null(row.CompletedAt);
                Assert.Null(row.Archived);
                break;
            case "IN-PROCESS":
                Assert.NotNull(row.StartedAt);
                Assert.Null(row.CompletedAt);
                Assert.Null(row.Archived);
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
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vtodo("u1", "SUMMARY:Weird", "STATUS:SOMETHING-ELSE"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.SingleAsync();
        Assert.Null(row.StartedAt);
        Assert.Null(row.CompletedAt);
        Assert.Null(row.Archived);
    }

    [Fact]
    public async Task Import_Due_MapsToDeadlineDateComponent()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vtodo("d1", "SUMMARY:Deadline", "DUE;VALUE=DATE:20301231"));
        await ImportAsync(client, ics);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.SingleAsync();
        Assert.Equal(new DateOnly(2030, 12, 31), row.Deadline);
    }

    [Fact]
    public async Task Import_MatchedCategory_LinksTag_UnmatchedIsSkippedAndCounted()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var tagId = await SeedTagAsync(factory, "Errands");

        var ics = Vcalendar(Vtodo("c1", "SUMMARY:Shop", "CATEGORIES:errands,Nonexistent"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedTagLinkCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.Include(t => t.ItemTags).SingleAsync();
        Assert.Equal(tagId, Assert.Single(row.ItemTags).JournalTaskTagId);
    }

    /// <summary>
    /// The defect fix (issue #434 §9-A). This service enforced a hardcoded 50 links per kind while the
    /// administrator's <c>JournalTaskMaxLinksPerKind</c> setting was honoured on the create/update path
    /// and silently ignored here. It also gains the named skip reason the journal path gains, so both
    /// import summaries describe the same thing the same way. Fails against <c>main</c>.
    /// </summary>
    [Fact]
    public async Task Import_TagLinks_HonourTheConfiguredCap_AndReportTheRemainder()
    {
        var factory = Factory;
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.JournalTaskMaxLinksPerKind, "2");
        using var client = factory.ClientFor(ReadWrite);

        var names = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            names.Add($"captag{i}");
            await SeedTagAsync(factory, names[i]);
        }

        var ics = Vcalendar(Vtodo("cap-cfg", "SUMMARY:Six tags", "CATEGORIES:" + string.Join(",", names)));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(4, result.SkippedTagLinkCount);

        var capped = Assert.Single(result.Skipped, group => group.Reason.Contains("per-task cap"));
        Assert.Equal(4, capped.Count);
        Assert.Contains("2", capped.Reason);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.Include(t => t.ItemTags).SingleAsync();
        Assert.Equal(2, row.ItemTags.Count);
    }

    [Fact]
    public async Task Import_ArchivedTagCategory_IsResolved()
    {
        // The export writes an archived tag's name like any other, so the import must match it — otherwise
        // re-importing an export silently strips archived tags from the tasks it updates.
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var tagId = await SeedTagAsync(factory, "Retired", archived: true);

        var ics = Vcalendar(Vtodo("c2", "SUMMARY:Task", "CATEGORIES:retired"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedTagLinkCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.Include(t => t.ItemTags).SingleAsync();
        Assert.Equal(tagId, Assert.Single(row.ItemTags).JournalTaskTagId);
    }

    [Fact]
    public async Task ReImport_OfOwnExport_KeepsArchivedTagLink()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var activeId = await SeedTagAsync(factory, "Home");
        var archivedId = await SeedTagAsync(factory, "Retired");
        await CreateAsync(client, new NewJournalTask { Title = "Tagged", TagIds = [activeId, archivedId] });
        using (var archiveScope = factory.Services.CreateScope())
        {
            // A tag archived after it was linked: the link stays, and must survive the round-trip.
            var archiveContext = archiveScope.ServiceProvider.GetRequiredService<OdysseyContext>();
            (await archiveContext.JournalTaskTags.SingleAsync(t => t.JournalTaskTagId == archivedId)).Archived = DateTime.UtcNow;
            await archiveContext.SaveChangesAsync();
        }

        var exported = await (await client.GetAsync(IcsPath)).Content.ReadAsStringAsync();
        var result = await ImportAsync(client, exported);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.SkippedTagLinkCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.Include(t => t.ItemTags).SingleAsync();
        Assert.Equal(new[] { activeId, archivedId }.Order(), row.ItemTags.Select(l => l.JournalTaskTagId).Order());
    }

    [Fact]
    public async Task Import_DuplicateTagName_PrefersTheActiveTag()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await SeedTagAsync(factory, "Errand", archived: true);
        var activeId = await SeedTagAsync(factory, "errand");

        var result = await ImportAsync(client, Vcalendar(Vtodo("c3", "SUMMARY:Task", "CATEGORIES:ERRAND")));

        Assert.Equal(0, result.SkippedTagLinkCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.Include(t => t.ItemTags).SingleAsync();
        Assert.Equal(activeId, Assert.Single(row.ItemTags).JournalTaskTagId);
    }

    [Fact]
    public async Task Import_ResolvableAttachment_WithFilesRead_LinksIt()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFiles);
        var fileId = await SeedFileAsync(factory, "doc.pdf", "application/pdf");

        var ics = Vcalendar(Vtodo("a1", "SUMMARY:With file", $"ATTACH:odyssey-file:{fileId}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedAttachmentCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.Include(t => t.Attachments).SingleAsync();
        Assert.Equal(fileId, Assert.Single(row.Attachments).FileId);
    }

    [Fact]
    public async Task Import_UnresolvableAttachment_IsSkippedButTaskImports()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFiles);

        var ics = Vcalendar(Vtodo("a2", "SUMMARY:Ghost file", $"ATTACH:odyssey-file:{Guid.NewGuid()}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedAttachmentCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.Include(t => t.Attachments).SingleAsync();
        Assert.Empty(row.Attachments);
    }

    [Fact]
    public async Task Import_Attachment_WithoutFilesRead_IsSkipped()
    {
        var factory = Factory; // no files.read
        using var client = factory.ClientFor(ReadWrite);
        var fileId = await SeedFileAsync(factory, "doc.pdf", "application/pdf");

        var ics = Vcalendar(Vtodo("a3", "SUMMARY:Gated", $"ATTACH:odyssey-file:{fileId}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedAttachmentCount);
    }

    /// <summary>
    /// Re-importing a task without <c>files.read</c> skips its attachment references and leaves the
    /// links it already has alone, as the journal-entry import does (§9, N1). This path used to replace
    /// them with the empty set, deleting attachments the caller could not even see (issue #287 M8).
    /// </summary>
    [Fact]
    public async Task Reimport_WithoutFilesRead_KeepsTheExistingAttachments()
    {
        var factory = Factory; // no files.read
        using var client = factory.ClientFor(ReadWrite);
        var fileId = await SeedFileAsync(factory, "doc.pdf", "application/pdf");
        var ics = Vcalendar(Vtodo("keep-1", "SUMMARY:Has a file", $"ATTACH:odyssey-file:{fileId}"));
        await ImportAsync(client, ics);

        using (var scope = factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var task = await ctx.JournalTasks.SingleAsync();
            ctx.Add(new JournalTaskAttachment { JournalTaskId = task.JournalTaskId, FileId = fileId, CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(1, result.SkippedAttachmentCount);
        using var verify = factory.Services.CreateScope();
        var row = await verify.ServiceProvider.GetRequiredService<OdysseyContext>()
            .JournalTasks.Include(t => t.Attachments).SingleAsync();
        Assert.Equal(fileId, Assert.Single(row.Attachments).FileId);
    }

    [Fact]
    public async Task Import_NonOdysseyAttachmentScheme_IsIgnoredNotCounted()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFiles);

        var ics = Vcalendar(Vtodo("a4", "SUMMARY:External link", "ATTACH:https://example.com/file.pdf"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedAttachmentCount);
    }

    [Fact]
    public async Task Import_RecurringVTodo_IsSkippedWithReason()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vtodo("r1", "SUMMARY:Repeats", "STATUS:NEEDS-ACTION", "RRULE:FREQ=WEEKLY;COUNT=5"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Equal("Recurring VTODO not supported", group.Reason);
    }

    [Fact]
    public async Task Import_MissingSummary_IsSkippedWithSample()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vtodo("n1", "STATUS:NEEDS-ACTION"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("SUMMARY", group.Reason);
        Assert.Contains("(untitled)", group.SampleTitles);
    }

    [Fact]
    public async Task Import_TitleTooLong_IsSkippedNotTruncated()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vtodo("l1", $"SUMMARY:{new string('x', 201)}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("Title", group.Reason);
    }

    [Fact]
    public async Task Import_DuplicateUidWithinFile_LastWriteWins_NoDuplicate()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(
            Vtodo("dupe", "SUMMARY:First"),
            Vtodo("dupe", "SUMMARY:Second"));
        var result = await ImportAsync(client, ics);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.SingleAsync(); // exactly one row
        Assert.Equal("Second", row.Title);
        Assert.Equal(1, result.ImportedCount);
    }

    [Fact]
    public async Task Import_TooManyVTodos_ReturnsBadRequest_NothingImported()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        // The shipped default is unlimited (the task surface carries attached files and moves whole boards), so
        // an administrator's finite cap is what this pins.
        const int cap = 5;
        await SetSettingAsync(factory, SystemSettingsKeys.TaskIcsMaxImportTasks, cap.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var todos = Enumerable.Range(0, cap + 1)
            .Select(i => Vtodo($"u{i}", $"SUMMARY:T{i}"));
        var ics = Vcalendar(todos.ToArray());

        var response = await PostIcsAsync(client, ics);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await ctx.JournalTasks.CountAsync());
    }

    [Fact]
    public async Task Import_MalformedFile_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var response = await PostIcsAsync(client, "this is not an ics file");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_NonIcsExtension_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vtodo("x1", "SUMMARY:x"));
        var response = await PostIcsAsync(client, ics, "data.txt");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_WrongContentType_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vtodo("x2", "SUMMARY:x"));
        var response = await PostIcsAsync(client, ics, "data.ics", contentType: "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_OctetStreamContentType_IsAccepted()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vtodo("o1", "SUMMARY:Octet"));
        var response = await PostIcsAsync(client, ics, "tasks.ics", contentType: "application/octet-stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Import_OnlyVEvents_IsNoOp_NotError()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//EN\r\n" +
                  "BEGIN:VEVENT\r\nUID:e1\r\nDTSTART:20300101T090000Z\r\nSUMMARY:Event\r\nEND:VEVENT\r\n" +
                  "END:VCALENDAR\r\n";
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(0, result.UpdatedCount);
    }

    [Fact]
    public async Task Import_WithOnlyCreateClaim_ReturnsForbidden()
    {
        var factory = Factory;
        using var client = factory.ClientFor([PermissionClaims.TasksCreate, PermissionClaims.TasksRead]);

        var ics = Vcalendar(Vtodo("p1", "SUMMARY:x"));
        var response = await PostIcsAsync(client, ics);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------ ExternalUid on the direct API

    [Fact]
    public async Task Create_WithoutExternalUid_GeneratesUrnUuid()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var created = await CreateAsync(client, new NewJournalTask { Title = "Auto" });

        Assert.StartsWith("urn:uuid:", created.ExternalUid);
    }

    [Fact]
    public async Task Create_WithSuppliedExternalUid_StoresItVerbatim()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var created = await CreateAsync(client, new NewJournalTask { Title = "Given", ExternalUid = "my-own-id" });

        Assert.Equal("my-own-id", created.ExternalUid);
    }

    [Fact]
    public async Task Create_WithExternalUidAlreadyUsed_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await CreateAsync(client, new NewJournalTask { Title = "First", ExternalUid = "shared-id" });

        var post = await client.PostAsJsonAsync(Path, new NewJournalTask { Title = "Second", ExternalUid = "shared-id" });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Update_WithNewExternalUid_StoresItVerbatim()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var created = await CreateAsync(client, new NewJournalTask { Title = "Task" });

        var put = await client.PutAsJsonAsync($"{Path}/{created.JournalTaskId}",
            new UpdateJournalTask { Title = "Task", ExternalUid = "reassigned-id" });
        put.EnsureSuccessStatusCode();

        var updated = (await put.Content.ReadFromJsonAsync<ExistingJournalTask>())!;
        Assert.Equal("reassigned-id", updated.ExternalUid);
    }

    [Fact]
    public async Task Update_WithExternalUidUsedByAnotherTask_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await CreateAsync(client, new NewJournalTask { Title = "A", ExternalUid = "uid-a" });
        var b = await CreateAsync(client, new NewJournalTask { Title = "B", ExternalUid = "uid-b" });

        var put = await client.PutAsJsonAsync($"{Path}/{b.JournalTaskId}",
            new UpdateJournalTask { Title = "B", ExternalUid = "uid-a" });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    // ------------------------------------------------------------------ import position (AC #14) + link diffing

    [Fact]
    public async Task Import_NewTasks_AppendToEndOfColumn()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var a = await CreateAsync(client, new NewJournalTask { Title = "A" }); // Backlog pos 0
        var b = await CreateAsync(client, new NewJournalTask { Title = "B" }); // Backlog pos 1
        Assert.Equal(0, a.Position);
        Assert.Equal(1, b.Position);

        var ics = Vcalendar(
            Vtodo("new-1", "SUMMARY:New1", "STATUS:NEEDS-ACTION"),
            Vtodo("new-2", "SUMMARY:New2", "STATUS:NEEDS-ACTION"));
        var result = await ImportAsync(client, ics);
        Assert.Equal(2, result.ImportedCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(2, (await ctx.JournalTasks.SingleAsync(t => t.ExternalUid == "new-1")).Position);
        Assert.Equal(3, (await ctx.JournalTasks.SingleAsync(t => t.ExternalUid == "new-2")).Position);
    }

    [Fact]
    public async Task Import_StatusChangeUpdate_AppendsToNewColumn_LeavesOthersUndisturbed()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        var a = await CreateAsync(client, new NewJournalTask { Title = "A" }); // Backlog pos 0
        var b = await CreateAsync(client, new NewJournalTask { Title = "B" }); // Backlog pos 1

        // Move A to Doing via import; B's position must be undisturbed (AC #14).
        var ics = Vcalendar(Vtodo(a.ExternalUid, "SUMMARY:A", "STATUS:IN-PROCESS"));
        var result = await ImportAsync(client, ics);
        Assert.Equal(1, result.UpdatedCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var movedA = await ctx.JournalTasks.SingleAsync(t => t.JournalTaskId == a.JournalTaskId);
        var keptB = await ctx.JournalTasks.SingleAsync(t => t.JournalTaskId == b.JournalTaskId);
        Assert.NotNull(movedA.StartedAt);      // now Doing
        Assert.Equal(0, movedA.Position);      // appended to the (empty) Doing column
        Assert.Equal(1, keptB.Position);       // untouched
    }

    [Fact]
    public async Task Import_UnchangedStatusUpdate_LeavesPositionAndTimestampsUntouched()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await CreateAsync(client, new NewJournalTask { Title = "A" });          // Backlog pos 0
        var b = await CreateAsync(client, new NewJournalTask { Title = "B" });  // Backlog pos 1

        // Re-import B unchanged (still Backlog): position must not be recomputed, nor timestamps re-stamped.
        var ics = Vcalendar(Vtodo(b.ExternalUid, "SUMMARY:B", "STATUS:NEEDS-ACTION"));
        await ImportAsync(client, ics);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var keptB = await ctx.JournalTasks.SingleAsync(t => t.JournalTaskId == b.JournalTaskId);
        Assert.Equal(1, keptB.Position);
        Assert.Null(keptB.StartedAt);
        Assert.Null(keptB.CompletedAt);
    }

    [Fact]
    public async Task Import_Update_RemovesTagAndAttachmentLinksNotInFile()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFiles);
        var alpha = await SeedTagAsync(factory, "Alpha");
        var beta = await SeedTagAsync(factory, "Beta");
        var fileId = await SeedFileAsync(factory, "doc.pdf", "application/pdf");

        var created = await CreateAsync(client, new NewJournalTask
        {
            Title = "Linked",
            TagIds = [alpha, beta],
            AttachmentFileIds = [fileId],
        });

        // Re-import the same task naming only Alpha and no ATTACH: Beta + the attachment must be diffed off.
        var ics = Vcalendar(Vtodo(created.ExternalUid, "SUMMARY:Linked", "CATEGORIES:Alpha"));
        var result = await ImportAsync(client, ics);
        Assert.Equal(1, result.UpdatedCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks
            .Include(t => t.ItemTags).Include(t => t.Attachments)
            .SingleAsync(t => t.JournalTaskId == created.JournalTaskId);
        Assert.Equal(alpha, Assert.Single(row.ItemTags).JournalTaskTagId);
        Assert.Empty(row.Attachments);
    }

    [Fact]
    public async Task Import_DescriptionTooLong_IsSkipped()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);

        var ics = Vcalendar(Vtodo("d-long", "SUMMARY:Big", $"DESCRIPTION:{new string('d', 4097)}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("Description", group.Reason);
    }

    // ------------------------------------------------------------------ attached files travel inline

    [Fact]
    public async Task Export_WithAttachedFile_WithoutFilesRead_IsForbidden_AndSoIsTheSummary()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFiles);
        var fileId = await SeedFileAsync(factory, "note.txt", "text/plain", NoteBytes);
        await CreateAsync(client, new NewJournalTask { Title = "With file", AttachmentFileIds = [fileId] });

        var readOnly = Factory;
        using var readOnlyClient = readOnly.ClientFor(ReadOnly);

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync(IcsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync(IcsPath + "/summary")).StatusCode);
    }

    [Fact]
    public async Task Export_WithoutAttachedFiles_NeedsOnlyTasksRead()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await CreateAsync(client, new NewJournalTask { Title = "Plain" });

        var readOnly = Factory;
        using var readOnlyClient = readOnly.ClientFor(ReadOnly);

        Assert.Equal(HttpStatusCode.OK, (await readOnlyClient.GetAsync(IcsPath)).StatusCode);
    }

    [Fact]
    public async Task Summary_CountsTasksAndAttachedFiles_ForAllFilteredAndSingle()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFiles);
        var fileId = await SeedFileAsync(factory, "note.txt", "text/plain", NoteBytes);
        var withFile = await CreateAsync(client, new NewJournalTask { Title = "Alpha with file", AttachmentFileIds = [fileId] });
        await CreateAsync(client, new NewJournalTask { Title = "Beta" });

        var all = await client.GetFromJsonAsync<TaskIcsExportSummary>(IcsPath + "/summary?statuses=Backlog&statuses=Doing&statuses=Done&statuses=Archived");
        var filtered = await client.GetFromJsonAsync<TaskIcsExportSummary>(IcsPath + "/summary?search=Beta");
        var single = await client.GetFromJsonAsync<TaskIcsExportSummary>(IcsPath + $"/summary?statuses=Archived&statuses=Backlog&ids={withFile.JournalTaskId}");

        Assert.Equal((2, 1, (long)NoteBytes.Length), (all!.TaskCount, all.AttachmentCount, all.AttachmentBytes));
        Assert.Equal((1, 0), (filtered!.TaskCount, filtered.AttachmentCount));
        Assert.Equal((1, 1), (single!.TaskCount, single.AttachmentCount));
    }

    [Fact]
    public async Task Export_SingleAndFiltered_CarryTheFileInline()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFiles);
        var fileId = await SeedFileAsync(factory, "note.txt", "text/plain", NoteBytes);
        var task = await CreateAsync(client, new NewJournalTask { Title = "Camping", AttachmentFileIds = [fileId] });

        var single = Unfold(await client.GetStringAsync(IcsPath + $"?statuses=Backlog&ids={task.JournalTaskId}"));
        var filtered = Unfold(await client.GetStringAsync(IcsPath + "?search=Camp"));

        foreach (var body in new[] { single, filtered })
        {
            Assert.Contains(";ENCODING=BASE64;VALUE=BINARY;FILENAME=\"note.txt\";", body);
            Assert.Contains($"X-ODYSSEY-SHA256={Sha256(NoteBytes)}:" + Convert.ToBase64String(NoteBytes), body);
        }
    }

    [Fact]
    public async Task Export_OverTheByteCap_IsRefusedBeforeAnyByte()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFiles);
        await SetSettingAsync(factory, SystemSettingsKeys.TaskIcsMaxExportMegabytes, "1");
        var big = new byte[1024 * 1024]; // 1 MB of content is ~1.4 MB once base64-encoded and folded
        var fileId = await SeedFileAsync(factory, "big.bin", "application/octet-stream", big);
        await CreateAsync(client, new NewJournalTask { Title = "Big", AttachmentFileIds = [fileId] });

        var summary = await client.GetAsync(IcsPath + "/summary");
        var export = await client.GetAsync(IcsPath);

        Assert.Equal(HttpStatusCode.BadRequest, summary.StatusCode);
        Assert.Contains("1 MB export limit", await summary.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, export.StatusCode);
    }

    [Fact]
    public async Task RoundTrip_SameInstance_ReusesTheFile_NoDuplicate()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFileUploads);
        var fileId = await SeedFileAsync(factory, "note.txt", "text/plain", NoteBytes);
        await CreateAsync(client, new NewJournalTask { Title = "Camping", AttachmentFileIds = [fileId] });

        var exported = await client.GetStringAsync(IcsPath);
        var result = await ImportAsync(client, exported);

        Assert.Equal((1, 0, 1, 0), (result.UpdatedCount, result.StoredFileCount, result.ReusedFileCount, result.SkippedAttachmentCount));
        Assert.Single(await FilesAsync(factory));
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.Include(t => t.Attachments).SingleAsync();
        Assert.Equal(fileId, Assert.Single(row.Attachments).FileId);
    }

    [Fact]
    public async Task RoundTrip_IntoAnotherInstance_StoresTheFile_WithItsNameTypeAndBytes()
    {
        await using var source = new ApiFactory(ReadWriteWithFiles);
        using var sourceClient = source.CreateClient();
        var fileId = await SeedFileAsync(source, "note.txt", "text/plain", NoteBytes);
        await CreateAsync(sourceClient, new NewJournalTask { Title = "Camping", AttachmentFileIds = [fileId] });
        var exported = await sourceClient.GetStringAsync(IcsPath);

        await using var target = new ApiFactory(ReadWriteWithFileUploads);
        using var targetClient = target.CreateClient();
        var result = await ImportAsync(targetClient, exported);

        Assert.Equal((1, 1, 0), (result.ImportedCount, result.StoredFileCount, result.ReusedFileCount));
        var stored = Assert.Single(await FilesAsync(target));
        Assert.Equal(("note.txt", "text/plain", Sha256(NoteBytes)), (stored.FileName, stored.ContentType, stored.Sha256Hash));
        Assert.NotNull(stored.JournalStoredObjectId); // on disk, in the Journal area — not a legacy blob
        Assert.Equal(NoteBytes, await ContentAsync(targetClient, stored.Id));

        // Importing the same file again matches what the first import stored: still one file.
        var again = await ImportAsync(targetClient, exported);
        Assert.Equal((1, 0, 1), (again.UpdatedCount, again.StoredFileCount, again.ReusedFileCount));
        Assert.Single(await FilesAsync(target));
    }

    [Fact]
    public async Task Import_InlineFile_MatchingStoredContent_IsReusedByHash()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFileUploads);
        var existingId = await SeedFileAsync(factory, "existing.txt", "text/plain", NoteBytes);

        // No X-ODYSSEY-FILE-ID: a file from elsewhere whose content is already stored.
        var ics = Vcalendar(Vtodo("h1", "SUMMARY:Hash match", InlineAttach(NoteBytes, "renamed.txt", "text/plain")));
        var result = await ImportAsync(client, ics);

        Assert.Equal((0, 1), (result.StoredFileCount, result.ReusedFileCount));
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalTasks.Include(t => t.Attachments).SingleAsync();
        Assert.Equal(existingId, Assert.Single(row.Attachments).FileId);
    }

    [Fact]
    public async Task Import_SameInlineFileOnTwoTasks_IsStoredOnce()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFileUploads);
        var attach = InlineAttach(NoteBytes, "note.txt", "text/plain");

        var ics = Vcalendar(Vtodo("d1", "SUMMARY:One", attach), Vtodo("d2", "SUMMARY:Two", attach));
        var result = await ImportAsync(client, ics);

        Assert.Equal((2, 1, 1), (result.ImportedCount, result.StoredFileCount, result.ReusedFileCount));
        var file = Assert.Single(await FilesAsync(factory));
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.All(await ctx.JournalTasks.Include(t => t.Attachments).ToListAsync(),
            task => Assert.Equal(file.Id, Assert.Single(task.Attachments).FileId));
    }

    [Theory]
    [InlineData(PermissionClaims.FilesCreate)]
    [InlineData(PermissionClaims.FilesRead)]
    public async Task Import_InlineFile_WithoutBothFileClaims_IsForbidden_NothingImported(string missing)
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFileUploads.Where(c => c != missing).ToArray());

        var response = await PostIcsAsync(client, Vcalendar(Vtodo("f1", "SUMMARY:Gated", InlineAttach(NoteBytes, "note.txt", "text/plain"))));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await ctx.JournalTasks.CountAsync());
        Assert.Empty(await FilesAsync(factory));
    }

    [Fact]
    public async Task Import_InlineFile_Refusals_SkipTheFileButKeepTheTask()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFileUploads);
        var wrongHash = new string('0', 64);

        var ics = Vcalendar(
            Vtodo("r1", "SUMMARY:Damaged", "ATTACH;FMTTYPE=text/plain;ENCODING=BASE64;VALUE=BINARY:!!!not base64!!!"),
            Vtodo("r2", "SUMMARY:Checksum", InlineAttach(NoteBytes, "note.txt", "text/plain", $";X-ODYSSEY-SHA256={wrongHash}")),
            Vtodo("r3", "SUMMARY:Executable", InlineAttach([0x4D, 0x5A, 0x90, 0x00], "tool.exe", "application/x-msdownload")),
            Vtodo("r4", "SUMMARY:Mislabelled", InlineAttach(NoteBytes, "fake.png", "image/png")));
        var result = await ImportAsync(client, ics);

        Assert.Equal(4, result.ImportedCount);
        Assert.Equal(4, result.SkippedAttachmentCount);
        Assert.Equal(0, result.StoredFileCount);
        Assert.Empty(await FilesAsync(factory));
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
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFileUploads);
        await SetSettingAsync(factory, SystemSettingsKeys.FileStorageMaxUploadMegabytes, "1");
        var big = Encoding.UTF8.GetBytes(new string('a', 1536 * 1024)); // 1.5 MB, over the 1 MB upload cap
        var stored = Encoding.UTF8.GetBytes(new string('b', 1536 * 1024));
        var storedId = await SeedFileAsync(factory, "stored.txt", "text/plain", stored);

        var result = await ImportAsync(client, Vcalendar(
            Vtodo("u1", "SUMMARY:New big", InlineAttach(big, "big.txt", "text/plain")),
            Vtodo("u2", "SUMMARY:Known big", InlineAttach(stored, "stored.txt", "text/plain"))));

        Assert.Equal((2, 0, 1, 1), (result.ImportedCount, result.StoredFileCount, result.ReusedFileCount, result.SkippedAttachmentCount));
        var group = Assert.Single(result.Skipped);
        Assert.Contains("1 MB upload limit", group.Reason);
        Assert.Equal(["New big"], group.SampleTitles);
        Assert.Equal(storedId, Assert.Single(await FilesAsync(factory)).Id);
    }

    [Fact]
    public async Task Export_ThatGrowsPastTheByteCapWhileStreaming_IsAborted_NotTruncated()
    {
        // The pre-flight check judges attached files exactly, but task text only as it is written. Text alone over the
        // cap must end the response abnormally, so a browser reports a failed download instead of saving part of it.
        var factory = Factory;
        using var client = factory.ClientFor(ReadWrite);
        await SetSettingAsync(factory, SystemSettingsKeys.TaskIcsMaxExportMegabytes, "1");
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            for (var i = 0; i < 400; i++)
            {
                context.JournalTasks.Add(new JournalTask
                {
                    ExternalUid = $"bulk-{i}", Title = $"Bulk {i}", Content = new string('x', 4000), Position = i,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
            }

            await context.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(IcsPath + "/summary")).StatusCode); // nothing to judge up front
        var failure = await Record.ExceptionAsync(async () =>
        {
            using var response = await client.GetAsync(IcsPath);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"The export completed ({body.Length} chars) instead of being aborted.");
        });

        Assert.True(failure is HttpRequestException or IOException or OperationCanceledException, failure?.ToString());
    }

    [Fact]
    public async Task Import_InlineFile_LargerThanTheImportCap_IsRefusedWhole()
    {
        var factory = Factory;
        using var client = factory.ClientFor(ReadWriteWithFileUploads);
        await SetSettingAsync(factory, SystemSettingsKeys.TaskIcsMaxImportMegabytes, "1");

        var response = await PostIcsAsync(client, Vcalendar(Vtodo("b1", "SUMMARY:Big", InlineAttach(new byte[1024 * 1024], "big.txt", "text/plain"))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await FilesAsync(factory));
    }

    // ------------------------------------------------------------------ helpers

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Vcalendar(params string[] todos) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(todos) + "END:VCALENDAR\r\n";

    private static string Vtodo(string uid, params string[] lines) =>
        "BEGIN:VTODO\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VTODO\r\n";

    private static async Task<ExistingJournalTask> CreateAsync(HttpClient client, NewJournalTask request)
    {
        var post = await client.PostAsJsonAsync(Path, request);
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingJournalTask>())!;
    }

    private static async Task<TaskIcsImportResult> ImportAsync(HttpClient client, string ics)
    {
        var response = await PostIcsAsync(client, ics);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskIcsImportResult>())!;
    }

    private static async Task<HttpResponseMessage> PostIcsAsync(
        HttpClient client, string ics, string fileName = "tasks.ics", string contentType = "text/calendar")
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(ics));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);
        return await client.PostAsync(IcsPath, content);
    }

    private static async Task<Guid> SeedTagAsync(WebApplicationFactory<Program> factory, string name, bool archived = false)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.JournalTaskTags.Add(new ContextJournalTaskTag
        {
            JournalTaskTagId = id,
            Name = name,
            Archived = archived ? DateTime.UtcNow : null,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static readonly byte[] SeededFileContent = [1, 2, 3];

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    // One inline attachment line, folded the way a producer might: base64 split over continuation lines.
    private static string InlineAttach(byte[] content, string fileName, string contentType, string? extraParams = null)
    {
        var base64 = Convert.ToBase64String(content);
        var folded = string.Join("\r\n ", Enumerable.Range(0, (base64.Length + 59) / 60).Select(i => base64.Substring(i * 60, Math.Min(60, base64.Length - i * 60))));
        return $"ATTACH;FMTTYPE={contentType};ENCODING=BASE64;VALUE=BINARY;FILENAME=\"{fileName}\"{extraParams}:{folded}";
    }

    private static async Task<List<FileMetadata>> FilesAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.FileMetadata.AsNoTracking().ToListAsync();
    }

    private static async Task<byte[]> ContentAsync(HttpClient client, Guid fileId) =>
        await client.GetByteArrayAsync($"/api/files/{fileId}/content");

    // RFC 5545 unfolding: a CRLF followed by one space or tab is removed.
    private static string Unfold(string ics) => ics.Replace("\r\n ", string.Empty).Replace("\r\n\t", string.Empty);

    private static async Task SetSettingAsync(WebApplicationFactory<Program> factory, string key, string value)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        var row = await context.SystemSettings.SingleOrDefaultAsync(s => s.Key == key);
        if (row is null)
        {
            context.SystemSettings.Add(new SystemSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            row.Value = value;
        }

        await context.SaveChangesAsync();
    }

    private static Task<Guid> SeedFileAsync(WebApplicationFactory<Program> factory, string fileName, string contentType) =>
        SeedFileAsync(factory, fileName, contentType, SeededFileContent);

    private static async Task<Guid> SeedFileAsync(
        WebApplicationFactory<Program> factory, string fileName, string contentType, byte[] content)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var blobContent = content;
        var metadataId = Guid.NewGuid();
        context.FileMetadata.Add(new FileMetadata
        {
            Id = metadataId,
            UploadedByUserId = ActorUserId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = content.Length,
            Sha256Hash = Sha256(content),
            UploadedAtUtc = DateTime.UtcNow,
            FinanceStoredObjectId = await StoredFileFixtures.PutAsync(scope.ServiceProvider, blobContent),
        });
        await context.SaveChangesAsync();
        return metadataId;
    }

    public class ApiFactory : OdysseyApiFactory
    {
        private readonly string databaseName;

        // sharingStoreWith: a second caller (other claims) over the same database and file volume.
        public ApiFactory(IReadOnlyCollection<string>? permissions, ApiFactory? sharingStoreWith = null)
            : this(permissions, sharingStoreWith?.databaseName ?? $"domain-{Guid.NewGuid()}", sharingStoreWith)
        {
        }

        private ApiFactory(IReadOnlyCollection<string>? permissions, string databaseName, ApiFactory? sharingStoreWith)
            : base(permissions, ActorUserId, configureServices: services => IsolateDomainContext(services, databaseName),
                sharingStoreWith: sharingStoreWith)
        {
            this.databaseName = databaseName;
        }

        private static void IsolateDomainContext(IServiceCollection services, string databaseName)
        {
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName));
        }
    }

    /// <summary>The class's shared host: every caller's claims travel per request (<see cref="SharedApiHostClients"/>).</summary>
    public sealed class Host() : ApiFactory(permissions: null);
}
