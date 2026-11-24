using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Testing.FileStorage;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>The recipe endpoints (issue #525 §5 and §12) over the EF InMemory host.</summary>
public class RecipesApiTests(RecipesApiTests.Host host) : SharedApiHostTests<RecipesApiTests.Host>(host)
{
    private const string ActorUserId = "recipes-actor-id";
    private const string Path = "/api/recipes";

    private static readonly string[] Editor =
    [
        PermissionClaims.RecipesRead, PermissionClaims.RecipesCreate, PermissionClaims.RecipesUpdate,
        PermissionClaims.RecipesDelete, PermissionClaims.RecipeTagsRead,
    ];

    private static readonly string[] EditorWithPhotos = [.. Editor, PermissionClaims.PhotosRead];

    private static readonly string[] Guest = [PermissionClaims.RecipesRead, PermissionClaims.RecipeTagsRead];

    private static readonly string[] EditorWithFiles = [.. Editor, PermissionClaims.FilesRead];

    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ThenGet_ReturnsTheRecipeInSubmittedOrder()
    {
        var photoA = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId, "a.jpg");
        var photoB = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId, "b.jpg");
        var tag = await SeedTagAsync("Dinner");
        using var client = Factory.ClientWith(EditorWithPhotos);

        var request = Pancakes();
        request.PhotoIds = [photoB, photoA];
        request.TagIds = [tag];
        var post = await client.PostAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = (await post.Content.ReadFromJsonAsync<ExistingRecipe>())!;
        Assert.Equal(1, created.Version);
        Assert.NotNull(post.Headers.Location);

        var read = (await client.GetFromJsonAsync<ExistingRecipe>($"{Path}/{created.RecipeId}"))!;
        Assert.Equal(["wheat flour", "milk", "salt"], read.Ingredients.Select(i => i.Name));
        Assert.Equal("Batter", read.Ingredients[0].Section);
        Assert.Equal(IngredientUnit.ToTaste, read.Ingredients[2].Unit);
        Assert.Null(read.Ingredients[2].Amount);
        Assert.Equal(["Whisk.", "Fry."], read.Steps.Select(s => s.Text));
        Assert.Equal([photoB, photoA], read.Photos!.Select(p => p.PhotoId));
        Assert.Equal(0, read.Photos![0].Position);
        Assert.Equal(25, read.TotalMinutes);
        Assert.Equal([tag], read.TagIds);
        Assert.False(read.Scaled);
    }

    [Fact]
    public async Task Read_TrimsTrailingZerosFromAmounts()
    {
        using var client = Factory.ClientWith(Editor);
        var created = await CreateAsync(client, Pancakes());

        var json = await client.GetStringAsync($"{Path}/{created.RecipeId}");

        Assert.Contains("\"amount\":250,", json, StringComparison.Ordinal);
    }

    // ── Authorization ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "")]
    [InlineData("PUT", "/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/00000000-0000-0000-0000-000000000001")]
    public async Task EveryEndpoint_WithoutRecipeClaims_IsForbidden(string method, string suffix)
    {
        using var client = Factory.ClientWith(PermissionClaims.PhotosRead, PermissionClaims.JournalRead);
        using var message = new HttpRequestMessage(new HttpMethod(method), Path + suffix)
        {
            Content = method is "POST" or "PUT" ? JsonContent.Create(new { title = "x" }) : null,
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(message)).StatusCode);
    }

    [Fact]
    public async Task Guest_ReadsButCannotWrite()
    {
        using var editor = Factory.ClientWith(Editor);
        var created = await CreateAsync(editor, Pancakes());
        using var guest = Factory.ClientWith(Guest);

        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"{Path}/{created.RecipeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync(Path, Pancakes())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PutAsJsonAsync($"{Path}/{created.RecipeId}", Update(created))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.DeleteAsync($"{Path}/{created.RecipeId}")).StatusCode);
    }

    // ── Photo claim rules (§7.3, §7.4) ──────────────────────────────────────

    [Fact]
    public async Task Create_WithPhotosWithoutPhotosRead_IsForbidden_AndWithoutPhotosSucceeds()
    {
        var photo = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId);
        using var client = Factory.ClientWith(Editor);

        var withPhoto = Pancakes();
        withPhoto.PhotoIds = [photo];
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Path, withPhoto)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Path, Pancakes())).StatusCode);
    }

    [Fact]
    public async Task Update_WithoutPhotosRead_NullKeepsLinks_AnyArrayIsForbidden()
    {
        var photo = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId);
        using var withPhotos = Factory.ClientWith(EditorWithPhotos);
        var request = Pancakes();
        request.PhotoIds = [photo];
        var created = await CreateAsync(withPhotos, request);
        using var client = Factory.ClientWith(Editor);

        var same = Update(created);
        same.PhotoIds = [photo];
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", same)).StatusCode);

        var keep = Update(created);
        keep.PhotoIds = null;
        keep.Title = "Crêpes";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", keep)).StatusCode);

        var read = (await withPhotos.GetFromJsonAsync<ExistingRecipe>($"{Path}/{created.RecipeId}"))!;
        Assert.Equal([photo], read.Photos!.Select(p => p.PhotoId));
    }

    [Fact]
    public async Task WithoutPhotosRead_PhotoDataIsAbsent_AndTheFilterIsForbidden()
    {
        var photo = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId);
        using var withPhotos = Factory.ClientWith(EditorWithPhotos);
        var request = Pancakes();
        request.PhotoIds = [photo];
        var created = await CreateAsync(withPhotos, request);
        using var client = Factory.ClientWith(Editor);

        var read = (await client.GetFromJsonAsync<ExistingRecipe>($"{Path}/{created.RecipeId}"))!;
        Assert.Null(read.Photos);
        var row = (await client.GetFromJsonAsync<PagedResultDto>(Path))!.Items.Single();
        Assert.Null(row.CoverPhotoId);
        Assert.False(row.CoverThumbnailsReady);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Path}?hasPhotos=true")).StatusCode);

        var withClaim = (await withPhotos.GetFromJsonAsync<PagedResultDto>(Path))!.Items.Single();
        Assert.Equal(photo, withClaim.CoverPhotoId);
    }

    [Fact]
    public async Task WithoutTheClaim_AnExistingAndAMissingId_GiveTheSameForbidden()
    {
        var photo = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId);
        var tag = await SeedTagAsync("Lunch");
        using var client = Factory.ClientWith(PermissionClaims.RecipesRead, PermissionClaims.RecipesCreate);

        var existingPhoto = Pancakes();
        existingPhoto.PhotoIds = [photo];
        var missingPhoto = Pancakes();
        missingPhoto.PhotoIds = [Guid.NewGuid()];
        Assert.Equal(await BodyOf(client, existingPhoto), await BodyOf(client, missingPhoto));

        var existingTag = Pancakes();
        existingTag.TagIds = [tag];
        var missingTag = Pancakes();
        missingTag.TagIds = [Guid.NewGuid()];
        Assert.Equal(await BodyOf(client, existingTag), await BodyOf(client, missingTag));

        static async Task<string> BodyOf(HttpClient client, NewRecipe request)
        {
            var response = await client.PostAsJsonAsync(Path, request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            return Strip(await response.Content.ReadAsStringAsync());
        }

        static string Strip(string body) => WithoutTraceId(body);
    }

    // ── File links (Draft v6) ────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithFiles_ReturnsThemWithNameTypeAndSize_AndTheListCountsThem()
    {
        var pdf = await SeedFileAsync("original.pdf", "application/pdf", "the original");
        var scan = await SeedFileAsync("scan.png", "image/png", "pixels", StoredFileArea.Library);
        using var client = Factory.ClientWith(EditorWithFiles);

        var request = Pancakes();
        request.AttachmentFileIds = [pdf, scan];
        var created = await CreateAsync(client, request);

        // Unordered links come back by file name.
        Assert.Equal([pdf, scan], created.Attachments!.Select(a => a.FileId));
        Assert.Equal(["original.pdf", "scan.png"], created.Attachments!.Select(a => a.FileName));
        var original = created.Attachments!.Single(a => a.FileId == pdf);
        Assert.Equal("application/pdf", original.ContentType);
        Assert.Equal(12, original.SizeBytes);
        var row = (await client.GetFromJsonAsync<PagedResultDto>(Path))!.Items.Single(r => r.RecipeId == created.RecipeId);
        Assert.Equal(2, row.AttachmentCount);
        var filtered = (await client.GetFromJsonAsync<PagedResultDto>($"{Path}?hasAttachments=true"))!.Items;
        Assert.Contains(filtered, r => r.RecipeId == created.RecipeId);
    }

    [Fact]
    public async Task Create_WithFilesWithoutFilesRead_IsForbidden_ForAnExistingAndAMissingIdAlike()
    {
        var file = await SeedFileAsync("original.pdf", "application/pdf", "x");
        using var client = Factory.ClientWith(Editor);

        var existing = Pancakes();
        existing.AttachmentFileIds = [file];
        var missing = Pancakes();
        missing.AttachmentFileIds = [Guid.NewGuid()];
        var first = await client.PostAsJsonAsync(Path, existing);
        var second = await client.PostAsJsonAsync(Path, missing);

        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        Assert.Equal(WithoutTraceId(await first.Content.ReadAsStringAsync()), WithoutTraceId(await second.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Path, Pancakes())).StatusCode);
    }

    [Fact]
    public async Task Update_WithoutFilesRead_NullKeepsLinks_AnyArrayIsForbidden()
    {
        var file = await SeedFileAsync("original.pdf", "application/pdf", "x");
        using var withFiles = Factory.ClientWith(EditorWithFiles);
        var request = Pancakes();
        request.AttachmentFileIds = [file];
        var created = await CreateAsync(withFiles, request);
        using var client = Factory.ClientWith(Editor);

        var same = Update(created);
        same.AttachmentFileIds = [file];
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", same)).StatusCode);
        var none = Update(created);
        none.AttachmentFileIds = [];
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", none)).StatusCode);

        var keep = Update(created);
        keep.Title = "Crêpes";
        var put = await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", keep);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Null((await put.Content.ReadFromJsonAsync<ExistingRecipe>())!.Attachments);

        var read = (await withFiles.GetFromJsonAsync<ExistingRecipe>($"{Path}/{created.RecipeId}"))!;
        Assert.Equal([file], read.Attachments!.Select(a => a.FileId));
    }

    [Fact]
    public async Task WithoutFilesRead_FileDataIsAbsent_AndTheFilterIsForbidden()
    {
        var file = await SeedFileAsync("original.pdf", "application/pdf", "x");
        using var withFiles = Factory.ClientWith(EditorWithFiles);
        var request = Pancakes();
        request.AttachmentFileIds = [file];
        var created = await CreateAsync(withFiles, request);
        using var client = Factory.ClientWith(Editor);

        Assert.Null((await client.GetFromJsonAsync<ExistingRecipe>($"{Path}/{created.RecipeId}"))!.Attachments);
        var row = (await client.GetFromJsonAsync<PagedResultDto>(Path))!.Items.Single(r => r.RecipeId == created.RecipeId);
        Assert.Null(row.AttachmentCount);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Path}?hasAttachments=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Path}?hasAttachments=false")).StatusCode);
    }

    [Fact]
    public async Task Update_AddsAndRemovesFileLinks_InPlace()
    {
        var a = await SeedFileAsync("a.pdf", "application/pdf", "a");
        var b = await SeedFileAsync("b.txt", "text/plain", "b");
        using var client = Factory.ClientWith(EditorWithFiles);
        var request = Pancakes();
        request.AttachmentFileIds = [a];
        var created = await CreateAsync(client, request);
        var linkA = created.Attachments!.Single().RecipeAttachmentId;

        var update = Update(created);
        update.AttachmentFileIds = [a, b];
        var both = (await (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", update)).Content.ReadFromJsonAsync<ExistingRecipe>())!;
        Assert.Equal(linkA, both.Attachments!.Single(x => x.FileId == a).RecipeAttachmentId);

        var drop = Update(both);
        drop.AttachmentFileIds = [b];
        var onlyB = (await (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", drop)).Content.ReadFromJsonAsync<ExistingRecipe>())!;
        Assert.Equal([b], onlyB.Attachments!.Select(x => x.FileId));
    }

    [Fact]
    public async Task Create_WithDuplicateUnknownOrPhotoOriginalFiles_IsUnprocessable_KeyedOnAttachmentFileIds()
    {
        var file = await SeedFileAsync("a.pdf", "application/pdf", "a");
        var original = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId, "orig.jpg");
        Guid originalFileId;
        using (var scope = Factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            originalFileId = Guid.NewGuid();
            var photo = await context.Photos.SingleAsync(p => p.PhotoId == original);
            var hidden = await StoredFileFixtures.SeedFileAsync(scope.ServiceProvider, [1, 2, 3], "orig.jpg", "image/jpeg", id: originalFileId);
            photo.LegacyFileId = hidden.Id;
            await context.SaveChangesAsync();
        }

        using var client = Factory.ClientWith(EditorWithFiles);
        foreach (var ids in new[] { new[] { file, file }, [Guid.NewGuid()], [originalFileId] })
        {
            var request = Pancakes();
            request.AttachmentFileIds = ids;
            var response = await client.PostAsJsonAsync(Path, request);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(["attachmentFileIds"], await ErrorKeys(response));
        }
    }

    [Fact]
    public async Task Create_OverTheFileCap_IsBadRequest()
    {
        using var client = Factory.ClientWith(EditorWithFiles);
        var request = Pancakes();
        request.AttachmentFileIds = [.. Enumerable.Range(0, RecipeLimits.MaxAttachments + 1).Select(_ => Guid.NewGuid())];

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, request)).StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesTheFileLinks_AndKeepsTheFiles()
    {
        var file = await SeedFileAsync("a.pdf", "application/pdf", "a");
        using var client = Factory.ClientWith(EditorWithFiles);
        var request = Pancakes();
        request.AttachmentFileIds = [file];
        var created = await CreateAsync(client, request);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{created.RecipeId}")).StatusCode);

        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.RecipeAttachments.AnyAsync(l => l.RecipeId == created.RecipeId));
        Assert.True(await context.FileMetadata.AnyAsync(f => f.Id == file));
    }

    // Every area the Files store shows is linkable: the area is placement, never access (issue #448).
    [Theory]
    [InlineData(StoredFileArea.Finance)]
    [InlineData(StoredFileArea.Journal)]
    [InlineData(StoredFileArea.ContactAvatars)]
    [InlineData(StoredFileArea.Library)]
    public async Task Create_LinksAFileFromAnyFilesStoreArea(StoredFileArea area)
    {
        var file = await SeedFileAsync("a.pdf", "application/pdf", "a", area);
        using var client = Factory.ClientWith(EditorWithFiles);
        var request = Pancakes();
        request.AttachmentFileIds = [file];

        var created = await CreateAsync(client, request);

        Assert.Equal([file], created.Attachments!.Select(a => a.FileId));
    }

    [Fact]
    public async Task HasAttachmentsFalse_WithFilesRead_ReturnsOnlyRecipesWithoutFiles()
    {
        var file = await SeedFileAsync("a.pdf", "application/pdf", "a");
        using var client = Factory.ClientWith(EditorWithFiles);
        var request = Pancakes();
        request.AttachmentFileIds = [file];
        var withFile = await CreateAsync(client, request);
        var without = await CreateAsync(client, Pancakes());

        var items = (await client.GetFromJsonAsync<PagedResultDto>($"{Path}?hasAttachments=false"))!.Items;

        Assert.Contains(items, r => r.RecipeId == without.RecipeId);
        Assert.DoesNotContain(items, r => r.RecipeId == withFile.RecipeId);
        Assert.All(items, r => Assert.Equal(0, r.AttachmentCount));
    }

    [Fact]
    public async Task Update_WithFilesRead_NullKeepsLinks_AndAnEmptyArrayClearsThem()
    {
        var file = await SeedFileAsync("a.pdf", "application/pdf", "a");
        using var client = Factory.ClientWith(EditorWithFiles);
        var request = Pancakes();
        request.AttachmentFileIds = [file];
        var created = await CreateAsync(client, request);

        var keep = Update(created);
        Assert.Null(keep.AttachmentFileIds);
        var kept = (await (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", keep)).Content.ReadFromJsonAsync<ExistingRecipe>())!;
        Assert.Equal([file], kept.Attachments!.Select(a => a.FileId));

        var clear = Update(kept);
        clear.AttachmentFileIds = [];
        var cleared = (await (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", clear)).Content.ReadFromJsonAsync<ExistingRecipe>())!;
        Assert.Empty(cleared.Attachments!);
    }

    // Only ids the PUT adds are validated. A linked file cannot turn invisible (the link itself keeps it visible,
    // FileReferenceCounter), so a held id is skipped; an id the recipe no longer holds is new and checked again.
    [Fact]
    public async Task Update_RevalidatesAnIdTheRecipeNoLongerHolds()
    {
        var file = await SeedFileAsync("a.pdf", "application/pdf", "a");
        using var client = Factory.ClientWith(EditorWithFiles);
        var request = Pancakes();
        request.AttachmentFileIds = [file];
        var created = await CreateAsync(client, request);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.RecipeAttachments.RemoveRange(context.RecipeAttachments.Where(a => a.FileId == file));
            await context.SaveChangesAsync();
        }

        // The link is gone underneath; re-adding the same id is a new id and is checked again.
        var photo = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId, "orig.jpg");
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            (await context.Photos.SingleAsync(p => p.PhotoId == photo)).LegacyFileId = file;
            await context.SaveChangesAsync();
        }

        var readd = Update(created);
        readd.AttachmentFileIds = [file];
        var refused = await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", readd);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(["attachmentFileIds"], await ErrorKeys(refused));
    }

    [Fact]
    public async Task Update_KeepsAnAlreadyLinkedFile_AndRejectsItWithAnUnknownId()
    {
        var file = await SeedFileAsync("a.pdf", "application/pdf", "a");
        using var client = Factory.ClientWith(EditorWithFiles);
        var request = Pancakes();
        request.AttachmentFileIds = [file];
        var created = await CreateAsync(client, request);

        var same = Update(created);
        same.AttachmentFileIds = [file];
        var ok = await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", same);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var current = (await ok.Content.ReadFromJsonAsync<ExistingRecipe>())!;

        var bad = Update(current);
        bad.AttachmentFileIds = [file, Guid.NewGuid()];
        var refused = await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", bad);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(["attachmentFileIds"], await ErrorKeys(refused));
        var read = (await client.GetFromJsonAsync<ExistingRecipe>($"{Path}/{created.RecipeId}"))!;
        Assert.Equal([file], read.Attachments!.Select(a => a.FileId));
    }

    [Fact]
    public async Task Create_AtExactlyTheFileCap_IsCreated_AndAPutOverIt_IsBadRequest()
    {
        var files = new List<Guid>();
        for (var i = 0; i < RecipeLimits.MaxAttachments; i++)
        {
            files.Add(await SeedFileAsync($"f{i:00}.txt", "text/plain", "x"));
        }

        using var client = Factory.ClientWith(EditorWithFiles);
        var request = Pancakes();
        request.AttachmentFileIds = [.. files];
        var created = await CreateAsync(client, request);
        Assert.Equal(RecipeLimits.MaxAttachments, created.Attachments!.Count);

        var over = Update(created);
        over.AttachmentFileIds = [.. files, Guid.NewGuid()];
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", over)).StatusCode);
    }

    // ── Scaling (§8.3) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Get_WithServings_ScalesOnlyScalableAmounts_AndWritesNothing()
    {
        using var client = Factory.ClientWith(Editor);
        var request = Pancakes();
        request.Ingredients = [.. request.Ingredients, new() { Amount = 1, Unit = IngredientUnit.Pinch, Name = "sugar" }];
        var created = await CreateAsync(client, request);

        var scaled = (await client.GetFromJsonAsync<ExistingRecipe>($"{Path}/{created.RecipeId}?servings=8"))!;

        Assert.True(scaled.Scaled);
        Assert.Equal(8, scaled.DisplayedServings);
        Assert.Equal(4, scaled.Servings);
        Assert.Equal(500m, scaled.Ingredients[0].Amount);
        Assert.Equal(1m, scaled.Ingredients[1].Amount);
        Assert.Null(scaled.Ingredients[2].Amount);
        Assert.Equal(1m, scaled.Ingredients[3].Amount);
        Assert.Equal(created.Steps.Select(s => s.Text), scaled.Steps.Select(s => s.Text));
        Assert.Equal(created.PrepMinutes, scaled.PrepMinutes);

        var reread = (await client.GetFromJsonAsync<ExistingRecipe>($"{Path}/{created.RecipeId}"))!;
        Assert.Equal(created.Version, reread.Version);
        Assert.Equal(created.UpdatedAt, reread.UpdatedAt);
        Assert.Equal(250m, reread.Ingredients[0].Amount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("two")]
    public async Task Get_WithServingsOutOfRange_IsBadRequest(string servings)
    {
        using var client = Factory.ClientWith(Editor);
        var created = await CreateAsync(client, Pancakes());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Path}/{created.RecipeId}?servings={servings}")).StatusCode);
    }

    // ── Validation (§8.1) ───────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"servings":4}""")]                                                           // no title
    [InlineData("""{"title":"x"}""")]                                                            // no servings
    [InlineData("""{"title":"x","servings":0}""")]
    [InlineData("""{"title":"x","servings":101}""")]
    [InlineData("""{"title":"x","servings":4,"prepMinutes":10081}""")]
    [InlineData("""{"title":"x","servings":4,"ingredients":[{"name":"egg"}]}""")]                 // omitted unit
    [InlineData("""{"title":"x","servings":4,"ingredients":[{"name":"egg","unit":99}]}""")]
    [InlineData("""{"title":"x","servings":4,"ingredients":[{"name":"egg","unit":0,"amount":0}]}""")]
    [InlineData("""{"title":"x","servings":4,"ingredients":[{"name":"egg","unit":0,"amount":1000000}]}""")]
    [InlineData("""{"title":"x","servings":4,"ingredients":[{"unit":0}]}""")]                     // no name
    [InlineData("""{"title":"x","servings":4,"steps":[{"section":"a"}]}""")]                     // no text
    [InlineData("""{"title":"   ","servings":4}""")]                                             // blank title
    [InlineData("""{"title":"x","servings":4,"cookMinutes":10081}""")]
    [InlineData("""{"title":"x","servings":4,"prepMinutes":-1}""")]
    [InlineData("""{"title":"x","servings":4,"ingredients":[{"name":"egg","unit":0,"section":"#S65#"}]}""")]
    [InlineData("""{"title":"x","servings":4,"servingsLabel":"#S33#"}""")]
    [InlineData("""{"title":"#S201#","servings":4}""")]
    [InlineData("""{"title":"x","servings":4,"description":"#S1001#"}""")]
    [InlineData("""{"title":"x","servings":4,"source":"#S501#"}""")]
    [InlineData("""{"title":"x","servings":4,"notes":"#S8001#"}""")]
    [InlineData("""{"title":"x","servings":4,"ingredients":[{"name":"#S201#","unit":0}]}""")]
    [InlineData("""{"title":"x","servings":4,"steps":[{"text":"#S4001#"}]}""")]
    [InlineData("""{"title":"x","servings":4,"photoIds":#G21#}""")]
    [InlineData("""{"title":"x","servings":4,"tagIds":#G21#}""")]
    public async Task Create_WithAnInvalidField_IsBadRequest(string body)
    {
        using var client = Factory.ClientWith(EditorWithPhotos);

        var response = await client.PostAsync(Path, new StringContent(Expand(body), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty(await ErrorKeys(response));
    }

    [Fact]
    public async Task Create_WithTooManySteps_IsBadRequest()
    {
        using var client = Factory.ClientWith(Editor);
        var request = Pancakes();
        request.Steps = Enumerable.Range(0, RecipeLimits.MaxSteps + 1).Select(i => new RecipeStepRequest { Text = $"s{i}" }).ToArray();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, request)).StatusCode);
    }

    [Fact]
    public async Task Create_AtEveryCap_IsAccepted()
    {
        using var client = Factory.ClientWith(Editor);
        var request = new NewRecipe
        {
            Title = new string('t', RecipeLimits.MaxTitleLength),
            Description = new string('d', RecipeLimits.MaxDescriptionLength),
            Notes = new string('n', RecipeLimits.MaxNotesLength),
            Source = new string('s', RecipeLimits.MaxSourceLength),
            Servings = RecipeLimits.MaxServings,
            ServingsLabel = new string('l', RecipeLimits.MaxServingsLabelLength),
            PrepMinutes = RecipeLimits.MaxMinutes,
            CookMinutes = RecipeLimits.MaxMinutes,
            Ingredients = Enumerable.Range(0, RecipeLimits.MaxIngredients).Select(_ => new RecipeIngredientRequest
            {
                Section = new string('x', RecipeLimits.MaxSectionLength),
                Amount = 999998.999m,
                Unit = IngredientUnit.Gram,
                Name = new string('i', RecipeLimits.MaxIngredientNameLength),
            }).ToArray(),
            Steps = Enumerable.Range(0, RecipeLimits.MaxSteps)
                .Select(_ => new RecipeStepRequest { Section = new string('x', RecipeLimits.MaxSectionLength), Text = new string('w', RecipeLimits.MaxStepTextLength) })
                .ToArray(),
        };

        var created = await CreateAsync(client, request);

        Assert.Equal(RecipeLimits.MaxIngredients, created.Ingredients.Count);
        Assert.Equal(RecipeLimits.MaxSteps, created.Steps.Count);
        Assert.Equal(2 * RecipeLimits.MaxMinutes, created.TotalMinutes);
    }

    // "#S<n>#" is a string of n characters; "#G<n>#" an array of n fresh GUIDs.
    private static string Expand(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "#([SG])(\\d+)#", m =>
        {
            var n = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            return m.Groups[1].Value == "S"
                ? new string('a', n)
                : JsonSerializer.Serialize(Enumerable.Range(0, n).Select(_ => Guid.NewGuid()));
        });

    [Fact]
    public async Task Create_OverTheIngredientCap_IsBadRequest()
    {
        using var client = Factory.ClientWith(Editor);
        var request = Pancakes();
        request.Ingredients = Enumerable.Range(0, RecipeLimits.MaxIngredients + 1)
            .Select(i => new RecipeIngredientRequest { Name = $"i{i}", Unit = IngredientUnit.None }).ToArray();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, request)).StatusCode);
    }

    [Fact]
    public async Task Update_WithoutVersion_IsBadRequest()
    {
        using var client = Factory.ClientWith(Editor);
        var created = await CreateAsync(client, Pancakes());
        var request = Update(created);
        request.Version = null;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", request)).StatusCode);
    }

    public static TheoryData<RecipeIngredientRequest, string> RuleViolations => new()
    {
        { new() { Name = "egg", Unit = IngredientUnit.None, Amount = 1.2345m }, "ingredients[0].amount" },
        { new() { Name = "salt", Unit = IngredientUnit.ToTaste, Amount = 1 }, "ingredients[0].amount" },
    };

    [Theory]
    [MemberData(nameof(RuleViolations))]
    public async Task Create_BreakingACrossFieldRule_IsUnprocessable_KeyedOnTheField(RecipeIngredientRequest line, string key)
    {
        using var client = Factory.ClientWith(Editor);
        var request = Pancakes();
        request.Ingredients = [line];

        var response = await client.PostAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(key, await ErrorKeys(response));
    }

    [Fact]
    public async Task Create_WithDuplicateOrUnknownLinks_IsUnprocessable()
    {
        var photo = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId);
        var tag = await SeedTagAsync("Snack");
        var archived = await SeedTagAsync("Old", archived: true);
        using var client = Factory.ClientWith(EditorWithPhotos);

        foreach (var (mutate, key) in new (Action<NewRecipe>, string)[]
        {
            (r => r.PhotoIds = [photo, photo], "photoIds"),
            (r => r.PhotoIds = [Guid.NewGuid()], "photoIds"),
            (r => r.TagIds = [tag, tag], "tagIds"),
            (r => r.TagIds = [Guid.NewGuid()], "tagIds"),
            (r => r.TagIds = [archived], "tagIds"),
        })
        {
            var request = Pancakes();
            mutate(request);
            var response = await client.PostAsJsonAsync(Path, request);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains(key, await ErrorKeys(response));
        }
    }

    [Fact]
    public async Task Update_KeepingAnArchivedTag_Succeeds()
    {
        var tag = await SeedTagAsync("Seasonal");
        using var client = Factory.ClientWith(Editor);
        var request = Pancakes();
        request.TagIds = [tag];
        var created = await CreateAsync(client, request);
        await ArchiveTagAsync(tag);

        var update = Update(created);
        update.TagIds = [tag];

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", update)).StatusCode);
    }

    [Fact]
    public async Task NestedObjects_InTheBody_CreateNothing()
    {
        var photo = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId, "keep.jpg");
        using var client = Factory.ClientWith(EditorWithPhotos);
        var body = JsonSerializer.Serialize(new
        {
            title = "Toast",
            servings = 1,
            tags = new[] { new { name = "Injected" } },
            photos = new[] { new { photoId = photo, title = "renamed" } },
        });

        var response = await client.PostAsync(Path, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExistingRecipe>())!;

        Assert.Empty(created.TagIds);
        Assert.Empty(created.Photos!);
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.RecipeTags.AnyAsync());
        Assert.Null((await context.Photos.SingleAsync(p => p.PhotoId == photo)).Title);
    }

    // ── Update semantics ────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ReplacesChildren_AndReordersPhotos()
    {
        var photoA = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId, "a.jpg");
        var photoB = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId, "b.jpg");
        using var client = Factory.ClientWith(EditorWithPhotos);
        var request = Pancakes();
        request.PhotoIds = [photoA, photoB];
        var created = await CreateAsync(client, request);

        var update = Update(created);
        update.Ingredients = [new() { Amount = 2, Unit = IngredientUnit.None, Name = "eggs" }];
        update.Steps = [new() { Text = "Boil." }];
        update.PhotoIds = [photoB, photoA];
        var put = await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", update);
        put.EnsureSuccessStatusCode();
        var updated = (await put.Content.ReadFromJsonAsync<ExistingRecipe>())!;

        Assert.Equal(2, updated.Version);
        Assert.Equal(["eggs"], updated.Ingredients.Select(i => i.Name));
        Assert.Equal(["Boil."], updated.Steps.Select(s => s.Text));
        Assert.Equal([photoB, photoA], updated.Photos!.Select(p => p.PhotoId));

        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(1, await context.RecipeIngredients.CountAsync(i => i.RecipeId == created.RecipeId));
        Assert.Equal(1, await context.RecipeSteps.CountAsync(s => s.RecipeId == created.RecipeId));
    }

    [Fact]
    public async Task Update_UnknownId_IsNotFound_AndCreatesNothing()
    {
        using var client = Factory.ClientWith(Editor);
        var created = await CreateAsync(client, Pancakes());
        var request = Update(created);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Path}/{Guid.NewGuid()}", request)).StatusCode);

        await using var scope = Factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Recipes.CountAsync());
    }

    [Fact]
    public async Task Update_UnknownId_GivesTheSameNotFound_WithOrWithoutTheLinkClaims()
    {
        var id = Guid.NewGuid();
        var request = new UpdateRecipe { Title = "x", Servings = 1, Version = 1, PhotoIds = [Guid.NewGuid()], TagIds = [Guid.NewGuid()] };
        using var bare = Factory.ClientWith(PermissionClaims.RecipesUpdate);
        using var full = Factory.ClientWith(EditorWithPhotos);

        var withoutClaims = await bare.PutAsJsonAsync($"{Path}/{id}", request);
        var withClaims = await full.PutAsJsonAsync($"{Path}/{id}", request);

        Assert.Equal(HttpStatusCode.NotFound, withoutClaims.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withClaims.StatusCode);
    }

    [Fact]
    public async Task Update_WithAStaleVersion_IsAVersionConflict_AndChangesNothing()
    {
        using var client = Factory.ClientWith(Editor);
        var created = await CreateAsync(client, Pancakes());

        var first = Update(created);
        first.Title = "First";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", first)).StatusCode);

        var second = Update(created);
        second.Title = "Second";
        var response = await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", second);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(RecipeErrorCodes.VersionConflict, problem.RootElement.GetProperty("code").GetString());
        var read = (await client.GetFromJsonAsync<ExistingRecipe>($"{Path}/{created.RecipeId}"))!;
        Assert.Equal("First", read.Title);
        Assert.Equal(2, read.Version);
    }

    [Fact]
    public async Task Archive_IsAPutTransition_AndHidesTheRecipeFromTheDefaultList()
    {
        using var client = Factory.ClientWith(Editor);
        var created = await CreateAsync(client, Pancakes());
        var update = Update(created);
        update.Archived = true;

        var archived = (await (await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", update)).Content.ReadFromJsonAsync<ExistingRecipe>())!;

        Assert.NotNull(archived.Archived);
        Assert.Empty((await client.GetFromJsonAsync<PagedResultDto>(Path))!.Items);
        Assert.Single((await client.GetFromJsonAsync<PagedResultDto>($"{Path}?status=Archived"))!.Items);
    }

    [Fact]
    public async Task Delete_RemovesTheChildren_AndKeepsPhotosAndTags()
    {
        var photo = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId);
        var tag = await SeedTagAsync("Bread");
        using var client = Factory.ClientWith(EditorWithPhotos);
        var request = Pancakes();
        request.PhotoIds = [photo];
        request.TagIds = [tag];
        var created = await CreateAsync(client, request);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{created.RecipeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Path}/{created.RecipeId}")).StatusCode);

        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.RecipeIngredients.AnyAsync());
        Assert.False(await context.RecipeSteps.AnyAsync());
        Assert.False(await context.RecipePhotos.AnyAsync());
        Assert.False(await context.RecipeTagLinks.AnyAsync());
        Assert.True(await context.Photos.AnyAsync(p => p.PhotoId == photo));
        Assert.True(await context.RecipeTags.AnyAsync(t => t.RecipeTagId == tag));
    }

    // ── Authors (§7.3) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Read_CarriesAuthorNames_AndNoRawUserId()
    {
        await Factory.SeedActorUserAsync("cook@example.com", "Cook");
        using var client = Factory.ClientWith(Editor);
        var created = await CreateAsync(client, Pancakes());

        var json = await client.GetStringAsync($"{Path}/{created.RecipeId}");

        Assert.DoesNotContain(ActorUserId, json, StringComparison.Ordinal);
        Assert.DoesNotContain("ByUserId", json, StringComparison.Ordinal);
    }

    // ── List (§5 item 1) ────────────────────────────────────────────────────

    [Fact]
    public async Task List_FiltersByIngredientSearch_TagsAndTime_AndSortsNullTotalsLast()
    {
        var dinner = await SeedTagAsync("Dinner");
        var quick = await SeedTagAsync("Quick");
        using var client = Factory.ClientWith(Editor);

        var a = Pancakes();
        a.Title = "A";
        a.TagIds = [dinner, quick];
        await CreateAsync(client, a);
        var b = Pancakes();
        b.Title = "B";
        b.PrepMinutes = 60;
        b.TagIds = [dinner];
        b.Ingredients = [new() { Unit = IngredientUnit.None, Name = "potatoes" }];
        await CreateAsync(client, b);
        var c = Pancakes();
        c.Title = "C";
        c.PrepMinutes = null;
        c.CookMinutes = null;
        await CreateAsync(client, c);

        Assert.Equal(["B"], await Titles($"{Path}?search=potato"));
        Assert.Equal(["A"], await Titles($"{Path}?tagIds={dinner}&tagIds={quick}"));
        Assert.Equal(["A"], await Titles($"{Path}?maxTotalMinutes=30"));
        Assert.Equal(["A", "B", "C"], await Titles($"{Path}?sortBy=TotalMinutes&sortDir=Asc"));
        Assert.Equal(["B", "A", "C"], await Titles($"{Path}?sortBy=TotalMinutes&sortDir=Desc"));

        var tooMany = string.Join('&', Enumerable.Range(0, 51).Select(_ => $"tagIds={Guid.NewGuid()}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Path}?{tooMany}")).StatusCode);

        async Task<List<string>> Titles(string url) =>
            (await client.GetFromJsonAsync<PagedResultDto>(url))!.Items.Select(i => i.Title).ToList();
    }

    [Fact]
    public async Task List_CarriesCountsAndTheTagIconContract()
    {
        var tag = await SeedTagAsync("Zebra", icon: "restaurant");
        var other = await SeedTagAsync("Apple");
        using var client = Factory.ClientWith(Editor);
        var request = Pancakes();
        request.TagIds = [tag, other];
        await CreateAsync(client, request);

        var row = (await client.GetFromJsonAsync<PagedResultDto>(Path))!.Items.Single();

        Assert.Equal(3, row.IngredientCount);
        Assert.Equal(2, row.StepCount);
        Assert.Equal([other, tag], row.TagIds);
    }

    // ── Transport (§7.11) ───────────────────────────────────────────────────

    [Fact]
    public void BothWrites_CarryTheFourMegabyteLimit()
    {
        // The in-process host cannot enforce [RequestSizeLimit] (the WeatherApiTests note), so this reads the routed
        // endpoints' metadata the request-size middleware consults; GlobalExceptionHandler's 413 mapping is pinned there.
        var endpoints = Factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .SelectMany(source => source.Endpoints).ToList();

        foreach (var name in new[] { "PostRecipe", "PutRecipe" })
        {
            var endpoint = Assert.Single(endpoints,
                e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IRouteNameMetadata>()?.RouteName == name);
            var limit = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>();
            Assert.Equal(RecipeLimits.MaxRequestBytes, limit?.MaxRequestBodySize);
        }
    }

    // ── Coverage added for the round-1 test review ───────────────────────────

    [Fact]
    public async Task WithoutPhotosRead_ThePostAndPutResponsesCarryNoPhotos()
    {
        using var client = Factory.ClientWith(Editor);

        var created = await CreateAsync(client, Pancakes());
        Assert.Null(created.Photos);

        var put = await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", Update(created));
        var updated = (await put.Content.ReadFromJsonAsync<ExistingRecipe>())!;
        Assert.Null(updated.Photos);
    }

    [Fact]
    public async Task WithPhotosRead_TheCoverIsTheFirstPhoto_AndThumbnailReadinessComesFromTheStore()
    {
        var plain = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId, "plain.jpg");
        var ready = await SeedPhotoWithThumbnailsAsync();
        using var client = Factory.ClientWith(EditorWithPhotos);
        var request = Pancakes();
        request.PhotoIds = [ready, plain];
        var created = await CreateAsync(client, request);

        Assert.Equal([true, false], created.Photos!.Select(p => p.ThumbnailsReady));
        var row = (await client.GetFromJsonAsync<PagedResultDto>(Path))!.Items.Single();
        Assert.Equal(ready, row.CoverPhotoId);
        Assert.True(row.CoverThumbnailsReady);
    }

    [Fact]
    public async Task Reads_CarryTheTagIconContract_TagsInNameOrder_WithTheWinningIconAndColour()
    {
        var zebra = await SeedTagAsync("Zebra", icon: "restaurant", color: "#AA0000");
        var apple = await SeedTagAsync("Apple");
        using var client = Factory.ClientWith(Editor);
        var request = Pancakes();
        request.TagIds = [zebra, apple];
        var created = await CreateAsync(client, request);

        var read = (await client.GetFromJsonAsync<ExistingRecipe>($"{Path}/{created.RecipeId}"))!;
        var row = (await client.GetFromJsonAsync<PagedResultDto>(Path))!.Items.Single();

        foreach (var (tagIds, icon, color) in new[] { (read.TagIds, read.DisplayIcon, read.DisplayColor), (row.TagIds, row.DisplayIcon, row.DisplayColor) })
        {
            Assert.Equal([apple, zebra], tagIds);
            Assert.Equal("restaurant", icon);
            Assert.Equal("#AA0000", color);
        }
    }

    [Fact]
    public async Task WithoutTheClaim_APutAddingAnExistingOrAMissingId_GivesTheSameForbidden()
    {
        var photo = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId);
        var tag = await SeedTagAsync("Supper");
        using var editor = Factory.ClientWith(Editor);
        var created = await CreateAsync(editor, Pancakes());
        using var client = Factory.ClientWith(PermissionClaims.RecipesRead, PermissionClaims.RecipesUpdate);

        Assert.Equal(await BodyOf(r => r.PhotoIds = [photo]), await BodyOf(r => r.PhotoIds = [Guid.NewGuid()]));
        Assert.Equal(await BodyOf(r => r.TagIds = [tag]), await BodyOf(r => r.TagIds = [Guid.NewGuid()]));

        async Task<string> BodyOf(Action<UpdateRecipe> mutate)
        {
            var request = Update(created);
            mutate(request);
            var response = await client.PutAsJsonAsync($"{Path}/{created.RecipeId}", request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            return WithoutTraceId(await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Update_UnknownId_GivesTheSameNotFoundBody_WithOrWithoutTheLinkClaims()
    {
        var id = Guid.NewGuid();
        var request = new UpdateRecipe { Title = "x", Servings = 1, Version = 1, PhotoIds = [Guid.NewGuid()], TagIds = [Guid.NewGuid()] };
        using var bare = Factory.ClientWith(PermissionClaims.RecipesUpdate);
        using var full = Factory.ClientWith(EditorWithPhotos);

        var withoutClaims = WithoutTraceId(await (await bare.PutAsJsonAsync($"{Path}/{id}", request)).Content.ReadAsStringAsync());
        var withClaims = WithoutTraceId(await (await full.PutAsJsonAsync($"{Path}/{id}", request)).Content.ReadAsStringAsync());

        Assert.Equal(withClaims, withoutClaims);
    }

    [Fact]
    public async Task Reads_CarryTheAuthorsName_AndNoReadOrWriteCarriesARawUserId()
    {
        await Factory.SeedActorUserAsync("chef@example.com", "Chef");
        using var client = Factory.ClientWith(Editor);

        var post = await client.PostAsJsonAsync(Path, Pancakes());
        var postJson = await post.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<ExistingRecipe>(postJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var listJson = await client.GetStringAsync(Path);
        var read = (await client.GetFromJsonAsync<ExistingRecipe>($"{Path}/{created.RecipeId}"))!;

        Assert.Equal("Chef", created.CreatedByName);
        Assert.Equal("Chef", read.UpdatedByName);
        Assert.DoesNotContain(ActorUserId, postJson, StringComparison.Ordinal);
        Assert.DoesNotContain(ActorUserId, listJson, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> EndpointClaims => new()
    {
        { PermissionClaims.RecipesRead, "GET" },
        { PermissionClaims.RecipesCreate, "POST" },
        { PermissionClaims.RecipesUpdate, "PUT" },
        { PermissionClaims.RecipesDelete, "DELETE" },
    };

    /// <summary>Each endpoint needs its own claim: every other recipe claim together does not substitute for it.</summary>
    [Theory]
    [MemberData(nameof(EndpointClaims))]
    public async Task EachEndpoint_RequiresItsOwnClaim(string claim, string method)
    {
        using var editor = Factory.ClientWith(Editor);
        var created = await CreateAsync(editor, Pancakes());
        using var without = Factory.ClientWith(Editor.Where(c => c != claim).ToArray());
        using var with = Factory.ClientWith(claim, PermissionClaims.RecipeTagsRead);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(without)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Send(with)).StatusCode);

        Task<HttpResponseMessage> Send(HttpClient client) => method switch
        {
            "GET" => client.GetAsync(Path),
            "POST" => client.PostAsJsonAsync(Path, Pancakes()),
            "PUT" => client.PutAsJsonAsync($"{Path}/{created.RecipeId}", Update(created)),
            _ => client.DeleteAsync($"{Path}/{Guid.NewGuid()}"),
        };
    }

    /// <summary>§7.2's role table on the fast tier (the E2E matrix self-skips without a stack).</summary>
    [Fact]
    public void TheRoleTable_GivesEditorsEveryRecipeClaim_AndGuestTheTwoReads()
    {
        string[] all =
        [
            PermissionClaims.RecipesCreate, PermissionClaims.RecipesRead, PermissionClaims.RecipesUpdate, PermissionClaims.RecipesDelete,
            PermissionClaims.RecipeTagsCreate, PermissionClaims.RecipeTagsRead, PermissionClaims.RecipeTagsUpdate, PermissionClaims.RecipeTagsDelete,
        ];
        var roles = Odyssey.Context.Authorization.RolePermissions.RoleClaimMap.ToDictionary(r => r.RoleName, r => r.Claims);

        foreach (var role in new[] { "Admin", "Owner", "User" })
        {
            Assert.All(all, claim => Assert.Contains(claim, roles[role]));
        }

        Assert.Equal(
            [PermissionClaims.RecipesRead, PermissionClaims.RecipeTagsRead],
            roles["Guest"].Where(all.Contains).Order(StringComparer.Ordinal));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static NewRecipe Pancakes() => new()
    {
        Title = "Pancakes",
        Servings = 4,
        PrepMinutes = 10,
        CookMinutes = 15,
        Ingredients =
        [
            new() { Section = "Batter", Amount = 250, Unit = IngredientUnit.Gram, Name = "wheat flour" },
            new() { Amount = 0.5m, Unit = IngredientUnit.Litre, Name = "milk" },
            new() { Unit = IngredientUnit.ToTaste, Name = "salt" },
        ],
        Steps = [new() { Text = "Whisk." }, new() { Text = "Fry." }],
    };

    private static UpdateRecipe Update(ExistingRecipe recipe) => new()
    {
        Title = recipe.Title,
        Servings = recipe.Servings,
        PrepMinutes = recipe.PrepMinutes,
        CookMinutes = recipe.CookMinutes,
        Ingredients = recipe.Ingredients.Select(i => new RecipeIngredientRequest
        {
            Section = i.Section,
            Amount = i.Amount,
            Unit = i.Unit,
            Name = i.Name,
            Optional = i.Optional,
        }).ToArray(),
        Steps = recipe.Steps.Select(s => new RecipeStepRequest { Section = s.Section, Text = s.Text }).ToArray(),
        TagIds = [.. recipe.TagIds],
        Version = recipe.Version,
    };

    private static async Task<ExistingRecipe> CreateAsync(HttpClient client, NewRecipe request)
    {
        var response = await client.PostAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExistingRecipe>())!;
    }

    private static async Task<List<string>> ErrorKeys(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(p => p.Name).ToList()
            : [];
    }

    private async Task<Guid> SeedTagAsync(string name, bool archived = false, string? icon = null, string? color = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        var tag = new RecipeTag { RecipeTagId = Guid.NewGuid(), Name = name, Icon = icon, Color = color, Archived = archived ? DateTime.UtcNow : null };
        context.RecipeTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.RecipeTagId;
    }

    private async Task ArchiveTagAsync(Guid id)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        (await context.RecipeTags.SingleAsync(t => t.RecipeTagId == id)).Archived = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    private async Task<Guid> SeedFileAsync(
        string fileName, string contentType, string content, StoredFileArea area = StoredFileArea.Finance)
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
        var file = await StoredFileFixtures.SeedFileAsync(
            scope.ServiceProvider, Encoding.UTF8.GetBytes(content), fileName, contentType, area, uploadedByUserId: ActorUserId);
        return file.Id;
    }

    // The trace id differs per request; nothing else may.
    private static string WithoutTraceId(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", string.Empty);

    /// <summary>A library photo with both renditions stored, the <c>PhotoThumbnailApiTests</c> seeding.</summary>
    private async Task<Guid> SeedPhotoWithThumbnailsAsync()
    {
        byte[] webP = [.. "RIFF\0\0\0\0WEBPVP8 "u8, 1, 2, 3, 4];
        var photoId = await PhotoTestSupport.SeedPhotoAsync(Factory, ActorUserId, "ready.jpg");
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var store = scope.ServiceProvider.GetRequiredService<Odyssey.Core.FileStorage.IObjectStore>();
        foreach (var (size, bytes) in new[] { (Odyssey.Dtos.Journal.ThumbnailSize.Small, webP), (Odyssey.Dtos.Journal.ThumbnailSize.Medium, [.. webP, 2]) })
        {
            using var stream = new MemoryStream(bytes);
            var stored = await store.PutAsync(
                Odyssey.Core.Journal.Thumbnails.PhotoThumbnailStorage.FeatureKey,
                new Odyssey.Core.Journal.Thumbnails.PhotoThumbnailLayout(size, 1), "ready.jpg", null, "image/webp",
                stream, Odyssey.Core.Journal.Thumbnails.PhotoThumbnailRecipe.MaxOutputBytes, CancellationToken.None);
            context.PhotoThumbnails.Add(new PhotoThumbnail
            {
                PhotoThumbnailId = Guid.NewGuid(),
                PhotoId = photoId,
                Size = size,
                StoredObjectId = stored.Id,
                PixelWidth = 200,
                PixelHeight = 150,
                RecipeVersion = Odyssey.Core.Journal.Thumbnails.PhotoThumbnailRecipe.Version,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }

        await context.SaveChangesAsync();
        return photoId;
    }

    private sealed record PagedResultDto(List<RecipeSummary> Items, int TotalCount);

    /// <summary>The class's shared host: every caller's claims travel per request.</summary>
    public sealed class Host() : OdysseyApiFactory([], ActorUserId);
}
