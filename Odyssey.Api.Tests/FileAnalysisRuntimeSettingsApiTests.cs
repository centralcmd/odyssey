using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Api.SystemSettings;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-level coverage for the three file-analysis settings issue #439 moved out of deploy-time
/// configuration: the kill switch, the model, and the provider base URL.
///
/// <para>
/// The base URL is the highest-consequence value in the whole settings store, and most of the weight
/// here is on it. <c>FileAnalysis:ApiKey</c> is not in this store — it moved to the ENCRYPTED secret
/// store in issue #445 — but it is still attached to the outbound client, so repointing this sends that
/// key to the new host — accepted, and paid for
/// with the security claim, an audit line, an https-only shape validator that also rejects a path, and
/// a host-only audit projection. The projection test is the one with teeth: it plants a
/// credential-bearing row <em>directly</em>, because the write validator refuses that shape and the
/// value being replaced in an audit line was never subject to it.
/// </para>
/// </summary>
public class FileAnalysisRuntimeSettingsApiTests
{
    private const string Path = "/api/system-settings";
    private const string ActorUserId = "77777777-7777-7777-7777-777777777777";

    private static readonly string[] ReadAndSecurity =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate];

    /// <summary>Read plus only the ORDINARY write claim — none of the three is reachable with it.</summary>
    private static readonly string[] ReadAndOrdinaryOnly =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    // ── AC 1 / AC 6 — the seeded read shape ──────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ServesTheThreeSeededDefaults()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.NotNull(dto);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisEnabled, dto!.FileAnalysisEnabled);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisModel, dto.FileAnalysisModel);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisBaseUrl, dto.FileAnalysisBaseUrl);
    }

    /// <summary>
    /// AC 6 — the migration's seeded values equal the shared constants <em>by reference</em>. The seed
    /// writes the constant rather than a literal, so this reads the rows the seed produced and compares
    /// them to the same symbols the DTO defaults and the client catalogue both name.
    /// </summary>
    [Fact]
    public async Task MigrationSeed_MatchesTheSharedDefaultConstants()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var rows = await context.SystemSettings.AsNoTracking()
            .ToDictionaryAsync(row => row.Key, row => row.Value);

        Assert.Equal("false", rows[SystemSettingsKeys.FileAnalysisEnabled]);
        Assert.False(SystemSettingsDefaults.FileAnalysisEnabled);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisModel, rows[SystemSettingsKeys.FileAnalysisModel]);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisBaseUrl, rows[SystemSettingsKeys.FileAnalysisBaseUrl]);

        // The seed leaves UpdatedBy null, meaning no administrator has taken ownership of the row —
        // which is what the settings page's provenance line reads, and what a first write replaces.
        var seeded = await context.SystemSettings.AsNoTracking()
            .Where(row => row.Key == SystemSettingsKeys.FileAnalysisEnabled
                || row.Key == SystemSettingsKeys.FileAnalysisModel
                || row.Key == SystemSettingsKeys.FileAnalysisBaseUrl)
            .ToListAsync();
        Assert.All(seeded, row => Assert.Null(row.UpdatedBy));
    }

    // ── AC 2 / AC 3 — the claim gate ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(EachFieldPresent))]
    public async Task Put_WithoutTheSecurityClaim_IsForbidden_AndChangesNothing(
        string fieldName, SystemSettingsUpdate request)
    {
        await using var factory = new ApiFactory(ReadAndOrdinaryOnly);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(fieldName, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisEnabled, dto!.FileAnalysisEnabled);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisModel, dto.FileAnalysisModel);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisBaseUrl, dto.FileAnalysisBaseUrl);
    }

    public static TheoryData<string, SystemSettingsUpdate> EachFieldPresent() => new()
    {
        { nameof(SystemSettingsUpdate.FileAnalysisEnabled), new SystemSettingsUpdate { FileAnalysisEnabled = true } },
        { nameof(SystemSettingsUpdate.FileAnalysisModel), new SystemSettingsUpdate { FileAnalysisModel = "claude-opus-5" } },
        { nameof(SystemSettingsUpdate.FileAnalysisBaseUrl), new SystemSettingsUpdate { FileAnalysisBaseUrl = "https://gateway.internal" } },
    };

    /// <summary>
    /// AC 3 — absent means "leave unchanged", never a permission event. This is what lets an admin
    /// holding only the ordinary write claim save the count rows in the same section.
    /// </summary>
    [Fact]
    public async Task Put_OmittingAllThree_Succeeds_ForAnOrdinaryWriter()
    {
        await using var factory = new ApiFactory(ReadAndOrdinaryOnly);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisMatchMaxVocabulary = 400 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal(SystemSettingsDefaults.FileAnalysisEnabled, dto!.FileAnalysisEnabled);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisModel, dto.FileAnalysisModel);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisBaseUrl, dto.FileAnalysisBaseUrl);
    }

    // ── AC 4 — audit ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangingAnyOfTheThree_WritesAnAuditLineNamingOldAndNew()
    {
        await using var factory = new LoggingApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            FileAnalysisEnabled = true,
            FileAnalysisModel = "claude-opus-5",
        })).EnsureSuccessStatusCode();

        var audit = factory.Logs.Entries
            .Where(entry => entry.Message.Contains("security-claim change", StringComparison.Ordinal))
            .Select(entry => entry.Message)
            .ToList();

        Assert.Contains(audit, line =>
            line.Contains(SystemSettingsKeys.FileAnalysisEnabled, StringComparison.Ordinal)
            && line.Contains("false -> true", StringComparison.Ordinal));
        Assert.Contains(audit, line =>
            line.Contains(SystemSettingsKeys.FileAnalysisModel, StringComparison.Ordinal)
            && line.Contains("claude-sonnet-5 -> claude-opus-5", StringComparison.Ordinal));
    }

    /// <summary>
    /// <strong>AC 14 (security finding 4).</strong> The audit line echoes the value being <em>replaced</em>,
    /// and the write validator never saw that one. A row planted by a restore can carry
    /// <c>https://key:secret@host</c> — so without the descriptor's host-only <c>AuditProjection</c>, the
    /// first administrator to correct such a row through the UI would write that credential into the
    /// application log. The row is planted directly here because the API would refuse to store it.
    /// </summary>
    [Fact]
    public async Task ReplacingAPlantedCredentialBearingBaseUrl_LogsHostsOnly_NeverTheCredential()
    {
        await using var factory = new LoggingApiFactory(ReadAndSecurity);
        await factory.SetSystemSettingAsync(
            SystemSettingsKeys.FileAnalysisBaseUrl, "https://apikey:s3cr3t@old-gateway.internal");
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisBaseUrl = "https://new-gateway.internal" }))
            .EnsureSuccessStatusCode();

        var line = Assert.Single(factory.Logs.Entries,
            entry => entry.Message.Contains(SystemSettingsKeys.FileAnalysisBaseUrl, StringComparison.Ordinal)
                && entry.Message.Contains("security-claim change", StringComparison.Ordinal)).Message;

        // Still reconstructable — both HOSTS are named, so the change is not obscured.
        Assert.Contains("old-gateway.internal", line, StringComparison.Ordinal);
        Assert.Contains("new-gateway.internal", line, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apikey", line, StringComparison.OrdinalIgnoreCase);
        // And nothing anywhere in the captured output carries it either.
        Assert.DoesNotContain(factory.Logs.Entries,
            entry => entry.Message.Contains("s3cr3t", StringComparison.OrdinalIgnoreCase));
    }

    // ── AC 8-13 — base-URL shape validation ──────────────────────────────────────────────────────

    /// <summary>
    /// AC 12 (security finding 5) is the path rule, and it is the one that changed from the first draft.
    /// The provider resolves a <em>root-absolute</em> <c>/v1/messages</c> against the base URI, which
    /// discards any path the administrator configured — so accepting a path would mean a clean save, an
    /// advisory naming a host that looks right, a job stamp recording that same host, and requests
    /// carrying the API key going to a path nobody configured.
    /// </summary>
    [Theory]
    [InlineData("http://api.anthropic.com")]      // AC 8 — https only
    [InlineData("ftp://api.anthropic.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("api.anthropic.com")]             // not absolute
    [InlineData("https:///v1")]                   // empty host
    [InlineData("https://key:secret@gateway.internal")]  // AC 9 — userinfo
    [InlineData("https://host?token=leaky")]
    [InlineData("https://host#fragment")]
    [InlineData("https://host/v1/messages")]      // AC 12 — any non-empty path
    [InlineData("https://host/proxy")]
    public async Task Put_WithAnUnusableBaseUrl_Returns400KeyedByTheField(string baseUrl)
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { FileAnalysisBaseUrl = baseUrl });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(nameof(SystemSettingsUpdate.FileAnalysisBaseUrl), body, StringComparison.Ordinal);
        Assert.Contains("https://", body, StringComparison.Ordinal);
        Assert.Contains("the provider appends /v1/messages itself", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>AC 9 — the rejected value's credential must not come back in the response either.</summary>
    [Fact]
    public async Task Put_WithACredentialBearingBaseUrl_DoesNotEchoTheCredential()
    {
        await using var factory = new LoggingApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisBaseUrl = "https://key:s3cr3t@gateway.internal" });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("s3cr3t", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.Logs.Entries,
            entry => entry.Message.Contains("s3cr3t", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AC 13 — private, loopback and link-local hosts are ACCEPTED by design. An internal corporate
    /// gateway is the main reason this setting is editable at all; a private-range block would break
    /// the primary legitimate use case while doing nothing about the trusted-admin threat model.
    /// </summary>
    [Theory]
    [InlineData("https://gateway.internal")]
    [InlineData("https://127.0.0.1:8443")]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://localhost")]
    public async Task Put_WithAPrivateOrLoopbackHost_IsAccepted(string baseUrl)
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { FileAnalysisBaseUrl = baseUrl });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal(baseUrl, dto!.FileAnalysisBaseUrl);
    }

    /// <summary>
    /// AC 10 — canonicalisation. Without it <c>https://host</c> and <c>https://host/</c> are two distinct
    /// stored values, so a GET→PUT round trip of unchanged data stops being a no-op and emits an audit
    /// line for a change nobody made.
    /// </summary>
    [Fact]
    public async Task TrailingSlashIsCanonicalisedAway_SoTheSecondSaveIsNotAChange()
    {
        await using var factory = new LoggingApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var first = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisBaseUrl = "https://gateway.internal/" });
        var firstDto = await first.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal("https://gateway.internal", firstDto!.FileAnalysisBaseUrl);

        var auditLinesAfterFirst = BaseUrlAuditLines(factory);

        (await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisBaseUrl = "https://gateway.internal" }))
            .EnsureSuccessStatusCode();

        Assert.Equal(auditLinesAfterFirst, BaseUrlAuditLines(factory));
    }

    private static int BaseUrlAuditLines(LoggingApiFactory factory) =>
        factory.Logs.Entries.Count(entry =>
            entry.Message.Contains(SystemSettingsKeys.FileAnalysisBaseUrl, StringComparison.Ordinal)
            && entry.Message.Contains("security-claim change", StringComparison.Ordinal));

    // ── AC 11 — the model's static bound ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_WithAnOverlongModel_Returns400()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisModel = new string('m', 129) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(nameof(SystemSettingsUpdate.FileAnalysisModel),
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 11's guard half. The DTO's 128 must stay at or below <c>FileAnalysisJob.AnalyzerModel</c>'s
    /// <c>[StringLength(256)]</c>, so the entity bound can never fire and a <c>RequestCapCeilings</c>
    /// validator against 256 would be exactly the decorative ceiling <c>CLAUDE.md</c> warns about. If
    /// this relationship ever inverts, the DTO bound stops being the real one — silently.
    /// </summary>
    [Fact]
    public void TheModelWriteBound_IsTighterThanTheEntityColumn()
    {
        var dtoBound = typeof(SystemSettingsUpdate)
            .GetProperty(nameof(SystemSettingsUpdate.FileAnalysisModel))!
            .GetCustomAttribute<StringLengthAttribute>()!.MaximumLength;

        var entityBound = typeof(FileAnalysisJob)
            .GetProperty(nameof(FileAnalysisJob.AnalyzerModel))!
            .GetCustomAttribute<StringLengthAttribute>()!.MaximumLength;

        Assert.Equal(128, dtoBound);
        Assert.True(dtoBound <= entityBound,
            $"The write bound ({dtoBound}) must stay at or below the column it is stamped into ({entityBound}).");
    }

    // ── AC 7 — cache eviction ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A save evicts <c>FileAnalysisSettingsLookup</c>'s entry, so an immediate re-read by the writing
    /// instance returns the new value. Observed through the disclosure endpoint, which is the one
    /// surface that serves the model out of that cached snapshot.
    /// </summary>
    [Fact]
    public async Task SavingTheModel_EvictsTheSnapshot_SoAnImmediateReadSeesIt()
    {
        await using var factory = new ApiFactory(
            [.. ReadAndSecurity, PermissionClaims.FileAnalysisRead]);
        using var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>("/api/file-analysis/disclosure");
        Assert.Equal(SystemSettingsDefaults.FileAnalysisModel, before!.Model);

        (await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { FileAnalysisModel = "claude-opus-5" }))
            .EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>("/api/file-analysis/disclosure");
        Assert.Equal("claude-opus-5", after!.Model);
    }

    // ── AC 42-46 — advisories ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 42. Not an "above the default" comparison like the cost advisories: every enabled state is
    /// worth stating, because the setting authorises transferring personal data to a third party — and
    /// it names the region, the fact that decides whether those transfers fall under Art. 44-49.
    /// </summary>
    [Fact]
    public async Task TurningAnalysisOn_CarriesAnAdvisoryNamingTheProcessorAndRegion_AndStillCommits()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { FileAnalysisEnabled = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.True(dto!.FileAnalysisEnabled);

        var advisory = Assert.Contains(nameof(SystemSettingsUpdate.FileAnalysisEnabled), dto.Warnings);
        Assert.Contains(SystemSettingsDefaults.FileAnalysisProcessor, advisory, StringComparison.Ordinal);
        Assert.Contains(SystemSettingsDefaults.FileAnalysisProcessorRegion, advisory, StringComparison.Ordinal);
        Assert.Contains("per-document consent", advisory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeavingAnalysisOff_CarriesNoEnabledAdvisory()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.DoesNotContain(nameof(SystemSettingsUpdate.FileAnalysisEnabled), dto!.Warnings.Keys);
    }

    /// <summary>AC 43 — the host and only the host, and the write still commits.</summary>
    [Fact]
    public async Task RepointingTheBaseUrl_CarriesAHostOnlyAdvisory_AndStillCommits()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisBaseUrl = "https://gateway.internal" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal("https://gateway.internal", dto!.FileAnalysisBaseUrl);

        var advisory = Assert.Contains(nameof(SystemSettingsUpdate.FileAnalysisBaseUrl), dto.Warnings);
        Assert.Contains("gateway.internal", advisory, StringComparison.Ordinal);
        Assert.Contains("API key", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", advisory, StringComparison.Ordinal);
    }

    /// <summary>A planted path/credential value still yields a host-only advisory, never the raw string.</summary>
    [Fact]
    public async Task APlantedCredentialBearingBaseUrl_StillAdvisesTheHostOnly()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        await factory.SetSystemSettingAsync(
            SystemSettingsKeys.FileAnalysisBaseUrl, "https://apikey:s3cr3t@gateway.internal/v1?token=leaky");
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        var advisory = Assert.Contains(nameof(SystemSettingsUpdate.FileAnalysisBaseUrl), dto!.Warnings);
        Assert.Contains("gateway.internal", advisory, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("leaky", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/v1", advisory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheShippedBaseUrl_CarriesNoAdvisory()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.DoesNotContain(nameof(SystemSettingsUpdate.FileAnalysisBaseUrl), dto!.Warnings.Keys);
    }

    [Fact]
    public async Task ChangingTheModel_CarriesANonRetroactivityAdvisory()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisModel = "claude-opus-5" });

        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        var advisory = Assert.Contains(nameof(SystemSettingsUpdate.FileAnalysisModel), dto!.Warnings);
        Assert.Contains("keep the model they ran under", advisory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <strong>AC 45 — the swallow itself.</strong> A throwing advisory is caught, omitted, and does not
    /// take the request down with it.
    ///
    /// <para>
    /// Driven through <c>SystemSettingsService.TryAdvise</c> with a delegate that genuinely throws. The
    /// earlier version of this coverage planted pathological <em>values</em> and asserted a <c>200</c> —
    /// but every shipped advisory is defensively coded (<c>Uri.TryCreate</c>, <c>string.Equals</c> on
    /// non-null strings), so none of them actually threw on those inputs and the test passed with the
    /// <c>catch</c> block deleted. The delegates live in a static registry a test cannot substitute
    /// into, which is why the swallow was extracted into a seam rather than left reachable only through
    /// the HTTP surface.
    /// </para>
    /// </summary>
    [Fact]
    public void AThrowingAdvisory_IsSwallowed_AndOmitsOnlyItself()
    {
        var boom = new InvalidOperationException("advisory blew up");
        var thrower = new BoolSetting
        {
            Key = SystemSettingsKeys.FileAnalysisEnabled,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisEnabled),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = "false",
            Read = r => r.FileAnalysisEnabled,
            Write = (dto, v) => dto.FileAnalysisEnabled = v,
            Advise = (_, _) => throw boom,
        };

        var logger = new CapturingLogger();

        var message = SystemSettingsService.TryAdvise(
            thrower, new SystemSettingsDto(), AdvisoryContext.Empty, logger);

        Assert.Null(message);
        Assert.Contains(logger.Entries, entry => ReferenceEquals(entry.Exception, boom));
    }

    /// <summary>
    /// And a well-behaved advisory beside it still produces its text — so the assertion above is
    /// "omits only itself", not "omits everything".
    /// </summary>
    [Fact]
    public void AWellBehavedAdvisory_StillProducesItsText()
    {
        var healthy = new BoolSetting
        {
            Key = SystemSettingsKeys.FileAnalysisEnabled,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisEnabled),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = "false",
            Read = r => r.FileAnalysisEnabled,
            Write = (dto, v) => dto.FileAnalysisEnabled = v,
            Advise = (_, _) => "still here",
        };

        Assert.Equal("still here", SystemSettingsService.TryAdvise(
            healthy, new SystemSettingsDto(), AdvisoryContext.Empty, new CapturingLogger()));
    }

    /// <summary>Minimal <see cref="ILogger"/> that keeps the exception each entry carried.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception), exception));
    }

    /// <summary>
    /// The end-to-end companion: pathological stored values across the file-analysis rows still save.
    /// It does not prove the swallow (see above for why), but it does prove the advisories cope with
    /// values the write path would have refused — which is the state a restore or hand edit can leave.
    ///
    /// <para>
    /// Scoped to the STRING settings deliberately. A planted non-numeric value in an <c>IntSetting</c>
    /// row throws from <c>Project</c> — before any advisory runs — and 500s the read; that is
    /// pre-existing behaviour across every numeric setting, unrelated to this feature, and folding it
    /// in here would make this test fail for a reason it is not about.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PathologicalStoredValues_NeverFailASave()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisBaseUrl, "://not-a-url");
        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisProcessor, "");
        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisProcessorRegion, "");
        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisModel, "");
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { FileAnalysisEnabled = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.True(dto!.FileAnalysisEnabled);
    }

    /// <summary>
    /// AC 46 — <c>AdvisoryContext</c> no longer carries the base-URL host, and
    /// <c>SystemSettingsService</c> no longer injects <c>IOptions&lt;FileAnalysisOptions&gt;</c> to build
    /// it. The base URL is a setting now, so the correspondence advisory reads it off the DTO like every
    /// other value; leaving the configuration dependency in place would let the two sources drift with
    /// nothing failing.
    /// </summary>
    [Fact]
    public void TheAdvisoryContext_NoLongerCarriesAConfiguredBaseUrl()
    {
        var contextType = typeof(SystemSettingsRegistry).Assembly
            .GetType("Odyssey.Api.SystemSettings.AdvisoryContext")!;

        Assert.DoesNotContain(
            contextType.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            // EqualityContract is compiler-generated on every record; everything else would be state a
            // delegate could reach.
            property => property.Name != "EqualityContract");

        var constructorParameters = typeof(SystemSettingsService)
            .GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType.Name)
            .ToList();

        Assert.DoesNotContain(constructorParameters,
            name => name.Contains("IOptions", StringComparison.Ordinal));
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    /// <summary>Captures the API's log output, for the audit and no-leak assertions.</summary>
    private sealed class LoggingApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId)
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
        }
    }
}
