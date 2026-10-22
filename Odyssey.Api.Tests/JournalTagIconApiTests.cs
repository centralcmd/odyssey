using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #345 over real HTTP: the icon on the journal, task and photo tag write and read paths, and the
/// display icon and tag order on every journal entry, task and photo response shape.
/// </summary>
public class JournalTagIconApiTests
{
    private const string ActorUserId = "tag-icon-actor";

    public enum Kind
    {
        Journal,
        Task,
        Photo,
    }

    public static TheoryData<Kind> Kinds => new() { Kind.Journal, Kind.Task, Kind.Photo };

    // ── Tag endpoints ──────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Post_with_a_catalogue_icon_returns_and_reads_it_back(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(TagAdmin(kind), ActorUserId);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(TagPath(kind), new { name = "Holidays", icon = "beach_access" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("beach_access", await IconOf(response));
        using var read = JsonDocument.Parse(await client.GetStringAsync(response.Headers.Location!));
        Assert.Equal("beach_access", read.RootElement.GetProperty("icon").GetString());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Post_without_an_icon_stores_null(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(TagAdmin(kind), ActorUserId);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(TagPath(kind), new { name = "Holidays" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(await IconOf(response));
        Assert.Null(await StoredIconAsync(factory, kind));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Put_changes_the_icon_and_an_omitted_icon_resets_it(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(TagAdmin(kind), ActorUserId);
        var id = await SeedTagAsync(factory, kind, "Holidays", "beach_access");
        using var client = factory.CreateClient();

        var changed = await client.PutAsJsonAsync($"{TagPath(kind)}/{id}", new { name = "Holidays", archived = false, icon = "flight" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("flight", await IconOf(changed));

        var reset = await client.PutAsJsonAsync($"{TagPath(kind)}/{id}", new { name = "Holidays", archived = false });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Null(await IconOf(reset));
        Assert.Null(await StoredIconAsync(factory, kind));
    }

    public static TheoryData<Kind, string> InvalidIcons()
    {
        var data = new TheoryData<Kind, string>();
        foreach (var kind in Enum.GetValues<Kind>())
        {
            foreach (var icon in new[] { "local_offer", "", "  ", "Beach_Access", "<svg>", "not_an_icon_zq", new string('q', TagIcons.MaxKeyLength + 1) })
            {
                data.Add(kind, icon);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidIcons))]
    public async Task An_invalid_icon_is_a_400_keyed_on_Icon_that_echoes_nothing_and_persists_nothing(Kind kind, string icon)
    {
        await using var factory = new OdysseyApiFactory(TagAdmin(kind), ActorUserId);
        var id = await SeedTagAsync(factory, kind, "Existing", "home");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TagPath(kind), new { name = "New", icon });
        var put = await client.PutAsJsonAsync($"{TagPath(kind)}/{id}", new { name = "Existing", archived = false, icon });

        foreach (var response in new[] { post, put })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(raw);
            var errors = body.RootElement.GetProperty("errors").GetProperty("Icon");
            Assert.Equal([TagIcons.InvalidIconMessage], errors.EnumerateArray().Select(e => e.GetString()));
            if (!string.IsNullOrWhiteSpace(icon))
            {
                Assert.DoesNotContain(icon, raw, StringComparison.Ordinal);
                Assert.DoesNotContain(JsonEncodedText.Encode(icon).ToString(), raw, StringComparison.Ordinal);
            }
        }

        Assert.Equal("home", await StoredIconAsync(factory, kind));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_stored_unknown_key_reads_as_null_and_never_500s(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(TagAdmin(kind), ActorUserId);
        var id = await SeedTagAsync(factory, kind, "Legacy", "<script>retired</script>");
        using var client = factory.CreateClient();

        var single = await client.GetAsync($"{TagPath(kind)}/{id}");
        var list = await client.GetAsync(TagPath(kind));

        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain("retired", await single.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("retired", await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(await IconOf(single));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Put_without_the_update_claim_is_forbidden_whatever_the_body(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(TagAdmin(kind).Where(claim => !claim.EndsWith(".update", StringComparison.Ordinal)).ToArray(), ActorUserId);
        var id = await SeedTagAsync(factory, kind, "Holidays", null);
        using var client = factory.CreateClient();

        var valid = await client.PutAsJsonAsync($"{TagPath(kind)}/{id}", new { name = "Holidays", archived = false, icon = "flight" });
        var invalid = await client.PutAsJsonAsync($"{TagPath(kind)}/{id}", new { name = "Holidays", archived = false, icon = "<svg>" });

        Assert.Equal(HttpStatusCode.Forbidden, valid.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
    }

    // ── Record display icon (AC 7, AC 8, AC 11) ────────────────────────────────

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Every_record_response_carries_the_display_icon_and_name_ordered_tag_ids(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(RecordAdmin(kind), ActorUserId);
        var trip = await SeedTagAsync(factory, kind, "Trip", "beach_access");
        var art = await SeedTagAsync(factory, kind, "art", null);
        var auto = await SeedTagAsync(factory, kind, "Auto", "directions_car");
        var fileId = kind == Kind.Photo ? await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId) : Guid.Empty;
        using var client = factory.CreateClient();

        // POST: Trip and art — art sorts first but has no icon, so Trip's icon wins.
        var post = await client.PostAsJsonAsync(RecordPath(kind), CreateBody(kind, fileId, [trip, art]));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = await RecordOf(post);
        Assert.Equal("beach_access", created.DisplayIcon);
        Assert.Equal([art, trip], created.TagIds);

        // PUT: Auto joins and sorts first, so its icon wins.
        var put = await client.PutAsJsonAsync($"{RecordPath(kind)}/{created.Id}", UpdateBody(kind, [trip, auto, art]));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await RecordOf(put);
        Assert.Equal("directions_car", updated.DisplayIcon);
        Assert.Equal([art, auto, trip], updated.TagIds);

        // Archiving a tag already on the record leaves it taking part.
        await ArchiveTagAsync(factory, kind, auto);
        var detail = await RecordOf(await client.GetAsync($"{RecordPath(kind)}/{created.Id}"));
        Assert.Equal("directions_car", detail.DisplayIcon);
        Assert.Equal([art, auto, trip], detail.TagIds);

        var listed = await ListAsync(client, kind);
        Assert.Equal("directions_car", listed.DisplayIcon);
        Assert.Equal([art, auto, trip], listed.TagIds);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task The_record_read_claim_alone_yields_the_display_icon(Kind kind)
    {
        await using var factory = new OdysseyApiFactory([ReadClaim(kind)], ActorUserId);
        var trip = await SeedTagAsync(factory, kind, "Trip", "beach_access");
        await SeedRecordAsync(factory, kind, trip);
        using var client = factory.CreateClient();

        Assert.Equal("beach_access", (await ListAsync(client, kind)).DisplayIcon);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task An_untagged_record_has_the_default_display_icon_on_the_wire(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(RecordAdmin(kind), ActorUserId);
        var fileId = kind == Kind.Photo ? await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId) : Guid.Empty;
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(RecordPath(kind), CreateBody(kind, fileId, []));

        Assert.Equal(TagIcons.Default, (await RecordOf(post)).DisplayIcon);
        Assert.Equal(TagIcons.Default, (await ListAsync(client, kind)).DisplayIcon);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Renaming_a_tag_changes_which_icon_a_record_shows(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(RecordAdmin(kind), ActorUserId);
        var bills = await SeedTagAsync(factory, kind, "Bills", "receipt_long");
        var food = await SeedTagAsync(factory, kind, "Food", "restaurant");
        var fileId = kind == Kind.Photo ? await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId) : Guid.Empty;
        using var client = factory.CreateClient();
        var created = await RecordOf(await client.PostAsJsonAsync(RecordPath(kind), CreateBody(kind, fileId, [bills, food])));
        Assert.Equal("receipt_long", created.DisplayIcon);

        await RenameTagAsync(factory, kind, food, "Apples");

        var detail = await RecordOf(await client.GetAsync($"{RecordPath(kind)}/{created.Id}"));
        Assert.Equal("restaurant", detail.DisplayIcon);
        Assert.Equal([food, bills], detail.TagIds);
    }

    [Fact]
    public async Task A_photo_whose_keywords_create_a_tag_resolves_it_in_the_same_response()
    {
        await using var factory = new OdysseyApiFactory(
            [.. RecordAdmin(Kind.Photo), PermissionClaims.PhotoTagsCreate], ActorUserId,
            configureServices: services =>
            {
                services.RemoveAll<IPhotoMetadataExtractor>();
                services.AddSingleton<IPhotoMetadataExtractor>(new StubPhotoMetadataExtractor(new PhotoMetadata { Keywords = ["Zebra"] }));
            });
        var trip = await SeedTagAsync(factory, Kind.Photo, "Trip", "beach_access");
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var created = await RecordOf(await client.PostAsJsonAsync(RecordPath(Kind.Photo), CreateBody(Kind.Photo, fileId, [trip])));

        Assert.Equal(2, created.TagIds.Count);
        Assert.Equal(trip, created.TagIds[0]);
        Assert.Equal("beach_access", created.DisplayIcon);
        Assert.Null(await StoredIconAsync(factory, Kind.Photo, "Zebra"));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed record Record(Guid Id, List<Guid> TagIds, string DisplayIcon);

    private static string TagPath(Kind kind) => kind switch
    {
        Kind.Journal => "/api/journal-tags",
        Kind.Task => "/api/task-tags",
        _ => "/api/photo-tags",
    };

    private static string RecordPath(Kind kind) => kind switch
    {
        Kind.Journal => "/api/journal-entries",
        Kind.Task => "/api/tasks",
        _ => "/api/photos",
    };

    private static string ReadClaim(Kind kind) => kind switch
    {
        Kind.Journal => PermissionClaims.JournalRead,
        Kind.Task => PermissionClaims.TasksRead,
        _ => PermissionClaims.PhotosRead,
    };

    private static string[] TagAdmin(Kind kind) => kind switch
    {
        Kind.Journal => [PermissionClaims.JournalTagsCreate, PermissionClaims.JournalTagsRead, PermissionClaims.JournalTagsUpdate],
        Kind.Task => [PermissionClaims.TaskTagsCreate, PermissionClaims.TaskTagsRead, PermissionClaims.TaskTagsUpdate],
        _ => [PermissionClaims.PhotoTagsCreate, PermissionClaims.PhotoTagsRead, PermissionClaims.PhotoTagsUpdate],
    };

    private static string[] RecordAdmin(Kind kind) => kind switch
    {
        Kind.Journal => [PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate],
        Kind.Task => [PermissionClaims.TasksRead, PermissionClaims.TasksCreate, PermissionClaims.TasksUpdate],
        _ => [PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate, PermissionClaims.PhotosUpdate, PermissionClaims.FilesRead],
    };

    private static object CreateBody(Kind kind, Guid fileId, Guid[] tagIds) => kind switch
    {
        Kind.Journal => new { title = "A day", content = "Dear journal.", entryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), tagIds },
        Kind.Task => new { title = "A task", tagIds },
        _ => new { fileId, tagIds },
    };

    private static object UpdateBody(Kind kind, Guid[] tagIds) => kind switch
    {
        Kind.Journal => new { title = "A day", content = "Dear journal.", entryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), archived = false, tagIds },
        Kind.Task => new { title = "A task", tagIds },
        _ => new { tagIds },
    };

    private static async Task<string?> IconOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("icon").GetString();
    }

    private static async Task<Record> RecordOf(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Parse(body.RootElement);
    }

    private static Record Parse(JsonElement element)
    {
        var id = element.EnumerateObject()
            .First(property => property.Name is "journalEntryId" or "journalTaskId" or "photoId")
            .Value.GetGuid();
        return new Record(
            id,
            element.GetProperty("tagIds").EnumerateArray().Select(tag => tag.GetGuid()).ToList(),
            element.GetProperty("displayIcon").GetString()!);
    }

    private static async Task<Record> ListAsync(HttpClient client, Kind kind)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync(RecordPath(kind)));
        return Parse(Assert.Single(body.RootElement.GetProperty("items").EnumerateArray().ToList()));
    }

    private static async Task<Guid> SeedTagAsync(
        WebApplicationFactory<Program> factory, Kind kind, string name, string? icon)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        switch (kind)
        {
            case Kind.Journal:
                context.JournalTags.Add(new JournalTag { JournalTagId = id, Name = name, Icon = icon });
                break;
            case Kind.Task:
                context.JournalTaskTags.Add(new JournalTaskTag { JournalTaskTagId = id, Name = name, Icon = icon });
                break;
            default:
                context.PhotoTags.Add(new PhotoTag { PhotoTagId = id, Name = name, Icon = icon });
                break;
        }

        await context.SaveChangesAsync();
        return id;
    }

    private static async Task SeedRecordAsync(WebApplicationFactory<Program> factory, Kind kind, Guid tagId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = DateTime.UtcNow;
        switch (kind)
        {
            case Kind.Journal:
                var entry = new JournalEntry { ExternalUid = Guid.NewGuid().ToString(), Title = "A day", Content = "Dear journal.", EntryDate = now, CreatedAt = now, UpdatedAt = now };
                entry.EntryTags.Add(new JournalEntryTag { JournalTagId = tagId });
                context.JournalEntries.Add(entry);
                break;
            case Kind.Task:
                var task = new JournalTask { ExternalUid = Guid.NewGuid().ToString(), Title = "A task", CreatedAt = now, UpdatedAt = now };
                task.ItemTags.Add(new JournalTaskTagLink { JournalTaskTagId = tagId });
                context.JournalTasks.Add(task);
                break;
            default:
                var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
                var photo = new Photo { FileId = fileId, CreatedAt = now, UpdatedAt = now };
                photo.Tags.Add(new PhotoTagLink { PhotoTagId = tagId });
                context.Photos.Add(photo);
                break;
        }

        await context.SaveChangesAsync();
    }

    private static async Task ArchiveTagAsync(WebApplicationFactory<Program> factory, Kind kind, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = DateTime.UtcNow;
        switch (kind)
        {
            case Kind.Journal:
                (await context.JournalTags.SingleAsync(tag => tag.JournalTagId == id)).Archived = now;
                break;
            case Kind.Task:
                (await context.JournalTaskTags.SingleAsync(tag => tag.JournalTaskTagId == id)).Archived = now;
                break;
            default:
                (await context.PhotoTags.SingleAsync(tag => tag.PhotoTagId == id)).Archived = now;
                break;
        }

        await context.SaveChangesAsync();
    }

    private static async Task RenameTagAsync(WebApplicationFactory<Program> factory, Kind kind, Guid id, string name)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        switch (kind)
        {
            case Kind.Journal:
                (await context.JournalTags.SingleAsync(tag => tag.JournalTagId == id)).Name = name;
                break;
            case Kind.Task:
                (await context.JournalTaskTags.SingleAsync(tag => tag.JournalTaskTagId == id)).Name = name;
                break;
            default:
                (await context.PhotoTags.SingleAsync(tag => tag.PhotoTagId == id)).Name = name;
                break;
        }

        await context.SaveChangesAsync();
    }

    private static async Task<string?> StoredIconAsync(WebApplicationFactory<Program> factory, Kind kind, string? name = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return kind switch
        {
            Kind.Journal => (await context.JournalTags.AsNoTracking().SingleAsync(tag => name == null || tag.Name == name)).Icon,
            Kind.Task => (await context.JournalTaskTags.AsNoTracking().SingleAsync(tag => name == null || tag.Name == name)).Icon,
            _ => (await context.PhotoTags.AsNoTracking().SingleAsync(tag => name == null || tag.Name == name)).Icon,
        };
    }
}
