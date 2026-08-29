using Odyssey.Context;
using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Odyssey.Api.Tests;

public class PhotosApiTests
{
    private const string ActorUserId = "photos-actor-id";
    private const string Path = "/api/photos";

    private static readonly string[] ReadOnly = [PermissionClaims.PhotosRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate,
        PermissionClaims.PhotosUpdate, PermissionClaims.PhotosDelete, PermissionClaims.FilesRead,
    ];

    private static readonly string[] ReadWriteWithTags =
    [
        PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate, PermissionClaims.PhotosUpdate,
        PermissionClaims.PhotosDelete, PermissionClaims.FilesRead, PermissionClaims.PhotoTagsCreate,
        PermissionClaims.PhotoTagsRead,
    ];

    // ── Authorization matrix (criterion #10) ───────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task List_Guest_WithoutPhotoClaims_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task Mutations_WithReadOnly_ReturnForbidden()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewPhoto { FileId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{Path}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Post_WithoutFilesRead_ReturnsForbidden()
    {
        // photos.create alone cannot link a file (confused-deputy guard, §7).
        await using var factory = new ApiFactory([PermissionClaims.PhotosCreate, PermissionClaims.PhotosRead]);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewPhoto { FileId = fileId });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    // ── Add / validation ───────────────────────────────────────────────────────

