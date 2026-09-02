using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-level coverage for the fifteen tuning settings issue #434 migrated out of
/// <c>appsettings.json</c>, POCO defaults and <c>const</c> declarations.
///
/// <para>
/// The tests that carry the most weight here are the <strong>direction</strong> ones. Three of the
/// fifteen are single-direction, and their bound is not a literal: it names the shared
/// <see cref="SystemSettingsDefaults"/> constant the migration also seeds. Widening one of those ranges
/// "so a ceiling validator has something to reject" is the tempting change that would re-open the write
/// amplification the tighten-only conversion closed, so the pin is asserted <em>by value against the
/// constant</em> and by source text against the constant's NAME — a literal that happens to equal it
/// today would fail the second.
/// </para>
/// </summary>
public class TuningSystemSettingsApiTests
{
    private const string Path = "/api/system-settings";
    private const string AccountLimitsPath = "/api/account-limits";
    private const string ActorUserId = "55555555-5555-5555-5555-555555555555";

    private static readonly string[] ReadAndBoth =
    [
        PermissionClaims.SystemSettingsRead,
        PermissionClaims.SystemSettingsUpdate,
        PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    /// <summary>Read plus only the ORDINARY write claim — enough for thirteen of the fifteen.</summary>
    private static readonly string[] ReadAndOrdinaryOnly =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    private static readonly string[] ReadOnly = [PermissionClaims.SystemSettingsRead];

    // ── AC 4 / AC 18 — the read shape ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ServesAllFifteenCompiledDefaults()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.NotNull(dto);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisMaxTokens, dto!.FileAnalysisMaxTokens);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisMatchMaxVocabulary, dto.FileAnalysisMatchMaxVocabulary);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisMatchTimeoutSeconds, dto.FileAnalysisMatchTimeoutSeconds);
        Assert.Equal(SystemSettingsDefaults.PhotoMetadataReadMegabytes, dto.PhotoMetadataReadMegabytes);
        Assert.Equal(
            SystemSettingsDefaults.PhotoMetadataExtractionTimeoutSeconds, dto.PhotoMetadataExtractionTimeoutSeconds);
        Assert.Equal(SystemSettingsDefaults.CalendarMaxWindowDays, dto.CalendarMaxWindowDays);
        Assert.Equal(SystemSettingsDefaults.CalendarMaxEventDurationDays, dto.CalendarMaxEventDurationDays);
        Assert.Equal(
            SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows, dto.CalendarIcsMaxAggregateExportRows);
        Assert.Equal(
            SystemSettingsDefaults.CalendarIcsMaxAggregateOccurrences, dto.CalendarIcsMaxAggregateOccurrences);
        Assert.Equal(
            SystemSettingsDefaults.CalendarIcsMaxAggregateExportWindowDays,
            dto.CalendarIcsMaxAggregateExportWindowDays);
        Assert.Equal(
            SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences, dto.RecurrenceMaxGeneratedOccurrences);
        Assert.Equal(
            SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry,
            dto.ContactVCardMaxRepeatablePropertiesPerEntry);
        Assert.Equal(SystemSettingsDefaults.ImportMaxSamplesPerSkipReason, dto.ImportMaxSamplesPerSkipReason);
        Assert.Equal(SystemSettingsDefaults.EmailMaxTrackedRecipients, dto.EmailMaxTrackedRecipients);
        Assert.Equal(
            SystemSettingsDefaults.AccountMaxSmartTagsPerAccount, dto.AccountMaxSmartTagsPerAccount);
    }

    /// <summary>
    /// Six bound projections — five ceilings and one floor. A WebAssembly client cannot read a server
    /// attribute, so without these the control has no way to bound itself and would offer a value the
    /// API is going to reject on every save.
    /// </summary>
    [Fact]
    public async Task Get_PublishesTheSixBoundProjections()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.Equal(40_000, dto!.CalendarIcsMaxAggregateExportRowsCeiling);
        Assert.Equal(20_000, dto.CalendarIcsMaxAggregateOccurrencesCeiling);
        Assert.Equal(16, dto.PhotoMetadataReadMegabytesCeiling);

