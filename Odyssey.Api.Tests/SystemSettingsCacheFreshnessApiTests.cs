using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Core.Finance;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #28's literal symptom, end to end: a saved cap is in force on the very next request rather
/// than up to the 30s TTL later.
///
/// <para>
/// <strong>Why this exists next to <see cref="SystemSettingsCacheEvictionTests"/> rather than instead
/// of it.</strong> Those four are reflection-only: they prove each descriptor <em>names</em> the entry
/// that serves it, which is exactly the defect class that occurred (a copy-pasted constant) and is the
/// half that generalises to every future setting. What they cannot see is a failure that leaves every
/// constant textually right and still strands the value — a wrong DI lifetime on the cache or a lookup
/// would do it. These drive the real <c>PUT</c> through the real pipeline against a shared
/// <c>IMemoryCache</c>, so the two halves fail for different reasons.
/// </para>
///
/// <para>
/// The cache is warmed through the lookup first, deliberately. Against a cold cache a save looks
/// correct whatever it evicts, because the next read has to hit the database anyway — which is what
/// made the original bug present as intermittent.
/// </para>
/// </summary>
public class SystemSettingsCacheFreshnessApiTests
{
    private const string Path = "/api/system-settings";
    private const string ActorUserId = "33333333-3333-3333-3333-333333333333";

    private static readonly string[] ReadAndOrdinary =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    private static async Task<FinanceRequestCaps> ReadCapsAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISystemSettingsLookup>().GetRequestCapsAsync();
    }

    private static async Task<InsurancePolicySettings> ReadInsuranceAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<ISystemSettingsLookup>().GetInsurancePolicySettingsAsync();
    }

    private static async Task SaveAsync(HttpClient client, SystemSettingsUpdate update)
    {
        var response = await client.PutAsJsonAsync(Path, update);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Saving_the_renewals_cap_is_in_force_on_the_next_request()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var before = await ReadCapsAsync(factory);
        var wanted = before.MaxRenewalsPerPolicy - 1;

        await SaveAsync(client, new SystemSettingsUpdate { InsuranceMaxRenewalsPerPolicy = wanted });

        var after = await ReadCapsAsync(factory);
        Assert.Equal(wanted, after.MaxRenewalsPerPolicy);
    }

    [Fact]
    public async Task Saving_the_files_per_parent_cap_is_in_force_on_the_next_request()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var before = await ReadCapsAsync(factory);
        var wanted = before.MaxFilesPerParent - 1;

        await SaveAsync(client, new SystemSettingsUpdate { InsuranceMaxFilesPerParent = wanted });

        var after = await ReadCapsAsync(factory);
        Assert.Equal(wanted, after.MaxFilesPerParent);
    }

    /// <summary>
    /// The other half of the original defect, and the reason the fix is not just "evict both entries".
    /// A cap lives on a different record from the insurance snapshot, so saving one must leave the
    /// other's warm entry alone — dropping it costs a needless re-read of two rows the save never
    /// touched.
    /// </summary>
    [Fact]
    public async Task Saving_a_cap_leaves_the_insurance_snapshot_entry_warm()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        _ = await ReadCapsAsync(factory);
        var insuranceBefore = await ReadInsuranceAsync(factory);

        await SaveAsync(client, new SystemSettingsUpdate
        {
            InsuranceMaxRenewalsPerPolicy = insuranceBefore.MaxSummaryPolicies + 7,
        });

        // Same instance back means the entry was never evicted: the lookup caches the record it built,
        // so a re-read after an eviction would hand back a different object with equal values.
        Assert.Same(insuranceBefore, await ReadInsuranceAsync(factory));
    }

    /// <summary>The converse: a genuine insurance-snapshot change must still land immediately.</summary>
    [Fact]
    public async Task Saving_the_expiring_soon_window_is_in_force_on_the_next_request()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var before = await ReadInsuranceAsync(factory);
        var wanted = before.ExpiringSoonWindowDays + 1;

        await SaveAsync(client, new SystemSettingsUpdate { InsuranceExpiringSoonWindowDays = wanted });

        var after = await ReadInsuranceAsync(factory);
        Assert.Equal(wanted, after.ExpiringSoonWindowDays);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
