using System.Net;
using System.Net.Http.Json;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using ContextJournalTaskTag = Odyssey.Context.JournalTaskTag;
using JournalTaskStatus = Odyssey.Dtos.Journal.JournalTaskStatus;

namespace Odyssey.Api.Tests;

public class JournalTasksApiTests
{
    private const string ActorUserId = "tasks-actor-id";
    private const string Path = "/api/tasks";

    private static readonly string[] ReadOnly = [PermissionClaims.TasksRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.TasksRead, PermissionClaims.TasksCreate,
        PermissionClaims.TasksUpdate, PermissionClaims.TasksDelete,
    ];

    private static readonly string[] ReadWriteWithFiles =
    [
        PermissionClaims.TasksRead, PermissionClaims.TasksCreate,
        PermissionClaims.TasksUpdate, PermissionClaims.TasksDelete, PermissionClaims.FilesRead,
    ];

    // ── Authorization matrix (criterion #8) ───────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_Guest_WithoutTaskClaims_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mutations_WithReadOnlyPermission_ReturnForbidden()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewTask());
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var delete = await client.DeleteAsync($"{Path}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    // ── CRUD happy path (criterion #2) ─────────────────────────────────────────

    [Fact]
    public async Task Post_Get_Update_Delete_RoundTrip()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewTask(title: "Pay rent"));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = await post.Content.ReadFromJsonAsync<ExistingJournalTask>();
        Assert.Equal("Pay rent", created!.Title);
        Assert.Equal(JournalTaskStatus.Backlog, created.Status);
        Assert.Equal(ActorUserId, created.CreatedByUserId);
        Assert.Null(created.CompletedAt);

        var fetched = await client.GetFromJsonAsync<ExistingJournalTask>($"{Path}/{created.JournalTaskId}");
        Assert.Equal("Pay rent", fetched!.Title);

        var list = await client.GetPagedItemsAsync<JournalTaskSummary>(Path);
        Assert.Contains(list!, t => t.JournalTaskId == created.JournalTaskId);

        var put = await client.PutAsJsonAsync($"{Path}/{created.JournalTaskId}", UpdateTask(title: "Pay rent (April)"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await put.Content.ReadFromJsonAsync<ExistingJournalTask>();
        Assert.Equal("Pay rent (April)", updated!.Title);
        Assert.Equal(ActorUserId, updated.UpdatedByUserId);

        var delete = await client.DeleteAsync($"{Path}/{created.JournalTaskId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var afterDelete = await client.GetAsync($"{Path}/{created.JournalTaskId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownTask_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Status lifecycle (criterion #2) ────────────────────────────────────────

    [Fact]
    public async Task PatchStatus_ToDone_StampsCompletedAt_ThenBack_ClearsIt()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewTask());

        var toDoing = await SetStatusAsync(client, id, JournalTaskStatus.Doing);
        Assert.Equal(JournalTaskStatus.Doing, toDoing.Status);
        Assert.Null(toDoing.CompletedAt);

        var toDone = await SetStatusAsync(client, id, JournalTaskStatus.Done);
        Assert.Equal(JournalTaskStatus.Done, toDone.Status);
        Assert.NotNull(toDone.CompletedAt);

        var backToDoing = await SetStatusAsync(client, id, JournalTaskStatus.Doing);
        Assert.Equal(JournalTaskStatus.Doing, backToDoing.Status);
        Assert.Null(backToDoing.CompletedAt);
    }

    [Fact]
    public async Task Put_StatusChange_UnknownTask_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"{Path}/{Guid.NewGuid()}",
            new UpdateJournalTask { Title = "Ghost", Status = JournalTaskStatus.Done });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Archived_TasksHiddenByDefault_ReachableViaStatusFilter()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewTask(title: "Old chore"));
        await SetStatusAsync(client, id, JournalTaskStatus.Archived);

        var defaultList = await client.GetPagedItemsAsync<JournalTaskSummary>(Path);
        Assert.DoesNotContain(defaultList!, t => t.JournalTaskId == id);

        var archivedList = await client.GetPagedItemsAsync<JournalTaskSummary>($"{Path}?statuses=Archived");
        Assert.Contains(archivedList!, t => t.JournalTaskId == id);
    }

    // ── Reorder: gap-free re-sequence (criterion #2) ───────────────────────────

