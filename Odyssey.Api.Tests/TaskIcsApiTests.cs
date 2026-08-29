using Odyssey.Context;
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
public class TaskIcsApiTests
{
    private const string ActorUserId = "task-ics-actor-id";
    private const string Path = "/api/tasks";
    private const string IcsPath = "/api/tasks/ics";

    // The shipped default for TaskIcsMaxImportTasks (issue #343 §6) — behavior-preserving with the
    // old hard-coded TaskIcsService.MaxVTodos this replaces.
    private const int DefaultMaxImportTasks = 2000;

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

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Export_EmptyBoard_ReturnsValidVCalendar_WithNoSniff()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

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
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(IcsPath);

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(fileName);
        Assert.Matches(@"^odyssey-tasks-\d{8}-\d{6}Z\.ics$", fileName!);
    }

    [Fact]
    public async Task Export_WithoutReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(IcsPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Export_Task_EmitsVTodo_WithUidSummaryStatusDueCategoriesAttach()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
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
        Assert.Contains($"ATTACH:odyssey-file:{fileId}", body);
        Assert.Contains("PERCENT-COMPLETE:0", body);
    }

    [Fact]
    public async Task Export_DueValueType_MatchesDtstartPresence()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.JournalTaskMaxLinksPerKind, "2");
        using var client = factory.CreateClient();

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
    public async Task Import_ArchivedTagCategory_IsNotResolved()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await SeedTagAsync(factory, "Retired", archived: true);

        var ics = Vcalendar(Vtodo("c2", "SUMMARY:Task", "CATEGORIES:Retired"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedTagLinkCount);
    }

    [Fact]
    public async Task Import_ResolvableAttachment_WithFilesRead_LinksIt()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();

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
        await using var factory = new ApiFactory(ReadWrite); // no files.read
        using var client = factory.CreateClient();
        var fileId = await SeedFileAsync(factory, "doc.pdf", "application/pdf");

        var ics = Vcalendar(Vtodo("a3", "SUMMARY:Gated", $"ATTACH:odyssey-file:{fileId}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedAttachmentCount);
    }

    [Fact]
    public async Task Import_NonOdysseyAttachmentScheme_IsIgnoredNotCounted()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vtodo("a4", "SUMMARY:External link", "ATTACH:https://example.com/file.pdf"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedAttachmentCount);
    }

    [Fact]
    public async Task Import_RecurringVTodo_IsSkippedWithReason()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vtodo("r1", "SUMMARY:Repeats", "STATUS:NEEDS-ACTION", "RRULE:FREQ=WEEKLY;COUNT=5"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Equal("Recurring VTODO not supported", group.Reason);
    }

    [Fact]
    public async Task Import_MissingSummary_IsSkippedWithSample()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vtodo("l1", $"SUMMARY:{new string('x', 201)}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("Title", group.Reason);
    }

    [Fact]
    public async Task Import_DuplicateUidWithinFile_LastWriteWins_NoDuplicate()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var todos = Enumerable.Range(0, DefaultMaxImportTasks + 1)
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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await PostIcsAsync(client, "this is not an ics file");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_NonIcsExtension_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vtodo("x1", "SUMMARY:x"));
        var response = await PostIcsAsync(client, ics, "data.txt");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_WrongContentType_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vtodo("x2", "SUMMARY:x"));
        var response = await PostIcsAsync(client, ics, "data.ics", contentType: "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_OctetStreamContentType_IsAccepted()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vtodo("o1", "SUMMARY:Octet"));
        var response = await PostIcsAsync(client, ics, "tasks.ics", contentType: "application/octet-stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Import_OnlyVEvents_IsNoOp_NotError()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

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
        await using var factory = new ApiFactory([PermissionClaims.TasksCreate, PermissionClaims.TasksRead]);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vtodo("p1", "SUMMARY:x"));
        var response = await PostIcsAsync(client, ics);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------ ExternalUid on the direct API

    [Fact]
    public async Task Create_WithoutExternalUid_GeneratesUrnUuid()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewJournalTask { Title = "Auto" });

        Assert.StartsWith("urn:uuid:", created.ExternalUid);
    }

    [Fact]
    public async Task Create_WithSuppliedExternalUid_StoresItVerbatim()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewJournalTask { Title = "Given", ExternalUid = "my-own-id" });

        Assert.Equal("my-own-id", created.ExternalUid);
    }

    [Fact]
    public async Task Create_WithExternalUidAlreadyUsed_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await CreateAsync(client, new NewJournalTask { Title = "First", ExternalUid = "shared-id" });

        var post = await client.PostAsJsonAsync(Path, new NewJournalTask { Title = "Second", ExternalUid = "shared-id" });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Update_WithNewExternalUid_StoresItVerbatim()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
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
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vtodo("d-long", "SUMMARY:Big", $"DESCRIPTION:{new string('d', 4097)}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("Description", group.Reason);
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

    private static async Task<Guid> SeedFileAsync(WebApplicationFactory<Program> factory, string fileName, string contentType)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var metadataId = Guid.NewGuid();
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(new FileMetadata
        {
            Id = metadataId,
            UploadedByUserId = ActorUserId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = 3,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = blob.Id,
            FileBlob = blob,
        });
        await context.SaveChangesAsync();
        return metadataId;
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(IReadOnlyCollection<string>? permissions)
            : base(permissions, ActorUserId, configureServices: IsolateDomainContext)
        {
        }

        private static void IsolateDomainContext(IServiceCollection services)
        {
            var databaseName = $"domain-{Guid.NewGuid()}";
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName));
        }
    }
}
