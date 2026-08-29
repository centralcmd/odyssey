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

public class JournalTagsApiTests
{
    private const string ActorUserId = "journal-tags-actor-id";
    private const string Path = "/api/journal-tags";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.JournalTagsRead, PermissionClaims.JournalTagsCreate,
        PermissionClaims.JournalTagsUpdate, PermissionClaims.JournalTagsDelete,
    ];

    // Also linking a tag onto an entry (for the in-use delete test) needs the journal write claims.
    private static readonly string[] ReadWriteWithEntries =
    [
        PermissionClaims.JournalTagsRead, PermissionClaims.JournalTagsCreate,
        PermissionClaims.JournalTagsUpdate, PermissionClaims.JournalTagsDelete,
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate,
    ];

    // ── Authorization (criterion #8) ──────────────────────────────────────────

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

    // ── CRUD + duplicate + in-use (criterion #3) ──────────────────────────────

    [Fact]
    public async Task Create_Then_List_ReturnsTag()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "Travel", "Trips and holidays");
        Assert.Equal("Travel", created.Name);

        var tags = await client.GetPagedItemsAsync<ExistingJournalTag>(Path);
        Assert.Contains(tags, t => t.JournalTagId == created.JournalTagId && t.Name == "Travel");
    }

    [Fact]
    public async Task Create_DuplicateName_CaseInsensitive_ReturnsConflict()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await CreateAsync(client, "Travel");

        var dup = await client.PostAsJsonAsync(Path, new NewJournalTag { Name = "TRAVEL" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Rename_And_Archive_ViaPut()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "Wrok");

        var put = await client.PutAsJsonAsync($"{Path}/{created.JournalTagId}",
            new UpdateJournalTag { Name = "Work", Description = "Job-related", Archived = false });
        put.EnsureSuccessStatusCode();
        var renamed = (await put.Content.ReadFromJsonAsync<ExistingJournalTag>())!;
        Assert.Equal("Work", renamed.Name);
        Assert.Null(renamed.Archived);

        var archive = await client.PutAsJsonAsync($"{Path}/{created.JournalTagId}",
            new UpdateJournalTag { Name = "Work", Archived = true });
        archive.EnsureSuccessStatusCode();
        var archived = (await archive.Content.ReadFromJsonAsync<ExistingJournalTag>())!;
        Assert.NotNull(archived.Archived);

        // Archived tags are excluded by the Active filter and visible under the Archived filter.
        var active = await client.GetPagedItemsAsync<ExistingJournalTag>($"{Path}?status=Active");
        Assert.DoesNotContain(active, t => t.JournalTagId == created.JournalTagId);

        var onlyArchived = await client.GetPagedItemsAsync<ExistingJournalTag>($"{Path}?status=Archived");
        Assert.Contains(onlyArchived, t => t.JournalTagId == created.JournalTagId);
    }

    [Fact]
    public async Task Delete_UnusedTag_ReturnsNoContent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "Ephemeral");

        var delete = await client.DeleteAsync($"{Path}/{created.JournalTagId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Delete_TagInUse_ReturnsConflict_NotServerError()
    {
        await using var factory = new ApiFactory(ReadWriteWithEntries);
        using var client = factory.CreateClient();

        var tag = await CreateAsync(client, "Linked");

        // Link the tag onto a journal entry so the delete pre-check trips.
        var entry = await client.PostAsJsonAsync("/api/journal-entries", new NewJournalEntry
        {
            Title = "Tagged entry",
            Content = "Body",
            EntryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            TagIds = [tag.JournalTagId],
        });
        entry.EnsureSuccessStatusCode();

        var delete = await client.DeleteAsync($"{Path}/{tag.JournalTagId}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task Rename_ToExistingName_ReturnsConflict()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await CreateAsync(client, "Travel");
        var work = await CreateAsync(client, "Work");

        var put = await client.PutAsJsonAsync($"{Path}/{work.JournalTagId}",
            new UpdateJournalTag { Name = "travel" });

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
    }

    private static async Task<ExistingJournalTag> CreateAsync(HttpClient client, string name, string? description = null)
    {
        var post = await client.PostAsJsonAsync(Path, new NewJournalTag { Name = name, Description = description });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingJournalTag>())!;
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
