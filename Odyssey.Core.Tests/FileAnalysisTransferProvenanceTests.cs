using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

/// <summary>
/// What a completed transfer records, and what it refuses to do (issue #439 §5.3c, §6, §11).
///
/// <para>
/// The four stamps written at job creation — model, destination host, processor and region — all come
/// from <strong>one snapshot read</strong>, which is what lets them describe a single coherent moment
/// rather than a mixture. The model and the host were the point of the exercise; the processor and the
/// region joined them because the processor survived only <em>incidentally</em> (interpolated into the
/// composed consent sentence) and the region — the fact that decides whether a transfer fell under GDPR
/// Art. 44-49 — survived nowhere at all, despite being admin-editable since issue #421.
/// </para>
/// </summary>
public class FileAnalysisTransferProvenanceTests
{
    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();
    private readonly FakeFileAnalysisSettingsLookup settingsLookup = new();

    // ── AC 29-31 — the target the request was BUILT with is the value stamped ────────────────────

    [Fact]
    public async Task TheModelInForce_IsSentToTheProvider_AndStampedOnTheJob()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        settingsLookup.Settings = settingsLookup.Settings with { Model = "claude-opus-5" };
        var provider = FakeProvider.Returning(Extracted());
        var service = CreateService(context, provider);

        await service.AnalyzeAsync(accountId, fileId, "user-1", Consent());

