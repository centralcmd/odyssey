using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.SystemSettings;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-level coverage for the three Subscriptions summary limits issue #437 migrated out of two
/// <c>private const</c>s and one missing bound, plus the store-wide read-path hardening that landed
/// with them.
///
/// <para>
/// The hardening tests carry the most weight. Before them, four of the five <c>Project</c>
/// implementations ended in a throwing parse inside an untry-caught loop over every descriptor, so a
/// single corrupt row returned <c>500</c> from <c>GET /api/system-settings</c> — the page an
/// administrator would use to repair that row — and made a successful <c>PUT</c> <c>500</c>
/// <em>after</em> committing. Both entry points are exercised here, because both return through the
/// same private <c>AssembleAsync</c>.
/// </para>
/// </summary>
public class SubscriptionSystemSettingsApiTests
{
    private const string Path = "/api/system-settings";
    private const string ActorUserId = "77777777-7777-7777-7777-777777777777";

    private static readonly string[] ReadOnly = [PermissionClaims.SystemSettingsRead];

    private static readonly string[] ReadAndCountUpdate =
    [
        PermissionClaims.SystemSettingsRead,
        PermissionClaims.SystemSettingsUpdate,
    ];

    private static readonly string[] ReadAndBoth =
    [
        PermissionClaims.SystemSettingsRead,
        PermissionClaims.SystemSettingsUpdate,
        PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    // ── AC 1 — the seeded values ────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 1. On a freshly migrated database the three read back at exactly the values the constants
    /// they replace carried (45, 6) plus the new bound (1000), so a default install is behaviourally
    /// identical for the two migrated ones.
    /// </summary>
    [Fact]
    public async Task Get_OnAFreshlyMigratedDatabase_ReturnsTheSeededSubscriptionLimits()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            await context.Database.EnsureCreatedAsync();
        }

        var dto = await GetSettings(factory);

