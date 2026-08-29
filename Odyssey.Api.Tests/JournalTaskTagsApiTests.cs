using System.Net;
using System.Net.Http.Json;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

public class JournalTaskTagsApiTests
{
    private const string ActorUserId = "task-tags-actor-id";
    private const string Path = "/api/task-tags";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.TaskTagsRead, PermissionClaims.TaskTagsCreate,
        PermissionClaims.TaskTagsUpdate, PermissionClaims.TaskTagsDelete,
    ];

    private static readonly string[] ReadWriteWithTasks =
    [
        PermissionClaims.TaskTagsRead, PermissionClaims.TaskTagsCreate,
        PermissionClaims.TaskTagsUpdate, PermissionClaims.TaskTagsDelete,
        PermissionClaims.TasksRead, PermissionClaims.TasksCreate,
    ];

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutTagClaims_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_Then_List_ReturnsTag()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "Urgent");

        var tags = await client.GetPagedItemsAsync<ExistingJournalTaskTag>(Path);
        Assert.Contains(tags, t => t.JournalTaskTagId == created.JournalTaskTagId && t.Name == "Urgent");
    }

    [Fact]
    public async Task Create_DuplicateName_CaseInsensitive_ReturnsConflict()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await CreateAsync(client, "Urgent");

        var dup = await client.PostAsJsonAsync(Path, new NewJournalTaskTag { Name = "urgent" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Delete_UnusedTag_ReturnsNoContent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "Ephemeral");

        var delete = await client.DeleteAsync($"{Path}/{created.JournalTaskTagId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Delete_TagInUse_ReturnsConflict_NotServerError()
    {
        await using var factory = new ApiFactory(ReadWriteWithTasks);
        using var client = factory.CreateClient();

        var tag = await CreateAsync(client, "Linked");

        var task = await client.PostAsJsonAsync("/api/tasks", new NewJournalTask
        {
            Title = "Tagged task",
            TagIds = [tag.JournalTaskTagId],
        });
        task.EnsureSuccessStatusCode();

        var delete = await client.DeleteAsync($"{Path}/{tag.JournalTaskTagId}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task Rename_And_Archive_ViaPut()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "Wrok");

        var put = await client.PutAsJsonAsync($"{Path}/{created.JournalTaskTagId}",
            new UpdateJournalTaskTag { Name = "Work", Description = "Job-related", Archived = false });
        put.EnsureSuccessStatusCode();
        var renamed = (await put.Content.ReadFromJsonAsync<ExistingJournalTaskTag>())!;
        Assert.Equal("Work", renamed.Name);
        Assert.Null(renamed.Archived);

        var archive = await client.PutAsJsonAsync($"{Path}/{created.JournalTaskTagId}",
            new UpdateJournalTaskTag { Name = "Work", Archived = true });
        archive.EnsureSuccessStatusCode();
        Assert.NotNull((await archive.Content.ReadFromJsonAsync<ExistingJournalTaskTag>())!.Archived);

        var active = await client.GetPagedItemsAsync<ExistingJournalTaskTag>($"{Path}?status=Active");
        Assert.DoesNotContain(active, t => t.JournalTaskTagId == created.JournalTaskTagId);

        var onlyArchived = await client.GetPagedItemsAsync<ExistingJournalTaskTag>($"{Path}?status=Archived");
        Assert.Contains(onlyArchived, t => t.JournalTaskTagId == created.JournalTaskTagId);
    }

    [Fact]
    public async Task Rename_ToExistingName_ReturnsConflict()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await CreateAsync(client, "Urgent");
        var other = await CreateAsync(client, "Later");

        var put = await client.PutAsJsonAsync($"{Path}/{other.JournalTaskTagId}",
            new UpdateJournalTaskTag { Name = "URGENT" });

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
    }

    private static async Task<ExistingJournalTaskTag> CreateAsync(HttpClient client, string name, string? description = null)
    {
        var post = await client.PostAsJsonAsync(Path, new NewJournalTaskTag { Name = name, Description = description });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingJournalTaskTag>())!;
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
