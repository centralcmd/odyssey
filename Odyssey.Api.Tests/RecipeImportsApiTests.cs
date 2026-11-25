using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Recipes.Import;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;
using Odyssey.Testing.Fixtures;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The recipe-import contract over HTTP (issue #602 §5, §12) on the EF InMemory host. Under <c>Testing</c> the API runs
/// the queued import inline, against a stubbed <see cref="IRecipeExtractionProvider"/>.
/// </summary>
public class RecipeImportsApiTests(RecipeImportsApiTests.Host host) : SharedApiHostTests<RecipeImportsApiTests.Host>(host)
{
    private const string Actor = "recipe-import-actor";
    private const string Base = "/api/recipe-imports";

    private static readonly string[] Full =
    [
        PermissionClaims.RecipesImport, PermissionClaims.RecipesCreate, PermissionClaims.RecipesRead,
        PermissionClaims.FilesRead, PermissionClaims.FilesCreate, PermissionClaims.RecipeTagsRead, PermissionClaims.PhotosRead,
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Happy path (AC 1) ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Text_import_is_202_with_a_location_and_completes_with_one_pending_draft()
    {
        var factory = await ReadyAsync();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Base, await NewTextImportAsync(client));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var detail = await Read<RecipeImportDetail>(response);
        Assert.EndsWith($"{Base}/{detail.Summary.Id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(detail.Summary.Status == RecipeImportStatus.Completed, $"{detail.Summary.Status} {detail.Summary.FailureCode}");
        Assert.True(detail.DocumentSent);
        var draft = Assert.Single(detail.Drafts);
        Assert.Equal(RecipeImportDraftStatus.Pending, draft.Status);
        Assert.Equal("Cardamom buns", draft.Content!.Title);
        Assert.Contains(RecipeImportWarningCodes.ServingsMissing, draft.Warnings);
        Assert.Equal(1, Factory.Provider.Calls);
    }

    [Fact]
    public async Task Every_response_is_no_store_including_refusals_and_not_found()
    {
        var factory = await ReadyAsync();
        using var client = factory.CreateClient();
        using var outsider = factory.ClientFor([PermissionClaims.RecipesRead]);

        var ok = await client.GetAsync(Base);
        var missing = await client.GetAsync($"{Base}/{Guid.NewGuid()}");
        var forbidden = await outsider.GetAsync(Base);

        foreach (var response in new[] { ok, missing, forbidden })
        {
            Assert.True(response.Headers.CacheControl?.NoStore, $"{(int)response.StatusCode} lacks no-store");
        }

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    // ── Claims (AC 3, 4) ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", Base)]
    [InlineData("GET", Base + "/options")]
    [InlineData("GET", Base + "/library-files")]
    [InlineData("POST", Base)]
    public async Task Every_endpoint_is_403_without_recipes_import(string method, string path)
    {
        var factory = await ReadyAsync();
        using var client = factory.ClientFor([.. Full.Where(c => c != PermissionClaims.RecipesImport)]);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(new { clientRequestId = Guid.NewGuid() }) : null,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_without_recipes_create_is_403()
    {
        var factory = await ReadyAsync();
        using var client = factory.ClientFor([.. Full.Where(c => c != PermissionClaims.RecipesCreate)]);

        var response = await client.PostAsJsonAsync(Base, await NewTextImportAsync(client));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Existing_files_without_files_read_are_403_for_a_real_and_a_missing_id_alike()
    {
        var factory = await ReadyAsync();
        var file = await SeedFileAsync(factory, Encoding.UTF8.GetBytes("Buns"), "buns.txt", "text/plain", StoredFileArea.Library);
        using var client = factory.ClientFor([.. Full.Where(c => c != PermissionClaims.FilesRead)]);

        foreach (var id in new[] { file.Id, Guid.NewGuid() })
        {
            var body = await NewTextImportAsync(client, existing: [id]);
            var response = await client.PostAsJsonAsync(Base, body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_finance_or_journal_file_is_the_same_404_as_a_missing_one()
    {
        var factory = await ReadyAsync();
        var finance = await SeedFileAsync(factory, Encoding.UTF8.GetBytes("x"), "f.txt", "text/plain", StoredFileArea.Finance);
        var journal = await SeedFileAsync(factory, Encoding.UTF8.GetBytes("y"), "j.txt", "text/plain", StoredFileArea.Journal);
        using var client = factory.CreateClient();

        foreach (var id in new[] { finance.Id, journal.Id, Guid.NewGuid() })
        {
            var response = await client.PostAsJsonAsync(Base, await NewTextImportAsync(client, existing: [id]));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(0, Factory.Provider.Calls);
    }

    // ── §9 refusals (AC 8, 10) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Switched_off_is_503_recipe_import_disabled()
    {
        var factory = Factory.ForCaller(Full);
        await SeedRequesterAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Base, await NewTextImportAsync(client));

        await AssertProblem(response, HttpStatusCode.ServiceUnavailable, RecipeImportErrorCodes.Disabled);
    }

    [Fact]
    public async Task A_stale_disclosure_is_409_and_a_missing_consent_422()
    {
        var factory = await ReadyAsync();
        using var client = factory.CreateClient();

        var stale = await NewTextImportAsync(client);
        stale.DisclosureVersion = "not-the-version";
        await AssertProblem(await client.PostAsJsonAsync(Base, stale), HttpStatusCode.Conflict, RecipeImportErrorCodes.DisclosureChanged);

        var unconsented = await NewTextImportAsync(client);
        unconsented.ConsentAcknowledged = false;
        await AssertProblem(await client.PostAsJsonAsync(Base, unconsented), HttpStatusCode.UnprocessableEntity, RecipeImportErrorCodes.ConsentRequired);
        Assert.Equal(0, Factory.Provider.Calls);
    }

    [Fact]
    public async Task No_source_is_400()
    {
        var factory = await ReadyAsync();
        using var client = factory.CreateClient();
        var body = await NewTextImportAsync(client);
        body.Text = null;

        var response = await client.PostAsJsonAsync(Base, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_caller_without_tags_gets_a_different_disclosure_and_sends_no_tag_names()
    {
        var factory = await ReadyAsync();
        await SeedTagAsync(factory, "Baking");
        using var tagged = factory.CreateClient();
        using var untagged = factory.ClientFor([.. Full.Where(c => c != PermissionClaims.RecipeTagsRead)], actorUserId: Actor);

        var withTags = await tagged.GetFromJsonAsync<RecipeImportOptions>($"{Base}/options", Json);
        var withoutTags = await untagged.GetFromJsonAsync<RecipeImportOptions>($"{Base}/options", Json);

        Assert.True(withTags!.SendsTagNames);
        Assert.False(withoutTags!.SendsTagNames);
        Assert.NotEqual(withTags.DisclosureVersion, withoutTags.DisclosureVersion);

        var response = await untagged.PostAsJsonAsync(Base, await NewTextImportAsync(untagged));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(Factory.Provider.LastRequest!.Tags);
    }

    // ── Idempotency (AC 17) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_repeated_client_request_id_returns_the_existing_import_and_sends_nothing_more()
    {
        var factory = await ReadyAsync();
        using var client = factory.CreateClient();
        var body = await NewTextImportAsync(client);

        var first = await Read<RecipeImportDetail>(await client.PostAsJsonAsync(Base, body));
        body.Text = "A different recipe entirely";
        var repeat = await client.PostAsJsonAsync(Base, body);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(first.Summary.Id, (await Read<RecipeImportDetail>(repeat)).Summary.Id);
        Assert.Equal(1, Factory.Provider.Calls);
    }

    // ── Uploads (AC 2) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_upload_sends_one_block_per_source_in_order_and_stores_the_stripped_image()
    {
        var factory = await ReadyAsync();
        using var client = factory.CreateClient();
        var options = await client.GetFromJsonAsync<RecipeImportOptions>($"{Base}/options", Json);
        var jpeg = ContactImageFixtures.JpegWithExifAndComment();

        using var form = new MultipartFormDataContent
        {
            { new StringContent(Guid.NewGuid().ToString()), "clientRequestId" },
            { new StringContent("Mormor's buns"), "text" },
            { new StringContent("true"), "consentAcknowledged" },
            { new StringContent(options!.DisclosureVersion), "disclosureVersion" },
            { File(Encoding.UTF8.GetBytes("Filling: 150 g butter"), "text/plain"), "files", "filling.txt" },
            { File(jpeg, "image/jpeg"), "files", "card.jpg" },
        };
        var response = await client.PostAsync($"{Base}/uploads", form);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var sent = Factory.Provider.LastRequest!.Sources;
        Assert.Equal([RecipeImportSourceKind.Text, RecipeImportSourceKind.File, RecipeImportSourceKind.File], sent.Select(s => s.Kind));
        Assert.Equal("Mormor's buns", sent[0].Text);
        Assert.Equal("Filling: 150 g butter", sent[1].Text);
        Assert.True(sent[2].Bytes!.Length < jpeg.Length, "the image was sent unstripped");

        var detail = await Read<RecipeImportDetail>(response);
        var stored = detail.Sources.Single(s => s.File?.ContentType == "image/jpeg").File!;
        using var scope = factory.Services.CreateScope();
        var read = await scope.ServiceProvider.GetRequiredService<StoredFileContentSource>().ReadAllAsync(stored.FileId, 10_000_000, default);
        Assert.Equal(sent[2].Bytes, read!.Value.Bytes);
    }

    [Fact]
    public async Task An_unsupported_upload_is_415_and_leaves_nothing_behind()
    {
        var factory = await ReadyAsync();
        using var client = factory.CreateClient();
        var options = await client.GetFromJsonAsync<RecipeImportOptions>($"{Base}/options", Json);

        using var form = new MultipartFormDataContent
        {
            { new StringContent(Guid.NewGuid().ToString()), "clientRequestId" },
            { new StringContent("true"), "consentAcknowledged" },
            { new StringContent(options!.DisclosureVersion), "disclosureVersion" },
            { File(Encoding.UTF8.GetBytes("a,b"), "text/csv"), "files", "list.csv" },
        };
        var response = await client.PostAsync($"{Base}/uploads", form);

        await AssertProblem(response, HttpStatusCode.UnsupportedMediaType, RecipeImportErrorCodes.UnsupportedSourceType);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.RecipeImports.ToListAsync());
        Assert.Empty(await context.FileMetadata.ToListAsync());
    }

    [Fact]
    public async Task Uploads_without_files_create_are_403()
    {
        var factory = await ReadyAsync();
        using var client = factory.ClientFor([.. Full.Where(c => c != PermissionClaims.FilesCreate)]);

        using var form = new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "clientRequestId" } };
        var response = await client.PostAsync($"{Base}/uploads", form);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Draft edits (AC 7, 24) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_with_null_tags_by_a_caller_without_tags_keeps_them_and_an_array_is_403()
    {
        var factory = await ReadyAsync();
        var tag = await SeedTagAsync(factory, "Baking");
        Factory.Provider.TagRefs = ["t1"];
        using var client = factory.CreateClient();
        var detail = await Read<RecipeImportDetail>(await client.PostAsJsonAsync(Base, await NewTextImportAsync(client)));
        var draft = detail.Drafts[0];
        Assert.Equal([tag], draft.Content!.TagIds!);

        using var untagged = factory.ClientFor([.. Full.Where(c => c != PermissionClaims.RecipeTagsRead)], actorUserId: Actor);
        var read = await untagged.GetFromJsonAsync<RecipeImportDetail>($"{Base}/{detail.Summary.Id}", Json);
        Assert.Null(read!.Drafts[0].Content!.TagIds);

        var content = read.Drafts[0].Content! with { Title = "Cardamom buns, Mormor's" };
        var saved = await untagged.PutAsJsonAsync($"{Base}/{detail.Summary.Id}/drafts/{draft.Id}",
            new UpdateRecipeImportDraft { Content = content, Version = draft.Version }, Json);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var forbidden = await untagged.PutAsJsonAsync($"{Base}/{detail.Summary.Id}/drafts/{draft.Id}",
            new UpdateRecipeImportDraft { Content = content with { TagIds = [] }, Version = draft.Version + 1 }, Json);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var after = await client.GetFromJsonAsync<RecipeImportDetail>($"{Base}/{detail.Summary.Id}", Json);
        Assert.Equal([tag], after!.Drafts[0].Content!.TagIds!);
        Assert.Equal("Cardamom buns, Mormor's", after.Drafts[0].Content!.Title);
        Assert.Equal("Cardamom buns, Mormor's", after.Summary.DraftTitle);
    }

    [Fact]
    public async Task A_stale_version_on_put_and_accept_is_409_version_conflict()
    {
        var factory = await ReadyAsync();
        using var client = factory.CreateClient();
        var detail = await Read<RecipeImportDetail>(await client.PostAsJsonAsync(Base, await NewTextImportAsync(client)));
        var draft = detail.Drafts[0];

        var put = await client.PutAsJsonAsync($"{Base}/{detail.Summary.Id}/drafts/{draft.Id}",
            new UpdateRecipeImportDraft { Content = draft.Content, Version = draft.Version + 5 }, Json);
        await AssertProblem(put, HttpStatusCode.Conflict, RecipeImportErrorCodes.VersionConflict);

        var accept = await client.PostAsJsonAsync($"{Base}/{detail.Summary.Id}/drafts/{draft.Id}/accept",
            new AcceptRecipeImportDraft { Recipe = Recipe(), Version = draft.Version + 5 }, Json);
        await AssertProblem(accept, HttpStatusCode.Conflict, RecipeImportErrorCodes.VersionConflict);
    }

    // ── Accept, discard (AC 5 InMemory half) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_creates_the_recipe_with_the_source_files_attached_and_a_second_accept_is_409()
    {
        var factory = await ReadyAsync();
        var file = await SeedFileAsync(factory, Encoding.UTF8.GetBytes("Buns, 500 g flour"), "buns.txt", "text/plain", StoredFileArea.Library);
        using var client = factory.CreateClient();
        var detail = await Read<RecipeImportDetail>(await client.PostAsJsonAsync(Base, await NewTextImportAsync(client, existing: [file.Id])));
        var draft = detail.Drafts[0];

        var accept = await client.PostAsJsonAsync($"{Base}/{detail.Summary.Id}/drafts/{draft.Id}/accept",
            new AcceptRecipeImportDraft { Recipe = Recipe(), AttachSourceFiles = true, Version = draft.Version }, Json);

        Assert.Equal(HttpStatusCode.Created, accept.StatusCode);
        var result = await Read<AcceptRecipeImportDraftResult>(accept);
        Assert.Equal(1, result.AttachedFileCount);
        Assert.EndsWith($"/api/recipes/{result.RecipeId}", accept.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var recipe = await context.Recipes.Include(r => r.Attachments).Include(r => r.Photos).SingleAsync(r => r.RecipeId == result.RecipeId);
            Assert.Equal([file.Id], recipe.Attachments.Select(a => a.FileId));
            Assert.Empty(recipe.Photos);
        }

        var again = await client.PostAsJsonAsync($"{Base}/{detail.Summary.Id}/drafts/{draft.Id}/accept",
            new AcceptRecipeImportDraft { Recipe = Recipe(), Version = draft.Version + 1 }, Json);
        await AssertProblem(again, HttpStatusCode.Conflict, RecipeImportErrorCodes.DraftNotPending);
        var list = await client.GetFromJsonAsync<PagedResult<RecipeImportSummary>>(Base, Json);
        Assert.Equal(result.RecipeId, list!.Items.Single().AcceptedRecipeId);
    }

    [Fact]
    public async Task Discard_keeps_the_import_and_creates_nothing()
    {
        var factory = await ReadyAsync();
        using var client = factory.CreateClient();
        var detail = await Read<RecipeImportDetail>(await client.PostAsJsonAsync(Base, await NewTextImportAsync(client)));
        var draft = detail.Drafts[0];

        var discard = await client.PostAsync($"{Base}/{detail.Summary.Id}/drafts/{draft.Id}/discard", null);

        Assert.Equal(HttpStatusCode.NoContent, discard.StatusCode);
        var after = await client.GetFromJsonAsync<RecipeImportDetail>($"{Base}/{detail.Summary.Id}", Json);
        Assert.Equal(RecipeImportDraftStatus.Discarded, after!.Drafts[0].Status);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Recipes.ToListAsync());
    }

    // ── Cancel, delete (AC 13, 15) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_queued_import_cancels_and_a_completed_one_is_409_already_sent()
    {
        var factory = await ReadyAsync();
        var queued = await SeedQueuedAsync(factory);
        using var client = factory.CreateClient();
        var completed = await Read<RecipeImportDetail>(await client.PostAsJsonAsync(Base, await NewTextImportAsync(client)));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"{Base}/{queued}/cancel", null)).StatusCode);
        await AssertProblem(await client.PostAsync($"{Base}/{completed.Summary.Id}/cancel", null),
            HttpStatusCode.Conflict, RecipeImportErrorCodes.AlreadySent);
        await AssertProblem(await client.DeleteAsync($"{Base}/{await SeedQueuedAsync(factory)}"),
            HttpStatusCode.Conflict, RecipeImportErrorCodes.InProgress);
    }

    [Fact]
    public async Task Delete_leaves_a_tombstone_releases_an_owned_file_and_keeps_a_chosen_one()
    {
        var factory = await ReadyAsync();
        var chosen = await SeedFileAsync(factory, Encoding.UTF8.GetBytes("Chosen"), "chosen.txt", "text/plain", StoredFileArea.Library);
        using var client = factory.CreateClient();
        var options = await client.GetFromJsonAsync<RecipeImportOptions>($"{Base}/options", Json);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Guid.NewGuid().ToString()), "clientRequestId" },
            { new StringContent("true"), "consentAcknowledged" },
            { new StringContent(options!.DisclosureVersion), "disclosureVersion" },
            { new StringContent(chosen.Id.ToString()), "existingFileIds" },
            { File(Encoding.UTF8.GetBytes("Uploaded"), "text/plain"), "files", "uploaded.txt" },
        };
        var detail = await Read<RecipeImportDetail>(await client.PostAsync($"{Base}/uploads", form));
        var owned = detail.Sources.Single(s => s.File?.FileName == "uploaded.txt").File!.FileId;

        var delete = await client.DeleteAsync($"{Base}/{detail.Summary.Id}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/{detail.Summary.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<PagedResult<RecipeImportSummary>>($"{Base}?includePurged=true", Json))!.Items);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await context.RecipeImports.Include(i => i.Sources).SingleAsync();
        Assert.Equal(Actor, row.DeletedByUserId);
        Assert.NotNull(row.ContentPurgedAt);
        Assert.All(row.Sources, s => Assert.Null(s.FileId));
        Assert.False(await context.FileMetadata.AnyAsync(f => f.Id == owned));
        Assert.True(await context.FileMetadata.AnyAsync(f => f.Id == chosen.Id));
    }

    // ── The file picker (AC 25) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Library_files_lists_only_library_files_of_allowed_types_as_a_five_field_projection()
    {
        var factory = await ReadyAsync();
        var library = await SeedFileAsync(factory, Encoding.UTF8.GetBytes("Buns"), "buns.txt", "text/plain", StoredFileArea.Library);
        await SeedFileAsync(factory, Encoding.UTF8.GetBytes("Rent"), "rent.txt", "text/plain", StoredFileArea.Finance);
        await SeedFileAsync(factory, Encoding.UTF8.GetBytes("Diary"), "diary.txt", "text/plain", StoredFileArea.Journal);
        using var client = factory.CreateClient();
        using var withoutFiles = factory.ClientFor([.. Full.Where(c => c != PermissionClaims.FilesRead)]);

        var response = await client.GetAsync($"{Base}/library-files");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal(library.Id, item.GetProperty("fileId").GetGuid());
        Assert.Equal(
            ["contentType", "fileId", "fileName", "sizeBytes", "uploadedAt"],
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, (await withoutFiles.GetAsync($"{Base}/library-files")).StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task<Host> ReadyAsync()
    {
        var factory = Factory.ForCaller(Full);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.RecipeImportEnabled, "true");
        await SeedRequesterAsync(factory);
        return (Host)factory;
    }

    /// <summary>The worker re-reads the requester's claims from the database (S7), so the actor exists there with them.</summary>
    private static async Task SeedRequesterAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        if (!await context.Users.AnyAsync(u => u.Id == Actor))
        {
            context.Users.Add(new ApplicationUser { Id = Actor, UserName = "cook@example.com", NormalizedUserName = "COOK@EXAMPLE.COM" });
        }

        foreach (var claim in Full)
        {
            context.UserClaims.Add(new IdentityUserClaim<string> { UserId = Actor, ClaimType = PermissionClaims.Type, ClaimValue = claim });
        }

        await context.SaveChangesAsync();

        // A new account starts disabled under admin approval; this one is an enabled member of the household.
        var user = await context.Users.SingleAsync(u => u.Id == Actor);
        user.LockoutEnd = null;
        await context.SaveChangesAsync();
    }

    private static async Task<CreateRecipeImport> NewTextImportAsync(HttpClient client, Guid[]? existing = null)
    {
        var options = await client.GetFromJsonAsync<RecipeImportOptions>($"{Base}/options", Json);
        return new CreateRecipeImport
        {
            ClientRequestId = Guid.NewGuid(),
            Text = "Cardamom buns: 500 g flour, 3 dl milk. Knead and bake.",
            ExistingFileIds = existing ?? [],
            ConsentAcknowledged = true,
            DisclosureVersion = options?.DisclosureVersion,
        };
    }

    private static async Task<FileMetadata> SeedFileAsync(OdysseyApiFactory factory, byte[] bytes, string name, string type, StoredFileArea area)
    {
        using var scope = factory.Services.CreateScope();
        return await StoredFileFixtures.SeedFileAsync(scope.ServiceProvider, bytes, name, type, area);
    }

    private static async Task<Guid> SeedTagAsync(OdysseyApiFactory factory, string name)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var tag = new RecipeTag { RecipeTagId = Guid.NewGuid(), Name = name };
        context.RecipeTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.RecipeTagId;
    }

    private static async Task<Guid> SeedQueuedAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var import = new RecipeImport
        {
            RecipeImportId = Guid.NewGuid(),
            Status = RecipeImportStatus.Queued,
            ClientRequestId = Guid.NewGuid(),
            RequestedByUserId = Actor,
            QueuedAt = DateTime.UtcNow,
            Generation = 1,
        };
        import.Sources.Add(new RecipeImportSource { Ordinal = 0, Kind = RecipeImportSourceKind.Text, Text = "Soup" });
        context.RecipeImports.Add(import);
        await context.SaveChangesAsync();
        return import.RecipeImportId;
    }

    private static NewRecipe Recipe() => new()
    {
        Title = "Cardamom buns",
        Servings = 12,
        Ingredients = [new RecipeIngredientRequest { Name = "wheat flour", Amount = 500, Unit = IngredientUnit.Gram }],
        Steps = [new RecipeStepRequest { Text = "Knead and bake." }],
    };

    private static ByteArrayContent File(byte[] bytes, string type)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return content;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"expected {(int)status}, got {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    /// <summary>A provider that returns one fixed recipe and records what it was sent.</summary>
    public sealed class StubProvider : IRecipeExtractionProvider
    {
        public int Calls { get; private set; }

        public RecipeExtractionRequest? LastRequest { get; private set; }

        public IReadOnlyList<string> TagRefs { get; set; } = [];

        public void Reset()
        {
            Calls = 0;
            LastRequest = null;
            TagRefs = [];
        }

        public Task<RecipeExtractionResult> ExtractAsync(RecipeExtractionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(new RecipeExtractionResult(RecipeExtractionOutcome.Extracted,
            [
                new ExtractedRecipe("Cardamom buns", null, null, null, null, "buns", 40, 12,
                    [new ExtractedIngredient("Dough", 500, null, IngredientUnit.Gram, null, "wheat flour", false)],
                    [new ExtractedStep(null, "Knead and bake.")],
                    TagRefs, []),
            ]));
        }
    }

    public sealed class Host() : OdysseyApiFactory([], actorUserId: Actor, configureServices: services =>
    {
        services.RemoveAll<IRecipeExtractionProvider>();
        services.AddSingleton<IRecipeExtractionProvider>(SharedProvider);
        services.RemoveAll<ISecretSettingsReader>();
        services.AddSingleton<ISecretSettingsReader>(new StubSecretSettingsReader().Found(SecretSettingKeys.FileAnalysisApiKey, "test-key"));
    })
    {
        private static readonly StubProvider SharedProvider = new();

        public StubProvider Provider => SharedProvider;

        public override async Task ResetStoreAsync()
        {
            SharedProvider.Reset();
            await base.ResetStoreAsync();
        }
    }
}