        Assert.Equal(45, dto.SubscriptionRenewalWindowDays);
        Assert.Equal(6, dto.SubscriptionMaxSummaryRenewals);
        Assert.Equal(1000, dto.SubscriptionMaxSummarySubscriptions);
    }

    /// <summary>The seeds must equal the shipped defaults, or the migration silently changes behaviour.</summary>
    [Fact]
    public void TheSeededValues_AreTheShippedDefaults()
    {
        Assert.Equal(45, SystemSettingsDefaults.SubscriptionRenewalWindowDays);
        Assert.Equal(6, SystemSettingsDefaults.SubscriptionMaxSummaryRenewals);
        Assert.Equal(1000, SystemSettingsDefaults.SubscriptionMaxSummarySubscriptions);
    }

    // ── AC 2, AC 4, AC 5 — the write path ───────────────────────────────────────────────────────

    [Fact]
    public async Task Put_WithARaiseInsideTheBound_IsHonouredAndEchoedOnTheFullDto()
    {
        await using var factory = new ApiFactory(ReadAndCountUpdate);
        var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            Path, new SystemSettingsUpdate { SubscriptionRenewalWindowDays = 90 });

        // 200 with the FULL DTO, not 204: the client gates its entire success path on a response body,
        // so a 204 would silently skip the dirty-dot clear, the saved flash and every cache invalidation.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.NotNull(dto);
        Assert.Equal(90, dto!.SubscriptionRenewalWindowDays);

        Assert.Equal(90, (await GetSettings(factory)).SubscriptionRenewalWindowDays);
    }

    /// <summary>
    /// AC 4. Both ends of each bound, and the stored value is unchanged by a rejected write. The
    /// renewals cap is the interesting one: its maximum is 50, not the 100000 its sibling caps carry.
    /// </summary>
    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.SubscriptionRenewalWindowDays), 0)]
    [InlineData(nameof(SystemSettingsUpdate.SubscriptionRenewalWindowDays), 366)]
    [InlineData(nameof(SystemSettingsUpdate.SubscriptionMaxSummaryRenewals), 0)]
    [InlineData(nameof(SystemSettingsUpdate.SubscriptionMaxSummaryRenewals), 51)]
    [InlineData(nameof(SystemSettingsUpdate.SubscriptionMaxSummarySubscriptions), 0)]
    [InlineData(nameof(SystemSettingsUpdate.SubscriptionMaxSummarySubscriptions), 100001)]
    public async Task Put_WithAnOutOfRangeValue_IsRejectedAndChangesNothing(string field, int value)
    {
        await using var factory = new ApiFactory(ReadAndCountUpdate);
        var client = factory.CreateClient();

        var before = await GetSettings(factory);

        var request = new SystemSettingsUpdate();
        typeof(SystemSettingsUpdate).GetProperty(field)!.SetValue(request, value);

        var response = await client.PutAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var after = await GetSettings(factory);
        Assert.Equal(before.SubscriptionRenewalWindowDays, after.SubscriptionRenewalWindowDays);
        Assert.Equal(before.SubscriptionMaxSummaryRenewals, after.SubscriptionMaxSummaryRenewals);
        Assert.Equal(before.SubscriptionMaxSummarySubscriptions, after.SubscriptionMaxSummarySubscriptions);
    }

    /// <summary>The three bounds, asserted against the attribute so the numbers above are not a coincidence.</summary>
    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.SubscriptionRenewalWindowDays), 1, 365)]
    [InlineData(nameof(SystemSettingsUpdate.SubscriptionMaxSummaryRenewals), 1, 50)]
    [InlineData(nameof(SystemSettingsUpdate.SubscriptionMaxSummarySubscriptions), 1, 100000)]
    public void TheWriteBound_IsTheAdvertisedRange(string field, int min, int max)
    {
        var range = typeof(SystemSettingsUpdate).GetProperty(field)!.GetCustomAttribute<RangeAttribute>();

        Assert.NotNull(range);
        Assert.Equal(min, range!.Minimum);
        Assert.Equal(max, range.Maximum);
    }

    /// <summary>
    /// AC 5. Each of the three individually requires <c>system-settings.update</c>. A caller holding
    /// only the read claim is rejected wholesale, and nothing is persisted.
    /// </summary>
    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.SubscriptionRenewalWindowDays), 90)]
    [InlineData(nameof(SystemSettingsUpdate.SubscriptionMaxSummaryRenewals), 10)]
    [InlineData(nameof(SystemSettingsUpdate.SubscriptionMaxSummarySubscriptions), 500)]
    public async Task Put_WithoutTheCountUpdateClaim_IsForbiddenPerField(string field, int value)
    {
        await using var factory = new ApiFactory(ReadOnly);
        var client = factory.CreateClient();

        var request = new SystemSettingsUpdate();
        typeof(SystemSettingsUpdate).GetProperty(field)!.SetValue(request, value);

        var response = await client.PutAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var after = await GetSettings(factory);
        Assert.Equal(45, after.SubscriptionRenewalWindowDays);
        Assert.Equal(6, after.SubscriptionMaxSummaryRenewals);
        Assert.Equal(1000, after.SubscriptionMaxSummarySubscriptions);
    }

    // ── AC 10 — mass assignment ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 10. All three new write fields are <c>int?</c> scalars: none accepts a nested object and none
    /// establishes a relationship, so no over-posting surface is added. A body carrying an unexpected
    /// nested object alongside them neither creates nor mutates any entity.
    /// </summary>
    [Fact]
    public async Task Put_WithAnUnexpectedNestedObject_CreatesAndMutatesNothing()
    {
        await using var factory = new ApiFactory(ReadAndCountUpdate);
        var client = factory.CreateClient();

        var body = new StringContent(
            """
            {
              "subscriptionRenewalWindowDays": 90,
              "subscriptionMaxSummaryRenewals": { "value": 40 },
              "subscription": { "subscriptionId": "00000000-0000-0000-0000-000000000001", "name": "injected" }
            }
            """,
            System.Text.Encoding.UTF8, "application/json");

        await client.PutAsync(Path, body);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var keys = await context.SystemSettings.AsNoTracking().Select(row => row.Key).ToListAsync();

        Assert.All(keys, key => Assert.Contains(key, SystemSettingsKeys.AllKeys));
    }

    /// <summary>The three write fields are scalars, asserted structurally rather than by example.</summary>
    [Fact]
    public void TheThreeWriteFields_AreNullableIntScalars()
    {
        string[] fields =
        [
            nameof(SystemSettingsUpdate.SubscriptionRenewalWindowDays),
            nameof(SystemSettingsUpdate.SubscriptionMaxSummaryRenewals),
            nameof(SystemSettingsUpdate.SubscriptionMaxSummarySubscriptions),
        ];

        Assert.All(fields, field =>
            Assert.Equal(typeof(int?), typeof(SystemSettingsUpdate).GetProperty(field)!.PropertyType));
    }

    // ── AC 20, AC 21 — a corrupt row no longer 500s, for ANY kind ───────────────────────────────

    /// <summary>
    /// AC 20. Asserted once per unsafe kind, and three of the four use a key OUTSIDE this round's
    /// three — the defect spans the store rather than the new keys.
    ///
    /// <para>
    /// <c>FileAnalysisEnabled</c> is the deliberate bool case: it is the switch that stops personal
    /// data leaving the deployment for a third-party processor, so an administrator locked out of the
    /// page that repairs it is the availability harm at its sharpest.
    /// </para>
    /// </summary>
    [Theory]
    // BoolSetting — a perimeter/transfer toggle.
    [InlineData(SystemSettingsKeys.FileAnalysisEnabled, "not-a-bool")]
    // IntSetting — one of this round's own keys.
    [InlineData(SystemSettingsKeys.SubscriptionRenewalWindowDays, "abc")]
    // CapacitySetting — a count cap, whose non-throwing parser already existed and was never adopted.
    [InlineData(SystemSettingsKeys.ContactVCardMaxExportRows, "not-a-count")]
    // DecimalSetting — the auto-link threshold.
    [InlineData(SystemSettingsKeys.FileAnalysisMatchAutoLinkThreshold, "zero point six")]
    public async Task Get_WithACorruptRowOfAnyKind_ReturnsOkWithThatKeysDefault(string key, string corrupt)
    {
        await using var factory = new ApiFactory(ReadOnly);
        await SystemSettingsSeed.SetAsync(factory.Services, key, corrupt);

        var client = factory.CreateClient();
        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.NotNull(dto);

        // The projected value is the compiled default for that key.
        var descriptor = SystemSettingsRegistry.ByKey[key];
        var expected = new SystemSettingsDto();
        descriptor.Project(descriptor.DefaultValue, expected);
        var property = typeof(SystemSettingsDto).GetProperty(descriptor.FieldName)!;

        Assert.Equal(property.GetValue(expected), property.GetValue(dto!));
    }

    /// <summary>
    /// AC 21. A <c>PUT</c> that commits successfully but meets a corrupt row during re-assembly still
    /// returns <c>200</c> — pinning that a write cannot <c>500</c> <em>after</em> committing. The
    /// corrupt key is deliberately one the request does not touch.
    /// </summary>
    [Fact]
    public async Task Put_ThatCommitsAndThenMeetsACorruptRow_StillReturnsOkWithTheAssembledDto()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.FileAnalysisMatchAutoLinkThreshold, "corrupt");

        var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync(
            Path, new SystemSettingsUpdate { SubscriptionMaxSummaryRenewals = 10 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.NotNull(dto);
        Assert.Equal(10, dto!.SubscriptionMaxSummaryRenewals);

        // …and the write really did commit.
        Assert.Equal(10, (await GetSettings(factory)).SubscriptionMaxSummaryRenewals);
    }

    // ── AC 23 — Project clamps too ──────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 23. An out-of-bound row is clamped by the PROJECTION as well as by the engine, so the two
    /// report the same number for that case. Both clamp against the same
    /// <see cref="SystemSettingsBounds"/> pair, which is why the agreement is a consequence rather than
    /// a coincidence.
    /// </summary>
    [Theory]
    [InlineData(SystemSettingsKeys.SubscriptionMaxSummarySubscriptions, "2000000000", 100000)]
    [InlineData(SystemSettingsKeys.SubscriptionMaxSummaryRenewals, "5000", 50)]
    [InlineData(SystemSettingsKeys.SubscriptionRenewalWindowDays, "0", 1)]
    public async Task Get_WithAnOutOfBoundRow_ProjectsTheNearerBound(string key, string stored, int expected)
    {
        await using var factory = new ApiFactory(ReadOnly);
        await SystemSettingsSeed.SetAsync(factory.Services, key, stored);

        var dto = await GetSettings(factory);
        var property = typeof(SystemSettingsDto).GetProperty(SystemSettingsRegistry.ByKey[key].FieldName)!;

        Assert.Equal(expected, property.GetValue(dto));
    }

    // ── AC 29 — the administrator is told, on the GET ───────────────────────────────────────────

    /// <summary>
    /// AC 29. A corrupt row produces a <c>Warnings</c> entry keyed by that setting's
    /// <c>SystemSettingsUpdate</c> property name — the same join key <c>ApiProblem.Errors</c> uses —
    /// and a matching <c>ProjectionFaults</c> entry saying which condition applies.
    ///
    /// <para>
    /// <strong>Asserted on the GET, not on the PUT response.</strong> <c>Save()</c> writes every
    /// editable row — <c>IntRequest</c> gates on <c>CanEdit</c>, never on dirty state — so the page
    /// repairs the corrupt row on its way out and the <c>PUT</c> body can never carry the advisory.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Get_WithAnUnreadableRow_ReportsTheUnreadablePredicateAndKind()
    {
        await using var factory = new ApiFactory(ReadOnly);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.SubscriptionRenewalWindowDays, "abc");

        var dto = await GetSettings(factory);
        var field = nameof(SystemSettingsUpdate.SubscriptionRenewalWindowDays);

        Assert.Equal(SettingFaultKind.Unreadable, dto.ProjectionFaults[field]);
        Assert.Equal(
            "The stored value couldn't be read, so the shipped default is shown.",
            dto.Warnings[field]);

        // The stored value is NEVER echoed back to the caller.
        Assert.DoesNotContain("abc", dto.Warnings[field], StringComparison.Ordinal);
    }

    /// <summary>
    /// The clamped predicate is pinned separately, because "could not be read" is <em>false</em> for a
    /// clamped row: the value parsed perfectly well, it was simply outside its pair. It names the bound
    /// pair and the EFFECTIVE value, none of which is the stored value.
    ///
    /// <para>
    /// The range is written out in words rather than with an en dash: at default punctuation levels
    /// NVDA and JAWS speak neither <c>–</c> nor <c>-</c>, and "1 50" is ambiguous with a list — on the
    /// one clause that exists to be acted on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Get_WithAClampedRow_ReportsTheClampedPredicateAndKind()
    {
        await using var factory = new ApiFactory(ReadOnly);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.SubscriptionMaxSummarySubscriptions, "2000000000");

        var dto = await GetSettings(factory);
        var field = nameof(SystemSettingsUpdate.SubscriptionMaxSummarySubscriptions);

        Assert.Equal(SettingFaultKind.Clamped, dto.ProjectionFaults[field]);
        Assert.Equal(
            "The stored value was outside the allowed range of 1 to 100000 and is being read as 100000.",
            dto.Warnings[field]);
        Assert.DoesNotContain("2000000000", dto.Warnings[field], StringComparison.Ordinal);
    }

    /// <summary>
    /// The precedence rule, asserted on a field carrying <strong>both</strong> conditions at once —
    /// <c>SubscriptionMaxSummaryRenewals</c> stored as <c>"5000"</c> clamps to 50, and 50 is above the
    /// shipped default of 6, so it also satisfies <c>AboveDefault</c>. The row must show the PROJECTION
    /// advisory.
    ///
    /// <para>
    /// Without the both-conditions case this would pass on any key that has no <c>Advise</c> delegate
    /// at all — the "passes on the omission it exists to catch" shape.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Get_WithAFieldCarryingBothAdvisories_ShowsTheProjectionOne()
    {
        await using var factory = new ApiFactory(ReadOnly);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.SubscriptionMaxSummaryRenewals, "5000");

        var dto = await GetSettings(factory);
        var field = nameof(SystemSettingsUpdate.SubscriptionMaxSummaryRenewals);

        // The clamp really does put it above the shipped default, so AboveDefault genuinely fires too.
        Assert.Equal(50, dto.SubscriptionMaxSummaryRenewals);
        Assert.True(50 > SystemSettingsDefaults.SubscriptionMaxSummaryRenewals);

        Assert.Equal(SettingFaultKind.Clamped, dto.ProjectionFaults[field]);
        Assert.Equal(
            "The stored value was outside the allowed range of 1 to 50 and is being read as 50.",
            dto.Warnings[field]);
        Assert.DoesNotContain("above the shipped default", dto.Warnings[field], StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 19. The cost advisory on a <strong>healthy</strong> row, and neither of the other two rows
    /// carries one. The healthy-row qualifier is load-bearing: on a clamped row the projection advisory
    /// takes precedence, so without it this and the precedence test could both pass while the
    /// precedence rule was never implemented.
    /// </summary>
    [Fact]
    public async Task Put_RaisingTheRenewalCapOnAHealthyRow_SurfacesTheCostAdvisoryOnly()
    {
        await using var factory = new ApiFactory(ReadAndCountUpdate);
        var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            Path,
            new SystemSettingsUpdate
            {
                SubscriptionMaxSummaryRenewals = 40,
                SubscriptionRenewalWindowDays = 90,
                SubscriptionMaxSummarySubscriptions = 5000,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.NotNull(dto);

        Assert.Equal(
            "Set to 40, above the shipped default of 6. Each renewal is rendered as its own block above the list.",
            dto!.Warnings[nameof(SystemSettingsUpdate.SubscriptionMaxSummaryRenewals)]);

        // A cost advisory is not a fault: no ProjectionFaults entry, and the write was not blocked.
        Assert.Empty(dto.ProjectionFaults);
        Assert.False(dto.Warnings.ContainsKey(nameof(SystemSettingsUpdate.SubscriptionRenewalWindowDays)));
        Assert.False(dto.Warnings.ContainsKey(nameof(SystemSettingsUpdate.SubscriptionMaxSummarySubscriptions)));
    }

    /// <summary>
    /// The two kinds are distinguishable from the DTO alone, and a healthy row carrying only a cost
    /// advisory produces NO <c>ProjectionFaults</c> entry — without which an implementation with no
    /// discriminator at all would pass.
    /// </summary>
    [Fact]
    public async Task Get_WithOneRowOfEachKind_DistinguishesThemOnTheWire()
    {
        await using var factory = new ApiFactory(ReadOnly);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.SubscriptionRenewalWindowDays, "abc");
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.SubscriptionMaxSummarySubscriptions, "2000000000");
        // Healthy but above its default — a cost advisory with no fault.
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.SubscriptionMaxSummaryRenewals, "20");

        var dto = await GetSettings(factory);

        Assert.Equal(
            SettingFaultKind.Unreadable,
            dto.ProjectionFaults[nameof(SystemSettingsUpdate.SubscriptionRenewalWindowDays)]);
        Assert.Equal(
            SettingFaultKind.Clamped,
            dto.ProjectionFaults[nameof(SystemSettingsUpdate.SubscriptionMaxSummarySubscriptions)]);
        Assert.False(
            dto.ProjectionFaults.ContainsKey(nameof(SystemSettingsUpdate.SubscriptionMaxSummaryRenewals)));
        Assert.True(
            dto.Warnings.ContainsKey(nameof(SystemSettingsUpdate.SubscriptionMaxSummaryRenewals)));
    }

    /// <summary>
    /// The enum's members start at 1, so neither <c>GetValueOrDefault</c> nor a missing-key
    /// <c>TryGetValue</c> yields the alarming kind for a healthy field — and the client's existing
    /// advisory reader is exactly that miss-path idiom.
    /// </summary>
    [Fact]
    public void SettingFaultKind_HasNoZeroMember()
    {
        Assert.Equal(1, (int)SettingFaultKind.Unreadable);
        Assert.Equal(2, (int)SettingFaultKind.Clamped);
        Assert.DoesNotContain(0, Enum.GetValues<SettingFaultKind>().Select(kind => (int)kind));
    }

    // ── AC 13, AC 14 — cache eviction, in both directions ───────────────────────────────────────

    /// <summary>
    /// AC 13. A save evicts the subscriptions entry synchronously on the writing instance, so the very
    /// next summary read sees the new value without waiting out the 30-second TTL — the administrator
    /// who just changed it never gets served this instance's own stale read.
    /// </summary>
    [Fact]
    public async Task Put_ChangingASubscriptionLimit_EvictsTheSubscriptionsCacheEntry()
    {
        await using var factory = new ApiFactory(ReadAndCountUpdate);

        await WarmSubscriptionLookup(factory);
        Assert.True(Cache(factory).TryGetValue(SubscriptionCacheKey, out _));

        var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync(
            Path, new SystemSettingsUpdate { SubscriptionRenewalWindowDays = 120 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.False(Cache(factory).TryGetValue(SubscriptionCacheKey, out _));

        // …and the next read really does see the new value.
        Assert.Equal(120, (await ReadSubscriptionLookup(factory)).RenewalWindowDays);
    }

    /// <summary>
    /// AC 14, first direction: an unrelated save must not evict the subscriptions entry. Sharing one
    /// cache key across settings families is the mistake this round's separate key exists to avoid.
    /// </summary>
    [Fact]
    public async Task Put_ChangingAnUnrelatedSetting_LeavesTheSubscriptionsEntryAlone()
    {
        await using var factory = new ApiFactory(ReadAndCountUpdate);

        await WarmSubscriptionLookup(factory);

        var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync(
            Path, new SystemSettingsUpdate { InsuranceExpiringSoonWindowDays = 99 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(Cache(factory).TryGetValue(SubscriptionCacheKey, out _));
    }

    /// <summary>AC 14, the other direction: a subscriptions save must not evict the insurance entry.</summary>
    [Fact]
    public async Task Put_ChangingASubscriptionLimit_LeavesTheInsuranceEntryAlone()
    {
        await using var factory = new ApiFactory(ReadAndCountUpdate);

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Odyssey.Core.Finance.ISystemSettingsLookup>()
                .GetInsurancePolicySettingsAsync();
        }

        Assert.True(Cache(factory).TryGetValue(InsuranceCacheKey, out _));

        var client = factory.CreateClient();
        await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { SubscriptionMaxSummaryRenewals = 12 });

        Assert.True(Cache(factory).TryGetValue(InsuranceCacheKey, out _));
    }

    // ── AC 6 — the constants are gone, at source level ──────────────────────────────────────────

    /// <summary>
    /// AC 6. A source-level assertion, so the constants cannot be reintroduced alongside the settings
    /// and quietly win again.
    /// </summary>
    [Fact]
    public void SubscriptionService_DeclaresNoRenewalWindowOrLimitConstant()
    {
        var source = File.ReadAllText(SolutionFile("Odyssey.Core", "Finance", "SubscriptionService.cs"));

        Assert.DoesNotContain("const int RenewalWindowDays", source, StringComparison.Ordinal);
        Assert.DoesNotContain("const int RenewalLimit", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// None of the three ever had a configuration surface, so none gets a config-adoption entry:
    /// adopting a key that never had one would let a stray environment variable start overriding an
    /// administrator's saved setting.
    /// </summary>
    [Fact]
    public void TheThreeKeys_HaveNoConfigAdoptionEntry()
    {
        var source = File.ReadAllText(
            SolutionFile("Odyssey.MigrationService", "SystemSettingsConfigAdoption.cs"));

        Assert.DoesNotContain("Subscription", source, StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    // The lookup's cache keys are internal to SystemSettingsService, so they are restated here as
    // literals: a rename fails these tests loudly rather than letting them pass for the wrong reason.
    private const string SubscriptionCacheKey = "system-settings:subscription-settings";
    private const string InsuranceCacheKey = "system-settings:insurance-policy-settings";

    private static Microsoft.Extensions.Caching.Memory.IMemoryCache Cache(ApiFactory factory) =>
        factory.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();

    private static async Task WarmSubscriptionLookup(ApiFactory factory) => await ReadSubscriptionLookup(factory);

    private static async Task<Odyssey.Core.Finance.SubscriptionSettings> ReadSubscriptionLookup(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<Odyssey.Core.Finance.ISystemSettingsLookup>()
            .GetSubscriptionSettingsAsync();
    }

    private static async Task<SystemSettingsDto> GetSettings(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);
        Assert.NotNull(dto);
        return dto!;
    }

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
}
