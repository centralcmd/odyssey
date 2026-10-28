using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Core.Finance.ExchangeRateSync.Providers;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The exchange-rate sync HTTP contract (issue #349 §5, §7): the claim gates, run-now executing inline
/// under <c>Testing</c>, and the rate endpoints' new fetched-row rules. Norges Bank is an in-process stub;
/// the parsing and planning are covered in <c>Odyssey.Core.Tests</c>.
/// </summary>
public class ExchangeRateSyncApiTests
{
    private const string Sync = "/api/system-settings/exchange-rate-sync";

    /// <summary>
    /// A fixed Friday, served as the API's TimeProvider and used by the stub, so neither a year rollover
    /// nor a weekend 1 January can move what these tests see.
    /// </summary>
    private static readonly DateTime FixedNow = new(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Admin =
    [
        PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate,
        PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesUpdate, PermissionClaims.ExchangeRatesDelete,
    ];

    /// <summary>What Owner holds on rates: everything but the system-settings claims.</summary>
    private static readonly string[] RateOwner =
    [
        PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesCreate,
        PermissionClaims.ExchangeRatesUpdate, PermissionClaims.ExchangeRatesDelete,
    ];

    private static readonly string[] SettingsOnly =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate, PermissionClaims.ExchangeRatesRead];

    /// <summary>The seeded currency catalogue (NOK, USD, …) comes from the model's HasData.</summary>
    private static async Task SeedAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
    }

    [Fact]
    public async Task Sync_endpoints_are_Admin_only()
    {
        await using var factory = new ApiFactory(RateOwner);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Sync}/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Sync}/currencies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Sync}/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{Sync}/runs", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{Sync}/currencies/USD", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{Sync}/currencies/USD/rates")).StatusCode);
    }

    [Fact]
    public async Task A_settings_reader_reads_the_sync_but_cannot_change_or_run_it()
    {
        await using var factory = new ApiFactory([PermissionClaims.SystemSettingsRead, PermissionClaims.ExchangeRatesDelete]);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Sync}/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Sync}/currencies")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Sync}/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{Sync}/runs", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{Sync}/currencies/USD", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{Sync}/currencies/USD/rates")).StatusCode);
    }

    [Fact]
    public async Task Enable_then_run_now_fetches_inline_and_logs_the_run()
    {
        await using var factory = new ApiFactory(Admin);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"{Sync}/currencies/usd", new ExchangeRateSyncCurrencyUpdate { Enabled = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await put.Content.ReadFromJsonAsync<ExistingExchangeRateSyncCurrency>())!.Enabled);

        var post = await client.PostAsync($"{Sync}/runs", null);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var runId = (await post.Content.ReadFromJsonAsync<ExchangeRateSyncRunStarted>())!.RunId;

        var runs = await client.GetFromJsonAsync<PagedResult<ExistingExchangeRateSyncRun>>($"{Sync}/runs");
        var run = Assert.Single(runs!.Items);
        Assert.Equal(runId, run.Id);
        Assert.Equal(ExchangeRateSyncRunStatus.Succeeded, run.Status);
        Assert.Equal(["USD"], run.CurrencyCodes);
        Assert.True(run.RowsInserted > 0);

        var status = await client.GetFromJsonAsync<ExchangeRateSyncStatus>($"{Sync}/status");
        Assert.Null(status!.CurrentRun);
        Assert.Equal(runId, status.LastRun!.Id);
        Assert.Equal("NOK", status.BaseCurrencyCode);
        Assert.Equal(1, status.EnabledCount);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync($"{Sync}/runs", null)).StatusCode);

        var fetched = await client.GetFromJsonAsync<PagedResult<ExistingExchangeRate>>("/api/exchange-rates?source=Fetched&limit=1");
        Assert.Equal(ExchangeRateProvider.NorgesBank, Assert.Single(fetched!.Items).Provider);
    }

    [Fact]
    public async Task The_currency_list_carries_the_sync_projection_only_for_a_settings_reader()
    {
        await using (var owner = new ApiFactory([.. RateOwner, PermissionClaims.CurrenciesRead]))
        {
            await SeedAsync(owner);
            using var client = owner.CreateClient();

            var plain = await client.GetFromJsonAsync<PagedResult<ExistingCurrency>>("/api/currencies?limit=100");
            Assert.NotEmpty(plain!.Items);
            Assert.All(plain.Items, item => Assert.Null(item.Sync));
            // The result alone would disclose the sync state, so the filter and the sort need the claim too.
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/currencies?sync=On")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/currencies?sortBy=Sync")).StatusCode);
        }

        await using var admin = new ApiFactory([.. Admin, PermissionClaims.CurrenciesRead]);
        await SeedAsync(admin);
        using var adminClient = admin.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await adminClient.PutAsJsonAsync($"{Sync}/currencies/USD", new { enabled = true })).StatusCode);

        var synced = await adminClient.GetFromJsonAsync<PagedResult<ExistingCurrency>>("/api/currencies?sync=On&limit=100");
        Assert.Equal(["NOK", "USD"], synced!.Items.Select(item => item.CurrencyCode).Order());
        Assert.All(synced.Items, item => Assert.True(item.Sync!.Enabled));
        Assert.True(Assert.Single(synced.Items, item => item.CurrencyCode == "NOK").Sync!.Locked);

        var bySync = await adminClient.GetFromJsonAsync<PagedResult<ExistingCurrency>>("/api/currencies?sortBy=Sync&limit=100");
        Assert.Equal(["NOK", "USD"], bySync!.Items.Take(2).Select(item => item.CurrencyCode));
        Assert.All(bySync.Items.Skip(2), item => Assert.False(item.Sync?.Enabled ?? false));
    }

    [Fact]
    public async Task Archiving_a_currency_stops_its_sync_and_the_base_cannot_be_archived_while_synced()
    {
        await using var factory = new ApiFactory([.. Admin, PermissionClaims.CurrenciesRead, PermissionClaims.CurrenciesUpdate]);
        await SeedAsync(factory);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Sync}/currencies/USD", new { enabled = true })).StatusCode);

        var nok = await client.PutAsJsonAsync("/api/currencies/NOK",
            new NewCurrency { CurrencyCode = "NOK", Name = "Norwegian Krone", MinorUnits = 2, Symbol = "kr", Archived = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nok.StatusCode);

        var usd = await client.PutAsJsonAsync("/api/currencies/USD",
            new NewCurrency { CurrencyCode = "USD", Name = "US Dollar", MinorUnits = 2, Symbol = "$", Archived = true });
        Assert.Equal(HttpStatusCode.NoContent, usd.StatusCode);

        var synced = await client.GetFromJsonAsync<PagedResult<ExistingCurrency>>("/api/currencies?sync=On&limit=100");
        Assert.DoesNotContain(synced!.Items, item => item.CurrencyCode == "USD");
        var restart = await client.PutAsJsonAsync($"{Sync}/currencies/USD", new { enabled = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, restart.StatusCode);
        Assert.Contains("Restore it before syncing it", await restart.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_per_currency_put_validates_its_route_and_body()
    {
        await using var factory = new ApiFactory(Admin);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Sync}/currencies/U1", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Sync}/currencies/USD", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Sync}/currencies/QQQ", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"{Sync}/currencies/NOK", new { enabled = true })).StatusCode);
    }

    [Fact]
    public async Task A_fetched_rate_is_read_by_everyone_but_written_only_by_an_administrator()
    {
        await using var admin = new ApiFactory(Admin);
        await SeedAsync(admin);
        using (var client = admin.CreateClient())
        {
            await client.PutAsJsonAsync($"{Sync}/currencies/USD", new ExchangeRateSyncCurrencyUpdate { Enabled = true });
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"{Sync}/runs", null)).StatusCode);
        }

        Guid fetchedId;
        using (var scope = admin.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            fetchedId = (await context.ExchangeRates.FirstAsync(rate => rate.Provider != null)).ExchangeRateId;
        }

        await using var owner = new ApiFactory(RateOwner, sharingStoreWith: admin);
        using var ownerClient = owner.CreateClient();

        var summary = await ownerClient.GetFromJsonAsync<ExchangeRateSummary>("/api/exchange-rates/summary");
        Assert.True(summary!.BySource.Fetched > 0);
        Assert.Equal(ExchangeRateSyncConstants.Attribution, summary.Attribution);

        var current = await ownerClient.GetAsync("/api/exchange-rates?status=Current&status=Upcoming");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.GetAsync("/api/exchange-rates?status=Bogus")).StatusCode);

        var put = await ownerClient.PutAsJsonAsync($"/api/exchange-rates/{fetchedId}", new UpdateExchangeRate { Rate = 1m, AsOf = FixedNow });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Contains("source", await put.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.DeleteAsync($"/api/exchange-rates/{fetchedId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ownerClient.DeleteAsync($"/api/exchange-rates/{Guid.NewGuid()}")).StatusCode);

        using var adminClient = admin.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await adminClient.DeleteAsync($"/api/exchange-rates/{fetchedId}")).StatusCode);
    }

    [Fact]
    public async Task A_purge_needs_both_claims_and_a_disabled_currency()
    {
        await using var admin = new ApiFactory(Admin);
        await SeedAsync(admin);
        using var client = admin.CreateClient();
        await client.PutAsJsonAsync($"{Sync}/currencies/USD", new ExchangeRateSyncCurrencyUpdate { Enabled = true });
        await client.PostAsync($"{Sync}/runs", null);

        await using var settingsOnly = new ApiFactory(SettingsOnly, sharingStoreWith: admin);
        using (var settingsClient = settingsOnly.CreateClient())
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await settingsClient.DeleteAsync($"{Sync}/currencies/USD/rates")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.DeleteAsync($"{Sync}/currencies/USD/rates")).StatusCode);

        await client.PutAsJsonAsync($"{Sync}/currencies/USD", new ExchangeRateSyncCurrencyUpdate { Enabled = false });
        var purge = await client.DeleteAsync($"{Sync}/currencies/USD/rates");
        Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
        Assert.True((await purge.Content.ReadFromJsonAsync<ExchangeRatePurgeResult>())!.RowsDeleted > 0);

        var summary = await client.GetFromJsonAsync<ExchangeRateSummary>("/api/exchange-rates/summary");
        Assert.Equal(0, summary!.BySource.Fetched);
    }

    [Fact]
    public async Task The_global_start_date_is_bounded_by_its_floor_and_by_today()
    {
        await using var factory = new ApiFactory(Admin);
        await SeedAsync(factory);
        using var client = factory.CreateClient();
        var tomorrow = FixedNow.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/system-settings",
            new SystemSettingsUpdate { ExchangeRateHistoryStartDate = tomorrow })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/system-settings",
            new SystemSettingsUpdate { ExchangeRateHistoryStartDate = "1979-12-31" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/system-settings",
            new SystemSettingsUpdate { ExchangeRateHistoryStartDate = "01.01.2020" })).StatusCode);

        var saved = await client.PutAsJsonAsync("/api/system-settings",
            new SystemSettingsUpdate { ExchangeRateHistoryStartDate = "2020-01-01", ExchangeRateSyncIntervalHours = 2 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var dto = (await saved.Content.ReadFromJsonAsync<SystemSettingsDto>())!;
        Assert.Equal("2020-01-01", dto.ExchangeRateHistoryStartDate);
        Assert.Contains(nameof(SystemSettingsUpdate.ExchangeRateSyncIntervalHours), dto.Warnings.Keys);

        var status = await client.GetFromJsonAsync<ExchangeRateSyncStatus>($"{Sync}/status");
        Assert.Equal(new DateOnly(2020, 1, 1), status!.EffectiveGlobalHistoryStartDate);
        Assert.Equal(2, status.IntervalHours);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/system-settings",
            new SystemSettingsUpdate { ExchangeRateHistoryStartDate = "" })).StatusCode);
        status = await client.GetFromJsonAsync<ExchangeRateSyncStatus>($"{Sync}/status");
        Assert.Equal(new DateOnly(FixedNow.Year, 1, 1), status!.EffectiveGlobalHistoryStartDate);
    }

    /// <summary>
    /// The worker's job (no test host registers it, so built by hand here): a manual run the API started
    /// is left unclaimed for the worker, which claims and finishes it on its next tick — and a second claim
    /// of the same run finds nothing.
    /// </summary>
    [Fact]
    public async Task The_worker_job_claims_and_finishes_a_manual_run_once()
    {
        await using var factory = new ApiFactory(Admin);
        await SeedAsync(factory);
        using (var client = factory.CreateClient())
        {
            await client.PutAsJsonAsync($"{Sync}/currencies/USD", new ExchangeRateSyncCurrencyUpdate { Enabled = true });
        }

        Guid runId;
        using (var scope = factory.Services.CreateScope())
        {
            runId = (await scope.ServiceProvider.GetRequiredService<ExchangeRateSyncService>()
                .StartManualAsync("admin")).ExchangeRateSyncRunId;
        }

        await new ExchangeRateSyncJob(factory.Services.GetRequiredService<IServiceScopeFactory>())
            .RunOnceAsync(CancellationToken.None);

        using (var scope = factory.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<OdysseyContext>().ExchangeRateSyncRuns
                .AsNoTracking().SingleAsync(value => value.ExchangeRateSyncRunId == runId);
            Assert.Equal(ExchangeRateSyncRunStatus.Succeeded, run.Status);
            Assert.NotNull(run.ClaimedAtUtc);
            Assert.True(run.RowsInserted > 0);

            Assert.Null(await scope.ServiceProvider.GetRequiredService<ExchangeRateSyncService>().ClaimRequestedRunAsync());
        }
    }

    /// <summary>Background jobs run in Odyssey.Worker, never in the API: the API host neither hosts nor resolves the job.</summary>
    [Fact]
    public async Task The_api_host_does_not_run_the_sync_job()
    {
        await using var factory = new ApiFactory(Admin);

        Assert.Null(factory.Services.GetService<ExchangeRateSyncJob>());
        Assert.DoesNotContain(factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            service => service.GetType().FullName!.Contains("ExchangeRateSync", StringComparison.Ordinal));
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions, OdysseyApiFactory? sharingStoreWith = null)
        : OdysseyApiFactory(
            permissions,
            "exchange-rate-sync-actor",
            configureServices: services =>
            {
                services.AddHttpClient(nameof(NorgesBankClient)).ConfigurePrimaryHttpMessageHandler(() => new StubProvider());
                services.AddSingleton(ExchangeRateSyncPacing.None);
                services.AddSingleton<TimeProvider>(new FixedClock());
            },
            sharingStoreWith: sharingStoreWith);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(FixedNow, TimeSpan.Zero);
    }

    /// <summary>
    /// Norges Bank in-process: USD and EUR published as of today, from 1999; a data request gets one
    /// observation per weekday in its span for each requested code.
    /// </summary>
    private sealed class StubProvider : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            var today = DateOnly.FromDateTime(FixedNow);
            if (uri.Contains("lastNObservations"))
            {
                return Task.FromResult(Json(Sdmx(["USD", "EUR"], [today])));
            }

            if (uri.Contains("firstNObservations"))
            {
                return Task.FromResult(Json(Sdmx(["USD", "EUR"], [new DateOnly(1999, 1, 4)])));
            }

            var codes = Regex.Match(uri, @"EXR/B\.([A-Z+]+)\.NOK").Groups[1].Value.Split('+');
            var from = DateOnly.Parse(Regex.Match(uri, "startPeriod=([0-9-]+)").Groups[1].Value, CultureInfo.InvariantCulture);
            var to = DateOnly.Parse(Regex.Match(uri, "endPeriod=([0-9-]+)").Groups[1].Value, CultureInfo.InvariantCulture);
            var days = new List<DateOnly>();
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                {
                    days.Add(day);
                }
            }

            return Task.FromResult(Json(Sdmx(codes, days)));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static string Sdmx(string[] codes, IReadOnlyList<DateOnly> days) =>
            JsonSerializer.Serialize(new
            {
                data = new
                {
                    dataSets = new[]
                    {
                        new
                        {
                            series = codes.Select((_, i) => (i, Key: $"0:{i}:0:0")).ToDictionary(
                                x => x.Key,
                                _ => new
                                {
                                    attributes = new[] { 0 },
                                    observations = days.Select((_, index) => index)
                                        .ToDictionary(index => index.ToString(CultureInfo.InvariantCulture), _ => new[] { "10.5" }),
                                }),
                        },
                    },
                    structure = new
                    {
                        dimensions = new
                        {
                            series = new object[]
                            {
                                new { id = "FREQ", values = new[] { new { id = "B" } } },
                                new { id = "BASE_CUR", values = codes.Select(code => new { id = code }).ToArray() },
                                new { id = "QUOTE_CUR", values = new[] { new { id = "NOK" } } },
                                new { id = "TENOR", values = new[] { new { id = "SP" } } },
                            },
                            observation = new[]
                            {
                                new { id = "TIME_PERIOD", values = days.Select(day => new { id = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }).ToArray() },
                            },
                        },
                        attributes = new { series = new[] { new { id = "UNIT_MULT", values = new[] { new { id = "0" } } } } },
                    },
                },
            });
    }
}
