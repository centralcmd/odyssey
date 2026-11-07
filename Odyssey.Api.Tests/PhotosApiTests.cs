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
        PermissionClaims.PhotosUpdate, PermissionClaims.PhotosDelete,
    ];

    private static readonly string[] ReadWriteWithTags =
    [
        PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate, PermissionClaims.PhotosUpdate,
        PermissionClaims.PhotosDelete, PermissionClaims.PhotoTagsCreate,
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

        var post = await PhotoTestSupport.PostPhotoAsync(client, new NewPhotoUpload());
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{Path}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Post_WithPhotosCreate_AndNoFileClaims_Succeeds()
    {
        // Issue #428 AC 3: a photo is photo data — photos.create alone uploads one; files.* plays no part.
        await using var factory = new ApiFactory([PermissionClaims.PhotosCreate, PermissionClaims.PhotosRead]);
        using var client = factory.CreateClient();

        var post = await PhotoTestSupport.PostPhotoAsync(client);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
    }

    // ── Add / validation ───────────────────────────────────────────────────────

    [Fact]
    public async Task Post_NonImageFile_Returns415_AndWritesNothing()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await PhotoTestSupport.PostPhotoAsync(client, bytes: "%PDF-1.7"u8.ToArray(), fileName: "doc.pdf", contentType: "application/pdf");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, post.StatusCode);
        Assert.Empty(OriginalFiles(factory));
    }

    [Fact]
    public async Task Post_ImageTypeWhoseBytesDisagree_Returns415()
    {
        // Declared PNG, JPEG magic bytes: the magic-byte check refuses it (issue #428 §6).
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await PhotoTestSupport.PostPhotoAsync(client, fileName: "p.png", contentType: "image/png");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, post.StatusCode);
    }

    [Fact]
    public async Task Post_Svg_Returns415()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await PhotoTestSupport.PostPhotoAsync(
            client, bytes: "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), fileName: "x.svg", contentType: "image/svg+xml");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, post.StatusCode);
    }

    [Fact]
    public async Task Post_WithoutAFile_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await client.PostAsync(Path, new MultipartFormDataContent { { new StringContent("t"), "title" } });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    /// <summary>Issue #428 AC 8: the retired JSON shape with a file id makes no photo.</summary>
    [Fact]
    public async Task Post_JsonBodyWithFileId_IsRefused_AndMakesNoPhoto()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new { fileId });

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, post.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<OdysseyContext>().Photos);
    }

    [Fact]
    public async Task Post_CoordinateOutOfRange_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await PhotoTestSupport.PostPhotoAsync(client, new NewPhotoUpload { CapturedLatitude = 200 });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_LongitudeOutOfRange_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await PhotoTestSupport.PostPhotoAsync(client, new NewPhotoUpload { CapturedLongitude = 200 });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Theory]
    [InlineData(1899)]
    [InlineData(2101)]
    public async Task Post_TakenAtOutsideYearWindow_ReturnsBadRequest(int year)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await PhotoTestSupport.PostPhotoAsync(client, new NewPhotoUpload { TakenAt = new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Unspecified) });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_OverTagLinkCap_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var tooMany = new Guid[PhotoLimits.MaxLinksPerKind + 1];
        var post = await PhotoTestSupport.PostPhotoAsync(client, new NewPhotoUpload { TagIds = tooMany });
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
        using var client = factory.CreateClient();

        var post = await PhotoTestSupport.PostPhotoAsync(client, new NewPhotoUpload { TagIds = [Guid.NewGuid(), Guid.NewGuid()] });

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
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhotoUpload { Title = "Sunset", Caption = "Golden hour" });
        Assert.Equal("image/jpeg", created.ContentType);
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
        using var client = factory.CreateClient();

        var tagPost = await client.PostAsJsonAsync("/api/photo-tags", new NewPhotoTag { Name = "StatsTag" });
        tagPost.EnsureSuccessStatusCode();
        var tagId = (await tagPost.Content.ReadFromJsonAsync<ExistingPhotoTag>())!.PhotoTagId;

        // One favourited active photo + one archived photo, both carrying the tag.
        await CreateAsync(client, new NewPhotoUpload { Favourite = true, TagIds = [tagId] });
        var archived = await CreateAsync(client, new NewPhotoUpload { TagIds = [tagId] });
        (await client.PutAsJsonAsync($"{Path}/{archived.PhotoId}",
            new UpdatePhoto { Title = "Archived", Archived = true, TagIds = [tagId] })).EnsureSuccessStatusCode();

        var stats = await client.GetFromJsonAsync<PhotoLibraryStats>($"{Path}/stats");

        Assert.Equal(1, stats!.TotalCount);      // the archived photo is excluded
        Assert.Equal(1, stats.FavouriteCount);
        Assert.Equal(1, stats.TagCounts.Single(c => c.Key == tagId).Count); // only the active photo's link counts
    }

    // ── File name: the photo's own, renamed under photos.update (issue #428 §5.4) ─────────────

    [Fact]
    public async Task Get_IncludesTheUploadedFileName()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhotoUpload(), "original.jpg");
        Assert.Equal("original.jpg", created.FileName);

        var fetched = await client.GetFromJsonAsync<ExistingPhoto>($"{Path}/{created.PhotoId}");
        Assert.Equal("original.jpg", fetched!.FileName);
    }

    [Fact]
    public async Task Put_Rename_WithPhotosUpdate_AndNoFilesUpdate_RenamesThePhoto_NotTheStoredFile()
    {
        // ReadWrite holds no files.* claim at all: the rename is photo data (issue #428 AC 15).
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, new NewPhotoUpload(), "original.jpg");
        var storedBefore = OriginalFiles(factory);

        var put = await client.PutAsJsonAsync($"{Path}/{created.PhotoId}", new UpdatePhoto { FileName = "renamed.jpg" });
        put.EnsureSuccessStatusCode();
        Assert.Equal("renamed.jpg", (await put.Content.ReadFromJsonAsync<ExistingPhoto>())!.FileName);

        var fetched = await client.GetFromJsonAsync<ExistingPhoto>($"{Path}/{created.PhotoId}");
        Assert.Equal("renamed.jpg", fetched!.FileName);
        var content = await client.GetAsync($"{Path}/{created.PhotoId}/content");
        Assert.Contains("renamed.jpg", content.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        // The stored copy keeps its upload-time name, and no Files-store row was written.
        Assert.Equal(storedBefore, OriginalFiles(factory));
        using var scope = factory.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileMetadata);
    }

    [Fact]
    public async Task Put_Rename_SanitizesTheFileName()
    {
        // Issue #247's sanitizer, now on the photo's own name.
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, new NewPhotoUpload(), "original.jpg");

        var put = await client.PutAsJsonAsync($"{Path}/{created.PhotoId}", new UpdatePhoto { FileName = "beach\r\n\"day\".jpg" });

        put.EnsureSuccessStatusCode();
        Assert.Equal("beach_day_.jpg", (await put.Content.ReadFromJsonAsync<ExistingPhoto>())!.FileName);
    }

    [Fact]
    public async Task Put_RenameToNothingAfterSanitizing_Is400_AndThePhotoIsNotHalfUpdated()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, new NewPhotoUpload(), "keep.jpg");

        var put = await client.PutAsJsonAsync($"{Path}/{created.PhotoId}",
            new UpdatePhoto { FileName = "///", Title = "should not land" });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var fetched = await client.GetFromJsonAsync<ExistingPhoto>($"{Path}/{created.PhotoId}");
        Assert.Equal("keep.jpg", fetched!.FileName);
        Assert.NotEqual("should not land", fetched.Title);
    }

    /// <summary>Issue #428 §5.4: a legacy photo's rename writes the photo, never the shared file it still wraps.</summary>
    [Fact]
    public async Task Put_Rename_OfALegacyPhoto_LeavesItsFileUntouched()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (photoId, fileId) = await PhotoTestSupport.SeedLegacyPhotoAsync(factory, ActorUserId, "A.jpg");
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"{Path}/{photoId}", new UpdatePhoto { FileName = "B.jpg" });

        put.EnsureSuccessStatusCode();
        Assert.Equal("B.jpg", (await put.Content.ReadFromJsonAsync<ExistingPhoto>())!.FileName);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal("A.jpg", (await context.FileMetadata.SingleAsync(f => f.Id == fileId)).FileName);
    }

    private static List<string> OriginalFiles(OdysseyApiFactory factory) =>
        [.. Directory.EnumerateFiles(factory.PhotoVolumeRoot, "*", SearchOption.AllDirectories)
            .Where(f => f.Contains($"{System.IO.Path.DirectorySeparatorChar}photo-originals{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order()];

    // ── Extraction: resilience, caller precedence, fill (criteria #1, #2, #3) ──

    [Fact]
    public async Task Post_GarbageImage_Succeeds_WithNoMetadata()
    {
        // Real extractor over non-image bytes: extraction fails internally → null fields, add still succeeds.
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhotoUpload());
        Assert.Null(created.TakenAt);
        Assert.Null(created.CapturedLatitude);
        Assert.Null(created.PixelWidth);
    }

    [Fact]
    public async Task Post_CallerValuesWin_OverExtraction()
    {
        var extracted = new PhotoMetadata { Title = "EXIF title", TakenAt = new DateTime(2001, 1, 1), Latitude = 10, Longitude = 20, PixelWidth = 4000, PixelHeight = 3000 };
        await using var factory = new ApiFactory(ReadWrite, extracted);
        using var client = factory.CreateClient();

        var caller = new NewPhotoUpload { Title = "My title", CapturedLatitude = -5, CapturedLongitude = -6 };
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
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhotoUpload());
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
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhotoUpload());
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
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhotoUpload());
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
        using var client = factory.CreateClient();

        var post = await PhotoTestSupport.PostPhotoAsync(client, new NewPhotoUpload { TagIds = [Guid.NewGuid()] });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task Post_NonPersonContact_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var org = await PhotoTestSupport.SeedPersonAsync(factory, "Acme", Odyssey.Dtos.ContactType.Organization);
        using var client = factory.CreateClient();

        var post = await PhotoTestSupport.PostPhotoAsync(client, new NewPhotoUpload { PersonContactIds = [org] });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task Post_PersonContact_LinksAndFilters()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var person = await PhotoTestSupport.SeedPersonAsync(factory);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhotoUpload { PersonContactIds = [person] });
        Assert.Contains(person, created.PersonContactIds);

        var filtered = await client.GetPagedItemsAsync<PhotoSummary>($"{Path}?personIds={person}");
        Assert.Contains(filtered!, p => p.PhotoId == created.PhotoId);
    }

    [Fact]
    public async Task Get_AfterLinkedPersonDeleted_OmitsTheDanglingLink()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var person = await PhotoTestSupport.SeedPersonAsync(factory);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhotoUpload { PersonContactIds = [person] });

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
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhotoUpload { Title = "Fave" });
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
        var person = await PhotoTestSupport.SeedPersonAsync(factory, "Grace Hopper");
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhotoUpload { PersonContactIds = [person] });
        var json = await client.GetStringAsync($"{Path}/{created.PhotoId}");
        Assert.DoesNotContain("Grace Hopper", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_WithNestedForeignObjects_DoesNotCreateThem()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        // Form fields named like the entity's navigations bind to nothing (mass-assignment guard).
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Odyssey.Testing.FileStorage.PhotoTestData.Jpeg(7));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(part, "file", "p.jpg");
        form.Add(new StringContent("INJECTED"), "tags[0].name");
        form.Add(new StringContent(Guid.NewGuid().ToString()), "people[0].contactId");
        var post = await client.PostAsync(Path, form);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.PhotoTags.AnyAsync());
        Assert.False(await context.PhotoPeople.AnyAsync());
    }

    // ── The original: stored as uploaded, served by photo id (issue #428 §5.2) ───────────────────

    /// <summary>
    /// Criterion #3d and issue #428 AC 1/9: the original is stored byte-for-byte on the photo volume, never rewritten by
    /// an edit, and served exactly back with nosniff, CORP same-site, a strong ETag and range support.
    /// </summary>
    [Fact]
    public async Task Upload_StoresTheOriginalOnDisk_AndTheContentRouteServesItExactly()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var bytes = Odyssey.Testing.FileStorage.PhotoTestData.Jpeg(42);

        var created = await PhotoTestSupport.UploadPhotoAsync(client, new NewPhotoUpload { Title = "t" }, bytes, "beach.jpg");
        await client.PutAsJsonAsync($"{Path}/{created.PhotoId}", new UpdatePhoto { Title = "edited", CapturedLatitude = 12 });

        var stored = Assert.Single(OriginalFiles(factory));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(stored));
        Assert.Matches(@"photo-originals[\\/]\d{4}[\\/]1[\\/]beach\.jpg$", stored);
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var photo = await context.Photos.SingleAsync(p => p.PhotoId == created.PhotoId);
            Assert.Null(photo.FileId);
            Assert.NotNull(photo.StoredObjectId);
            Assert.Equal(Odyssey.Testing.FileStorage.PhotoTestData.Sha256(bytes), photo.Sha256Hash);
            Assert.Empty(context.FileMetadata);
            Assert.Empty(context.FileBlob);
        }

        var content = await client.GetAsync($"{Path}/{created.PhotoId}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(bytes, await content.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", content.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", content.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-site", content.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal($"\"{Odyssey.Testing.FileStorage.PhotoTestData.Sha256(bytes)}\"", content.Headers.ETag!.Tag);

        using var ranged = new HttpRequestMessage(HttpMethod.Get, $"{Path}/{created.PhotoId}/content");
        ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);
        var partial = await client.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(bytes[..4], await partial.Content.ReadAsByteArrayAsync());

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"{Path}/{created.PhotoId}/content");
        conditional.Headers.IfNoneMatch.Add(content.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);
    }

    [Fact]
    public async Task ContentRoute_ServesALegacyPhotoFromItsFile_AndNeedsPhotosRead()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var bytes = Odyssey.Testing.FileStorage.PhotoTestData.Jpeg(9);
        var (photoId, _) = await PhotoTestSupport.SeedLegacyPhotoAsync(factory, ActorUserId, "old.jpg", bytes);
        using var client = factory.CreateClient();

        var content = await client.GetAsync($"{Path}/{photoId}/content");

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(bytes, await content.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path}/{Guid.NewGuid()}/content")).StatusCode);

        // files.read reaches no photo original (issue #428 §7.2).
        await using var filesOnly = new ApiFactory([PermissionClaims.FilesRead]);
        using var filesClient = filesOnly.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await filesClient.GetAsync($"{Path}/{photoId}/content")).StatusCode);
    }

    /// <summary>Issue #428 AC 2: a photo uploaded now is not a Files-store file, so the Files list never shows it.</summary>
    [Fact]
    public async Task AnUploadedPhoto_IsNotOnTheFilesList()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.FilesRead]);
        using var client = factory.CreateClient();
        await PhotoTestSupport.UploadPhotoAsync(client);

        var files = await client.GetPagedItemsAsync<Odyssey.Dtos.Finance.FileListItem>("/api/files");

        Assert.Empty(files!);
    }

    // ── Duplicates (issue #428 §3.2, AC 5) ───────────────────────────────────────

    [Fact]
    public async Task Upload_TheSameBytesTwice_Is409_NamingTheExistingPhotoToAReader_AndWritesNothing()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var bytes = Odyssey.Testing.FileStorage.PhotoTestData.Jpeg(5);
        var first = await PhotoTestSupport.UploadPhotoAsync(client, bytes: bytes);

        var second = await PhotoTestSupport.PostPhotoAsync(client, bytes: bytes, fileName: "again.jpg");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var problem = System.Text.Json.JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(first.PhotoId, problem.RootElement.GetProperty("existingPhotoId").GetGuid());
        Assert.False(problem.RootElement.GetProperty("existingArchived").GetBoolean());
        Assert.Single(OriginalFiles(factory));
    }

    [Fact]
    public async Task Upload_ADuplicate_WithoutPhotosRead_Is409_WithNoId()
    {
        await using var factory = new ApiFactory([PermissionClaims.PhotosCreate]);
        using var client = factory.CreateClient();
        var bytes = Odyssey.Testing.FileStorage.PhotoTestData.Jpeg(6);
        (await PhotoTestSupport.PostPhotoAsync(client, bytes: bytes)).EnsureSuccessStatusCode();

        var second = await PhotoTestSupport.PostPhotoAsync(client, bytes: bytes);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.DoesNotContain("existingPhotoId", body, StringComparison.Ordinal);
        Assert.DoesNotContain("existingArchived", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_AnArchivedPhotosBytes_SaysItIsArchived()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var bytes = Odyssey.Testing.FileStorage.PhotoTestData.Jpeg(8);
        var first = await PhotoTestSupport.UploadPhotoAsync(client, bytes: bytes);
        (await client.PutAsJsonAsync($"{Path}/{first.PhotoId}", new UpdatePhoto { Archived = true })).EnsureSuccessStatusCode();

        var second = await PhotoTestSupport.PostPhotoAsync(client, bytes: bytes);

        using var problem = System.Text.Json.JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("existingArchived").GetBoolean());
    }

    [Fact]
    public async Task Upload_TheBytesOfALegacyPhoto_IsADuplicate_ButAFinanceFilesBytesAreNot()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var legacyBytes = Odyssey.Testing.FileStorage.PhotoTestData.Jpeg(11);
        var financeBytes = Odyssey.Testing.FileStorage.PhotoTestData.Jpeg(12);
        await PhotoTestSupport.SeedLegacyPhotoAsync(factory, ActorUserId, bytes: legacyBytes);
        using (var scope = factory.Services.CreateScope())
        {
            // A finance image no photo wraps: never an oracle for finance documents (issue #428 §7.7).
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var blob = new FileBlob { Id = Guid.NewGuid(), Content = financeBytes };
            context.FileBlob.Add(blob);
            context.FileMetadata.Add(new FileMetadata
            {
                Id = Guid.NewGuid(), FileName = "receipt.jpg", ContentType = "image/jpeg", SizeBytes = financeBytes.Length,
                Sha256Hash = Odyssey.Testing.FileStorage.PhotoTestData.Sha256(financeBytes), FileBlobId = blob.Id, UploadedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Conflict, (await PhotoTestSupport.PostPhotoAsync(client, bytes: legacyBytes)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PhotoTestSupport.PostPhotoAsync(client, bytes: financeBytes)).StatusCode);
    }

    // ── Upload failures (issue #428 §5.1, §9) ──────────────────────────────────────

    [Fact]
    public async Task Upload_OverTheUploadCap_Is413_NamingTheCap()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.FileStorageMaxUploadMegabytes, "1");
        using var client = factory.CreateClient();
        var big = new byte[(1024 * 1024) + 10];
        Odyssey.Testing.FileStorage.PhotoTestData.Jpeg(1).CopyTo(big, 0);

        var post = await PhotoTestSupport.PostPhotoAsync(client, bytes: big);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, post.StatusCode);
        Assert.Contains("1 MB", await post.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_BelowTheUploadReserve_Is507_AndWritesNothing()
    {
        await using var factory = new ApiFactory(ReadWrite);
        factory.PhotoVolumeSpace.Override = (2L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024);
        using var client = factory.CreateClient();

        var post = await PhotoTestSupport.PostPhotoAsync(client);

        Assert.Equal(HttpStatusCode.InsufficientStorage, post.StatusCode);
        Assert.Empty(OriginalFiles(factory));
        using var scope = factory.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<OdysseyContext>().Photos);
    }

    /// <summary>Issue #428 AC 13: deleting a photo releases its original (graced) and touches no Files-store row.</summary>
    [Fact]
    public async Task Delete_ReleasesTheOriginal_Graced()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, new NewPhotoUpload { Title = "t" });
        Guid objectId;
        using (var scope = factory.Services.CreateScope())
        {
            objectId = (await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Photos
                .SingleAsync(p => p.PhotoId == created.PhotoId)).StoredObjectId!.Value;
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{created.PhotoId}")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            Assert.False(await context.Photos.AnyAsync(photo => photo.PhotoId == created.PhotoId));
            var released = await context.StoredObjects.SingleAsync(o => o.Id == objectId);
            Assert.Equal(StoredObjectState.PendingDelete, released.State);
            Assert.False(released.DeleteImmediately);
        }

        // The bytes stay until the maintenance pass after the grace.
        Assert.Single(OriginalFiles(factory));
    }

    /// <summary>
    /// A LEGACY photo (created before issue #428) and the file it wraps are still deleted together: the database
    /// enforces the reverse direction with a cascading FK; this direction is application code in PhotoService.
    /// </summary>
    [Fact]
    public async Task Delete_OfALegacyPhoto_AlsoRemovesTheFileItWrapped()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var (photoId, fileId) = await PhotoTestSupport.SeedLegacyPhotoAsync(factory, ActorUserId);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{photoId}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.FileMetadata.AnyAsync(f => f.Id == fileId));
        Assert.False(await context.Photos.AnyAsync(photo => photo.PhotoId == photoId));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // ── Author-name attribution (#316): Photos route CreatedByUserId through the shared claim-aware
    //    resolver on both the list and the single-photo read. ──────────────────────────────────────
    [Fact]
    public async Task Author_DisplayName_Resolves_On_List_And_Detail()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await factory.SeedActorUserAsync(displayName: "Ada L.");
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhotoUpload());

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
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, new NewPhotoUpload());

        var detail = await client.GetFromJsonAsync<ExistingPhoto>($"{Path}/{created.PhotoId}");
        Assert.Equal("Unknown user", detail!.CreatedByName);
        Assert.NotEqual(userName, detail.CreatedByName);
    }

    private static Task<ExistingPhoto> CreateAsync(HttpClient client, NewPhotoUpload request, string fileName = "photo.jpg") =>
        PhotoTestSupport.UploadPhotoAsync(client, request, fileName: fileName);


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
