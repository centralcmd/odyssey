using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Api.Identity;
using Odyssey.Api.SystemSettings;
using Odyssey.Dtos.Application;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-level coverage for GET/PUT <c>/api/system-settings</c> (issue #349): the read-claim gate on
/// both verbs, the per-field write-claim split (reject wholesale, no partial apply), the nullable
/// "leave unchanged" write contract, range validation on the two Contracts summary fields, and that audit
/// fields are never accepted from the request body.
/// </summary>
public class SystemSettingsApiTests
{
    private const string ActorUserId = "system-settings-actor-id";
    private const string Path = "/api/system-settings";

    private static readonly string[] ReadOnly = [PermissionClaims.SystemSettingsRead];

    private static readonly string[] ReadAndCosmeticUpdate =
    [
        PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate,
    ];

    private static readonly string[] ReadAndSecurityUpdate =
    [
        PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    private static readonly string[] FullAccess =
    [
        PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate, PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    // ── Claim wiring (compile-time — no DB/HTTP needed) ────────────────────────

    [Fact]
    public void AdminClaims_IncludesAllThreeSystemSettingsClaims()
    {
        Assert.Contains(PermissionClaims.SystemSettingsRead, RolePermissions.AdminClaims);
        Assert.Contains(PermissionClaims.SystemSettingsUpdate, RolePermissions.AdminClaims);
        Assert.Contains(PermissionClaims.SystemSettingsSecurityUpdate, RolePermissions.AdminClaims);
    }

    [Fact]
    public void OwnerUserGuestClaims_NeverIncludeSystemSettingsClaims()
    {
        string[] systemSettingsClaims =
        [
            PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate, PermissionClaims.SystemSettingsSecurityUpdate,
        ];

        foreach (var claim in systemSettingsClaims)
        {
            Assert.DoesNotContain(claim, RolePermissions.OwnerClaims);
            Assert.DoesNotContain(claim, RolePermissions.UserClaims);
            Assert.DoesNotContain(claim, RolePermissions.GuestClaims);
        }
    }

    // ── GET ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithoutReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithReadClaim_ReturnsDefaults_WhenNoRowsSeeded()
    {
        // No EnsureCreated: an empty SystemSettings table falls back to the documented defaults —
        // matching the migration-seeded values exactly, so a caller can never observe the difference.
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.NotNull(dto);
        Assert.False(dto!.RequireTwoFactor);
        Assert.False(dto.TwoFactorEnforced);
        Assert.True(dto.RegistrationRequireAdminApproval);
        Assert.True(dto.EmailRequireConfirmation);
        Assert.Equal(45, dto.ContractEndingWindowDays);
        Assert.Equal(1000, dto.ContractMaxSummaryContracts);

        // Import/export volume caps (issue #343 §6/§13, AC 1) — count defaults are behavior-preserving;
        // the size (MB) defaults were later unified to 64 across all eight fields by a follow-up.
        Assert.Null(dto.ContactVCardMaxExportRows);
        Assert.Null(dto.ContactVCardMaxImportEntries);
        Assert.Equal(64, dto.ContactVCardMaxImportMegabytes);
        Assert.Equal(64, dto.ContactVCardMaxExportMegabytes);
        Assert.Equal(2000, dto.CalendarIcsMaxExportEvents);
        Assert.Equal(2000, dto.CalendarIcsMaxImportEvents);
        Assert.Equal(64, dto.CalendarIcsMaxImportMegabytes);
        Assert.Equal(64, dto.CalendarIcsMaxExportMegabytes);
        Assert.Equal(2000, dto.TaskIcsMaxExportTasks);
        Assert.Equal(2000, dto.TaskIcsMaxImportTasks);
        Assert.Equal(64, dto.TaskIcsMaxImportMegabytes);
        Assert.Equal(64, dto.TaskIcsMaxExportMegabytes);
        Assert.Equal(2000, dto.JournalIcsMaxExportRows);
        Assert.Equal(2000, dto.JournalIcsMaxImportEntries);
        Assert.Equal(64, dto.JournalIcsMaxImportMegabytes);
        Assert.Equal(64, dto.JournalIcsMaxExportMegabytes);
    }

    [Fact]
    public async Task Get_NeverWrites_EvenWhenTableIsEmpty()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        (await client.GetAsync(Path)).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.SystemSettings.ToListAsync());
    }

    [Fact]
    public async Task MigrationSeed_ProducesExactlyTheKnownKeyRows()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var rows = await context.SystemSettings.AsNoTracking().ToListAsync();
        // 59 before issue #437, +3 for the Subscriptions summary limits, +4 for the mail transport
        // and the public link origin (issue #8), +1 for the per-contract term cap (issue #135), +3
        // for the Contracts summary windows and its next-charge row cap, +1 for the per-contract
        // smart-tag cap (issue #166), then -3 when the standalone subscriptions feature was removed
        // and -5 when the standalone insurance-policy feature was, then +1 for the per-property
        // smart-tag cap (issue #167), then +4 for weather (issue #348), then +2 for the exchange-rate sync
        // start date and interval (issue #349), then +1 for the file-storage deletion grace (issue #346).
        Assert.Equal(72, rows.Count);
        Assert.Equal(SystemSettingsKeys.AllKeys.OrderBy(k => k), rows.Select(r => r.Key).OrderBy(k => k));
    }

    // ── PUT — read-claim gate ───────────────────────────────────────────────────

    [Fact]
    public async Task Put_WithoutReadClaim_ReturnsForbidden_EvenWithBothWriteClaims()
    {
        await using var factory = new ApiFactory([PermissionClaims.SystemSettingsUpdate, PermissionClaims.SystemSettingsSecurityUpdate]);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { ContractEndingWindowDays = 60 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_AllFieldsNull_IsNoOp()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate());

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.NotNull(dto);
        Assert.Equal(45, dto!.ContractEndingWindowDays);
        Assert.Equal(1000, dto.ContractMaxSummaryContracts);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.SystemSettings.ToListAsync());
    }

    // ── PUT — split write claim: reject wholesale, no partial apply ───────────

    [Fact]
    public async Task Put_CosmeticOnly_Succeeds_ForCosmeticClaim_WithoutSecurityClaim()
    {
        await using var factory = new ApiFactory(ReadAndCosmeticUpdate);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            ContractEndingWindowDays = 60,
            ContractMaxSummaryContracts = 250,
        });

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal(60, dto!.ContractEndingWindowDays);
        Assert.Equal(250, dto.ContractMaxSummaryContracts);
    }

    [Fact]
    public async Task Put_PerimeterField_WithOnlyCosmeticClaim_ReturnsForbidden_AndPersistsNoField()
    {
        await using var factory = new ApiFactory(ReadAndCosmeticUpdate);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            ContractEndingWindowDays = 60, // this caller CAN set this…
            EmailRequireConfirmation = false,      // …but not this — must reject the whole request.
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("EmailRequireConfirmation", problem);

        // Nothing persisted — including the cosmetic field this caller did have permission for.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.SystemSettings.ToListAsync());
    }

    [Fact]
    public async Task Put_PerimeterOnly_Succeeds_ForSecurityClaim_WithoutCosmeticClaim()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            RegistrationRequireAdminApproval = false,
            EmailRequireConfirmation = false,
            RequireTwoFactor = true,
        });

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.False(dto!.RegistrationRequireAdminApproval);
        Assert.False(dto.EmailRequireConfirmation);
        Assert.True(dto.RequireTwoFactor);
        Assert.False(dto.TwoFactorEnforced); // always false, regardless of the stored value.
    }

    [Fact]
    public async Task Put_CosmeticField_WithOnlySecurityClaim_ReturnsForbidden_AndPersistsNoField()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            EmailRequireConfirmation = false,
            ContractMaxSummaryContracts = 250, // this caller lacks system-settings.update for this.
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.SystemSettings.ToListAsync());
    }

    // ── PUT — nullable contract closes the concurrency gap (checks stored DB values) ──

    [Fact]
    public async Task Put_NullPerimeterFields_DoesNotRevertConcurrentSecurityAdminChange()
    {
        await using var factory = new ApiFactory(permissions: []); // only used to stand up the host.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var service = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();

        var securityAdmin = PrincipalWith(PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate);
        var cosmeticAdmin = PrincipalWith(PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate);

        // Security-only admin flips EmailRequireConfirmation off.
        await service.UpdateAsync(securityAdmin, "security-admin", new SystemSettingsUpdate { EmailRequireConfirmation = false });

        // Cosmetic-only admin's client sends null for every field it structurally cannot edit (§3) —
        // never a copy of the value it loaded — and saves an unrelated cosmetic edit.
        await service.UpdateAsync(cosmeticAdmin, "cosmetic-admin", new SystemSettingsUpdate { ContractEndingWindowDays = 60 });

        // Asserts the STORED value, not just a 200 — an unconfigured Adapt-style mapper would have
        // silently written "false"-turned-"true"/overwritten this and still returned 200.
        var row = await context.SystemSettings.AsNoTracking()
            .SingleAsync(setting => setting.Key == SystemSettingsKeys.EmailRequireConfirmation);
        Assert.Equal("false", row.Value);
        Assert.Equal("security-admin", row.UpdatedBy);
    }

    [Fact]
    public async Task Put_NullField_NeverTouchesThatKeysRow()
    {
        // §3: the null-vs-value decision is keyed off claim possession, never off a transient
        // "disabled" UI state — modeled here directly by a caller that legitimately lacks one claim
        // saving successfully while a field it never touched stays completely untouched.
        await using var factory = new ApiFactory(permissions: []);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var service = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();

        var cosmeticAdmin = PrincipalWith(PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate);
        var dto = await service.UpdateAsync(cosmeticAdmin, "cosmetic-admin", new SystemSettingsUpdate { ContractMaxSummaryContracts = 500 });

        Assert.Equal(500, dto.ContractMaxSummaryContracts);
        var registrationRow = await context.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(setting => setting.Key == SystemSettingsKeys.RegistrationRequireAdminApproval);
        Assert.Null(registrationRow); // never touched — the caller sent null, not a loaded value.
    }

    // ── Validation (DataAnnotations at the model-binding boundary) ─────────────

    [Fact]
    public async Task Put_CosmeticFieldBelowRange_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { ContractEndingWindowDays = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_CosmeticFieldAboveRange_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { ContractMaxSummaryContracts = 100001 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_NullCosmeticField_NeverTriggersRangeCheck()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        // Only RequireTwoFactor is set; both cosmetic fields are null and must pass through untouched.
        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { RequireTwoFactor = true });

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Put_AuditFieldsInRequestBody_AreIgnored()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        // SystemSettingsUpdate has no updatedAt/updatedBy properties at all — a client-supplied value
        // for either is structurally impossible to bind and must be silently dropped, never echoed.
        var response = await client.PutAsync(Path, JsonContent.Create(new
        {
            requireTwoFactor = true,
            updatedAt = new DateTime(1999, 1, 1),
            updatedBy = "someone-else",
        }));

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.NotEqual(new DateTime(1999, 1, 1), dto!.UpdatedAt);
        Assert.Equal(ActorUserId, dto.UpdatedBy);
    }

    // ── Per-row provenance (the "Last changed by" stamp) ──────────────────────────

    [Fact]
    public async Task Get_SeededRows_CarryNoProvenance()
    {
        // A migration-seeded row has no UpdatedBy, so the page reads it as "Never changed".
        await using var factory = new ApiFactory(ReadOnly);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
        }

        using var client = factory.CreateClient();
        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.NotNull(dto);
        Assert.Empty(dto!.Provenance);
    }

    [Fact]
    public async Task Put_StampsOnlyTheRowsItWrote_KeyedByUpdateFieldName()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { ContractEndingWindowDays = 60 });

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        var stamp = Assert.Single(dto!.Provenance);
        Assert.Equal(nameof(SystemSettingsUpdate.ContractEndingWindowDays), stamp.Key);
        Assert.Equal(dto.UpdatedAt, stamp.Value.UpdatedAt);
        Assert.Equal(dto.UpdatedByDisplayName, stamp.Value.UpdatedByDisplayName);
        Assert.False(string.IsNullOrWhiteSpace(stamp.Value.UpdatedByDisplayName));
    }

    [Fact]
    public async Task Provenance_IsPerRow_NotTheMostRecentWriteEverywhere()
    {
        await using var factory = new ApiFactory(permissions: []);
        await SeedUserAsync(factory, FirstWriterId, "first@example.com", displayName: "Ada Lovelace");
        await SeedUserAsync(factory, SecondWriterId, "second@example.com", displayName: "Grace Hopper");
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();
        var admin = PrincipalWith(FullAccess);

        await service.UpdateAsync(admin, FirstWriterId, new SystemSettingsUpdate { ContractEndingWindowDays = 60 });
        var dto = await service.UpdateAsync(admin, SecondWriterId, new SystemSettingsUpdate { ContractChargeWindowDays = 30 });

        var rows = await ReadRowsAsync(factory, SystemSettingsKeys.ContractEndingWindowDays, SystemSettingsKeys.ContractChargeWindowDays);
        Assert.Equal(2, dto.Provenance.Count);

        var ending = dto.Provenance[nameof(SystemSettingsUpdate.ContractEndingWindowDays)];
        Assert.Equal("Ada Lovelace", ending.UpdatedByDisplayName);
        Assert.Equal(rows[SystemSettingsKeys.ContractEndingWindowDays].UpdatedAt, ending.UpdatedAt);

        var charge = dto.Provenance[nameof(SystemSettingsUpdate.ContractChargeWindowDays)];
        Assert.Equal("Grace Hopper", charge.UpdatedByDisplayName);
        Assert.Equal(rows[SystemSettingsKeys.ContractChargeWindowDays].UpdatedAt, charge.UpdatedAt);

        Assert.False(dto.Provenance.ContainsKey(nameof(SystemSettingsUpdate.ContractMaxSummaryContracts)));
    }

    [Fact]
    public async Task Provenance_UnresolvableWriter_IsUnknownUser()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();

        var dto = await service.UpdateAsync(
            PrincipalWith([.. FullAccess, PermissionClaims.UsersRead]),
            "no-such-user-id",
            new SystemSettingsUpdate { ContractEndingWindowDays = 60 });

        var stamp = dto.Provenance[nameof(SystemSettingsUpdate.ContractEndingWindowDays)];
        Assert.Equal(UserDisplayNameResolver.UnknownUser, stamp.UpdatedByDisplayName);
    }

    [Theory]
    [InlineData(false, UserDisplayNameResolver.UnknownUser)]
    [InlineData(true, "nameless@example.com")]
    public async Task Provenance_WriterWithoutDisplayName_RevealsEmailOnlyWithUsersRead(bool callerHoldsUsersRead, string expected)
    {
        // Mirrors the page-level UpdatedByDisplayName rule: with no profile name the resolver's only
        // remaining candidate is the email, which only a users.read holder may see.
        await using var factory = new ApiFactory(permissions: []);
        await SeedUserAsync(factory, FirstWriterId, "nameless@example.com", displayName: null);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();
        await service.UpdateAsync(PrincipalWith(FullAccess), FirstWriterId, new SystemSettingsUpdate { ContractEndingWindowDays = 60 });

        string[] readerClaims = callerHoldsUsersRead
            ? [PermissionClaims.SystemSettingsRead, PermissionClaims.UsersRead]
            : [PermissionClaims.SystemSettingsRead];
        var dto = await service.GetAsync(PrincipalWith(readerClaims));

        var stamp = dto.Provenance[nameof(SystemSettingsUpdate.ContractEndingWindowDays)];
        Assert.Equal(expected, stamp.UpdatedByDisplayName);
        Assert.Equal(dto.UpdatedByDisplayName, stamp.UpdatedByDisplayName);
    }

    private const string FirstWriterId = "system-settings-first-writer";
    private const string SecondWriterId = "system-settings-second-writer";

    private static async Task SeedUserAsync(OdysseyApiFactory factory, string id, string email, string? displayName)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
        });
        if (displayName is not null)
        {
            context.UserProfiles.Add(new UserProfile { UserId = id, DisplayName = displayName });
        }

        await context.SaveChangesAsync();
    }

    private static async Task<Dictionary<string, SystemSetting>> ReadRowsAsync(OdysseyApiFactory factory, params string[] keys)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.SystemSettings.AsNoTracking()
            .Where(row => keys.Contains(row.Key))
            .ToDictionaryAsync(row => row.Key);
    }

    [Fact]
    public async Task Provenance_CarriesNoRawUserId()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { ContractEndingWindowDays = 61 });

        response.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = json.RootElement.GetProperty("provenance")
            .GetProperty(nameof(SystemSettingsUpdate.ContractEndingWindowDays));
        Assert.Equal(
            ["updatedAt", "updatedByDisplayName"],
            entry.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(ActorUserId, entry.GetRawText(), StringComparison.Ordinal);
    }

    private static ClaimsPrincipal PrincipalWith(params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, ActorUserId) };
        claims.AddRange(permissions.Select(p => new Claim(PermissionClaims.Type, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