    [Fact]
    public async Task PatchPosition_MovesTaskAndReSequencesColumnGapFree()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        // Four Backlog tasks land at positions 0,1,2,3 in creation order.
        var a = await CreateAsync(client, NewTask(title: "A"));
        var b = await CreateAsync(client, NewTask(title: "B"));
        var c = await CreateAsync(client, NewTask(title: "C"));
        var d = await CreateAsync(client, NewTask(title: "D"));

        // Move D to the front of the column.
        var moved = await SetPositionAsync(client, d, 0);
        Assert.Equal(0, moved.Position);

        // The column re-sequences gap-free: D,A,B,C → 0,1,2,3.
        var positions = await PositionsByIdAsync(client);
        Assert.Equal(0, positions[d]);
        Assert.Equal(1, positions[a]);
        Assert.Equal(2, positions[b]);
        Assert.Equal(3, positions[c]);

        // Positions are contiguous with no gaps or duplicates.
        var ordered = positions.Values.OrderBy(p => p).ToList();
        Assert.Equal(new[] { 0, 1, 2, 3 }, ordered);
    }

    [Fact]
    public async Task PatchPosition_TargetBeyondEnd_ClampsToLastSlot()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var a = await CreateAsync(client, NewTask(title: "A"));
        var b = await CreateAsync(client, NewTask(title: "B"));
        var c = await CreateAsync(client, NewTask(title: "C"));

        // Move A far past the end; it clamps to the last position.
        var moved = await SetPositionAsync(client, a, 999);
        Assert.Equal(2, moved.Position);

        var positions = await PositionsByIdAsync(client);
        Assert.Equal(0, positions[b]);
        Assert.Equal(1, positions[c]);
        Assert.Equal(2, positions[a]);
    }

    [Fact]
    public async Task Put_Reposition_UnknownTask_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"{Path}/{Guid.NewGuid()}",
            new UpdateJournalTask { Title = "Ghost", Position = 0 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Tags & attachments ─────────────────────────────────────────────────────

    [Fact]
    public async Task Post_UnknownTag_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewTask(tagIds: [Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task List_FilteredByTag_ReturnsMatchingSubset()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var tagId = await SeedJournalTaskTagAsync(factory, "Urgent");
        using var client = factory.CreateClient();

        var tagged = await CreateAsync(client, NewTask(title: "Fix leak", tagIds: [tagId]));
        var untagged = await CreateAsync(client, NewTask(title: "Someday"));

        var results = await client.GetPagedItemsAsync<JournalTaskSummary>($"{Path}?tagIds={tagId}");
        Assert.Contains(results!, t => t.JournalTaskId == tagged);
        Assert.DoesNotContain(results!, t => t.JournalTaskId == untagged);
    }

    [Fact]
    public async Task Post_PdfAttachment_IsLinkedByIdOnly()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        var pdfId = await SeedFileAsync(factory, "spec.pdf", "application/pdf");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewTask(attachmentFileIds: [pdfId]));

        var fetched = await client.GetFromJsonAsync<ExistingJournalTask>($"{Path}/{id}");
        var attachment = Assert.Single(fetched!.Attachments);
        Assert.Equal(pdfId, attachment.FileId);
    }

    [Fact]
    public async Task Post_LinkingAttachment_WithoutFilesReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var pdfId = await SeedFileAsync(factory, "spec.pdf", "application/pdf");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewTask(attachmentFileIds: [pdfId]));

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    // ── Mass-assignment guard (criterion #6) ───────────────────────────────────

    [Fact]
    public async Task Post_WithNestedForeignObjects_DoesNotCreateTagsOrAttachments()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var body = new
        {
            title = "Sneaky task",
            content = "content",
            status = (int)JournalTaskStatus.Backlog,
            tagIds = Array.Empty<Guid>(),
            attachmentFileIds = Array.Empty<Guid>(),
            tags = new[] { new { taskTagId = Guid.NewGuid(), name = "INJECTED", normalizedName = "injected" } },
            attachments = new[] { new { fileId = Guid.NewGuid() } },
        };

        var post = await client.PostAsJsonAsync(Path, body);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using var scope = factory.Services.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await journal.JournalTaskTags.AnyAsync());
        Assert.False(await journal.JournalTaskTagLinks.AnyAsync());
        Assert.False(await journal.JournalTaskAttachments.AnyAsync());
    }

    // ── Search / pagination (criterion #7) ─────────────────────────────────────

    [Fact]
    public async Task List_Search_MatchesTitleAndContent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var byTitle = await CreateAsync(client, NewTask(title: "Renew passport"));
        var byContent = await CreateAsync(client, NewTask(title: "Errand", content: "remember the passport photos"));
        var unrelated = await CreateAsync(client, NewTask(title: "Water plants"));

        var results = await client.GetPagedItemsAsync<JournalTaskSummary>($"{Path}?search=passport");
        Assert.Contains(results!, t => t.JournalTaskId == byTitle);
        Assert.Contains(results!, t => t.JournalTaskId == byContent);
        Assert.DoesNotContain(results!, t => t.JournalTaskId == unrelated);
    }

    // The summary Snippet is a plain-text preview of Content, truncated to 200 chars (board card body).
    // Covers the three reachable branches: null content, short (≤200, incl. the boundary), and long (>200).
    [Fact]
    public async Task List_Summary_Snippet_TruncatesContentToMax()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var longContent = new string('x', 250);
        var boundaryContent = new string('y', 200);

        var noContent = await CreateAsync(client, NewTask(title: "No content", content: null));
        var boundary = await CreateAsync(client, NewTask(title: "Boundary", content: boundaryContent));
        var longTask = await CreateAsync(client, NewTask(title: "Long", content: longContent));

        var results = await client.GetPagedItemsAsync<JournalTaskSummary>(Path);

        Assert.Null(results!.Single(t => t.JournalTaskId == noContent).Snippet);
        Assert.Equal(boundaryContent, results!.Single(t => t.JournalTaskId == boundary).Snippet);

        var longSnippet = results!.Single(t => t.JournalTaskId == longTask).Snippet;
        Assert.NotNull(longSnippet);
        Assert.Equal(200, longSnippet!.Length);
        Assert.Equal(longContent[..200], longSnippet);
    }

    // Author-name enrichment (#314): the tasks controller resolves the author id → username/email the
    // same way the entries controller does; null when unresolvable.
    [Fact]
    public async Task Author_DisplayName_Resolves_When_The_User_Exists()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await factory.SeedActorUserAsync(displayName: "Ada L.");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewTask(title: "Mine"));

        var results = await client.GetPagedItemsAsync<JournalTaskSummary>(Path);
        Assert.Equal("Ada L.", results!.Single(t => t.JournalTaskId == id).CreatedByName);

        var detail = await client.GetFromJsonAsync<ExistingJournalTask>($"{Path}/{id}");
        Assert.Equal("Ada L.", detail!.CreatedByName);
    }

    [Fact]
    public async Task Author_DisplayName_IsUnknownUser_When_The_User_Is_Unresolvable()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewTask(title: "Orphan"));

        var results = await client.GetPagedItemsAsync<JournalTaskSummary>(Path);
        Assert.Equal("Unknown user", results!.Single(t => t.JournalTaskId == id).CreatedByName);
    }

    // A tasks reader (no users.read) whose author has no profile name gets "Unknown user", NEVER the
    // author's email (#315 data-minimisation fix, #316 §9).
    [Fact]
    public async Task Author_NoProfileName_And_NoUsersRead_ReturnsUnknownUser_NotEmail()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var userName = await factory.SeedActorUserAsync();
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewTask(title: "Mine"));

        var results = await client.GetPagedItemsAsync<JournalTaskSummary>(Path);
        var name = results!.Single(t => t.JournalTaskId == id).CreatedByName;
        Assert.Equal("Unknown user", name);
        Assert.NotEqual(userName, name);
    }

    [Fact]
    public async Task List_LimitAboveMaximum_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?limit=100000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_NegativeOffset_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?offset=-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // ── Derived status from timestamps (issue #311 review) ────────────────────

    [Fact]
    public async Task Status_IsDerivedFromTimestamps()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewTask());
        var backlog = (await client.GetFromJsonAsync<ExistingJournalTask>($"{Path}/{id}"))!;
        Assert.Equal(JournalTaskStatus.Backlog, backlog.Status);
        Assert.Null(backlog.StartedAt);
        Assert.Null(backlog.CompletedAt);
        Assert.Null(backlog.Archived);

        var doing = await SetStatusAsync(client, id, JournalTaskStatus.Doing);
        Assert.Equal(JournalTaskStatus.Doing, doing.Status);
        Assert.NotNull(doing.StartedAt);
        Assert.Null(doing.CompletedAt);

        var done = await SetStatusAsync(client, id, JournalTaskStatus.Done);
        Assert.Equal(JournalTaskStatus.Done, done.Status);
        Assert.NotNull(done.CompletedAt);

        var archived = await SetStatusAsync(client, id, JournalTaskStatus.Archived);
        Assert.Equal(JournalTaskStatus.Archived, archived.Status);
        Assert.NotNull(archived.Archived);

        // Back to Backlog clears every derived timestamp.
        var reset = await SetStatusAsync(client, id, JournalTaskStatus.Backlog);
        Assert.Equal(JournalTaskStatus.Backlog, reset.Status);
        Assert.Null(reset.StartedAt);
        Assert.Null(reset.CompletedAt);
        Assert.Null(reset.Archived);
    }

    [Fact]
    public async Task Put_FieldOnly_PreservesStatusStartedAtAndPosition()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        // Two tasks moved to Doing → b ends up in Doing at Position 1.
        var a = await CreateAsync(client, NewTask(title: "A"));
        var b = await CreateAsync(client, NewTask(title: "B"));
        await SetStatusAsync(client, a, JournalTaskStatus.Doing);
        var doing = await SetStatusAsync(client, b, JournalTaskStatus.Doing);
        Assert.Equal(JournalTaskStatus.Doing, doing.Status);
        Assert.Equal(1, doing.Position);
        var startedAt = doing.StartedAt;
        Assert.NotNull(startedAt);

        // A pure field edit (no Status, no Position) must not disturb the board state.
        var put = await client.PutAsJsonAsync($"{Path}/{b}", new UpdateJournalTask { Title = "B (edited)" });
        put.EnsureSuccessStatusCode();
        var updated = (await put.Content.ReadFromJsonAsync<ExistingJournalTask>())!;

        Assert.Equal("B (edited)", updated.Title);
        Assert.Equal(JournalTaskStatus.Doing, updated.Status);
        Assert.Equal(startedAt, updated.StartedAt);
        Assert.Equal(1, updated.Position);
        Assert.Null(updated.CompletedAt);
    }

    // ── PUT link diffing (criterion #1) ───────────────────────────────────────

    [Fact]
    public async Task Put_DiffsLinkSets_AddsRemovesTagsAndAttachments()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();

        var tagA = await SeedJournalTaskTagAsync(factory, "A");
        var tagB = await SeedJournalTaskTagAsync(factory, "B");
        var tagC = await SeedJournalTaskTagAsync(factory, "C");
        var file1 = await SeedFileAsync(factory, "one.pdf", "application/pdf");
        var file2 = await SeedFileAsync(factory, "two.pdf", "application/pdf");

        var id = await CreateAsync(client, NewTask(tagIds: [tagA, tagB], attachmentFileIds: [file1]));

        var put = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateJournalTask
        {
            Title = "Revised",
            TagIds = [tagB, tagC],           // removes A, keeps B, adds C
            AttachmentFileIds = [file2],     // removes file1, adds file2
        });
        put.EnsureSuccessStatusCode();

        var task = (await client.GetFromJsonAsync<ExistingJournalTask>($"{Path}/{id}"))!;
        Assert.Equal(new[] { tagB, tagC }.OrderBy(x => x), task.TagIds.OrderBy(x => x));
        Assert.Equal(new[] { file2 }, task.Attachments.Select(a => a.FileId));
    }

    [Fact]
    public async Task UpdatePosition_OnDoneTask_IsNoOp()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewTask());
        await SetStatusAsync(client, id, JournalTaskStatus.Done);

        // Position is meaningless for Done; the endpoint accepts the call and returns the task unchanged.
        var result = await SetPositionAsync(client, id, 7);

        Assert.Equal(JournalTaskStatus.Done, result.Status);
    }

    [Fact]
    public async Task ChangingStatus_ClosesGap_InSourceColumn()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var t0 = await CreateAsync(client, NewTask(title: "T0")); // Backlog pos 0
        var t1 = await CreateAsync(client, NewTask(title: "T1")); // Backlog pos 1
        var t2 = await CreateAsync(client, NewTask(title: "T2")); // Backlog pos 2

        // Move the middle task out; the remaining Backlog positions must stay contiguous (0,1).
        await SetStatusAsync(client, t1, JournalTaskStatus.Done);

        var backlog = await PositionsByIdAsync(client);
        Assert.Equal(0, backlog[t0]);
        Assert.Equal(1, backlog[t2]);
    }

    // ── List sort / pagination (criterion #7) ─────────────────────────────────

    [Fact]
    public async Task List_UnknownSortKey_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?sortBy=NotAKey");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_SortByTitle_OrdersAscending()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await CreateAsync(client, NewTask(title: "Banana"));
        await CreateAsync(client, NewTask(title: "Apple"));
        await CreateAsync(client, NewTask(title: "Cherry"));

        var page = (await client.GetFromJsonAsync<PagedResult<JournalTaskSummary>>($"{Path}?sortBy=Title&sortDir=Asc"))!;

        Assert.Equal(["Apple", "Banana", "Cherry"], page.Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task List_WithNonZeroOffset_ReturnsRequestedWindow()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        // All land in Backlog with positions 0,1,2 in creation order; default sort is Position ascending.
        await CreateAsync(client, NewTask(title: "First"));
        await CreateAsync(client, NewTask(title: "Second"));
        await CreateAsync(client, NewTask(title: "Third"));

        var page = (await client.GetFromJsonAsync<PagedResult<JournalTaskSummary>>($"{Path}?offset=1&limit=1"))!;

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(1, page.Offset);
        Assert.Single(page.Items);
        Assert.Equal("Second", page.Items[0].Title);
    }

    private static NewJournalTask NewTask(
        string title = "Do the thing",
        string? content = null,
        JournalTaskStatus status = JournalTaskStatus.Backlog,
        Guid[]? tagIds = null,
        Guid[]? attachmentFileIds = null) => new()
    {
        Title = title,
        Content = content,
        Status = status,
        TagIds = tagIds ?? [],
        AttachmentFileIds = attachmentFileIds ?? [],
    };

    private static UpdateJournalTask UpdateTask(string title) => new()
    {
        Title = title,
    };

    private static async Task<Guid> CreateAsync(HttpClient client, NewJournalTask request)
    {
        var post = await client.PostAsJsonAsync(Path, request);
        post.EnsureSuccessStatusCode();
        var created = await post.Content.ReadFromJsonAsync<ExistingJournalTask>();
        return created!.JournalTaskId;
    }

    // Status and position now move through the unified PUT; these fetch the current task and re-PUT it
    // with the one field changed (preserving the other fields + link sets the PUT would otherwise replace).
    private static async Task<ExistingJournalTask> SetStatusAsync(HttpClient client, Guid id, JournalTaskStatus status)
    {
        var response = await client.PutAsJsonAsync($"{Path}/{id}", await BuildUpdateAsync(client, id) with { Status = status });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingJournalTask>())!;
    }

    private static async Task<ExistingJournalTask> SetPositionAsync(HttpClient client, Guid id, int targetPosition)
    {
        var response = await client.PutAsJsonAsync($"{Path}/{id}", await BuildUpdateAsync(client, id) with { Position = targetPosition });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingJournalTask>())!;
    }

    private static async Task<UpdateJournalTask> BuildUpdateAsync(HttpClient client, Guid id)
    {
        var current = (await client.GetFromJsonAsync<ExistingJournalTask>($"{Path}/{id}"))!;
        return new UpdateJournalTask
        {
            Title = current.Title,
            Content = current.Content,
            Deadline = current.Deadline,
            TagIds = current.TagIds.ToArray(),
            AttachmentFileIds = current.Attachments.Select(a => a.FileId).ToArray(),
        };
    }

    private static async Task<Dictionary<Guid, int>> PositionsByIdAsync(HttpClient client)
    {
        var list = await client.GetPagedItemsAsync<JournalTaskSummary>($"{Path}?statuses=Backlog");
        return list!.ToDictionary(t => t.JournalTaskId, t => t.Position);
    }

    private static async Task<Guid> SeedJournalTaskTagAsync(
        WebApplicationFactory<Program> factory, string name = "Tag", bool archived = false)
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
