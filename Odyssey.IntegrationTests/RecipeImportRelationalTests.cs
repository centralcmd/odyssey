using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Ai;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Recipes;
using Odyssey.Core.Recipes.Import;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of AI recipe import (issue #602 AC 6, 16, 17, 19): the races the EF InMemory tier cannot run —
/// the open-imports cap under parallel creates, two accepts of one draft, two creates with one idempotency key — and
/// the declared on-delete rules.
/// </summary>
[Collection(MariaDbSharedDatabaseCollection.Name)]
public class RecipeImportRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_recipe_imports";
    private const string Requester = "importer";

    private static readonly AiProcessorSettings Settings = new(
        "Anthropic", "United States", "Consent", "https://example.com/privacy", "claude-sonnet-5", "https://api.anthropic.com",
        4096, IsDegraded: false, ReadFailed: false);

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));

    [SkippableFact]
    public async Task The_foreign_keys_carry_the_declared_on_delete_rules()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        await using var context = NewContext();

        var rules = await context.Database.SqlQuery<ForeignKeyRule>($"""
                SELECT CONCAT(k.TABLE_NAME, '.', k.COLUMN_NAME) AS ColumnName, r.DELETE_RULE AS DeleteRule
                FROM information_schema.KEY_COLUMN_USAGE k
                JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                  ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                WHERE k.CONSTRAINT_SCHEMA = DATABASE() AND k.TABLE_NAME LIKE 'RecipeImport%'
                """)
            .ToDictionaryAsync(row => row.ColumnName, row => row.DeleteRule);

        Assert.Equal("CASCADE", rules["RecipeImportSources.RecipeImportId"]);
        Assert.Equal("SET NULL", rules["RecipeImportSources.FileId"]);
        Assert.Equal("CASCADE", rules["RecipeImportDrafts.RecipeImportId"]);
        Assert.Equal("SET NULL", rules["RecipeImportDrafts.AcceptedRecipeId"]);
        foreach (var column in new[] { "RecipeImports.RequestedByUserId", "RecipeImports.CancelledByUserId", "RecipeImports.DeletedByUserId",
                     "RecipeImportDrafts.UpdatedByUserId", "RecipeImportDrafts.AcceptedByUserId", "RecipeImportDrafts.DiscardedByUserId" })
        {
            Assert.Equal("SET NULL", rules[column]);
        }
    }

    [SkippableFact]
    public async Task Parallel_creates_past_the_cap_leave_exactly_five_open_and_the_rest_409()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        await PrepareAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = NewContext();
            try
            {
                await Service(context).CreateAsync(Create(Guid.NewGuid()), Caller(), long.MaxValue, NoNames, CancellationToken.None);
                return HttpStatusCode.Accepted;
            }
            catch (RecipeImportException ex)
            {
                return (HttpStatusCode)ex.StatusCode;
            }
        }));

        await using var verify = NewContext();
        Assert.Equal(RecipeImportLimits.MaxOpenImportsPerUser,
            await verify.RecipeImports.CountAsync(i => i.RequestedByUserId == Requester && i.Status == RecipeImportStatus.Queued));
        Assert.Equal(RecipeImportLimits.MaxOpenImportsPerUser, outcomes.Count(o => o == HttpStatusCode.Accepted));
        Assert.All(outcomes.Where(o => o != HttpStatusCode.Accepted), o => Assert.Equal(HttpStatusCode.Conflict, o));
    }

    [SkippableFact]
    public async Task Two_creates_with_one_key_produce_one_import_and_both_answer_with_it()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        await PrepareAsync();
        var key = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var context = NewContext();
            return await Service(context).CreateAsync(Create(key), Caller(), long.MaxValue, NoNames, CancellationToken.None);
        }));

        await using var verify = NewContext();
        var only = await verify.RecipeImports.SingleAsync(i => i.ClientRequestId == key);
        Assert.All(results, r => Assert.Equal(only.RecipeImportId, r.Import.Summary.Id));
    }

    [SkippableFact]
    public async Task Two_parallel_accepts_of_one_draft_create_exactly_one_recipe()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        await PrepareAsync();
        var (importId, draftId) = await SeedCompletedAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var context = NewContext();
            try
            {
                await Service(context).AcceptAsync(importId, draftId, new AcceptRecipeImportDraft
                {
                    Recipe = new NewRecipe
                    {
                        Title = "Raced buns",
                        Servings = 12,
                        Ingredients = [new RecipeIngredientRequest { Name = "flour", Amount = 500, Unit = IngredientUnit.Gram }],
                        Steps = [new RecipeStepRequest { Text = "Bake." }],
                    },
                    Version = 1,
                }, Caller(), CancellationToken.None);
                return "created";
            }
            catch (RecipeImportException ex)
            {
                return ex.Code!;
            }
        }));

        await using var verify = NewContext();
        Assert.Equal(1, await verify.Recipes.CountAsync(r => r.Title == "Raced buns"));
        Assert.Single(outcomes, o => o == "created");
        Assert.Single(outcomes, o => o is RecipeImportErrorCodes.VersionConflict or RecipeImportErrorCodes.DraftNotPending);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static readonly RecipeImportNames NoNames = (ids, _) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(ids.OfType<string>().Distinct().ToDictionary(id => id, id => id));

    private static RecipeImportCaller Caller() => new(Requester, CanReadFiles: true, CanReadTags: false, CanReadPhotos: false);

    private static RecipeImportCreate Create(Guid key) => new(
        key, "Buns: 500 g flour", [], [], ConsentAcknowledged: true, RecipeImportDisclosure.Compute(Settings, sendsTagNames: false));

    /// <summary>Clears this database's imports and recipes, seeds the requester and switches recipe import on.</summary>
    private async Task PrepareAsync()
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM `RecipeImports`");
        await context.Database.ExecuteSqlRawAsync("DELETE FROM `Recipes`");
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE `SystemSettings` SET `Value` = 'true' WHERE `Key` = {0}", SystemSettingsKeys.RecipeImportEnabled);
        if (!await context.Users.AnyAsync(u => u.Id == Requester))
        {
            context.Users.Add(new ApplicationUser { Id = Requester, UserName = "importer", NormalizedUserName = "IMPORTER" });
            await context.SaveChangesAsync();
        }
    }

    private async Task<(Guid ImportId, Guid DraftId)> SeedCompletedAsync()
    {
        await using var context = NewContext();
        var import = new RecipeImport
        {
            RecipeImportId = Guid.NewGuid(),
            Status = RecipeImportStatus.Completed,
            ClientRequestId = Guid.NewGuid(),
            RequestedByUserId = Requester,
            QueuedAt = time.GetUtcNow().UtcDateTime,
            FinishedAt = time.GetUtcNow().UtcDateTime,
            Generation = 3,
        };
        var draft = new RecipeImportDraft
        {
            RecipeImportDraftId = Guid.NewGuid(),
            Ordinal = 0,
            Status = RecipeImportDraftStatus.Pending,
            Title = "Buns",
            DraftJson = """{"schemaVersion":1,"title":"Buns","ingredients":[],"steps":[],"tagIds":[]}""",
            Warnings = "[]",
            Version = 1,
        };
        import.Drafts.Add(draft);
        context.RecipeImports.Add(import);
        await context.SaveChangesAsync();
        return (import.RecipeImportId, draft.RecipeImportDraftId);
    }

    private RecipeImportService Service(OdysseyContext context) => new(
        context,
        new FixedProcessorSettings(),
        new RecipeImportSettingsLookup(context, NullLogger<RecipeImportSettingsLookup>.Instance),
        new KeyFound(),
        new FixedUploadLimits(64L * 1024 * 1024),
        new FileValidationService(),
        new RecipeService(context, new PhotoLookup(context), NullLogger<RecipeService>.Instance),
        new RecipeImportQueue(context, new AiTransportOptions(120), time, NullLogger<RecipeImportQueue>.Instance),
        new StoredFileContentSource(context, null),
        time,
        NullLogger<RecipeImportService>.Instance);

    private OdysseyContext NewContext() => new(new DbContextOptionsBuilder<OdysseyContext>()
        .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString),
            mysql => mysql.EnableRetryOnFailure(5))
        .Options);

    private sealed class FixedProcessorSettings : IAiProcessorSettingsLookup
    {
        public Task<AiProcessorSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

        public Task<AiProcessorSettings> GetUncachedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
    }

    private sealed class KeyFound : ISecretSettingsReader
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(SecretResult.Found("k"));
    }

    private sealed record ForeignKeyRule(string ColumnName, string DeleteRule);
}
