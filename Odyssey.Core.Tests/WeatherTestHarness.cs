using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using LocationEntity = Odyssey.Context.Location;

namespace Odyssey.Core.Tests;

/// <summary>A clock the weather tests move by hand.</summary>
internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class FakeWeatherSettings : IWeatherSettingsLookup
{
    public WeatherSettings Value { get; set; } = new("ops@example.org", 60, 5, null, false);

    public Task<WeatherSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
}

internal sealed class FakeSecrets : ISecretSettingsReader
{
    public SecretResult Frost { get; set; } = SecretResult.NotSet;

    public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Frost);
}

/// <summary>A forecast source answering from a delegate; counts calls and records the site it was sent.</summary>
internal sealed class StubForecastSource : IWeatherForecastSource
{
    public Func<WeatherSiteQuery, Task<ForecastFetchResult>> Respond { get; set; } =
        _ => Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.NotModified));

    public int Calls { get; private set; }

    public WeatherSiteQuery? LastSite { get; private set; }

    public async Task<ForecastFetchResult> FetchAsync(
        WeatherSiteQuery site, string contact, string? ifModifiedSince, TimeSpan budget, CancellationToken cancellationToken)
    {
        Calls++;
        LastSite = site;
        return await Respond(site);
    }
}

internal sealed class StubObservationSource : IWeatherObservationSource
{
    public string? StationId { get; set; } = "SN18700";

    public Func<DateOnly, DateOnly, ObservationFetchResult> Daily { get; set; } = (_, _) => new ObservationFetchResult(true, []);

    public int StationCalls { get; private set; }

    public List<(DateOnly From, DateOnly To)> DailyCalls { get; } = [];

    public Task<StationLookupResult> FindStationAsync(WeatherSiteQuery site, string contact, string clientId, CancellationToken cancellationToken)
    {
        StationCalls++;
        return Task.FromResult(new StationLookupResult(true, StationId));
    }

    public Task<ObservationFetchResult> FetchDailyAsync(
        string stationId, DateOnly from, DateOnly to, string contact, string clientId, CancellationToken cancellationToken)
    {
        DailyCalls.Add((from, to));
        return Task.FromResult(Daily(from, to));
    }
}

/// <summary>
/// One shared EF InMemory store and the weather services over it. <see cref="NewContext"/> opens another
/// context on the same store, which is how the fencing tests act "from another request" mid-fetch.
/// </summary>
internal sealed class WeatherTestHarness : IDisposable
{
    public const string AdminId = "weather-admin";

    private readonly string name = Guid.NewGuid().ToString();
    // One root for every harness: a root per instance builds a new EF service provider each time.
    private static readonly InMemoryDatabaseRoot root = new();
    private readonly List<OdysseyContext> contexts = [];

    public WeatherTestHarness(DateTimeOffset? now = null)
    {
        Time = new ManualTime(now ?? new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        Context = NewContext();
    }

    public ManualTime Time { get; }

    public OdysseyContext Context { get; }

    public FakeWeatherSettings Settings { get; } = new();

    public FakeSecrets Secrets { get; } = new();

    public StubForecastSource Forecasts { get; } = new();

    public StubObservationSource Observations { get; } = new();

    public OdysseyContext NewContext()
    {
        var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(name, root)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        contexts.Add(context);
        return context;
    }

    public WeatherFetchService Fetch(OdysseyContext? context = null) =>
        new(context ?? Context, Forecasts, Observations, Settings, Secrets, Time);

    public WeatherReadService Read(OdysseyContext? context = null) =>
        new(context ?? Context, Fetch(context), Settings, Time);

    public WeatherLocationService Admin(OdysseyContext? context = null)
    {
        var c = context ?? Context;
        return new WeatherLocationService(c, Fetch(c), Settings, new TrackedWeatherHistoryPurger(c), Time);
    }

    public async Task<LocationEntity> AddLocationAsync(
        string name = "Home", decimal latitude = 59.91273m, decimal longitude = 10.75027m, string zone = "Europe/Oslo",
        bool archived = false)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var location = new LocationEntity
        {
            LocationId = Guid.NewGuid(),
            Name = name,
            Latitude = latitude,
            Longitude = longitude,
            AltitudeMeters = 12,
            TimeZoneId = zone,
            Archived = archived ? now : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Context.Locations.Add(location);
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();
        return location;
    }

    /// <summary>A location with weather turned on through the Admin service, the way production gets one.</summary>
    public async Task<LocationEntity> EnableAsync(string name = "Home", bool tracked = true, string zone = "Europe/Oslo")
    {
        var location = await AddLocationAsync(name, zone: zone);
        await Admin().UpsertAsync(location.LocationId, new UpsertWeatherLocation
        {
            Enabled = true,
            IsTracked = tracked,
            ExpectedLocationFingerprint = WeatherCoordinates.Fingerprint(location),
        }, AdminId, CancellationToken.None);
        Context.ChangeTracker.Clear();
        return location;
    }

    /// <summary>A Locationforecast run: hourly steps from <paramref name="fromUtc"/>, all at one temperature.</summary>
    public static ForecastFetchResult Run(DateTime issuedUtc, DateTime fromUtc, int hours, decimal temperature, string symbol = "cloudy") =>
        new(WeatherFetchOutcome.Ok,
            new ForecastPayload(issuedUtc, Enumerable.Range(0, hours).Select(h => new ForecastStepData(
                fromUtc.AddHours(h), 1, temperature, 70, 50, 1010, 3, 6, 180, 0.5m, symbol, 0.5m, 20, null, null)).ToList()),
            ExpiresUtc: issuedUtc.AddMinutes(30),
            LastModified: "Fri, 23 Oct 2026 11:30:00 GMT");

    public void Dispose()
    {
        foreach (var context in contexts)
        {
            context.Dispose();
        }
    }
}
