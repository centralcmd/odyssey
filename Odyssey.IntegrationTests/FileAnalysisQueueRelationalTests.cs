using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextJobStatus = Odyssey.Context.FileAnalysisJobStatus;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The file-analysis queue's races on real MariaDB (issue #444 AC 10/11). EF InMemory has no transactions and
/// applies a failed batch partially, so the two properties pinned here are only observable on a real engine:
/// two replicas claiming at once each get a different job, and a stale sweep that fails a row while its
/// provider call is in flight leaves the run's candidate rows rolled back with the lost job update.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class FileAnalysisQueueRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_file_analysis_queue";
    private const string UserId = "queue-requester";

    private static readonly FileAnalysisSettings Settings = new(
        "Anthropic", "United States", "Consent · GDPR Art. 6(1)(a)",
        "https://www.anthropic.com/legal/privacy", 90, 0.60m,
        MaxTokens: 8096, MatchTimeoutSeconds: 60,
        Model: "claude-sonnet-5", BaseUrl: "https://api.anthropic.com", IsDegraded: false);

    private readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);

    [SkippableFact]
    public async Task Two_replicas_claiming_at_once_each_get_a_different_job()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid first, second;
        await using (var context = NewContext())
        {
            await SeedRequesterAsync(context);
            first = await EnqueueAsync(context, await SeedStatementAsync(context));
            second = await EnqueueAsync(context, await SeedStatementAsync(context));
        }

        await using var a = NewContext();
        await using var b = NewContext();
        var claims = await Task.WhenAll(
            Queue(a).ClaimNextAsync(CancellationToken.None),
            Queue(b).ClaimNextAsync(CancellationToken.None));

        Assert.All(claims, claim => Assert.NotNull(claim));
        Assert.Equal(new[] { first, second }.Order(), claims.Select(c => c!.Value).Order());

        await using var verify = NewContext();
        var rows = await verify.FileAnalysisJobs.AsNoTracking().ToListAsync();
        Assert.All(rows, row => Assert.Equal(ContextJobStatus.Running, row.Status));
        Assert.Equal(2, rows.Select(row => row.RunId).Distinct().Count());
    }

    [SkippableFact]
    public async Task A_sweep_that_fails_the_row_mid_call_wins_and_no_candidate_survives()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid jobId;
        await using (var context = NewContext())
        {
            await SeedRequesterAsync(context);
            jobId = await EnqueueAsync(context, await SeedStatementAsync(context));
        }

        var provider = new SweepingProvider(() =>
        {
            // Another replica's sweep, an hour later (past the lease), from its own connection.
            using var other = NewContext();
            var later = new FakeTimeProvider(time.GetUtcNow() + TimeSpan.FromHours(1));
            Queue(other, later).SweepAsync().GetAwaiter().GetResult();
        });

        await using (var worker = NewContext())
        {
            Assert.True(await Queue(worker).TryClaimAsync(jobId, CancellationToken.None));
            var result = await Runner(worker, provider).RunAsync(jobId, CancellationToken.None);
            Assert.Equal(FileAnalysisRunResult.Lost, result);
        }

        Assert.True(provider.Called);
        await using var verify = NewContext();
        var job = await verify.FileAnalysisJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ContextJobStatus.Failed, job.Status);
        Assert.Equal(FileAnalysisCodes.Interrupted, job.FailureCode);
        Assert.Equal(0, await verify.FileAnalysisCandidateTransactions.CountAsync());
    }

    private FileAnalysisJobQueue Queue(OdysseyContext context, TimeProvider? clock = null) =>
        new(context, Options.Create(new FileAnalysisOptions()), clock ?? time, NullLogger<FileAnalysisJobQueue>.Instance);

    private FileAnalysisExtractionRunner Runner(OdysseyContext context, IFileAnalysisProvider provider) =>
        new(context, provider, new StaticSettingsLookup(), new EmbeddedFileAnalysisPromptTemplate(), new KeyStored(),
            time, NullLogger<FileAnalysisExtractionRunner>.Instance);

    private async Task<Guid> EnqueueAsync(OdysseyContext context, (Guid AccountId, Guid FileId) statement)
    {
        var service = new FileAnalysisService(
            context, new SweepingProvider(() => { }), new ContactLookup(context),
            Options.Create(new FileAnalysisOptions { Enabled = true }), new StaticSettingsLookup(),
            NullLogger<FileAnalysisService>.Instance, Queue(context), new EmbeddedFileAnalysisPromptTemplate(), time);
        var response = await service.AnalyzeAsync(statement.AccountId, statement.FileId, UserId, new AnalyzeFileRequest
        {
            ConsentAcknowledged = true,
            ConsentText = "I consent to sending the complete file.",
            ConsentMethod = "Per-document checkbox",
            DisclosureVersion = FileAnalysisDisclosureVersion.Compute(Settings),
        });
        time.Advance(TimeSpan.FromSeconds(1));
        return response.AnalysisJobId;
    }

    private static async Task SeedRequesterAsync(OdysseyContext context)
    {
        await AttributionUsers.EnsureAsync(context, UserId);
        var roleId = Guid.NewGuid().ToString();
        context.Roles.Add(new IdentityRole { Id = roleId, Name = "queue-role", NormalizedName = "QUEUE-ROLE" });
        context.UserRoles.Add(new IdentityUserRole<string> { UserId = UserId, RoleId = roleId });
        context.RoleClaims.Add(new IdentityRoleClaim<string>
        {
            RoleId = roleId, ClaimType = PermissionClaims.Type, ClaimValue = PermissionClaims.FileAnalysisCreate,
        });
        await context.SaveChangesAsync();

        // A new user is saved locked out pending admin approval; approve this one.
        var user = await context.Users.SingleAsync(u => u.Id == UserId);
        user.LockoutEnd = null;
        await context.SaveChangesAsync();
    }

    private static async Task<(Guid AccountId, Guid FileId)> SeedStatementAsync(OdysseyContext context)
    {
        var accountId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        context.Accounts.Add(new Account
        {
            AccountId = accountId,
            Name = "Checking",
            Description = "Seed",
            Opened = DateTime.UtcNow,
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
        });
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(new FileMetadata
        {
            Id = fileId,
            UploadedByUserId = UserId,
            FileName = "statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = blob.Content.Length,
            Sha256Hash = Convert.ToHexStringLower(SHA256.HashData(blob.Content)),
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = blob.Id,
        });
        context.AccountFiles.Add(new AccountFile
        {
            AccountId = accountId,
            FileMetadataId = fileId,
            AttachedByUserId = UserId,
            AttachedAtUtc = DateTime.UtcNow,
            FileType = Odyssey.Context.AccountFileType.Statement,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return (accountId, fileId);
    }

    private async Task MigrateAsync()
    {
        await using (var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options))
        {
            await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
            await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private OdysseyContext NewContext() => new(new DbContextOptionsBuilder<OdysseyContext>()
        .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
        .Options);

    /// <summary>Returns two transactions; <paramref name="onCall"/> runs inside the call — the race.</summary>
    private sealed class SweepingProvider(Action onCall) : IFileAnalysisProvider
    {
        public bool Called { get; private set; }

        public Task<List<ExtractedTransaction>> ExtractTransactionsAsync(
            byte[] fileContent, string contentType, string accountCurrencyCode, string promptTemplate,
            FileAnalysisTarget target, int maxTokens, string? promptAddition, CancellationToken cancellationToken = default)
        {
            Called = true;
            onCall();
            return Task.FromResult(new List<ExtractedTransaction>
            {
                new(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), null, "Coffee", null, null, -4.50m, "USD",
                    null, null, null, null, null, null),
                new(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), null, "Bread", null, null, -3.20m, "USD",
                    null, null, null, null, null, null),
            });
        }

        public Task<List<MatchedCandidate>> MatchTransactionsAsync(
            IReadOnlyList<MatchCandidateInput> candidates, IReadOnlyList<VocabularyEntry> contacts,
            IReadOnlyList<VocabularyEntry> tags, FileAnalysisTarget target, int maxTokens, MatchPromptOptions prompt,
            CancellationToken cancellationToken = default) => Task.FromResult(new List<MatchedCandidate>());
    }

    private sealed class StaticSettingsLookup : IFileAnalysisSettingsLookup
    {
        public Task<FileAnalysisSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

        public Task<FileAnalysisSettings> GetUncachedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class KeyStored : ISecretSettingsReader
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(SecretResult.Found("test-api-key"));
    }
}
