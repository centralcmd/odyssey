using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Xunit;
using Odyssey.Dtos;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The step that keeps an upgrade from silently changing behaviour an operator had configured
/// (issue #421 Wave 2, AC 30).
///
/// <para>
/// A migration seeds a compile-time constant, so on its own it would replace a configured sender
/// identity with <c>no-reply@odyssey.local</c> — no error, no log, just different mail from the next
/// send onwards. These tests pin the three cases that matter and, in particular, the one that
/// value-comparison alone cannot get right.
/// </para>
/// </summary>
public class SystemSettingsConfigAdoptionTests
{
    private const string ConfiguredSender = "billing@acme.test";

    private static OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase($"adoption-{Guid.NewGuid()}")
            .Options);

    /// <summary>
    /// Built through a real container, not by handing the class a context.
    ///
    /// <para>
    /// The first version of these tests newed it up with an <see cref="OdysseyContext"/>, which is
    /// exactly why they stayed green while the migrations job refused to start: the step took a scoped
    /// service in its constructor, and <c>Worker</c> is a singleton, so the container would not build.
    /// A test that bypasses the container cannot see that. Resolving the step from a provider — the way
    /// the job does — keeps the scoping contract under test.
    /// </para>
    /// </summary>
    private static SystemSettingsConfigAdoption Create(
        OdysseyContext context, params (string Key, string Value)[] configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(configuration.Select(pair =>
                new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build());
        services.AddSingleton<ILogger<SystemSettingsConfigAdoption>>(
            NullLogger<SystemSettingsConfigAdoption>.Instance);
        services.AddTransient<SystemSettingsConfigAdoption>();

        return services.BuildServiceProvider().GetRequiredService<SystemSettingsConfigAdoption>();
    }

    /// <summary>Seeds a row the way the migration does: the default value and no <c>UpdatedBy</c>.</summary>
    private static async Task SeedAsync(OdysseyContext context, string key, string value, string? updatedBy = null)
    {
        context.SystemSettings.Add(new SystemSetting
        {
            Key = key,
            Value = value,
            UpdatedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedBy = updatedBy,
        });
        await context.SaveChangesAsync();
    }

    private static Task<SystemSetting> RowAsync(OdysseyContext context, string key) =>
        context.SystemSettings.SingleAsync(setting => setting.Key == key);

    [Fact]
    public async Task AConfiguredValue_IsAdoptedOverTheSeededDefault()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.EmailFromAddress, SystemSettingsDefaults.EmailFromAddress);

        await Create(context, ("Email:FromAddress", ConfiguredSender)).ExecuteAsync(CancellationToken.None);

        Assert.Equal(ConfiguredSender, (await RowAsync(context, SystemSettingsKeys.EmailFromAddress)).Value);
    }

    /// <summary>
    /// The case value-comparison cannot handle, and the reason ownership is decided by
    /// <c>UpdatedBy</c>: an administrator who deliberately set the value back to the shipped default
    /// must not have configuration overwrite it on the next restart.
    /// </summary>
    [Fact]
    public async Task AnAdministratorsDeliberateDefault_IsNotOverwritten()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.EmailFromAddress,
            SystemSettingsDefaults.EmailFromAddress, updatedBy: "admin-user-id");

        await Create(context, ("Email:FromAddress", ConfiguredSender)).ExecuteAsync(CancellationToken.None);

        Assert.Equal(
            SystemSettingsDefaults.EmailFromAddress,
            (await RowAsync(context, SystemSettingsKeys.EmailFromAddress)).Value);
    }

    [Fact]
    public async Task AnAdministratorsChangedValue_IsNotOverwritten()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.EmailFromAddress, "chosen@acme.test", updatedBy: "admin-user-id");

        await Create(context, ("Email:FromAddress", ConfiguredSender)).ExecuteAsync(CancellationToken.None);

        Assert.Equal("chosen@acme.test", (await RowAsync(context, SystemSettingsKeys.EmailFromAddress)).Value);
    }

    [Fact]
    public async Task RunningTwice_ChangesNothingTheSecondTime()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.EmailFromAddress, SystemSettingsDefaults.EmailFromAddress);
        var adoption = Create(context, ("Email:FromAddress", ConfiguredSender));

        await adoption.ExecuteAsync(CancellationToken.None);
        var afterFirst = (await RowAsync(context, SystemSettingsKeys.EmailFromAddress)).UpdatedAt;

        await adoption.ExecuteAsync(CancellationToken.None);
        var row = await RowAsync(context, SystemSettingsKeys.EmailFromAddress);

        Assert.Equal(ConfiguredSender, row.Value);
        Assert.Equal(afterFirst, row.UpdatedAt);
    }

    [Fact]
    public async Task AbsentConfiguration_LeavesTheSeededValueAlone()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.EmailFromAddress, SystemSettingsDefaults.EmailFromAddress);

        await Create(context).ExecuteAsync(CancellationToken.None);

        Assert.Equal(
            SystemSettingsDefaults.EmailFromAddress,
            (await RowAsync(context, SystemSettingsKeys.EmailFromAddress)).Value);
    }

    [Fact]
    public async Task BlankConfiguration_IsTreatedAsAbsent()
    {
        // Compose passes `${EMAIL_FROM_ADDRESS:-}`, so an unset variable arrives as an empty string
        // rather than not arriving at all. Adopting that would blank the sender identity.
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.EmailFromAddress, SystemSettingsDefaults.EmailFromAddress);

        await Create(context, ("Email:FromAddress", "   ")).ExecuteAsync(CancellationToken.None);

        Assert.Equal(
            SystemSettingsDefaults.EmailFromAddress,
            (await RowAsync(context, SystemSettingsKeys.EmailFromAddress)).Value);
    }

    [Fact]
    public async Task AMissingRow_IsSkippedRatherThanInvented()
    {
        // Nothing seeded: the migration has not run, or this build does not know the key. Creating a
        // row here would race the seed and could persist a key the registry cannot project.
        await using var context = NewContext();

        await Create(context, ("Email:FromAddress", ConfiguredSender)).ExecuteAsync(CancellationToken.None);

        Assert.Empty(context.SystemSettings);
    }

    [Fact]
    public async Task EveryAdoptableSetting_IsCarriedOver()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.EmailFromAddress, SystemSettingsDefaults.EmailFromAddress);
        await SeedAsync(context, SystemSettingsKeys.EmailFromName, SystemSettingsDefaults.EmailFromName);
        await SeedAsync(context, SystemSettingsKeys.EmailPerRecipientLimit, "3");
        await SeedAsync(context, SystemSettingsKeys.EmailPerRecipientWindowMinutes, "60");

        await Create(context,
            ("Email:FromAddress", ConfiguredSender),
            ("Email:FromName", "Acme Billing"),
            ("Email:PerRecipientLimit", "9"),
            ("Email:PerRecipientWindowMinutes", "120")).ExecuteAsync(CancellationToken.None);

        Assert.Equal(ConfiguredSender, (await RowAsync(context, SystemSettingsKeys.EmailFromAddress)).Value);
        Assert.Equal("Acme Billing", (await RowAsync(context, SystemSettingsKeys.EmailFromName)).Value);
        Assert.Equal("9", (await RowAsync(context, SystemSettingsKeys.EmailPerRecipientLimit)).Value);
        Assert.Equal("120", (await RowAsync(context, SystemSettingsKeys.EmailPerRecipientWindowMinutes)).Value);
    }

    // ── the three file-analysis tuning keys (issue #434) ─────────────────────────────────────────
    //
    // The only three of the fifteen with an adoption entry: the other twelve were `const`s or POCO
    // defaults on a section with no configuration entry, so there was never a configured value to carry
    // over. Adoption can only rescue a value the MIGRATIONS JOB can see, which is why these three
    // needed env plumbing added alongside — a value an operator changed by editing the API's own
    // appsettings.json is not adoptable by any mechanism, and is a documented breaking change instead.

    [Fact]
    public async Task TheThreeFileAnalysisTuningKeys_AreCarriedOver()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisMaxTokens, "8096");
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisMatchMaxVocabulary, "500");
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisMatchTimeoutSeconds, "60");

        await Create(context,
            ("FileAnalysis:MaxTokens", "16000"),
            ("FileAnalysis:Match:MaxVocabulary", "1200"),
            ("FileAnalysis:Match:TimeoutSeconds", "90")).ExecuteAsync(CancellationToken.None);

        Assert.Equal("16000", (await RowAsync(context, SystemSettingsKeys.FileAnalysisMaxTokens)).Value);
        Assert.Equal("1200", (await RowAsync(context, SystemSettingsKeys.FileAnalysisMatchMaxVocabulary)).Value);
        Assert.Equal("90", (await RowAsync(context, SystemSettingsKeys.FileAnalysisMatchTimeoutSeconds)).Value);

        // UpdatedBy stays null: configuration keeps applying until an administrator takes ownership in
        // the UI, and an unresolvable id would render as "Unknown user" on the last-changed line.
        Assert.Null((await RowAsync(context, SystemSettingsKeys.FileAnalysisMaxTokens)).UpdatedBy);
    }

    /// <summary>
    /// <c>FileAnalysis:TimeoutSeconds</c> is deliberately NOT adoptable — it stays a startup value,
    /// consumed once by the resilience handler, so a runtime value could never reach a live pipeline.
    /// Adopting it would create a settings row nothing reads, and the plumbing to feed it.
    /// </summary>
    [Fact]
    public async Task TheStartupResilienceTimeout_IsNotAdoptable()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisMatchTimeoutSeconds, "60");

        await Create(context, ("FileAnalysis:TimeoutSeconds", "300")).ExecuteAsync(CancellationToken.None);

        Assert.Equal("60", (await RowAsync(context, SystemSettingsKeys.FileAnalysisMatchTimeoutSeconds)).Value);
    }

    // ── the kill switch, model and destination (issue #439) ──────────────────────────────────────
    //
    // All three had live environment plumbing (FILE_ANALYSIS_ENABLED / _MODEL / _BASE_URL through
    // Compose, .env and Aspire), so without adoption an operator running with analysis switched on and
    // a model configured would upgrade into the shipped defaults — analysis OFF at api.anthropic.com —
    // silently. All three are audited: they carry system-settings.security.update on the API side, and
    // adoption writes outside SystemSettingsService.

    /// <summary>AC 47 / AC 52 — all three are adoptable, and UpdatedBy stays null.</summary>
    [Fact]
    public async Task TheThreeFileAnalysisRuntimeKeys_AreCarriedOver()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisEnabled, "false");
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisModel, SystemSettingsDefaults.FileAnalysisModel);
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisBaseUrl, SystemSettingsDefaults.FileAnalysisBaseUrl);

        await Create(context,
            ("FileAnalysis:Enabled", "true"),
            ("FileAnalysis:Model", "claude-opus-5"),
            ("FileAnalysis:BaseUrl", "https://gateway.internal")).ExecuteAsync(CancellationToken.None);

        Assert.Equal("true", (await RowAsync(context, SystemSettingsKeys.FileAnalysisEnabled)).Value);
        Assert.Equal("claude-opus-5", (await RowAsync(context, SystemSettingsKeys.FileAnalysisModel)).Value);
        Assert.Equal("https://gateway.internal",
            (await RowAsync(context, SystemSettingsKeys.FileAnalysisBaseUrl)).Value);

        Assert.Null((await RowAsync(context, SystemSettingsKeys.FileAnalysisEnabled)).UpdatedBy);
    }

    /// <summary>
    /// The boolean is canonicalised to the lowercase form <c>BoolSetting.Format</c> stores, so a
    /// <c>True</c> in an operator's <c>.env</c> does not read as a change against a stored <c>true</c>
    /// on every restart.
    /// </summary>
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public async Task AConfiguredBoolean_IsCanonicalisedToLowercase(string configured)
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisEnabled, "false");

        await Create(context, ("FileAnalysis:Enabled", configured)).ExecuteAsync(CancellationToken.None);

        Assert.Equal("true", (await RowAsync(context, SystemSettingsKeys.FileAnalysisEnabled)).Value);
    }

    /// <summary>
    /// AC 49 — an unparseable boolean is logged and skipped, leaving the seeded value in place. Writing
    /// it would be worse than skipping: the read path treats an unusable value as degraded and fails
    /// closed, so a stray <c>FILE_ANALYSIS_ENABLED=yes</c> would silently disable analysis by a
    /// different route than the one an operator thinks they are using.
    /// </summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    public async Task AnUnparseableBoolean_IsRejected_LeavingTheSeededValue(string configured)
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisEnabled, "false");

        var logger = new CapturingLogger();
        await CreateWithLogger(context, logger, ("FileAnalysis:Enabled", configured))
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal("false", (await RowAsync(context, SystemSettingsKeys.FileAnalysisEnabled)).Value);
        Assert.Contains(logger.Messages, message => message.Contains("Rejecting config adoption"));
    }

    /// <summary>
    /// AC 50 — the base URL is validated against the same shape rule the <c>PUT</c> enforces, not just
    /// its <c>[StringLength]</c>. Without that, an <c>http://</c> value in an operator's <c>.env</c>
    /// would land in the store having bypassed the one rule that matters — and the read path would then
    /// treat it as degraded and refuse every analysis.
    /// </summary>
    [Theory]
    [InlineData("http://gateway.internal")]
    [InlineData("https://gateway.internal/proxy")]
    [InlineData("https://key:secret@gateway.internal")]
    [InlineData("gateway.internal")]
    public async Task AnUnusableConfiguredBaseUrl_IsRejected_LeavingTheSeededValue(string configured)
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisBaseUrl, SystemSettingsDefaults.FileAnalysisBaseUrl);

        var logger = new CapturingLogger();
        await CreateWithLogger(context, logger, ("FileAnalysis:BaseUrl", configured))
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal(SystemSettingsDefaults.FileAnalysisBaseUrl,
            (await RowAsync(context, SystemSettingsKeys.FileAnalysisBaseUrl)).Value);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("secret"));
    }

    /// <summary>A configured base URL is canonicalised the same way a saved one is (trailing slash off).</summary>
    [Fact]
    public async Task AConfiguredBaseUrl_IsCanonicalised()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisBaseUrl, SystemSettingsDefaults.FileAnalysisBaseUrl);

        await Create(context, ("FileAnalysis:BaseUrl", "https://gateway.internal/"))
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal("https://gateway.internal",
            (await RowAsync(context, SystemSettingsKeys.FileAnalysisBaseUrl)).Value);
    }

    /// <summary>AC 51 — an empty or unset variable is skipped, leaving the seed and a null UpdatedBy.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyConfiguredValue_IsSkipped(string configured)
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisEnabled, "false");
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisModel, SystemSettingsDefaults.FileAnalysisModel);

        await Create(context,
            ("FileAnalysis:Enabled", configured),
            ("FileAnalysis:Model", configured)).ExecuteAsync(CancellationToken.None);

        var enabled = await RowAsync(context, SystemSettingsKeys.FileAnalysisEnabled);
        Assert.Equal("false", enabled.Value);
        Assert.Null(enabled.UpdatedBy);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisModel,
            (await RowAsync(context, SystemSettingsKeys.FileAnalysisModel)).Value);
    }

    /// <summary>
    /// AC 48 — once an administrator owns the row, configuration no longer applies. Ownership is
    /// <c>UpdatedBy</c>, never a value comparison: comparing cannot tell "never touched" from "an
    /// administrator deliberately set it back to the default", and would overwrite the second on every
    /// restart.
    /// </summary>
    [Fact]
    public async Task AnAdministratorsSwitch_IsNotOverwritten_EvenWhenConfigurationDisagrees()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisEnabled, "false", updatedBy: "admin-user-id");

        await Create(context, ("FileAnalysis:Enabled", "true")).ExecuteAsync(CancellationToken.None);

        Assert.Equal("false", (await RowAsync(context, SystemSettingsKeys.FileAnalysisEnabled)).Value);
    }

    /// <summary>And the same when configuration happens to AGREE — the case value-comparison gets wrong.</summary>
    [Fact]
    public async Task AnAdministratorsSwitch_IsNotTouched_EvenWhenConfigurationAgrees()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisEnabled, "true", updatedBy: "admin-user-id");

        var logger = new CapturingLogger();
        await CreateWithLogger(context, logger, ("FileAnalysis:Enabled", "true"))
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal("true", (await RowAsync(context, SystemSettingsKeys.FileAnalysisEnabled)).Value);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("Adopting configured value"));
    }

    /// <summary>
    /// AC 47's audit half — all three carry the security claim on the API side, so all three emit an
    /// audit line when adopted. The base URL's line names hosts, exactly as the API's does.
    /// </summary>
    [Fact]
    public async Task AdoptingAnyOfTheThree_LogsAnAuditLine()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisEnabled, "false");
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisModel, SystemSettingsDefaults.FileAnalysisModel);
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisBaseUrl, SystemSettingsDefaults.FileAnalysisBaseUrl);

        var logger = new CapturingLogger();
        await CreateWithLogger(context, logger,
            ("FileAnalysis:Enabled", "true"),
            ("FileAnalysis:Model", "claude-opus-5"),
            ("FileAnalysis:BaseUrl", "https://gateway.internal")).ExecuteAsync(CancellationToken.None);

        var audit = logger.Messages.Where(message => message.Contains("security-claim change")).ToList();

        Assert.Contains(audit, line => line.Contains(SystemSettingsKeys.FileAnalysisEnabled)
            && line.Contains("false -> true"));
        Assert.Contains(audit, line => line.Contains(SystemSettingsKeys.FileAnalysisModel)
            && line.Contains("claude-opus-5"));
        Assert.Contains(audit, line => line.Contains(SystemSettingsKeys.FileAnalysisBaseUrl)
            && line.Contains("gateway.internal"));
    }

    /// <summary>
    /// AC 52 — the three keys are present in the adoption table at all. A seed with no adoption entry
    /// is the exact defect this step exists to prevent, and it fails silently: the row is written, the
    /// operator's value is discarded, and nothing says so.
    /// </summary>
    [Fact]
    public async Task AllThreeKeys_ArePresentInTheAdoptionTable()
    {
        // Asserted behaviourally rather than by reflecting over the private table: a key that is not
        // adoptable simply does not move, which is what a missing entry would look like in production.
        foreach (var (settingKey, configKey, seeded, configured) in new[]
        {
            (SystemSettingsKeys.FileAnalysisEnabled, "FileAnalysis:Enabled", "false", "true"),
            (SystemSettingsKeys.FileAnalysisModel, "FileAnalysis:Model",
                SystemSettingsDefaults.FileAnalysisModel, "claude-opus-5"),
            (SystemSettingsKeys.FileAnalysisBaseUrl, "FileAnalysis:BaseUrl",
                SystemSettingsDefaults.FileAnalysisBaseUrl, "https://gateway.internal"),
        })
        {
            await using var context = NewContext();
            await SeedAsync(context, settingKey, seeded);

            await Create(context, (configKey, configured)).ExecuteAsync(CancellationToken.None);

            Assert.Equal(configured, (await RowAsync(context, settingKey)).Value);
        }
    }

    /// <summary>
    /// <c>FileAnalysis:ApiKey</c> and <c>FileAnalysis:Provider</c> are deliberately NOT adoptable.
    /// <c>Provider</c> never moved into the store (issue #439 Non-Goal 2), so an adoption entry would
    /// create a settings row nothing reads. <c>ApiKey</c> did move, in issue #445 — but into the
    /// <em>secret</em> store, which adoption must not touch for a different and stronger reason (see
    /// <see cref="NoMigratedSecret_IsAdoptableFromConfiguration"/>).
    /// </summary>
    [Fact]
    public async Task TheApiKeyAndProvider_AreNotAdoptable()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisModel, SystemSettingsDefaults.FileAnalysisModel);

        await Create(context,
            ("FileAnalysis:ApiKey", "sk-ant-secret"),
            ("FileAnalysis:Provider", "Bedrock")).ExecuteAsync(CancellationToken.None);

        Assert.Single(context.SystemSettings);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisModel,
            (await RowAsync(context, SystemSettingsKeys.FileAnalysisModel)).Value);
    }

    /// <summary>
    /// AC 16. None of the five migrated credentials is adoptable, and the reason is not the usual one.
    ///
    /// <para>
    /// Adoption exists to carry an operator's configured value across on upgrade — but for a SECRET it
    /// is the wrong tool twice over. It reads the value from configuration, which means the plaintext
    /// would still have to be present in the environment at upgrade time, which is precisely what this
    /// migration exists to eliminate. And it stamps no <c>UpdatedBy</c> by design, leaving the row owned
    /// by configuration indefinitely — for a credential, that is ownership nobody can see and nobody can
    /// take back.
    /// </para>
    ///
    /// <para>
    /// Adoption also writes to <c>SystemSettings</c>, a plaintext table, so an entry here would not
    /// merely be inert: it would write a live credential in the clear beside every other setting.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NoMigratedSecret_IsAdoptableFromConfiguration()
    {
        await using var context = NewContext();

        await Create(context,
            ("FileAnalysis:ApiKey", "sk-ant-secret"),
            ("Email:Username", "relay-user"),
            ("Email:Password", "relay-password"),
            ("Email:RecipientHashKey", "hash-key"),
            ("Legal:PseudonymizationSecret", "pseudonymization-secret")).ExecuteAsync(CancellationToken.None);

        // Nothing was written at all — no row, under any key, carrying any of those values.
        Assert.Empty(context.SystemSettings);
    }

    // ── AC 30 — adoption validates before it writes ──────────────────────────────────────────────

    /// <summary>
    /// Adoption used to write <c>row.Value</c> unchecked, so an out-of-range environment value landed in
    /// the store and bypassed every bound a <c>PUT</c> enforces. For the three single-direction keys the
    /// bound IS the mechanism, so this is not a tidiness concern.
    /// </summary>
    [Theory]
    [InlineData("FileAnalysis:MaxTokens", "999999")]
    [InlineData("FileAnalysis:MaxTokens", "1")]
    [InlineData("FileAnalysis:MaxTokens", "not-a-number")]
    [InlineData("FileAnalysis:Match:MaxVocabulary", "0")]
    [InlineData("FileAnalysis:Match:TimeoutSeconds", "1")]
    public async Task AnOutOfRangeConfiguredValue_IsSkipped_LeavingTheSeededDefault(
        string configKey, string configured)
    {
        var settingKey = configKey switch
        {
            "FileAnalysis:MaxTokens" => SystemSettingsKeys.FileAnalysisMaxTokens,
            "FileAnalysis:Match:MaxVocabulary" => SystemSettingsKeys.FileAnalysisMatchMaxVocabulary,
            _ => SystemSettingsKeys.FileAnalysisMatchTimeoutSeconds,
        };
        var seeded = configKey switch
        {
            "FileAnalysis:MaxTokens" => "8096",
            "FileAnalysis:Match:MaxVocabulary" => "500",
            _ => "60",
        };

        await using var context = NewContext();
        await SeedAsync(context, settingKey, seeded);

        await Create(context, (configKey, configured)).ExecuteAsync(CancellationToken.None);

        Assert.Equal(seeded, (await RowAsync(context, settingKey)).Value);
    }

    /// <summary>
    /// And the accepted values are exactly the ones a <c>PUT</c> would accept, because both sides run the
    /// SAME data annotation on the real write DTO rather than a second copy of the bounds.
    /// </summary>
    [Fact]
    public async Task AnInRangeConfiguredValue_AtTheBoundary_IsAccepted()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisMaxTokens, "8096");

        await Create(context, ("FileAnalysis:MaxTokens", "64000")).ExecuteAsync(CancellationToken.None);

        Assert.Equal("64000", (await RowAsync(context, SystemSettingsKeys.FileAnalysisMaxTokens)).Value);
    }

    // ── AC 29 — adoption of an audited key leaves a trace ────────────────────────────────────────

    /// <summary>
    /// Adoption writes OUTSIDE <c>SystemSettingsService</c>, so the derived <c>AuditChanges</c> path
    /// never runs for it. Without an explicit line here, adoption would be the one path that can change a
    /// security-claim setting with no record of what it used to be.
    /// </summary>
    [Fact]
    public async Task AdoptingAnAuditedKey_LogsTheOldAndNewValue()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisMaxTokens, "8096");

        var logger = new CapturingLogger();
        await CreateWithLogger(context, logger, ("FileAnalysis:MaxTokens", "16000"))
            .ExecuteAsync(CancellationToken.None);

        var audit = Assert.Single(logger.Messages, message => message.Contains("security-claim change"));
        Assert.Contains("8096", audit);
        Assert.Contains("16000", audit);
        Assert.Contains(SystemSettingsKeys.FileAnalysisMaxTokens, audit);
    }

    /// <summary>A count-claim key is not audited — that is the existing #349 design, matched here.</summary>
    [Fact]
    public async Task AdoptingACountClaimKey_LogsNoAuditLine()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileAnalysisMatchMaxVocabulary, "500");

        var logger = new CapturingLogger();
        await CreateWithLogger(context, logger, ("FileAnalysis:Match:MaxVocabulary", "1200"))
            .ExecuteAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Messages, message => message.Contains("security-claim change"));
    }

    private static SystemSettingsConfigAdoption CreateWithLogger(
        OdysseyContext context,
        ILogger<SystemSettingsConfigAdoption> logger,
        params (string Key, string Value)[] configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(configuration.Select(pair =>
                new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build());
        services.AddSingleton(logger);
        services.AddTransient<SystemSettingsConfigAdoption>();

        return services.BuildServiceProvider().GetRequiredService<SystemSettingsConfigAdoption>();
    }

    /// <summary>Collects formatted log messages, for the two audit assertions above.</summary>
    private sealed class CapturingLogger : ILogger<SystemSettingsConfigAdoption>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    // ── the upload cap (issue #421 Wave 4) ────────────────────────────────────────────────────────
    //
    // The one adoptable setting whose two sides use different UNITS: configuration holds bytes, the
    // setting holds megabytes. Everything above copies the configured string verbatim; this one cannot,
    // and getting it wrong is silent — the row would hold a byte count and every upload would be
    // "capped" at 67108864 MB.

    [Fact]
    public async Task AConfiguredUploadCeiling_IsAdoptedConvertedToMegabytes()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileStorageMaxUploadMegabytes, "64");

        // 128 MB expressed the way configuration expresses it.
        await Create(context, ("FileStorage:MaxFileSizeBytes", "134217728"))
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal("128", (await RowAsync(context, SystemSettingsKeys.FileStorageMaxUploadMegabytes)).Value);
    }

    /// <summary>
    /// Without adoption this is the silent regression: an operator who had raised the configured limit
    /// upgrades, the migration seeds the shipped 64, and their upload cap is halved with no error and no
    /// log line.
    /// </summary>
    [Fact]
    public async Task AConfiguredUploadCeiling_IsNotSilentlyReplacedByTheShippedDefault()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileStorageMaxUploadMegabytes,
            SystemSettingsDefaults.FileStorageMaxUploadMegabytes.ToString());

        await Create(context, ("FileStorage:MaxFileSizeBytes", "268435456"))
            .ExecuteAsync(CancellationToken.None);

        Assert.NotEqual(
            SystemSettingsDefaults.FileStorageMaxUploadMegabytes.ToString(),
            (await RowAsync(context, SystemSettingsKeys.FileStorageMaxUploadMegabytes)).Value);
    }

    /// <summary>Rounds DOWN, so adoption can never widen the cap past what was configured.</summary>
    [Fact]
    public async Task APartialMegabyteCeiling_RoundsDown()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileStorageMaxUploadMegabytes, "64");

        // 100 MB + 1 byte.
        await Create(context, ("FileStorage:MaxFileSizeBytes", "104857601"))
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal("100", (await RowAsync(context, SystemSettingsKeys.FileStorageMaxUploadMegabytes)).Value);
    }

    /// <summary>
    /// A value below one megabyte would convert to 0 — a cap no upload could ever satisfy. Skipped
    /// rather than stored, leaving the seeded default in place.
    /// </summary>
    [Theory]
    [InlineData("1024")]
    [InlineData("not-a-number")]
    public async Task AnUnusableConfiguredCeiling_LeavesTheSeededValue(string configured)
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileStorageMaxUploadMegabytes, "64");

        await Create(context, ("FileStorage:MaxFileSizeBytes", configured))
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal("64", (await RowAsync(context, SystemSettingsKeys.FileStorageMaxUploadMegabytes)).Value);
    }

    /// <summary>Ownership beats configuration here too — the same <c>UpdatedBy</c> rule.</summary>
    [Fact]
    public async Task AnAdministratorsUploadCap_IsNotOverwritten()
    {
        await using var context = NewContext();
        await SeedAsync(context, SystemSettingsKeys.FileStorageMaxUploadMegabytes, "8",
            updatedBy: "admin-user-id");

        await Create(context, ("FileStorage:MaxFileSizeBytes", "134217728"))
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal("8", (await RowAsync(context, SystemSettingsKeys.FileStorageMaxUploadMegabytes)).Value);
    }
}
