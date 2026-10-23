using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Core.Journal.Geocoding;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The locations HTTP contract (issue #350 §5, AC 1, 4–7, 10–12, 17). The geocoding providers are replaced
/// by in-process fakes — the provider mapping itself is covered in <c>Odyssey.Core.Tests</c> against
/// recorded responses — so nothing here leaves the test host.
/// </summary>
public class LocationsApiTests
{
    private const string Path = "/api/locations";

    private static readonly string[] ReadOnly = [PermissionClaims.LocationsRead];

    private static readonly string[] ReadWrite =
        [PermissionClaims.LocationsRead, PermissionClaims.LocationsCreate, PermissionClaims.LocationsUpdate];

    private static NewLocation Cabin(string name = "Cabin") => new()
    {
        Name = name,
        Description = "Key under the blue pot",
        AddressText = "Fjellveien 12, 2630 Ringebu",
        Latitude = 61.52914m,
        Longitude = 10.13802m,
        AltitudeMeters = 820,
        TimeZoneId = "Europe/Oslo",
        Source = LocationSource.Kartverket,
        SourceReference = "3439-1020-12",
    };

    private static UpdateLocation Update(ExistingLocation l, bool archived = false) => new()
    {
        Name = l.Name,
        Description = l.Description,
        AddressText = l.AddressText,
        Latitude = l.Latitude,
        Longitude = l.Longitude,
        AltitudeMeters = l.AltitudeMeters,
        TimeZoneId = l.TimeZoneId,
        Source = l.Source,
        SourceReference = l.SourceReference,
        Archived = archived,
    };

    private static async Task<ExistingLocation> CreateAsync(HttpClient client, NewLocation location)
    {
        var response = await client.PostAsJsonAsync(Path, location);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExistingLocation>())!;
    }

    // ── Authorization (AC 1) ────────────────────────────────────────────────

    [Fact]
    public async Task Unauthenticated_is_401()
    {
        await using var factory = new ApiFactory(null);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task Read_only_reads_the_list_and_a_location_but_cannot_write_or_search()
    {
        var store = new ApiFactory(ReadWrite);
        await using var _ = store;
        using var writer = store.CreateClient();
        var cabin = await CreateAsync(writer, Cabin());

        await using var factory = new ApiFactory(ReadOnly, sharingStoreWith: store);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Path}/{cabin.LocationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Path, Cabin("Home"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{Path}/{cabin.LocationId}", Update(cabin))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Path}/search",
            new LocationSearchRequest { Query = "storgata", Region = LocationSearchRegion.Norway })).StatusCode);
    }

    [Fact]
    public async Task No_location_claim_is_403_on_read()
    {
        await using var factory = new ApiFactory([PermissionClaims.ContactsRead]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path)).StatusCode);
    }

    // ── CRUD (AC 2, 4, 5) ───────────────────────────────────────────────────

    [Fact]
    public async Task Create_returns_201_with_a_location_header_and_the_stored_shape()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Path, Cabin() with { Latitude = 59.912734m, Longitude = 10.750271m });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExistingLocation>())!;
        Assert.EndsWith($"/api/locations/{created.LocationId}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(59.91273m, created.Latitude);
        Assert.Equal(10.75027m, created.Longitude);
    }

    [Fact]
    public async Task Responses_never_carry_the_attribution_user_ids_and_attribution_matches_source()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, Cabin());
        var manual = await CreateAsync(client, Cabin("Mooring") with { Source = LocationSource.Manual, SourceReference = null });

        foreach (var json in new[]
        {
            await client.GetStringAsync($"{Path}/{created.LocationId}"),
            await client.GetStringAsync(Path),
        })
        {
            Assert.DoesNotContain("createdByUserId", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("updatedByUserId", json, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("© Kartverket", created.Attribution!.Text);
        Assert.Null(manual.Attribution);
    }

    [Fact]
    public async Task Archive_restore_and_the_status_filter()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var cabin = await CreateAsync(client, Cabin());
        await CreateAsync(client, Cabin("Home"));

        var archive = await client.PutAsJsonAsync($"{Path}/{cabin.LocationId}", Update(cabin, archived: true));
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.NotNull((await archive.Content.ReadFromJsonAsync<ExistingLocation>())!.Archived);

        Assert.Equal(["Home"], (await client.GetPagedItemsAsync<ExistingLocation>($"{Path}?status=Active")).Select(l => l.Name));
        Assert.Equal(["Cabin"], (await client.GetPagedItemsAsync<ExistingLocation>($"{Path}?status=Archived")).Select(l => l.Name));
        Assert.Equal(2, (await client.GetPagedItemsAsync<ExistingLocation>(Path)).Count);

        var restore = await client.PutAsJsonAsync($"{Path}/{cabin.LocationId}", Update(cabin, archived: false));
        Assert.Null((await restore.Content.ReadFromJsonAsync<ExistingLocation>())!.Archived);
    }

    [Fact]
    public async Task An_unknown_status_value_is_400()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Path}?status=Bogus")).StatusCode);
    }

    [Fact]
    public async Task Put_on_an_unknown_id_is_404_not_an_upsert()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var cabin = await CreateAsync(client, Cabin());

        var response = await client.PutAsJsonAsync($"{Path}/{Guid.NewGuid()}", Update(cabin));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_unknown_is_404_echoing_only_the_id()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        var response = await client.GetAsync($"{Path}/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(id.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Delete_is_405_there_is_no_delete()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var cabin = await CreateAsync(client, Cabin());

        var response = await client.DeleteAsync($"{Path}/{cabin.LocationId}");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Path}/{cabin.LocationId}")).StatusCode);
    }

    [Fact]
    public async Task A_duplicate_name_is_409_keyed_name()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await CreateAsync(client, Cabin());

        var response = await client.PostAsJsonAsync(Path, Cabin("Cabín"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("name", problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    // ── Validation (AC 6) ───────────────────────────────────────────────────

    public static TheoryData<string, object> InvalidBodies() => new()
    {
        { "Latitude", Body(latitude: 90.1m) },
        { "Longitude", Body(longitude: -180.1m) },
        { "AltitudeMeters", Body(altitude: 9001) },
        { "Name", Body(name: new string('n', 101)) },
        { "Description", Body(description: new string('d', 1001)) },
        { "TimeZoneId", Body(timeZoneId: "Mars/Base") },
        { "TimeZoneId", Body(timeZoneId: "../../etc") },
        { "Source", Body(includeSource: false) },
        { "SourceReference", Body(source: (int)LocationSource.Manual, sourceReference: "123") },
        { "SourceReference", Body(source: (int)LocationSource.Kartverket, sourceReference: "a/b") },
        { "SourceReference", Body(source: (int)LocationSource.OpenMeteo, sourceReference: "12a") },
        { "Latitude", Body(includeLatitude: false) },
    };

    private static Dictionary<string, object?> Body(
        string name = "Cabin", string? description = null, decimal latitude = 61.5m, decimal longitude = 10.1m,
        int? altitude = null, string timeZoneId = "Europe/Oslo", int source = (int)LocationSource.Kartverket,
        string? sourceReference = null, bool includeSource = true, bool includeLatitude = true)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["description"] = description,
            ["longitude"] = longitude,
            ["altitudeMeters"] = altitude,
            ["timeZoneId"] = timeZoneId,
            ["sourceReference"] = sourceReference,
        };
        if (includeLatitude)
        {
            body["latitude"] = latitude;
        }

        if (includeSource)
        {
            body["source"] = source;
        }

        return body;
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task An_invalid_body_is_400_keyed_on_the_field(string field, object body)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Path, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains(field, problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task An_invalid_update_body_is_400_keyed_on_the_field(string field, object body)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, Cabin("Target"));

        var response = await client.PutAsJsonAsync($"{Path}/{created.LocationId}", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains(field, problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    public static TheoryData<string, object> BoundaryBodies() => new()
    {
        { "north pole", Body(name: "a", latitude: 90m) },
        { "south pole", Body(name: "b", latitude: -90m) },
        { "east antimeridian", Body(name: "c", longitude: 180m) },
        { "west antimeridian", Body(name: "d", longitude: -180m) },
        { "lowest altitude", Body(name: "e", altitude: -500) },
        { "highest altitude", Body(name: "f", altitude: 9000) },
        { "longest name", Body(name: new string('n', 100)) },
        { "longest description", Body(name: "g", description: new string('d', 1000)) },
    };

    [Theory]
    [MemberData(nameof(BoundaryBodies))]
    public async Task A_value_exactly_at_a_limit_is_accepted(string because, object body)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Path, body);

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{because}: {response.StatusCode}");
    }

    [Fact]
    public async Task Renaming_onto_another_locations_name_is_a_409_keyed_name()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await CreateAsync(client, Cabin());
        var home = await CreateAsync(client, Cabin("Home"));

        var response = await client.PutAsJsonAsync($"{Path}/{home.LocationId}", Update(home) with { Name = "CABIN" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("name", problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_out_of_range_limit_is_400()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Path}?limit=-1")).StatusCode);
    }

    [Fact]
    public async Task List_search_matches_the_address()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await CreateAsync(client, Cabin());
        await CreateAsync(client, Cabin("Home") with { AddressText = "Storgata 1, 0155 Oslo", Description = null });

        var rows = await client.GetPagedItemsAsync<ExistingLocation>($"{Path}?search=ringebu");

        Assert.Equal(["Cabin"], rows.Select(l => l.Name));
    }

    // ── Search (AC 10, 11, 12) ──────────────────────────────────────────────

    [Fact]
    public async Task Search_returns_the_providers_normalized_candidates()
    {
        var fakes = new FakeSources();
        await using var factory = new ApiFactory(ReadWrite, fakes);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"{Path}/search",
            new LocationSearchRequest { Query = "Storgata 1", Region = LocationSearchRegion.Norway });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<LocationSearchResponse>())!;
        Assert.Equal(LocationSearchRegion.Norway, body.Region);
        Assert.Equal("Storgata 1", Assert.Single(body.Candidates).Name);
        Assert.Equal("© Kartverket", body.Attribution.Text);
        Assert.Equal(["storgata 1"], fakes.NorwayQueries);
        Assert.Empty(fakes.WorldQueries);
    }

    [Theory]
    [InlineData("a", LocationSearchRegion.Norway)]
    [InlineData("ab", LocationSearchRegion.World)]
    public async Task A_query_under_the_regions_minimum_is_400(string query, LocationSearchRegion region)
    {
        var fakes = new FakeSources();
        await using var factory = new ApiFactory(ReadWrite, fakes);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"{Path}/search", new LocationSearchRequest { Query = query, Region = region });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.NorwayQueries);
        Assert.Empty(fakes.WorldQueries);
    }

    [Fact]
    public async Task A_two_character_Norwegian_query_is_allowed()
    {
        var fakes = new FakeSources();
        await using var factory = new ApiFactory(ReadWrite, fakes);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"{Path}/search",
            new LocationSearchRequest { Query = "Ål", Region = LocationSearchRegion.Norway });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"query":"storgata","region":9}""")]
    [InlineData("""{"query":"storgata"}""")]
    public async Task An_unknown_or_missing_region_is_400(string json)
    {
        await using var factory = new ApiFactory(ReadWrite, new FakeSources());
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"{Path}/search",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_101_character_query_is_400()
    {
        await using var factory = new ApiFactory(ReadWrite, new FakeSources());
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"{Path}/search",
            new LocationSearchRequest { Query = new string('q', 101), Region = LocationSearchRegion.World });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unavailable_provider_is_503_geocoding_unavailable_with_no_provider_text()
    {
        await using var factory = new ApiFactory(ReadWrite, new FakeSources { Fail = true });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"{Path}/search",
            new LocationSearchRequest { Query = "storgata", Region = LocationSearchRegion.Norway });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.Equal("geocoding-unavailable", document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("storgata", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_per_user_limit_429s_one_user_and_leaves_another_unaffected()
    {
        await using var factory = new ApiFactory(ReadWrite, new FakeSources(), new Dictionary<string, string?>
        {
            ["RateLimiting:LocationSearch:PermitLimit"] = "3",
            ["RateLimiting:LocationSearch:WindowSeconds"] = "3600",
        });
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await SearchAsUser(client, "user-a", $"query {i}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await SearchAsUser(client, "user-a", "query 4")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SearchAsUser(client, "user-b", "query 5")).StatusCode);
    }

    [Fact]
    public async Task The_instance_wide_limit_429s_every_user_once_spent()
    {
        await using var factory = new ApiFactory(ReadWrite, new FakeSources(), new Dictionary<string, string?>
        {
            ["RateLimiting:LocationSearchGlobal:PermitLimit"] = "4",
            ["RateLimiting:LocationSearchGlobal:WindowSeconds"] = "3600",
        });
        using var client = factory.CreateClient();

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await SearchAsUser(client, $"user-{i}", $"query {i}")).StatusCode);
        }

        var rejected = await SearchAsUser(client, "user-new", "query x");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.RetryAfter is not null);
    }

    /// <summary>
    /// The instance-wide budget protects the providers' fair-use limits, and a cache hit never reaches a
    /// provider — so repeating a search spends nothing from it, while a new one still does.
    /// </summary>
    [Fact]
    public async Task A_cache_hit_spends_none_of_the_instance_wide_budget()
    {
        var sources = new FakeSources();
        await using var factory = new ApiFactory(ReadWrite, sources, new Dictionary<string, string?>
        {
            ["RateLimiting:LocationSearchGlobal:PermitLimit"] = "2",
            ["RateLimiting:LocationSearchGlobal:WindowSeconds"] = "3600",
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await SearchAsUser(client, "user-0", "storgata 1")).StatusCode);
        for (var i = 1; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await SearchAsUser(client, $"user-{i}", "storgata 1")).StatusCode);
        }

        Assert.Single(sources.WorldQueries);
        Assert.Equal(HttpStatusCode.OK, (await SearchAsUser(client, "user-7", "fjellveien")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SearchAsUser(client, "user-8", "ringebu")).StatusCode);
    }

    /// <summary>
    /// The ordering the security review asked for: a user rejected by their OWN limit spends nothing of
    /// the shared budget, so their spam cannot lock everyone else out.
    /// </summary>
    [Fact]
    public async Task A_request_rejected_by_the_per_user_limit_does_not_spend_the_global_budget()
    {
        await using var factory = new ApiFactory(ReadWrite, new FakeSources(), new Dictionary<string, string?>
        {
            ["RateLimiting:LocationSearch:PermitLimit"] = "1",
            ["RateLimiting:LocationSearch:WindowSeconds"] = "3600",
            ["RateLimiting:LocationSearchGlobal:PermitLimit"] = "2",
            ["RateLimiting:LocationSearchGlobal:WindowSeconds"] = "3600",
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await SearchAsUser(client, "spammer", "query 0")).StatusCode);
        for (var i = 1; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, (await SearchAsUser(client, "spammer", $"query {i}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await SearchAsUser(client, "neighbour", "query n")).StatusCode);
    }

    [Fact]
    public async Task No_log_line_anywhere_in_the_host_contains_the_query_or_the_stored_values()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new ApiFactory(ReadWrite, new FakeSources(), new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            ["Logging:LogLevel:System"] = "Trace",
        }, extra: services => services.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient();

        const string query = "Hemmeligveien 77";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{Path}/search",
            new LocationSearchRequest { Query = query, Region = LocationSearchRegion.Norway })).StatusCode);

        var created = await CreateAsync(client, Cabin("Secretcabin") with
        {
            Description = "Privatenote",
            AddressText = "Hiddenroad 5",
            Latitude = 61.12345m,
        });
        await client.PutAsJsonAsync($"{Path}/{created.LocationId}", Update(created) with { AddressText = "Otherroad 9" });

        Assert.NotEmpty(logs.Entries);
        foreach (var entry in logs.Entries)
        {
            var text = entry.Message + entry.Exception;
            Assert.DoesNotContain("Hemmeligveien", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Secretcabin", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Privatenote", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Hiddenroad", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Otherroad", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("61.12345", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(logs.Entries, entry => entry.Message.Contains("changed: [AddressText]", StringComparison.Ordinal));
    }

    /// <summary>
    /// §7.7 / N1: the two geocoding clients are registered with every HttpClient logger removed, because
    /// their outbound URLs carry the query. Behavioural, with a control: the same request through a client
    /// WITHOUT the removal does write <c>System.Net.Http.HttpClient.*</c> lines, so the absence on the real
    /// clients is not vacuous. Only the primary handler is swapped for a stub; the registration under test
    /// is otherwise the production one.
    /// </summary>
    [Fact]
    public async Task The_geocoding_clients_write_no_HttpClient_log_lines_while_an_ordinary_client_does()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new ApiFactory(ReadWrite, configuration: new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:System"] = "Trace",
        }, extra: services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            foreach (var name in new[] { nameof(KartverketLocationSearch), nameof(OpenMeteoLocationSearch), "control" })
            {
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
            }
        });
        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();

        foreach (var name in new[] { nameof(KartverketLocationSearch), nameof(OpenMeteoLocationSearch) })
        {
            using var client = clients.CreateClient(name);
            await client.GetAsync("v1/search?name=Hemmeligveien");
            Assert.DoesNotContain(logs.Entries, entry => entry.Category.StartsWith($"System.Net.Http.HttpClient.{name}", StringComparison.Ordinal));
        }

        using var control = clients.CreateClient("control");
        await control.GetAsync("https://example.test/control");
        Assert.Contains(logs.Entries, entry => entry.Category.StartsWith("System.Net.Http.HttpClient.control", StringComparison.Ordinal));
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    private static Task<HttpResponseMessage> SearchAsUser(HttpClient client, string user, string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Path}/search")
        {
            Content = JsonContent.Create(new LocationSearchRequest { Query = query, Region = LocationSearchRegion.World }),
        };
        request.Headers.Add(TestAuthHandler.ActorOverrideHeader, user);
        return client.SendAsync(request);
    }

    /// <summary>In-process stand-ins for the two providers. They record the query they receive.</summary>
    private sealed class FakeSources
    {
        public bool Fail { get; init; }

        public ConcurrentQueue<string> NorwayQueryLog { get; } = new();

        public ConcurrentQueue<string> WorldQueryLog { get; } = new();

        public IReadOnlyList<string> NorwayQueries => [.. NorwayQueryLog];

        public IReadOnlyList<string> WorldQueries => [.. WorldQueryLog];

        public IEnumerable<ILocationSearchSource> All() =>
            [new Fake(this, LocationSearchRegion.Norway), new Fake(this, LocationSearchRegion.World)];

        private sealed class Fake(FakeSources owner, LocationSearchRegion region) : ILocationSearchSource
        {
            public LocationSearchRegion Region => region;

            public Task<LocationSourceResult> SearchAsync(string query, CancellationToken cancellationToken)
            {
                (region == LocationSearchRegion.Norway ? owner.NorwayQueryLog : owner.WorldQueryLog).Enqueue(query);
                if (owner.Fail)
                {
                    throw new GeocodingUnavailableException(region == LocationSearchRegion.Norway ? "Kartverket" : "Open-Meteo");
                }

                return Task.FromResult(new LocationSourceResult(
                [
                    new LocationCandidate
                    {
                        Kind = LocationCandidateKind.Address,
                        Name = "Storgata 1",
                        Description = "0155 OSLO",
                        AddressText = "Storgata 1, 0155 OSLO",
                        Latitude = 59.91273m,
                        Longitude = 10.75027m,
                        TimeZoneId = "Europe/Oslo",
                        Source = region == LocationSearchRegion.Norway ? LocationSource.Kartverket : LocationSource.OpenMeteo,
                        SourceReference = region == LocationSearchRegion.Norway ? "0301-1-1" : "1",
                    },
                ], Partial: false));
            }
        }
    }

    private sealed class ApiFactory(
        IReadOnlyCollection<string>? permissions,
        FakeSources? sources = null,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? extra = null,
        OdysseyApiFactory? sharingStoreWith = null)
        : OdysseyApiFactory(
            permissions,
            "locations-actor",
            configuration,
            services =>
            {
                if (sources is not null)
                {
                    services.RemoveAll<ILocationSearchSource>();
                    foreach (var source in sources.All())
                    {
                        services.AddSingleton(source);
                    }
                }

                extra?.Invoke(services);
            },
            sharingStoreWith);
}