    [Fact]
    public async Task Post_NonImageFile_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var pdfId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId, "doc.pdf", "application/pdf");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewPhoto { FileId = pdfId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task Post_UnknownFile_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var post = await client.PostAsJsonAsync(Path, new NewPhoto { FileId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task Post_CoordinateOutOfRange_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewPhoto { FileId = fileId, CapturedLatitude = 200 });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_LongitudeOutOfRange_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewPhoto { FileId = fileId, CapturedLongitude = 200 });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Theory]
    [InlineData(1899)]
    [InlineData(2101)]
    public async Task Post_TakenAtOutsideYearWindow_ReturnsBadRequest(int year)
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path,
            new NewPhoto { FileId = fileId, TakenAt = new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Unspecified) });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_OverTagLinkCap_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var tooMany = new Guid[PhotoLimits.MaxLinksPerKind + 1];
        var post = await client.PostAsJsonAsync(Path, new NewPhoto { FileId = fileId, TagIds = tooMany });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    /// <summary>
    /// The point of migrating the cap (issue #421 Wave 3): a <em>lowered</em> setting binds at the
    /// service, with no restart. Two tag links are well under the shipped 50, so this can only fail on
    /// the seeded row — which is what makes it a real enforcement test rather than a round-trip one.
    /// </summary>
    [Fact]
    public async Task Post_OverALoweredTagLinkCap_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.PhotoMaxLinksPerKind, "1");
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path,
            new NewPhoto { FileId = fileId, TagIds = [Guid.NewGuid(), Guid.NewGuid()] });

        // 422, not the sibling test's 400: two ids satisfy `[MaxLength(50)]`, so this is the *service*
        // cap rejecting it. The differing status is itself the evidence that the setting was consulted.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task List_OverCapTagFilter_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var tagIds = string.Join("&", Enumerable.Range(0, 51).Select(_ => $"tagIds={Guid.NewGuid()}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Path}?{tagIds}")).StatusCode);
    }

    // ── CRUD roundtrip + archival (criteria #8) ────────────────────────────────

    [Fact]
    public async Task Post_Get_Update_Archive_Delete_RoundTrip()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId, Title = "Sunset", Caption = "Golden hour" });
        Assert.Equal(fileId, created.FileId);
        Assert.Equal("Sunset", created.Title);
        Assert.Null(created.Archived);

        var fetched = await client.GetFromJsonAsync<ExistingPhoto>($"{Path}/{created.PhotoId}");
        Assert.Equal("Golden hour", fetched!.Caption);

        // Archive via PUT.
        var put = await client.PutAsJsonAsync($"{Path}/{created.PhotoId}", new UpdatePhoto { Title = "Sunset", Archived = true });
        put.EnsureSuccessStatusCode();

        var activeList = await client.GetPagedItemsAsync<PhotoSummary>(Path);
        Assert.DoesNotContain(activeList!, p => p.PhotoId == created.PhotoId);
        var archivedList = await client.GetPagedItemsAsync<PhotoSummary>($"{Path}?status=Archived");
        Assert.Contains(archivedList!, p => p.PhotoId == created.PhotoId);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{created.PhotoId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path}/{created.PhotoId}")).StatusCode);
    }

    [Fact]
    public async Task Stats_CountsActivePhotosFavouritesAndTags_ExcludingArchived()
    {
        await using var factory = new ApiFactory(ReadWriteWithTags);
        var fileA = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        var fileB = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var tagPost = await client.PostAsJsonAsync("/api/photo-tags", new NewPhotoTag { Name = "StatsTag" });
        tagPost.EnsureSuccessStatusCode();
        var tagId = (await tagPost.Content.ReadFromJsonAsync<ExistingPhotoTag>())!.PhotoTagId;

        // One favourited active photo + one archived photo, both carrying the tag.
        await CreateAsync(client, new NewPhoto { FileId = fileA, Favourite = true, TagIds = [tagId] });
        var archived = await CreateAsync(client, new NewPhoto { FileId = fileB, TagIds = [tagId] });
        (await client.PutAsJsonAsync($"{Path}/{archived.PhotoId}",
            new UpdatePhoto { Title = "Archived", Archived = true, TagIds = [tagId] })).EnsureSuccessStatusCode();

        var stats = await client.GetFromJsonAsync<PhotoLibraryStats>($"{Path}/stats");

        Assert.Equal(1, stats!.TotalCount);      // the archived photo is excluded
        Assert.Equal(1, stats.FavouriteCount);
        Assert.Equal(1, stats.TagCounts.Single(c => c.Key == tagId).Count); // only the active photo's link counts
    }

    // ── File name: read-back + rename the backing Files-store record ───────────

    [Fact]
    public async Task Get_IncludesBackingFileName()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId, "original.jpg");
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId });
        Assert.Equal("original.jpg", created.FileName);

        var fetched = await client.GetFromJsonAsync<ExistingPhoto>($"{Path}/{created.PhotoId}");
        Assert.Equal("original.jpg", fetched!.FileName);
    }

    [Fact]
    public async Task Put_WithFilesUpdate_RenamesBackingFile()
    {
        string[] permissions = [.. ReadWrite, PermissionClaims.FilesUpdate];
        await using var factory = new ApiFactory(permissions);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId, "original.jpg");
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId });

        var put = await client.PutAsJsonAsync($"{Path}/{created.PhotoId}", new UpdatePhoto { FileName = "renamed.png" });
        put.EnsureSuccessStatusCode();
        var updated = (await put.Content.ReadFromJsonAsync<ExistingPhoto>())!;
        Assert.Equal("renamed.png", updated.FileName);

        // The rename hit the Files store, so a fresh read reflects it too.
        var fetched = await client.GetFromJsonAsync<ExistingPhoto>($"{Path}/{created.PhotoId}");
        Assert.Equal("renamed.png", fetched!.FileName);
    }

    [Fact]
    public async Task Put_RenameWithoutFilesUpdate_ReturnsForbidden_AndLeavesFileNameUnchanged()
    {
        // ReadWrite carries files.read but NOT files.update, so it cannot rename the backing file.
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId, "keep.jpg");
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId });

        var put = await client.PutAsJsonAsync($"{Path}/{created.PhotoId}", new UpdatePhoto { FileName = "hijacked.jpg" });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        // The guard runs before the update, so nothing was renamed.
        var fetched = await client.GetFromJsonAsync<ExistingPhoto>($"{Path}/{created.PhotoId}");
        Assert.Equal("keep.jpg", fetched!.FileName);
    }

    // ── Extraction: resilience, caller precedence, fill (criteria #1, #2, #3) ──

    [Fact]
    public async Task Post_GarbageImage_Succeeds_WithNoMetadata()
    {
        // Real extractor over non-image bytes: extraction fails internally → null fields, add still succeeds.
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId });
        Assert.Null(created.TakenAt);
        Assert.Null(created.CapturedLatitude);
        Assert.Null(created.PixelWidth);
    }

    [Fact]
    public async Task Post_CallerValuesWin_OverExtraction()
    {
        var extracted = new PhotoMetadata { Title = "EXIF title", TakenAt = new DateTime(2001, 1, 1), Latitude = 10, Longitude = 20, PixelWidth = 4000, PixelHeight = 3000 };
        await using var factory = new ApiFactory(ReadWrite, extracted);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var caller = new NewPhoto { FileId = fileId, Title = "My title", CapturedLatitude = -5, CapturedLongitude = -6 };
        var created = await CreateAsync(client, caller);

        Assert.Equal("My title", created.Title);          // caller wins
        Assert.Equal(-5, created.CapturedLatitude);        // caller wins
        Assert.Equal(new DateTime(2001, 1, 1), created.TakenAt); // extraction fills the null field
        Assert.Equal(4000, created.PixelWidth);            // extraction fills the null field
    }

    [Fact]
    public async Task Post_ExtractedKeyword_AutoCreatesTag_WhenClaimHeld()
    {
        var extracted = new PhotoMetadata { Keywords = ["Beach", "Sunset"] };
        await using var factory = new ApiFactory(ReadWriteWithTags, extracted);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId });
        Assert.Equal(2, created.TagIds.Count);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(2, await context.PhotoTags.CountAsync());
        Assert.True(await context.PhotoTags.AnyAsync(t => t.Name == "Beach"));
    }

    [Fact]
    public async Task Post_ExtractedKeyword_MatchesExistingTag_NoDuplicate()
    {
        var extracted = new PhotoMetadata { Keywords = ["beach"] }; // different case than the seeded tag
        await using var factory = new ApiFactory(ReadWriteWithTags, extracted);
        await PhotoTestSupport.SeedPhotoTagAsync(factory, "beach");
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId });
        Assert.Single(created.TagIds);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(1, await context.PhotoTags.CountAsync());
    }

    [Fact]
    public async Task Post_ExtractedKeyword_NotCreated_WhenClaimMissing()
    {
        var extracted = new PhotoMetadata { Keywords = ["Beach"] };
        await using var factory = new ApiFactory(ReadWrite, extracted); // no photos.tags.create
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId });
        Assert.Empty(created.TagIds); // keyword skipped, add still succeeds

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await context.PhotoTags.CountAsync());
    }

    // ── Tags / people / links ──────────────────────────────────────────────────

    [Fact]
    public async Task Post_UnknownTag_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewPhoto { FileId = fileId, TagIds = [Guid.NewGuid()] });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task Post_NonPersonContact_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        var org = await PhotoTestSupport.SeedPersonAsync(factory, "Acme", Odyssey.Dtos.ContactType.Organization);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new NewPhoto { FileId = fileId, PersonContactIds = [org] });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task Post_PersonContact_LinksAndFilters()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        var person = await PhotoTestSupport.SeedPersonAsync(factory);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId, PersonContactIds = [person] });
        Assert.Contains(person, created.PersonContactIds);

        var filtered = await client.GetPagedItemsAsync<PhotoSummary>($"{Path}?personIds={person}");
        Assert.Contains(filtered!, p => p.PhotoId == created.PhotoId);
    }

    [Fact]
    public async Task Get_AfterLinkedPersonDeleted_OmitsTheDanglingLink()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        var person = await PhotoTestSupport.SeedPersonAsync(factory);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId, PersonContactIds = [person] });

        using (var scope = factory.Services.CreateScope())
        {
            var journal = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            journal.Contacts.Remove(await journal.Contacts.FirstAsync(c => c.ContactId == person));
            await journal.SaveChangesAsync();
        }

        var fetched = await client.GetFromJsonAsync<ExistingPhoto>($"{Path}/{created.PhotoId}");
        Assert.DoesNotContain(person, fetched!.PersonContactIds); // dropped at read (§10.4)
    }

    // ── Favourites ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Favourite_ToggleViaPut_AndFilter()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId, Title = "Fave" });
        Assert.Null(created.Favourited);

        // Not in the favourites-only list yet.
        var before = await client.GetPagedItemsAsync<PhotoSummary>($"{Path}?favouritesOnly=true");
        Assert.DoesNotContain(before!, p => p.PhotoId == created.PhotoId);

        var put = await client.PutAsJsonAsync($"{Path}/{created.PhotoId}", new UpdatePhoto { Title = "Fave", Favourite = true });
        put.EnsureSuccessStatusCode();
        var updated = (await put.Content.ReadFromJsonAsync<ExistingPhoto>())!;
        Assert.NotNull(updated.Favourited);

        var after = await client.GetPagedItemsAsync<PhotoSummary>($"{Path}?favouritesOnly=true");
        Assert.Contains(after!, p => p.PhotoId == created.PhotoId);
    }

    // ── Read minimisation + mass-assignment (criteria #11, #12) ────────────────

    [Fact]
    public async Task Read_DoesNotLeakContactName()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        var person = await PhotoTestSupport.SeedPersonAsync(factory, "Grace Hopper");
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId, PersonContactIds = [person] });
        var json = await client.GetStringAsync($"{Path}/{created.PhotoId}");
        Assert.DoesNotContain("Grace Hopper", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_WithNestedForeignObjects_DoesNotCreateThem()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var body = new
        {
            fileId,
            tagIds = Array.Empty<Guid>(),
            personContactIds = Array.Empty<Guid>(),
            albumIds = Array.Empty<Guid>(),
            tags = new[] { new { photoTagId = Guid.NewGuid(), name = "INJECTED" } },
            people = new[] { new { contactId = Guid.NewGuid() } },
        };
        var post = await client.PostAsJsonAsync(Path, body);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.PhotoTags.AnyAsync());
        Assert.False(await context.PhotoPeople.AnyAsync());
    }

    // ── File is never modified (criterion #3d) ─────────────────────────────────

    /// <summary>
    /// Criterion #3d: the library never rewrites the image it wraps. Extracted metadata lives on the
    /// Photo row, so adding one and editing its title/coordinates must leave the bytes byte-identical.
    /// </summary>
    /// <remarks>
    /// This used to end by deleting the photo and asserting the file survived that too — the original
    /// §7 behaviour, since reversed: a photo and the file it wraps are now deleted together in both
    /// directions. Deleting is therefore its own test below rather than a tail assertion here, because
    /// "editing does not touch the bytes" and "deleting removes them" are two different claims and only
    /// the first is what #3d is about.
    /// </remarks>
    [Fact]
    public async Task Add_And_Edit_LeaveTheFileBytesUnchanged()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var (hashBefore, sizeBefore) = await ReadFileFingerprint(factory, fileId);

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId, Title = "t" });
        await client.PutAsJsonAsync($"{Path}/{created.PhotoId}", new UpdatePhoto { Title = "edited", CapturedLatitude = 12 });

        var (hashAfter, sizeAfter) = await ReadFileFingerprint(factory, fileId);
        Assert.Equal(hashBefore, hashAfter);
        Assert.Equal(sizeBefore, sizeAfter);
    }

    /// <summary>
    /// The other half of the pair, over HTTP: deleting a photo deletes the file it wraps. The database
    /// enforces the reverse direction with a cascading FK, but this direction is application code in
    /// <c>PhotoService</c>, so it is the one that needs a test at the API tier — the InMemory provider
    /// enforces no foreign keys, which means what passes here is genuinely the service's own doing.
    /// </summary>
    [Fact]
    public async Task Delete_AlsoRemovesTheFileThePhotoWrapped()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId, Title = "t" });
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{created.PhotoId}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.FileMetadata.AnyAsync(f => f.Id == fileId));
        Assert.False(await context.Photos.AnyAsync(photo => photo.PhotoId == created.PhotoId));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // ── Author-name attribution (#316): Photos route CreatedByUserId through the shared claim-aware
    //    resolver on both the list and the single-photo read. ──────────────────────────────────────
    [Fact]
    public async Task Author_DisplayName_Resolves_On_List_And_Detail()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await factory.SeedActorUserAsync(displayName: "Ada L.");
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId });

        var detail = await client.GetFromJsonAsync<ExistingPhoto>($"{Path}/{created.PhotoId}");
        Assert.Equal("Ada L.", detail!.CreatedByName);

        var list = await client.GetPagedItemsAsync<PhotoSummary>(Path);
        Assert.Equal("Ada L.", list!.Single(p => p.PhotoId == created.PhotoId).CreatedByName);
    }

    // A photos reader (no users.read) whose author has no profile name gets "Unknown user", NEVER the
    // author's email — the #315 minimisation fix extended to Photos (#316 §5).
    [Fact]
    public async Task Author_NoProfileName_And_NoUsersRead_ReturnsUnknownUser_NotEmail()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var userName = await factory.SeedActorUserAsync();
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhoto { FileId = fileId });

        var detail = await client.GetFromJsonAsync<ExistingPhoto>($"{Path}/{created.PhotoId}");
        Assert.Equal("Unknown user", detail!.CreatedByName);
        Assert.NotEqual(userName, detail.CreatedByName);
    }

    private static async Task<ExistingPhoto> CreateAsync(HttpClient client, NewPhoto request)
    {
        var post = await client.PostAsJsonAsync(Path, request);
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingPhoto>())!;
    }

    private static async Task<(string Hash, long Size)> ReadFileFingerprint(WebApplicationFactory<Program> factory, Guid fileId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var meta = await context.FileMetadata.AsNoTracking().FirstAsync(f => f.Id == fileId);
        var blob = await context.FileBlob.AsNoTracking().FirstAsync(b => b.Id == meta.FileBlobId);
        return (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blob.Content)), meta.SizeBytes);
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(IReadOnlyCollection<string>? permissions, PhotoMetadata? stubMetadata = null)
            : base(permissions, ActorUserId, configureServices: services => Configure(services, stubMetadata))
        {
        }

        private static void Configure(IServiceCollection services, PhotoMetadata? stubMetadata)
        {
            if (stubMetadata is not null)
            {
                services.RemoveAll<IPhotoMetadataExtractor>();
                services.AddSingleton<IPhotoMetadataExtractor>(new StubPhotoMetadataExtractor(stubMetadata));
            }
        }
    }
}