        Assert.Equal("claude-opus-5", provider.LastTarget!.Model);
        var job = await context.FileAnalysisJobs.SingleAsync();
        Assert.Equal("claude-opus-5", job.AnalyzerModel);
    }

    /// <summary>
    /// AC 30 — the destination reaches the provider whole, and the job records its <strong>host</strong>
    /// only. Host only because a gateway URL is exactly where a credential would be, and because the
    /// audit surface is read by people, not by the request builder.
    /// </summary>
    [Fact]
    public async Task TheBaseUrlInForce_IsSentToTheProvider_AndStampedAsItsHost()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        settingsLookup.Settings = settingsLookup.Settings with { BaseUrl = "https://gateway.internal" };
        var provider = FakeProvider.Returning(Extracted());
        var service = CreateService(context, provider);

        await service.AnalyzeAsync(accountId, fileId, "user-1", Consent());

        Assert.Equal("https://gateway.internal", provider.LastTarget!.BaseUrl);

        var job = await context.FileAnalysisJobs.SingleAsync();
        Assert.Equal("gateway.internal", job.AnalyzerBaseUrlHost);
        Assert.DoesNotContain("https", job.AnalyzerBaseUrlHost!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/", job.AnalyzerBaseUrlHost!, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 31 — a mid-flight settings change does not reach a run already in progress. The snapshot is
    /// read once per run, before the job row exists; the provider callback mutates the lookup while the
    /// transfer is happening, which is the only way to observe a second read if one were introduced.
    /// </summary>
    [Fact]
    public async Task ChangingTheModelMidFlight_DoesNotChangeWhatThisRunStamped()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        settingsLookup.Settings = settingsLookup.Settings with { Model = "claude-sonnet-5" };

        var provider = new FakeProvider((_, _, _, _, _) =>
        {
            // A concurrent administrator write, landing between the snapshot and the stamp.
            settingsLookup.Settings = settingsLookup.Settings with { Model = "claude-opus-5" };
            return Task.FromResult(new List<ExtractedTransaction> { Extracted() });
        });
        var service = CreateService(context, provider);

        await service.AnalyzeAsync(accountId, fileId, "user-1", Consent());

        Assert.Equal("claude-sonnet-5", provider.LastTarget!.Model);
        var job = await context.FileAnalysisJobs.SingleAsync();
        Assert.Equal("claude-sonnet-5", job.AnalyzerModel);
    }

    // ── AC 33-34 — the disclosure in force ───────────────────────────────────────────────────────

    /// <summary>
    /// AC 33 — a later change to the setting leaves a recorded transfer alone. The region is the one
    /// that matters most: it is the answer to "was this a third-country transfer?", and answering it
    /// from today's settings rather than from the record would be answering a different question.
    /// </summary>
    [Fact]
    public async Task TheProcessorAndRegionInForce_AreRecorded_AndUnaffectedByALaterChange()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        settingsLookup.Settings = settingsLookup.Settings with
        {
            Processor = "Acme Analysis GmbH",
            ProcessorRegion = "Norway",
        };
        var service = CreateService(context, FakeProvider.Returning(Extracted()));

        await service.AnalyzeAsync(accountId, fileId, "user-1", Consent());

        settingsLookup.Settings = settingsLookup.Settings with
        {
            Processor = "Anthropic",
            ProcessorRegion = "United States",
        };

        var job = await context.FileAnalysisJobs.AsNoTracking().SingleAsync();
        Assert.Equal("Acme Analysis GmbH", job.ProcessorInForce);
        Assert.Equal("Norway", job.ProcessorRegionInForce);
    }

    /// <summary>
    /// AC 34 — all four stamps derive from the <em>same</em> snapshot read. The provider callback
    /// mutates every one of them mid-transfer, so a second read anywhere in the path would show up as a
    /// mixture rather than as one coherent set.
    /// </summary>
    [Fact]
    public async Task AllFourStamps_ComeFromOneSnapshot_NeverAMixture()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        settingsLookup.Settings = settingsLookup.Settings with
        {
            Model = "claude-opus-5",
            BaseUrl = "https://first.internal",
            Processor = "First Processor",
            ProcessorRegion = "Norway",
        };

        var provider = new FakeProvider((_, _, _, _, _) =>
        {
            settingsLookup.Settings = settingsLookup.Settings with
            {
                Model = "claude-haiku-4-5",
                BaseUrl = "https://second.internal",
                Processor = "Second Processor",
                ProcessorRegion = "United States",
            };
            return Task.FromResult(new List<ExtractedTransaction> { Extracted() });
        });
        var service = CreateService(context, provider);

        await service.AnalyzeAsync(accountId, fileId, "user-1", Consent());

        var job = await context.FileAnalysisJobs.SingleAsync();
        Assert.Equal("claude-opus-5", job.AnalyzerModel);
        Assert.Equal("first.internal", job.AnalyzerBaseUrlHost);
        Assert.Equal("First Processor", job.ProcessorInForce);
        Assert.Equal("Norway", job.ProcessorRegionInForce);
    }

    /// <summary>
    /// AC 32 — jobs recorded before the migration carry nulls, and null means "recorded before this was
    /// tracked". No backfill: inventing a value — even the then-configured default — would put an answer
    /// into an audit record that was never observed.
    /// </summary>
    [Fact]
    public async Task AJobPredatingTheColumns_ReadsAsNotRecorded_NeverAsTodaysValues()
    {
        await using var context = TestContextFactory.Create();
        var (_, fileId) = await SeedAsync(context);
        var accountFile = await context.AccountFiles.SingleAsync(af => af.FileMetadataId == fileId);

        context.FileAnalysisJobs.Add(new FileAnalysisJob
        {
            AccountFileId = accountFile.Id,
            RequestedByUserId = "user-1",
            Status = Odyssey.Context.FileAnalysisJobStatus.Completed,
            StartedAt = DateTime.UtcNow.AddYears(-1),
            CompletedAt = DateTime.UtcNow.AddYears(-1),
            AnalyzerProvider = Odyssey.Context.AnalyzerProvider.Claude,
        });
        await context.SaveChangesAsync();

        var entry = Assert.Single(await CreateService(context, FakeProvider.Returning()).GetAuditLogAsync());

        Assert.Null(entry.AnalyzerBaseUrlHost);
        Assert.Null(entry.ProcessorInForce);
        Assert.Null(entry.ProcessorRegionInForce);
    }

    /// <summary>A completed transfer surfaces all three on the audit row, under file-analysis.audit.</summary>
    [Fact]
    public async Task TheAuditRow_CarriesTheThreeProvenanceFields()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        settingsLookup.Settings = settingsLookup.Settings with
        {
            BaseUrl = "https://gateway.internal",
            Processor = "Acme Analysis GmbH",
            ProcessorRegion = "Norway",
        };
        var service = CreateService(context, FakeProvider.Returning(Extracted()));

        await service.AnalyzeAsync(accountId, fileId, "user-1", Consent());

        var entry = Assert.Single(await service.GetAuditLogAsync());
        Assert.Equal("gateway.internal", entry.AnalyzerBaseUrlHost);
        Assert.Equal("Acme Analysis GmbH", entry.ProcessorInForce);
        Assert.Equal("Norway", entry.ProcessorRegionInForce);
    }

    // ── AC 36-38 — the consent is bound to the disclosure ────────────────────────────────────────

    /// <summary>
    /// AC 36 — a stale version refuses before anything happens. No job row, no provider request: the
    /// user consented to facts that no longer hold, so there is nothing to record and nothing to send.
    /// </summary>
    [Fact]
    public async Task AStaleDisclosureVersion_Refuses_CreatingNoJobAndMakingNoRequest()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        var provider = FakeProvider.Returning(Extracted());
        var service = CreateService(context, provider);

        await Assert.ThrowsAsync<FileAnalysisDisclosureChangedException>(() =>
            service.AnalyzeAsync(accountId, fileId, "user-1", Consent("a-version-from-before")));

        Assert.Empty(context.FileAnalysisJobs);
        Assert.Equal(0, provider.CallCount);
    }

    /// <summary>
    /// AC 37 — a MISSING version is a mismatch, not a skip. A client that sends none has not shown the
    /// user a disclosure this server can vouch for, and treating absence as agreement would make the
    /// whole check opt-out by omission.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMissingDisclosureVersion_IsTreatedAsAMismatch(string? echoed)
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        var provider = FakeProvider.Returning(Extracted());
        var service = CreateService(context, provider);

        await Assert.ThrowsAsync<FileAnalysisDisclosureChangedException>(() =>
            service.AnalyzeAsync(accountId, fileId, "user-1", new AnalyzeFileRequest
            {
                ConsentAcknowledged = true,
                ConsentText = "I consent.",
                DisclosureVersion = echoed,
            }));

        Assert.Empty(context.FileAnalysisJobs);
        Assert.Equal(0, provider.CallCount);
    }

    /// <summary>
    /// A change to any disclosure fact after the gate was rendered invalidates the echoed version — the
    /// mechanism working end to end rather than against a hand-made bad string.
    /// </summary>
    [Fact]
    public async Task ChangingTheProcessorAfterTheGateRendered_InvalidatesTheEchoedVersion()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        var consent = Consent(); // the version the gate would have rendered

        settingsLookup.Settings = settingsLookup.Settings with { Processor = "Acme Analysis GmbH" };

        var provider = FakeProvider.Returning(Extracted());
        await Assert.ThrowsAsync<FileAnalysisDisclosureChangedException>(() =>
            CreateService(context, provider).AnalyzeAsync(accountId, fileId, "user-1", consent));

        Assert.Equal(0, provider.CallCount);
    }

    /// <summary>
    /// <strong>AC 38 — ordering.</strong> Disabled beats stale, and misconfigured beats stale, so a
    /// disabled or broken instance never leaks disclosure state through a <c>409</c>.
    /// </summary>
    [Fact]
    public async Task WhenDisabled_AStaleVersionStillAnswersFeatureDisabled()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        settingsLookup.Enabled = false;
        var service = CreateService(context, FakeProvider.Returning(Extracted()));

        await Assert.ThrowsAsync<FileAnalysisDisabledException>(() =>
            service.AnalyzeAsync(accountId, fileId, "user-1", Consent("stale")));
    }

    [Fact]
    public async Task WhenTheModelIsUnusable_AStaleVersionStillAnswersConfigurationUnavailable()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        settingsLookup.Settings = settingsLookup.Settings with { Model = null };
        var provider = FakeProvider.Returning(Extracted());
        var service = CreateService(context, provider);

        await Assert.ThrowsAsync<FileAnalysisUnavailableException>(() =>
            service.AnalyzeAsync(accountId, fileId, "user-1", Consent("stale")));

        Assert.Empty(context.FileAnalysisJobs);
        Assert.Equal(0, provider.CallCount);
    }

    /// <summary>
    /// AC 22/23 at the service tier — an unusable model or destination <strong>refuses</strong>, and it
    /// does so before a job row exists and before any request goes out. Substituting the shipped
    /// default is the one thing forbidden: it would stamp a model that did not run, or send a document
    /// to a processor neither the administrator nor the consenting user chose.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AnUnusableModelOrBaseUrl_RefusesRatherThanSubstituting(bool modelNull, bool baseUrlNull)
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        settingsLookup.Settings = settingsLookup.Settings with
        {
            Model = modelNull ? null : settingsLookup.Settings.Model,
            BaseUrl = baseUrlNull ? null : settingsLookup.Settings.BaseUrl,
            IsDegraded = true,
        };
        var provider = FakeProvider.Returning(Extracted());
        var service = CreateService(context, provider);

        await Assert.ThrowsAsync<FileAnalysisUnavailableException>(() =>
            service.AnalyzeAsync(accountId, fileId, "user-1", Consent()));

        Assert.Empty(context.FileAnalysisJobs);
        Assert.Equal(0, provider.CallCount);
    }

    /// <summary>
    /// AC 24's service half — a degradation in one of the other seven fields leaves analysis WORKING.
    /// This is what stops the refusal quietly widening into "refuse on any degradation", which would be
    /// an unstated availability regression: a blank processor row taking the whole feature down.
    /// </summary>
    [Fact]
    public async Task ADegradedSnapshotWithUsableModelAndBaseUrl_StillAnalyses()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        settingsLookup.Settings = settingsLookup.Settings with { IsDegraded = true };
        var provider = FakeProvider.Returning(Extracted());

        await CreateService(context, provider).AnalyzeAsync(accountId, fileId, "user-1", Consent());

        Assert.Equal(1, provider.CallCount);
        Assert.Single(context.FileAnalysisJobs);
    }

    /// <summary>
    /// AC 25 — a provider response body never reaches a user-visible field. That rule was already
    /// correct and becomes <em>load-bearing</em> once the responding host is admin-set: an arbitrary
    /// host's response must not be reflected into a <c>file-analysis.read</c> user's review grid.
    /// </summary>
    [Fact]
    public async Task AProviderErrorBody_IsNeverPersistedOnTheJob()
    {
        const string hostile = "<script>alert(1)</script>";

        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedAsync(context);
        var service = CreateService(context, new FakeProvider(
            (_, _, _, _, _) => throw new FileAnalysisProviderException($"Claude API error 500: {hostile}")));

        await Assert.ThrowsAsync<FileAnalysisProviderException>(() =>
            service.AnalyzeAsync(accountId, fileId, "user-1", Consent()));

        var job = await context.FileAnalysisJobs.SingleAsync();
        Assert.Equal("provider_error", job.FailureCode);
        Assert.Equal("The analysis provider returned an error.", job.FailureMessage);
        Assert.DoesNotContain(hostile, job.FailureMessage!, StringComparison.Ordinal);

        // And nothing on the audit projection carries it either — that surface renders FailureMessage.
        var entry = Assert.Single(await service.GetAuditLogAsync());
        Assert.DoesNotContain(hostile, entry.Failure ?? string.Empty, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private FileAnalysisService CreateService(OdysseyContext context, IFileAnalysisProvider provider)
    {
        var promptPath = Path.Combine(Path.GetTempPath(), $"odyssey-prompt-{Guid.NewGuid():N}.txt");
        File.WriteAllText(promptPath, "Extract transactions.");
        return new FileAnalysisService(
            context, provider, TestContextFactory.ContactLookup(journal),
            Options.Create(new FileAnalysisOptions { PromptTemplatePath = promptPath }),
            settingsLookup, NullLogger<FileAnalysisService>.Instance);
    }

    /// <summary>The version the gate would have rendered, unless the test supplies its own.</summary>
    private AnalyzeFileRequest Consent(string? disclosureVersion = null) => new()
    {
        ConsentAcknowledged = true,
        ConsentText = "I consent to sending the complete file.",
        DisclosureVersion = disclosureVersion ?? FileAnalysisDisclosureVersion.Compute(settingsLookup.Settings),
    };

    private static ExtractedTransaction Extracted() =>
        new(DateTime.UtcNow.AddDays(-1), null, "Coffee", null, null, -4.50m, "USD",
            null, null, null, null, null, null);

    private static async Task<(Guid accountId, Guid fileId)> SeedAsync(OdysseyContext context)
    {
        var account = new Account
        {
            Name = "Checking",
            Description = "Primary",
            Opened = DateTime.UtcNow,
            AccountType = Odyssey.Context.AccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var fileId = Guid.NewGuid();
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(new FileMetadata
        {
            Id = fileId,
            UploadedByUserId = "user-1",
            FileName = "statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = "hash",
            FileBlobId = blob.Id,
            FileBlob = blob,
            UploadedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        context.AccountFiles.Add(new AccountFile
        {
            AccountId = account.AccountId,
            FileMetadataId = fileId,
            AttachedByUserId = "user-1",
            AttachedAtUtc = DateTime.UtcNow,
            FileType = Odyssey.Context.AccountFileType.Statement,
        });
        await context.SaveChangesAsync();

        return (account.AccountId, fileId);
    }

    /// <summary>
    /// A provider that records the target it was handed and counts its calls, so "no request was made"
    /// is asserted rather than inferred from the absence of a job row.
    /// </summary>
    private sealed class FakeProvider(
        Func<byte[], string, string, string, CancellationToken, Task<List<ExtractedTransaction>>> behaviour)
        : IFileAnalysisProvider
    {
        public FileAnalysisTarget? LastTarget { get; private set; }

        public int CallCount { get; private set; }

        public Task<List<ExtractedTransaction>> ExtractTransactionsAsync(
            byte[] fileContent, string contentType, string accountCurrencyCode, string promptTemplate,
            FileAnalysisTarget target, int maxTokens, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastTarget = target;
            return behaviour(fileContent, contentType, accountCurrencyCode, promptTemplate, cancellationToken);
        }

        public Task<List<MatchedCandidate>> MatchTransactionsAsync(
            IReadOnlyList<MatchCandidateInput> candidates,
            IReadOnlyList<VocabularyEntry> contactVocabulary,
            IReadOnlyList<VocabularyEntry> tagVocabulary,
            FileAnalysisTarget target, int maxTokens, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastTarget = target;
            return Task.FromResult(new List<MatchedCandidate>());
        }

        public static FakeProvider Returning(params ExtractedTransaction[] transactions) =>
            new((_, _, _, _, _) => Task.FromResult(transactions.ToList()));
    }
}
