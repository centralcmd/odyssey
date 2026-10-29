using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Api.SystemSettings;
using Odyssey.Core.SystemSettings;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The exchange-rate sync settings read path (issue #349 §8): the interval resolves through the shared
/// resolver with the LARGER conservative direction — fewer runs is the safe side for load on a public
/// service — and an unusable start date falls back to the dynamic default, never to a guess.
/// </summary>
public class ExchangeRateSyncSettingsLookupTests
{
    private static OdysseyContext CreateContext(string dbName) =>
        new(new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(dbName).Options);

    private static async Task<Core.Finance.ExchangeRateSync.ExchangeRateSyncSettings> ReadAsync(
        IMemoryCache? cache = null, params (string Key, string Value)[] rows)
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var seed = CreateContext(dbName))
        {
            foreach (var (key, value) in rows)
            {
                seed.SystemSettings.Add(new SystemSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
            }

            await seed.SaveChangesAsync();
        }

        await using var context = CreateContext(dbName);
        return await new ExchangeRateSyncSettingsLookup(
            context, cache ?? new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncSettingsLookup>.Instance).GetAsync();
    }

    [Fact]
    public async Task Absent_rows_are_healthy_and_resolve_to_the_defaults()
    {
        var settings = await ReadAsync();

        Assert.Null(settings.HistoryStartDate);
        Assert.Equal(SystemSettingsDefaults.ExchangeRateSyncIntervalHours, settings.IntervalHours);
        Assert.False(settings.ReadFailed);
    }

    [Theory]
    [InlineData("2020-01-01", 2020)]
    [InlineData("", null)]
    [InlineData("not a date", null)]
    [InlineData("1979-12-31", null)]
    public async Task The_start_date_is_used_only_when_it_satisfies_the_shared_rule(string stored, int? year)
    {
        var settings = await ReadAsync(rows: (SystemSettingsKeys.ExchangeRateHistoryStartDate, stored));

        Assert.Equal(year, settings.HistoryStartDate?.Year);
        Assert.False(settings.ReadFailed);
    }

    [Theory]
    [InlineData("6", 6)]
    [InlineData("0", SystemSettingsBounds.ExchangeRateSyncIntervalHoursMin)]
    [InlineData("500", SystemSettingsBounds.ExchangeRateSyncIntervalHoursMax)]
    public async Task A_parsed_interval_is_clamped_to_its_pair(string stored, int expected)
    {
        Assert.Equal(expected, (await ReadAsync(rows: (SystemSettingsKeys.ExchangeRateSyncIntervalHours, stored))).IntervalHours);
    }

    [Fact]
    public async Task An_unparseable_interval_degrades_to_the_larger_of_last_known_good_and_the_default()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal(100, (await ReadAsync(cache, (SystemSettingsKeys.ExchangeRateSyncIntervalHours, "100"))).IntervalHours);

        // Last known good 100 beats the default 24: the conservative direction is FEWER runs.
        Assert.Equal(100, (await ReadAsync(cache, (SystemSettingsKeys.ExchangeRateSyncIntervalHours, "x"))).IntervalHours);

        Assert.Equal(SystemSettingsDefaults.ExchangeRateSyncIntervalHours,
            (await ReadAsync(rows: (SystemSettingsKeys.ExchangeRateSyncIntervalHours, "x"))).IntervalHours);
    }

    [Fact]
    public async Task A_failed_query_is_reported_so_the_run_fails_as_settings_unusable()
    {
        var broken = CreateContext(Guid.NewGuid().ToString());
        await broken.DisposeAsync();

        var settings = await new ExchangeRateSyncSettingsLookup(
            broken, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncSettingsLookup>.Instance).GetAsync();

        Assert.True(settings.ReadFailed);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(24, false)]
    public void The_interval_advisory_starts_below_six_hours(int hours, bool advised)
    {
        var dto = new SystemSettingsDto { ExchangeRateSyncIntervalHours = hours };

        Assert.Equal(advised, SettingAdvisories.SyncIntervalBelowPublication(dto, default!) is not null);
    }
}
