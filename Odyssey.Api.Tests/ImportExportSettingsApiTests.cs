using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-level coverage for the sixteen import/export volume caps added to
/// <c>GET</c>/<c>PUT /api/system-settings</c> and the new <c>GET /api/import-limits</c> endpoint
/// (issue #343 §16, extended post-#343 with a per-surface export size cap and a Tasks export row
/// cap): contract semantics (the tri-state <see cref="CapacityLimit"/> shape, per-surface size
/// ranges, the round-trip rule, the round trip being a no-op), the claim split between count and
/// size caps, and the effective-limits endpoint's shape/auth.
/// </summary>
public class ImportExportSettingsApiTests
{
    private const string ActorUserId = "import-export-settings-actor-id";
    private const string SettingsPath = "/api/system-settings";
    private const string LimitsPath = "/api/import-limits";

    private static readonly string[] FullAccess =
    [
        PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate, PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    private static readonly string[] CountOnly =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    private static readonly string[] SizeOnly =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate];

    // ── Contract semantics (AC 12-16) ───────────────────────────────────────────

    [Fact]
    public async Task Put_OmittingCountField_LeavesStoredValueAndUpdatedAtUnchanged()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        // First write establishes a known, non-default value.
        (await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            ContactVCardMaxExportRows = new CapacityLimit { Value = 12345 },
        })).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var before = await context.SystemSettings.AsNoTracking()
            .SingleAsync(s => s.Key == SystemSettingsKeys.ContactVCardMaxExportRows);

        // A second, unrelated PUT omits the field entirely (null) — must not touch it.
        (await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            InsuranceMaxSummaryPolicies = 42,
        })).EnsureSuccessStatusCode();

        var after = await context.SystemSettings.AsNoTracking()
            .SingleAsync(s => s.Key == SystemSettingsKeys.ContactVCardMaxExportRows);
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Put_CapacityLimitWithBothUnlimitedAndValue_ReturnsBadRequest_PersistsNothing()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        var response = await client.PutAsync(SettingsPath, JsonContent.Create(new
        {
            contactVCardMaxExportRows = new { unlimited = true, value = 500 },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.SystemSettings.ToListAsync());
    }

    [Fact]
    public async Task Put_CapacityLimitWithNeitherUnlimitedNorValue_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        var response = await client.PutAsync(SettingsPath, JsonContent.Create(new
        {
            contactVCardMaxExportRows = new { unlimited = false },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(1024)] // at the shared ceiling — succeeds for all eight fields
    [InlineData(1025)] // one above it — rejected for all eight fields
    public async Task Put_AllEightSizeFields_ShareTheSame1024MbCeiling(int megabytes)
    {
        // Follow-up to #343: contacts' size range was originally narrower ([1,512]) than the three ICS
        // surfaces' ([1,1024]); a later request unified all four surfaces to the same [1,1024] range,
        // both import and export. Pins that this now holds for every one of the eight size fields —
        // PR #403 test-review finding: the previous version of this test only exercised 3 of the 8
        // despite its name/comment claiming full coverage.
        var expected = megabytes <= 1024 ? HttpStatusCode.OK : HttpStatusCode.BadRequest;

        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        var contactsImport = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            ContactVCardMaxImportMegabytes = megabytes,
        });
        Assert.Equal(expected, contactsImport.StatusCode);

        var contactsExport = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            ContactVCardMaxExportMegabytes = megabytes,
        });
        Assert.Equal(expected, contactsExport.StatusCode);

        var calendarImport = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            CalendarIcsMaxImportMegabytes = megabytes,
        });
        Assert.Equal(expected, calendarImport.StatusCode);

        var calendarExport = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            CalendarIcsMaxExportMegabytes = megabytes,
        });
        Assert.Equal(expected, calendarExport.StatusCode);

        var taskImport = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            TaskIcsMaxImportMegabytes = megabytes,
        });
        Assert.Equal(expected, taskImport.StatusCode);

        var taskExport = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            TaskIcsMaxExportMegabytes = megabytes,
        });
        Assert.Equal(expected, taskExport.StatusCode);

        var journalImport = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            JournalIcsMaxImportMegabytes = megabytes,
        });
        Assert.Equal(expected, journalImport.StatusCode);

        var journalExport = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            JournalIcsMaxExportMegabytes = megabytes,
        });
        Assert.Equal(expected, journalExport.StatusCode);
    }

    [Fact]
    public async Task Put_CalendarExportEventsUnlimited_Succeeds_NotCoupledToTheAggregateExportsHardCeiling()
    {
        // Follow-up fix (post-#343): CalendarIcsMaxExportEvents previously rejected "unlimited" and any
        // value above CalendarIcsService.MaxAggregateExportRows (20,000), on the mistaken assumption
        // that this setting also bounds the no-filter whole-calendar download. It doesn't — that
        // download uses the hard-coded constant directly, regardless of this setting (see
        // SystemSettingsService.UpdateAsync's Phase 1b comment) — so this field must behave exactly
        // like every other export count cap, "No limit" included.
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        var unlimited = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            CalendarIcsMaxExportEvents = new CapacityLimit { Unlimited = true },
            CalendarIcsMaxImportEvents = new CapacityLimit { Unlimited = true },
        });
        Assert.Equal(HttpStatusCode.OK, unlimited.StatusCode);

        var aboveTheOldCeiling = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            CalendarIcsMaxExportEvents = new CapacityLimit { Value = 50_000 },
            CalendarIcsMaxImportEvents = new CapacityLimit { Value = 50_000 },
        });
        Assert.Equal(HttpStatusCode.OK, aboveTheOldCeiling.StatusCode);
    }

    [Fact]
    public async Task Put_UnlimitedCount_SetsNoLimit()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            ContactVCardMaxImportEntries = new CapacityLimit { Unlimited = true },
        });

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Null(dto!.ContactVCardMaxImportEntries);
    }

    [Fact]
    public async Task GetThenPut_EveryField_ChangesNoStoredValue_NorUpdatedAt()
    {
        // AC 16: a GET→PUT round trip (adapting int? → CapacityLimit) is a true no-op.
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        // Establish a row for every one of the sixteen fields first (touching only some would leave the
        // rest to be lazily row-created — and hence UpdatedAt-stamped — by the round-trip PUT itself,
        // which is a real but separate "should never happen post-migration" edge case, not what AC 16
        // is testing). Values are deliberately non-default where that's meaningful.
        (await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            ContactVCardMaxExportRows = new CapacityLimit { Value = 100 },
            ContactVCardMaxImportEntries = new CapacityLimit { Value = 200 },
            ContactVCardMaxImportMegabytes = 100,
            ContactVCardMaxExportMegabytes = 100,
            CalendarIcsMaxExportEvents = new CapacityLimit { Value = 500 },
            CalendarIcsMaxImportEvents = new CapacityLimit { Value = 500 },
            CalendarIcsMaxImportMegabytes = 10,
            CalendarIcsMaxExportMegabytes = 10,
            TaskIcsMaxExportTasks = new CapacityLimit { Value = 700 },
            TaskIcsMaxImportTasks = new CapacityLimit { Value = 700 },
            TaskIcsMaxImportMegabytes = 10,
            TaskIcsMaxExportMegabytes = 10,
            JournalIcsMaxExportRows = new CapacityLimit { Value = 300 },
            JournalIcsMaxImportEntries = new CapacityLimit { Value = 400 },
            JournalIcsMaxImportMegabytes = 10,
            JournalIcsMaxExportMegabytes = 10,
        })).EnsureSuccessStatusCode();

        var loaded = (await (await client.GetAsync(SettingsPath)).Content.ReadFromJsonAsync<SystemSettingsDto>())!;

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        // Only the sixteen import/export keys are in scope for AC 16 — unlike these, the original five
        // fields' Apply helpers unconditionally bump UpdatedAt whenever present (pre-existing #349
        // behavior, out of this issue's scope), so they are deliberately left null (untouched) below
        // rather than resent, to keep this test isolated to the property under test.
        var importExportKeys = SystemSettingsKeys.AllKeys.Except(
        [
            SystemSettingsKeys.RequireTwoFactor, SystemSettingsKeys.RegistrationRequireAdminApproval,
            SystemSettingsKeys.EmailRequireConfirmation, SystemSettingsKeys.InsuranceExpiringSoonWindowDays,
            SystemSettingsKeys.InsuranceMaxSummaryPolicies,
        ]).ToList();
        var before = await context.SystemSettings.AsNoTracking()
            .Where(s => importExportKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => (s.Value, s.UpdatedAt));

        var roundTrip = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            ContactVCardMaxExportRows = ToCapacity(loaded.ContactVCardMaxExportRows),
            ContactVCardMaxImportEntries = ToCapacity(loaded.ContactVCardMaxImportEntries),
            ContactVCardMaxImportMegabytes = loaded.ContactVCardMaxImportMegabytes,
            ContactVCardMaxExportMegabytes = loaded.ContactVCardMaxExportMegabytes,
            CalendarIcsMaxExportEvents = ToCapacity(loaded.CalendarIcsMaxExportEvents),
            CalendarIcsMaxImportEvents = ToCapacity(loaded.CalendarIcsMaxImportEvents),
            CalendarIcsMaxImportMegabytes = loaded.CalendarIcsMaxImportMegabytes,
            CalendarIcsMaxExportMegabytes = loaded.CalendarIcsMaxExportMegabytes,
            TaskIcsMaxExportTasks = ToCapacity(loaded.TaskIcsMaxExportTasks),
            TaskIcsMaxImportTasks = ToCapacity(loaded.TaskIcsMaxImportTasks),
            TaskIcsMaxImportMegabytes = loaded.TaskIcsMaxImportMegabytes,
            TaskIcsMaxExportMegabytes = loaded.TaskIcsMaxExportMegabytes,
            JournalIcsMaxExportRows = ToCapacity(loaded.JournalIcsMaxExportRows),
            JournalIcsMaxImportEntries = ToCapacity(loaded.JournalIcsMaxImportEntries),
            JournalIcsMaxImportMegabytes = loaded.JournalIcsMaxImportMegabytes,
            JournalIcsMaxExportMegabytes = loaded.JournalIcsMaxExportMegabytes,
        });
        roundTrip.EnsureSuccessStatusCode();

        var after = await context.SystemSettings.AsNoTracking()
            .Where(s => importExportKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => (s.Value, s.UpdatedAt));
        Assert.Equal(before, after);
    }

    private static CapacityLimit ToCapacity(int? value) =>
        value is { } finite ? new CapacityLimit { Value = finite } : new CapacityLimit { Unlimited = true };

    // ── Round-trip rule (AC 24-26) ──────────────────────────────────────────────

    [Fact]
    public async Task Put_ExportExceedsImport_ReturnsBadRequest_NamingBothFields_PersistsNothing()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        // Establish both sides at an equal, consistent baseline first (the default export cap of 2000
        // would otherwise itself violate the round-trip rule against a lone import=1000 write).
        (await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            JournalIcsMaxExportRows = new CapacityLimit { Value = 1000 },
            JournalIcsMaxImportEntries = new CapacityLimit { Value = 1000 },
        })).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            JournalIcsMaxExportRows = new CapacityLimit { Value = 5000 },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("5000", body);
        Assert.Contains("1000", body);

        // Nothing from the rejected PUT persisted — the export cap is still the baseline "1000", not "5000".
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal("1000", await context.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingsKeys.JournalIcsMaxExportRows)
            .Select(s => s.Value)
            .FirstOrDefaultAsync());
    }

    [Fact]
    public async Task Put_OnlyImportLoweredBelowStoredExport_ReturnsBadRequest()
    {
        // Proves post-write evaluation: only the import side is touched, but the stored export side
        // (unlimited) now exceeds it.
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            JournalIcsMaxImportEntries = new CapacityLimit { Value = 100 },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_TaskExportExceedsImport_ReturnsBadRequest()
    {
        // Tasks gained an export row cap as a follow-up to the original issue #343 (previously
        // "Non-Goal 2" — no export cap at all); it now carries the same round-trip rule as the other
        // three surfaces.
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            TaskIcsMaxExportTasks = new CapacityLimit { Value = 1000 },
            TaskIcsMaxImportTasks = new CapacityLimit { Value = 1000 },
        })).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            TaskIcsMaxExportTasks = new CapacityLimit { Value = 5000 },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_EqualValues_Succeeds_AndUnlimitedImportWithFiniteExport_Succeeds()
    {
        await using var factory = new ApiFactory(FullAccess);
        using var client = factory.CreateClient();

        var equal = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            JournalIcsMaxExportRows = new CapacityLimit { Value = 500 },
            JournalIcsMaxImportEntries = new CapacityLimit { Value = 500 },
        });
        equal.EnsureSuccessStatusCode();

        var unlimitedImport = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            JournalIcsMaxExportRows = new CapacityLimit { Value = 999 },
            JournalIcsMaxImportEntries = new CapacityLimit { Unlimited = true },
        });
        unlimitedImport.EnsureSuccessStatusCode();
    }

    // ── Authorization / claim split (AC 17-19) ──────────────────────────────────

    [Fact]
    public async Task Put_UnlimitedCountField_WithoutUpdateClaim_ReturnsForbidden_PersistsNothing()
    {
        // sec F3 / AC 17: the claim check keys off the CapacityLimit object, never `.Value` — a caller
        // sending only {"unlimited": true} (Value null) must still be rejected.
        await using var factory = new ApiFactory([PermissionClaims.SystemSettingsRead]);
        using var client = factory.CreateClient();

        var response = await client.PutAsync(SettingsPath, JsonContent.Create(new
        {
            contactVCardMaxExportRows = new { unlimited = true },
        }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.SystemSettings.ToListAsync());
    }

    [Fact]
    public async Task Put_CountFieldsOnly_SucceedsWithUpdateClaim_SizeFieldRejectedWithoutSecurityClaim()
    {
        await using var factory = new ApiFactory(CountOnly);
        using var client = factory.CreateClient();

        var countsOnly = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            ContactVCardMaxExportRows = new CapacityLimit { Unlimited = true },
            ContactVCardMaxImportEntries = new CapacityLimit { Unlimited = true },
            CalendarIcsMaxExportEvents = new CapacityLimit { Value = 100 },
            CalendarIcsMaxImportEvents = new CapacityLimit { Value = 100 },
            TaskIcsMaxExportTasks = new CapacityLimit { Value = 100 },
            TaskIcsMaxImportTasks = new CapacityLimit { Value = 100 },
            JournalIcsMaxExportRows = new CapacityLimit { Value = 100 },
            JournalIcsMaxImportEntries = new CapacityLimit { Value = 100 },
        });
        countsOnly.EnsureSuccessStatusCode();

        var withSizeField = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            ContactVCardMaxImportMegabytes = 100,
        });
        Assert.Equal(HttpStatusCode.Forbidden, withSizeField.StatusCode);
        var body = await withSizeField.Content.ReadAsStringAsync();
        Assert.Contains("ContactVCardMaxImportMegabytes", body);
    }

    [Fact]
    public async Task Put_SizeFieldsOnly_SucceedsWithSecurityClaim_CountFieldRejectedWithoutUpdateClaim()
    {
        await using var factory = new ApiFactory(SizeOnly);
        using var client = factory.CreateClient();

        var sizesOnly = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            ContactVCardMaxImportMegabytes = 100,
            ContactVCardMaxExportMegabytes = 100,
            CalendarIcsMaxImportMegabytes = 10,
            CalendarIcsMaxExportMegabytes = 10,
            TaskIcsMaxImportMegabytes = 10,
            TaskIcsMaxExportMegabytes = 10,
            JournalIcsMaxImportMegabytes = 10,
            JournalIcsMaxExportMegabytes = 10,
        });
        sizesOnly.EnsureSuccessStatusCode();

        var withCountField = await client.PutAsJsonAsync(SettingsPath, new SystemSettingsUpdate
        {
            JournalIcsMaxExportRows = new CapacityLimit { Value = 500 },
        });
        Assert.Equal(HttpStatusCode.Forbidden, withCountField.StatusCode);
    }

    // ── GET /api/import-limits (AC 21-22) ───────────────────────────────────────

    [Fact]
    public async Task ImportLimits_AuthenticatedWithNoClaims_ReturnsOk()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(LimitsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ImportLimits_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(LimitsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ImportLimits_BodyContainsExactlyTheSixteenDocumentedProperties()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var body = await (await client.GetAsync(LimitsPath)).Content.ReadAsStringAsync();

        string[] expected =
        [
            "contactVCardMaxExportRows", "contactVCardMaxImportEntries", "contactVCardMaxImportMegabytes",
            "contactVCardMaxExportMegabytes",
            "calendarIcsMaxExportEvents", "calendarIcsMaxImportEvents", "calendarIcsMaxImportMegabytes",
            "calendarIcsMaxExportMegabytes",
            "taskIcsMaxExportTasks", "taskIcsMaxImportTasks", "taskIcsMaxImportMegabytes", "taskIcsMaxExportMegabytes",
            "journalIcsMaxExportRows", "journalIcsMaxImportEntries", "journalIcsMaxImportMegabytes",
            "journalIcsMaxExportMegabytes",
        ];
        foreach (var property in expected)
        {
            Assert.Contains($"\"{property}\"", body);
        }

        string[] mustNotAppear =
        [
            "updatedAt", "updatedBy", "updatedByDisplayName",
            "requireTwoFactor", "registrationRequireAdminApproval", "emailRequireConfirmation",
            "insuranceExpiringSoonWindowDays", "insuranceMaxSummaryPolicies",
        ];
        foreach (var property in mustNotAppear)
        {
            Assert.DoesNotContain($"\"{property}\"", body);
        }
    }

    [Fact]
    public async Task ImportLimits_DefaultInstall_MatchesSystemSettingsDefaults()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var dto = await (await client.GetAsync(LimitsPath)).Content.ReadFromJsonAsync<ImportLimitsDto>();

        Assert.NotNull(dto);
        Assert.Null(dto!.ContactVCardMaxExportRows);
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

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