        // The three single-direction pins ARE the shipped default, by reference to the same constant the
        // migration seeds and the [Range] names.
        Assert.Equal(
            SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences, dto.RecurrenceMaxGeneratedOccurrencesCeiling);
        Assert.Equal(
            SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry,
            dto.ContactVCardMaxRepeatablePropertiesPerEntryCeiling);
        Assert.Equal(SystemSettingsDefaults.EmailMaxTrackedRecipients, dto.EmailMaxTrackedRecipientsFloor);
    }

    // ── AC 6 / 7 / 8 — authorization ─────────────────────────────────────────────────────────────

    /// <summary>All thirteen count-claim fields in one body, as a caller holding only that claim.</summary>
    [Fact]
    public async Task Put_AllThirteenCountClaimFields_WithOnlyTheOrdinaryClaim_Succeeds()
    {
        await using var factory = new ApiFactory(ReadAndOrdinaryOnly);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            FileAnalysisMatchMaxVocabulary = 400,
            FileAnalysisMatchTimeoutSeconds = 45,
            PhotoMetadataExtractionTimeoutSeconds = 7,
            CalendarMaxWindowDays = 60,
            CalendarMaxEventDurationDays = 300,
            CalendarIcsMaxAggregateExportRows = 15_000,
            CalendarIcsMaxAggregateOccurrences = 4_000,
            CalendarIcsMaxAggregateExportWindowDays = 60,
            RecurrenceMaxGeneratedOccurrences = 500,
            ContactVCardMaxRepeatablePropertiesPerEntry = 100,
            ImportMaxSamplesPerSkipReason = 50,
            AccountMaxSmartTagsPerAccount = 5,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal(500, dto!.RecurrenceMaxGeneratedOccurrences);
        Assert.Equal(5, dto.AccountMaxSmartTagsPerAccount);
    }

    /// <summary>
    /// The three security-claim keys, each rejected for a caller holding only the ordinary claim — and
    /// nothing else in the same body is written either, because the claim check is all-or-nothing.
    /// </summary>
    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.FileAnalysisMaxTokens))]
    [InlineData(nameof(SystemSettingsUpdate.PhotoMetadataReadMegabytes))]
    [InlineData(nameof(SystemSettingsUpdate.EmailMaxTrackedRecipients))]
    public async Task Put_ASecurityClaimField_WithOnlyTheOrdinaryClaim_IsForbidden_AndWritesNothing(string field)
    {
        await using var factory = new ApiFactory(ReadAndOrdinaryOnly);
        using var client = factory.CreateClient();

        var request = new SystemSettingsUpdate { CalendarMaxWindowDays = 31 };
        switch (field)
        {
            case nameof(SystemSettingsUpdate.FileAnalysisMaxTokens):
                request.FileAnalysisMaxTokens = 32_000;
                break;
            case nameof(SystemSettingsUpdate.PhotoMetadataReadMegabytes):
                request.PhotoMetadataReadMegabytes = 12;
                break;
            default:
                request.EmailMaxTrackedRecipients = 50_000;
                break;
        }

        var response = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // No partial write: the field the caller COULD edit is untouched too.
        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);
        Assert.Equal(SystemSettingsDefaults.CalendarMaxWindowDays, dto!.CalendarMaxWindowDays);
    }

    [Fact]
    public async Task Put_WithNeitherWriteClaim_IsForbidden()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { CalendarMaxWindowDays = 31 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── AC 10 — the derived audit line ───────────────────────────────────────────────────────────

    /// <summary>
    /// A change to a security-claim key is audited; a change to a count-claim key is not.
    ///
    /// <para>
    /// <c>SystemSettingDescriptor.AuditChanges</c> is <strong>derived</strong> from the required claim
    /// rather than declared, which is what makes this automatic — and it is the deciding argument for
    /// putting all three of #434's security-claim keys there. An unaudited third-party spend lever is
    /// worse than an over-classified one, and on the ordinary claim
    /// <c>PhotoMetadataReadMegabytes</c> would be the only unaudited megabyte cap in a ten-row set.
    /// </para>
    ///
    /// <para>
    /// It is also the mitigation the whole-resource <c>PUT</c> rests on for those keys: a second admin
    /// saving a stale form silently reverts the first one's raise, and a silent revert is
    /// indistinguishable from a deliberate set — except that the audit line names actor, field, old and
    /// new. The thirteen count-claim keys' reverts stay invisible, which is accepted residue.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Put_ASecurityClaimKey_IsAudited_WithOldAndNewValues()
    {
        await using var factory = new LoggingApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisMaxTokens = 32_000 })).StatusCode);

        var audit = Assert.Single(factory.Logs.Entries, entry =>
            entry.Message.Contains("security-claim change", StringComparison.Ordinal));

        Assert.Contains(SystemSettingsKeys.FileAnalysisMaxTokens, audit.Message, StringComparison.Ordinal);
        Assert.Contains("8096", audit.Message, StringComparison.Ordinal);
        Assert.Contains("32000", audit.Message, StringComparison.Ordinal);
        Assert.Contains(ActorUserId, audit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Put_ACountClaimKey_IsNotAudited()
    {
        await using var factory = new LoggingApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { CalendarMaxWindowDays = 45 })).StatusCode);

        Assert.DoesNotContain(factory.Logs.Entries, entry =>
            entry.Message.Contains("security-claim change", StringComparison.Ordinal));
    }

    /// <summary>A resave of an unchanged value is not audited — only an actual change is.</summary>
    [Fact]
    public async Task Put_ASecurityClaimKey_AtItsCurrentValue_IsNotAudited()
    {
        await using var factory = new LoggingApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            FileAnalysisMaxTokens = SystemSettingsDefaults.FileAnalysisMaxTokens,
        })).StatusCode);

        Assert.DoesNotContain(factory.Logs.Entries, entry =>
            entry.Message.Contains("security-claim change", StringComparison.Ordinal));
    }

    // ── AC 11 / 12 — bounds ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every one of the fifteen, below its minimum and above its maximum. The message asserted is the
    /// attribute's own <c>ErrorMessage</c> — which names the bound and why it exists — rather than a
    /// bare "out of range", because that explanation is the only thing the administrator sees.
    /// </summary>
    [Theory]
    [MemberData(nameof(OutOfRangeCases))]
    public async Task Put_OutsideTheRange_IsRejected_WithTheFieldKeyedMessage(
        string field, int value, string expectedFragment)
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var request = new SystemSettingsUpdate();
        typeof(SystemSettingsUpdate).GetProperty(field)!.SetValue(request, value);

        var response = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(field, body, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, body, StringComparison.Ordinal);
    }

    public static TheoryData<string, int, string> OutOfRangeCases()
    {
        var data = new TheoryData<string, int, string>();

        void Both(string field, int min, int max, string fragment)
        {
            data.Add(field, min - 1, fragment);
            data.Add(field, max + 1, fragment);
        }

        Both(nameof(SystemSettingsUpdate.FileAnalysisMaxTokens), 1024, 64_000, "Max tokens must be between");
        Both(nameof(SystemSettingsUpdate.FileAnalysisMatchMaxVocabulary), 1, 5_000,
            "Match vocabulary cap must be between");
        Both(nameof(SystemSettingsUpdate.FileAnalysisMatchTimeoutSeconds), 5, 600, "Match timeout must be between");
        Both(nameof(SystemSettingsUpdate.PhotoMetadataReadMegabytes), 1, 16, "Metadata read size must be between");
        Both(nameof(SystemSettingsUpdate.PhotoMetadataExtractionTimeoutSeconds), 1, 120,
            "Metadata extraction timeout must be between");
        Both(nameof(SystemSettingsUpdate.CalendarMaxWindowDays), 1, 3_650, "calendar window must be between");
        Both(nameof(SystemSettingsUpdate.CalendarMaxEventDurationDays), 1, 3_650, "A single event may span");
        Both(nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateExportRows), 1, 40_000,
            "aggregate export row guard must be between");
        Both(nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateOccurrences), 1, 20_000,
            "aggregate occurrence budget must be between");
        Both(nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateExportWindowDays), 1, 3_650,
            "aggregate export window must be between");
        Both(nameof(SystemSettingsUpdate.RecurrenceMaxGeneratedOccurrences), 1,
            SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences, "can only be lowered, not raised");
        Both(nameof(SystemSettingsUpdate.ContactVCardMaxRepeatablePropertiesPerEntry), 1,
            SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry, "can only be lowered, not raised");
        Both(nameof(SystemSettingsUpdate.ImportMaxSamplesPerSkipReason), 1, 10_000,
            "Samples per skip reason must be between");
        Both(nameof(SystemSettingsUpdate.EmailMaxTrackedRecipients),
            SystemSettingsDefaults.EmailMaxTrackedRecipients, 200_000, "can only be raised, not lowered");
        Both(nameof(SystemSettingsUpdate.AccountMaxSmartTagsPerAccount), 1, 1_000,
            "Smart tags per account must be between");

        return data;
    }

    /// <summary>
    /// The three raisable, derived ceilings, at the boundary. Each maximum is twice the shipped default,
    /// derived from the concurrency actually permitted on that surface rather than asserted.
    /// </summary>
    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateExportRows), 40_000)]
    [InlineData(nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateOccurrences), 20_000)]
    [InlineData(nameof(SystemSettingsUpdate.PhotoMetadataReadMegabytes), 16)]
    public async Task Put_AtADerivedCeiling_IsAllowed_AndOneAboveIsNot(string field, int ceiling)
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();
        var property = typeof(SystemSettingsUpdate).GetProperty(field)!;

        var atCeiling = new SystemSettingsUpdate();
        property.SetValue(atCeiling, ceiling);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Path, atCeiling)).StatusCode);

        var overCeiling = new SystemSettingsUpdate();
        property.SetValue(overCeiling, ceiling + 1);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Path, overCeiling)).StatusCode);
    }

    // ── AC 13 / 14 — the three single directions, and the pin behind them ────────────────────────

    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.RecurrenceMaxGeneratedOccurrences), 1_000, 500)]
    [InlineData(nameof(SystemSettingsUpdate.ContactVCardMaxRepeatablePropertiesPerEntry), 200, 100)]
    public async Task Put_ATightenOnlyKey_AcceptsTheDefaultAndBelow_RejectsAbove(
        string field, int shippedDefault, int lower)
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();
        var property = typeof(SystemSettingsUpdate).GetProperty(field)!;

        foreach (var accepted in new[] { shippedDefault, lower })
        {
            var request = new SystemSettingsUpdate();
            property.SetValue(request, accepted);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Path, request)).StatusCode);
        }

        var raised = new SystemSettingsUpdate();
        property.SetValue(raised, shippedDefault + 1);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Path, raised)).StatusCode);
    }

    [Fact]
    public async Task Put_TheRaiseOnlyKey_AcceptsTheFloorAndAbove_RejectsBelow()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        foreach (var accepted in new[] { SystemSettingsDefaults.EmailMaxTrackedRecipients, 200_000 })
        {
            var response = await client.PutAsJsonAsync(Path,
                new SystemSettingsUpdate { EmailMaxTrackedRecipients = accepted });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        foreach (var rejected in new[] { SystemSettingsDefaults.EmailMaxTrackedRecipients - 1, 200_001 })
        {
            var response = await client.PutAsJsonAsync(Path,
                new SystemSettingsUpdate { EmailMaxTrackedRecipients = rejected });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    /// <summary>
    /// The pin, asserted by <em>value</em>: the <c>[Range]</c> end equals the shared constant. This is
    /// what makes widening a range to give a ceiling validator something to reject impossible without
    /// failing a test — the change that would re-open the write amplification the tighten-only
    /// conversion closed.
    /// </summary>
    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.RecurrenceMaxGeneratedOccurrences), false)]
    [InlineData(nameof(SystemSettingsUpdate.ContactVCardMaxRepeatablePropertiesPerEntry), false)]
    [InlineData(nameof(SystemSettingsUpdate.EmailMaxTrackedRecipients), true)]
    public void ASingleDirectionKeys_PinnedBound_IsTheSharedDefault(string field, bool pinnedEndIsMinimum)
    {
        var range = typeof(SystemSettingsUpdate).GetProperty(field)!
            .GetCustomAttribute<RangeAttribute>();
        Assert.NotNull(range);

        var expected = field switch
        {
            nameof(SystemSettingsUpdate.RecurrenceMaxGeneratedOccurrences) =>
                SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences,
            nameof(SystemSettingsUpdate.ContactVCardMaxRepeatablePropertiesPerEntry) =>
                SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry,
            _ => SystemSettingsDefaults.EmailMaxTrackedRecipients,
        };

        Assert.Equal(expected, pinnedEndIsMinimum ? range!.Minimum : range!.Maximum);
    }

    /// <summary>
    /// The pin, asserted by <em>name</em>. The value assertion above passes equally well against a
    /// literal that happens to equal the constant today; this one does not, so the two together are what
    /// make "expressed once" a property of the code rather than a coincidence.
    /// </summary>
    [Theory]
    [InlineData("SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences")]
    [InlineData("SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry")]
    [InlineData("SystemSettingsDefaults.EmailMaxTrackedRecipients")]
    public void ASingleDirectionKeys_RangeAttribute_NamesTheSharedConstant(string expression)
    {
        var source = File.ReadAllText(SolutionFile("Odyssey.Dtos", "Application", "SystemSettingsUpdate.cs"));

        // Matched inside the BOUNDS — the first two arguments of [Range(...)] — and nowhere else.
        //
        // Scoping this to the whole argument list would include ErrorMessage, so
        // `[Range(1, 30, ErrorMessage = "...SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences...")]`
        // would satisfy both this test and the value assertion above while the bound itself was a plain
        // literal. That takes a deliberately misleading edit rather than an accidental one, but the pair
        // of tests exists precisely to make the misleading edit impossible, so the loophole is worth
        // closing. (Raised by the test reviewer on PR #436.)
        var bounds = System.Text.RegularExpressions.Regex
            .Matches(source, @"\[Range\(\s*(?<bounds>[^,]+,\s*[^,)]+)")
            .Select(match => match.Groups["bounds"].Value)
            .ToList();

        Assert.Contains(bounds, argument => argument.Contains(expression, StringComparison.Ordinal));
    }

    // ── AC 15 — the reference boundary that makes the pin possible ───────────────────────────────

    /// <summary>
    /// <c>Odyssey.Dtos</c> must keep ZERO project references — it is referenced by eleven
    /// projects including the WebAssembly client, which is what lets both halves of the stack name the
    /// same constant.
    ///
    /// This is the whole of AC 15 now that the four DTO projects are one. It used to be a pair: the
    /// second half checked that <c>Odyssey.Application.Dtos</c> referenced only <c>Odyssey.Dtos</c>,
    /// because <c>Odyssey.Context</c> referenced it in turn and so the edge the
    /// <c>[Range]</c> pins would otherwise need was a reference CYCLE rather than merely a missing one.
    /// After the merge the constants and the attribute share one assembly, so the cycle is not
    /// reachable and the leaf property below is what keeps the pin possible.
    /// </summary>
    [Fact]
    public void SharedDtos_HasNoProjectReferences()
    {
        var project = File.ReadAllText(SolutionFile("Odyssey.Dtos", "Odyssey.Dtos.csproj"));
        Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
    }

    // ── AC 16 — the deleted validator layer stays deleted ────────────────────────────────────────

    /// <summary>
    /// No <c>RequestCapCeilings</c> validator exists for any of the fifteen. Their bounds are static
    /// constants, so they belong in the attribute — and because model validation runs FIRST, a validator
    /// repeating the same number would be unreachable through the API, which is the "decorative ceiling"
    /// flaw this feature faults its own earlier drafts for. The photo-link, album-member, upload and
    /// insurance-link ceilings stay, because those are runtime- or cross-assembly-derived and genuinely
    /// cannot live in an attribute — the insurance one (issue #27) is the compile-time constant on the
    /// Odyssey.Dtos write DTOs, which the settings [Range] cannot name without pinning the two together.
    /// </summary>
    [Fact]
    public void RequestCapCeilings_ValidatorSurface_IsUnchanged()
    {
        var validators = typeof(Odyssey.Api.SystemSettings.SystemSettingsService).Assembly
            .GetType("Odyssey.Api.SystemSettings.RequestCapCeilings")!
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(method => method.Name.StartsWith("Validate", StringComparison.Ordinal))
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["ValidateInsuranceLinksPerPolicy", "ValidatePhotoAlbumMembers", "ValidatePhotoLinksPerKind", "ValidateUploadMegabytes"],
            validators);
    }

    // ── AC 32 / 33 / 34 — DTO invariants and the warnings wire shape ─────────────────────────────

    /// <summary>
    /// The write DTO exposes no reference-typed property other than its existing <c>string?</c> text
    /// fields and the three-state <c>CapacityLimit</c> count caps — so there is no nested object to
    /// over-post into, structurally rather than by validation.
    /// </summary>
    [Fact]
    public void SystemSettingsUpdate_ExposesNoUnexpectedReferenceTypes()
    {
        var offenders = typeof(SystemSettingsUpdate)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => !property.PropertyType.IsValueType)
            .Where(property => property.PropertyType != typeof(string)
                            && property.PropertyType != typeof(CapacityLimit))
            .Select(property => $"{property.Name}: {property.PropertyType.Name}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "The write DTO must carry scalars, strings and CapacityLimit only: " + string.Join(", ", offenders));
    }

    /// <summary>A client cannot inject advisory text: there is no inbound warnings field at all.</summary>
    [Fact]
    public async Task Put_WithAWarningsMember_IgnoresIt()
    {
        Assert.Null(typeof(SystemSettingsUpdate).GetProperty("Warnings"));

        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var response = await client.PutAsync(Path, JsonContent.Create(new Dictionary<string, object>
        {
            ["calendarMaxWindowDays"] = 45,
            ["warnings"] = new Dictionary<string, string> { ["CalendarMaxWindowDays"] = "injected" },
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("injected", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The advisory→row join key, asserted against the actual JSON rather than the CLR object: the
    /// dictionary's keys must be the PascalCase <c>SystemSettingsUpdate</c> property names, matching
    /// <c>ApiProblem.Errors</c>. PascalCase dictionary keys currently survive only because
    /// <c>DictionaryKeyPolicy</c> happens to be null, so a future global serializer change must break
    /// this test rather than every advisory at runtime.
    /// </summary>
    [Fact]
    public async Task Warnings_SerializeWithPascalCaseKeys_UnderAWarningsProperty()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisMaxTokens = 32_000 });

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var warnings = json.RootElement.GetProperty("warnings");
        Assert.True(warnings.TryGetProperty(nameof(SystemSettingsUpdate.FileAnalysisMaxTokens), out _));
    }

    // ── AC 35 — advisories never change the outcome ──────────────────────────────────────────────

    /// <summary>
    /// A cost advisory fires above the shipped default, clears at it, and in neither case affects the
    /// status code or the persisted value. That is the entire point of a channel separate from
    /// <c>errors</c>.
    /// </summary>
    [Fact]
    public async Task Put_AboveTheShippedDefault_Persists_AndCarriesAnAdvisory()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var raised = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisMaxTokens = 32_000 });

        Assert.Equal(HttpStatusCode.OK, raised.StatusCode);
        var dto = await raised.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal(32_000, dto!.FileAnalysisMaxTokens);
        Assert.Contains(nameof(SystemSettingsUpdate.FileAnalysisMaxTokens), dto.Warnings.Keys);

        var restored = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            FileAnalysisMaxTokens = SystemSettingsDefaults.FileAnalysisMaxTokens,
        });

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var after = await restored.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.DoesNotContain(nameof(SystemSettingsUpdate.FileAnalysisMaxTokens), after!.Warnings.Keys);
    }

    /// <summary>All six cost advisories, each raised one step above its default.</summary>
    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.FileAnalysisMaxTokens), 8_097)]
    [InlineData(nameof(SystemSettingsUpdate.FileAnalysisMatchMaxVocabulary), 501)]
    [InlineData(nameof(SystemSettingsUpdate.PhotoMetadataReadMegabytes), 9)]
    [InlineData(nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateExportRows), 20_001)]
    [InlineData(nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateOccurrences), 5_001)]
    [InlineData(nameof(SystemSettingsUpdate.ImportMaxSamplesPerSkipReason), 101)]
    public async Task Put_EachCostAdvisory_FiresAboveItsDefault_AndStillSucceeds(string field, int value)
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var request = new SystemSettingsUpdate();
        typeof(SystemSettingsUpdate).GetProperty(field)!.SetValue(request, value);

        var response = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Contains(field, dto!.Warnings.Keys);
    }

    /// <summary>The two tighten-only keys carry no advisory: they cannot be raised, so there is no cost to warn about.</summary>
    [Fact]
    public async Task TightenOnlyKeys_CarryNoAdvisory_AtTheirMaximum()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            RecurrenceMaxGeneratedOccurrences = SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences,
            ContactVCardMaxRepeatablePropertiesPerEntry =
                SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry,
        });

        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.DoesNotContain(nameof(SystemSettingsUpdate.RecurrenceMaxGeneratedOccurrences), dto!.Warnings.Keys);
        Assert.DoesNotContain(
            nameof(SystemSettingsUpdate.ContactVCardMaxRepeatablePropertiesPerEntry), dto.Warnings.Keys);
    }

    // ── AC 9 / 22 / 23 — the claim-free account-limits endpoint ──────────────────────────────────

    /// <summary>
    /// Claim-free but authentication-required, matching <c>/api/upload-limits</c> and
    /// <c>/api/import-limits</c>. It returns one instance-wide integer that the shipped client already
    /// carried as a literal, so this strictly reduces what is baked into the browser bundle.
    /// </summary>
    [Fact]
    public async Task AccountLimits_ForACallerWithNoClaims_ReturnsTheEffectiveCap()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<AccountLimitsDto>(AccountLimitsPath);

        Assert.Equal(SystemSettingsDefaults.AccountMaxSmartTagsPerAccount, dto!.MaxSmartTagsPerAccount);
    }

    [Fact]
    public async Task AccountLimits_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(AccountLimitsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// An ABSENT row is healthy, not degraded. Getting this wrong <c>503</c>s every database whose
    /// settings rows have not been seeded — which is every fresh in-memory and development environment,
    /// and is exactly how the Wave 1 consent-gate endpoint broke.
    /// </summary>
    [Fact]
    public async Task AccountLimits_WithNoRowPresent_Returns200WithTheCompiledDefault()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(AccountLimitsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AccountLimitsDto>();
        Assert.Equal(SystemSettingsDefaults.AccountMaxSmartTagsPerAccount, dto!.MaxSmartTagsPerAccount);
    }

    /// <summary>
    /// A row present with an unusable value IS degraded — the row exists, so somebody stored something
    /// this setting cannot use. The endpoint fails closed rather than presenting a fallback as
    /// authoritative, while the enforcement path keeps using the conservative number.
    /// </summary>
    [Fact]
    public async Task AccountLimits_WithAnUnusableStoredValue_Returns503()
    {
        await using var factory = new ApiFactory([]);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.AccountMaxSmartTagsPerAccount, "not-a-number");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(AccountLimitsPath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>A saved cap reaches the endpoint on the next request — one cache key, evicted on write.</summary>
    [Fact]
    public async Task AccountLimits_AfterASave_ServesTheNewCap()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { AccountMaxSmartTagsPerAccount = 3 })).StatusCode);

        var dto = await client.GetFromJsonAsync<AccountLimitsDto>(AccountLimitsPath);
        Assert.Equal(3, dto!.MaxSmartTagsPerAccount);
    }

    // ── AC 21 — one setting, one cache key, one eviction ─────────────────────────────────────────

    /// <summary>
    /// Saving a link cap evicts the single cache entry that owns it, and the ICS import path observes the
    /// new value on its next request — no second entry to miss.
    ///
    /// <para>
    /// This is the test mirroring the cap onto a second lookup record would have failed.
    /// <c>SystemSettingDescriptor.CacheKeyToEvict</c> is a single string and the service evicts exactly
    /// that one, so a mirrored value would have sat behind two entries with only one evicted: lowering
    /// the cap would take effect on create/update at once and on ICS import up to 30 seconds later. A
    /// miniature, intermittent reintroduction of the very divergence the defect fix removes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Put_ALinkCap_IsObservedByTheIcsImportPath_Immediately()
    {
        await using var factory = new ApiFactory(
        [
            .. ReadAndBoth,
            PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate,
        ]);
        using var client = factory.CreateClient();

        // Warm the lookup's cache first, so the assertion is about EVICTION rather than a cold read.
        var names = await SeedTagsAsync(factory, 8);
        await ImportJournalEntryAsync(client, "warm", names);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { JournalEntryMaxLinksPerKind = 2 })).StatusCode);

        var result = await ImportJournalEntryAsync(client, "after-save", names);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(6, result.SkippedTagLinkCount);
    }

    private static async Task<List<string>> SeedTagsAsync(ApiFactory factory, int count)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<Odyssey.Context.OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            names.Add($"evict{i}");
            context.JournalTags.Add(new Odyssey.Context.JournalTag
            {
                JournalTagId = Guid.NewGuid(),
                Name = names[i],
            });
        }

        await context.SaveChangesAsync();
        return names;
    }

    private static async Task<Odyssey.Dtos.Journal.JournalEntryIcsImportResult> ImportJournalEntryAsync(
        HttpClient client, string uid, IReadOnlyList<string> tagNames)
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Test//EN",
            "BEGIN:VJOURNAL", $"UID:{uid}", "SUMMARY:Many tags", "DESCRIPTION:x",
            "DTSTART;VALUE=DATE:20260101", "CATEGORIES:" + string.Join(",", tagNames),
            "END:VJOURNAL", "END:VCALENDAR");

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(ics));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/calendar");
        content.Add(file, "file", "entries.ics");

        var response = await client.PostAsync("/api/journal-entries/vjournal", content);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Odyssey.Dtos.Journal.JournalEntryIcsImportResult>())!;
    }

    // ── AC 39 — the deleted duplicate stays deleted ──────────────────────────────────────────────

    /// <summary>
    /// No <c>MaxLinksPerKind</c> constant survives in <c>Odyssey.Core.Journal</c>.
    ///
    /// <para>
    /// It was a <em>duplicate</em>, not merely a hardcoded number: the same limit had been admin-editable
    /// as <c>JournalEntryMaxLinksPerKind</c>/<c>JournalTaskMaxLinksPerKind</c> since #421 Wave 3, so an
    /// administrator who lowered either one saw it honoured on create/update and silently ignored on ICS
    /// import. A reintroduced constant compiles, passes every behavioural test that does not lower the
    /// setting, and quietly restores the divergence.
    /// </para>
    ///
    /// <para>
    /// <c>PhotoLimits.MaxLinksPerKind</c> in <c>Odyssey.Dtos.Journal</c> is a different thing and stays:
    /// it feeds <c>[MaxLength]</c> on ten photo request DTOs, which is exactly why the photo cap is
    /// tighten-only and keeps a real <c>RequestCapCeilings</c> validator.
    /// </para>
    /// </summary>
    [Fact]
    public void NoLinkCapConstant_SurvivesInTheJournalProject()
    {
        var root = System.IO.Path.GetDirectoryName(SolutionFile("Odyssey.sln"))!;
        var journal = System.IO.Path.Combine(root, "Odyssey.Core", "Journal");

        var offenders = Directory
            .EnumerateFiles(journal, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(file => !file.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Select(file => (File: file, Text: File.ReadAllText(file)))
            .Where(pair => System.Text.RegularExpressions.Regex.IsMatch(
                pair.Text, @"const\s+int\s+MaxLinksPerKind\s*="))
            .Select(pair => System.IO.Path.GetRelativePath(root, pair.File))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A MaxLinksPerKind constant is back in Odyssey.Core.Journal — it duplicates an admin-editable "
            + "setting, which is how the ICS import path came to ignore it: " + string.Join(", ", offenders));
    }

    // ── AC 45 — the rate-limit exclusion is recorded where someone would look for it ─────────────

    /// <summary>
    /// Both rate-limit partitioners must carry an in-code note explaining why <c>RateLimiting:*</c> is
    /// not admin-editable (issue #421 Non-Goal 5, recorded in code by #434 D4).
    ///
    /// <para>
    /// The reason is easy to get wrong from the code alone, because the partitioner DOES re-read options
    /// per request — it is the limiter <em>factory</em> that runs once per partition key, so a changed
    /// limit reaches a partition that has never been seen before and never reaches a live one. Someone
    /// reading only the partitioner would reasonably conclude these are already runtime-configurable and
    /// migrate them. The note is what stops that, and this test is what stops the note being deleted.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("IdentityRateLimiting.cs")]
    [InlineData("AdminActionRateLimiting.cs")]
    public void BothRateLimitPartitioners_RecordWhyTheyAreNotAdminEditable(string file)
    {
        var source = File.ReadAllText(SolutionFile("Odyssey.Api", file));

        Assert.Contains("NOT admin-editable", source, StringComparison.Ordinal);
        Assert.Contains("only the first time", source, StringComparison.Ordinal);
        Assert.Contains("never reach a live one", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves a solution-relative file, for the two lints above whose subject is a cross-project
    /// contract (a <c>[Range]</c> naming a constant in another assembly, and the project references that
    /// make naming it possible) — reading only the compiled side would let those drift apart with the
    /// test still green.
    /// </summary>
    private static string SolutionFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir, "Odyssey.sln")))
        {
            dir = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }

        Assert.NotNull(dir);
        return System.IO.Path.Combine([dir!, .. parts]);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    /// <summary>Captures the API's log output, for the three audit assertions.</summary>
    private sealed class LoggingApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId)
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(Logs));
        }
    }
}
