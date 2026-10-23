using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using Odyssey.Core.Journal.Geocoding;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The geocoding proxy (issue #350 §3, §8, AC 8–10, 13–16), against stubbed handlers that serve REAL
/// responses recorded from Kartverket and Open-Meteo (<c>Fixtures/Geocoding</c>).
/// </summary>
public class LocationSearchTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Geocoding", name));

    private static (LocationSearchService Service, StubHandler Kartverket, StubHandler OpenMeteo) Build(
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? kartverket = null,
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? openMeteo = null)
    {
        var k = new StubHandler(kartverket ?? (request => Task.FromResult(Json(
            request.RequestUri!.AbsolutePath.StartsWith("/adresser", StringComparison.Ordinal)
                ? Fixture("kartverket-adresser-storgata-1.json")
                : Fixture("kartverket-stedsnavn-storgata-1.json")))));
        var o = new StubHandler(openMeteo ?? (_ => Task.FromResult(Json(Fixture("openmeteo-lisbon.json")))));
        var sources = new ILocationSearchSource[]
        {
            new KartverketLocationSearch(new HttpClient(k) { BaseAddress = new Uri(KartverketLocationSearch.BaseAddress) }),
            new OpenMeteoLocationSearch(new HttpClient(o) { BaseAddress = new Uri(OpenMeteoLocationSearch.BaseAddress) }),
        };
        return (new LocationSearchService(sources, new LocationSearchCache()), k, o);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LocationSearchRequest Norway(string q) => new() { Query = q, Region = LocationSearchRegion.Norway };

    private static LocationSearchRequest World(string q) => new() { Query = q, Region = LocationSearchRegion.World };

    [Fact]
    public async Task Norway_calls_only_Kartverket_both_endpoints_and_orders_addresses_before_place_names()
    {
        var (service, kartverket, openMeteo) = Build();

        var response = await service.SearchAsync(Norway("storgata 1"));

        Assert.Empty(openMeteo.Requests);
        Assert.All(kartverket.Requests, uri => Assert.Equal("ws.geonorge.no", uri.Host));
        Assert.Contains(kartverket.Requests, uri => uri.AbsolutePath == "/adresser/v1/sok");
        Assert.Contains(kartverket.Requests, uri => uri.AbsolutePath == "/stedsnavn/v1/navn");
        Assert.False(response.Partial);
        Assert.Equal("© Kartverket", response.Attribution.Text);

        var kinds = response.Candidates.Select(c => c.Kind).ToList();
        var firstPlace = kinds.IndexOf(LocationCandidateKind.PlaceName);
        Assert.True(firstPlace > 0);
        Assert.All(kinds.Take(firstPlace), kind => Assert.Equal(LocationCandidateKind.Address, kind));
        Assert.All(kinds.Skip(firstPlace), kind => Assert.Equal(LocationCandidateKind.PlaceName, kind));
    }

    [Fact]
    public async Task Kartverket_candidates_are_mapped_per_the_spec_table()
    {
        var (service, _, _) = Build();

        var response = await service.SearchAsync(Norway("storgata 1"));

        var address = response.Candidates.First(c => c.Kind == LocationCandidateKind.Address);
        Assert.Equal("Storgata 1", address.Name);
        Assert.Equal("3611 KONGSBERG", address.Description);
        Assert.Equal("Storgata 1, 3611 KONGSBERG", address.AddressText);
        Assert.Equal(59.66816m, address.Latitude);
        Assert.Equal(9.64967m, address.Longitude);
        Assert.Equal("Europe/Oslo", address.TimeZoneId);
        Assert.Equal(LocationSource.Kartverket, address.Source);
        Assert.Equal("3303-6060-1", address.SourceReference);
        Assert.True(LocationRules.IsValidReference(LocationSource.Kartverket, address.SourceReference));

        var place = response.Candidates.First(c => c.Kind == LocationCandidateKind.PlaceName);
        Assert.Contains(" — ", place.Description);
        Assert.Null(place.AddressText);
        Assert.Matches("^[0-9]+$", place.SourceReference!);
    }

    [Fact]
    public async Task World_calls_only_Open_Meteo_and_maps_its_candidates()
    {
        var (service, kartverket, openMeteo) = Build();

        var response = await service.SearchAsync(World("lisbon"));

        Assert.Empty(kartverket.Requests);
        var request = Assert.Single(openMeteo.Requests);
        Assert.Equal("geocoding-api.open-meteo.com", request.Host);
        Assert.Equal("© Open-Meteo", response.Attribution.Text);

        var lisbon = response.Candidates[0];
        Assert.Equal(LocationCandidateKind.Locality, lisbon.Kind);
        Assert.Equal("Lisbon", lisbon.Name);
        Assert.Equal("Lisbon District, Portugal", lisbon.Description);
        Assert.Equal("Europe/Lisbon", lisbon.TimeZoneId);
        Assert.Equal(68, lisbon.AltitudeMeters);
        Assert.Equal("2267057", lisbon.SourceReference);
    }

    [Fact]
    public async Task Open_Meteo_with_no_results_is_an_empty_list_not_a_failure()
    {
        var (service, _, _) = Build(openMeteo: _ => Task.FromResult(Json(Fixture("openmeteo-no-results.json"))));

        var response = await service.SearchAsync(World("zzqxqz"));

        Assert.Empty(response.Candidates);
    }

    [Fact]
    public async Task Duplicates_on_rounded_coordinates_and_normalized_name_collapse_to_the_first()
    {
        const string body = """
            {"navn":[
              {"skrivemåte":"Ål","navneobjekttype":"Tettsted","stedsnummer":1,"representasjonspunkt":{"nord":60.630241,"øst":8.560712},"kommuner":[{"kommunenavn":"Ål"}]},
              {"skrivemåte":"ÅL","navneobjekttype":"Bygd","stedsnummer":2,"representasjonspunkt":{"nord":60.630238,"øst":8.560709},"kommuner":[{"kommunenavn":"Ål"}]},
              {"skrivemåte":"Ål kyrkje","navneobjekttype":"Kirke","stedsnummer":3,"representasjonspunkt":{"nord":60.62926,"øst":8.55829},"kommuner":[{"kommunenavn":"Ål"}]}
            ]}
            """;
        var (service, _, _) = Build(kartverket: request => Task.FromResult(Json(
            request.RequestUri!.AbsolutePath.StartsWith("/adresser", StringComparison.Ordinal) ? """{"adresser":[]}""" : body)));

        var response = await service.SearchAsync(Norway("ål"));

        Assert.Equal(["Ål", "Ål kyrkje"], response.Candidates.Select(c => c.Name));
        Assert.Equal("1", response.Candidates[0].SourceReference);
    }

    [Fact]
    public async Task Provider_text_is_truncated_to_the_entity_limits_and_cleaned()
    {
        var longName = new string('N', 150);
        var longDescription = new string('D', 260);
        var body = $$"""
            {"results":[{"id":1,"name":"{{longName}}‮","latitude":1,"longitude":2,"timezone":"Europe/Oslo","admin1":"{{longDescription}}","country":"X"}]}
            """;
        var (service, _, _) = Build(openMeteo: _ => Task.FromResult(Json(body)));

        var candidate = Assert.Single((await service.SearchAsync(World("long name"))).Candidates);

        Assert.Equal(LocationRules.NameMaxLength, candidate.Name.Length);
        Assert.DoesNotContain('‮', candidate.Name);
        Assert.Equal(LocationRules.CandidateDescriptionMaxLength, candidate.Description!.Length);
    }

    [Fact]
    public async Task A_provider_time_zone_this_server_cannot_resolve_comes_back_as_null()
    {
        const string body = """{"results":[{"id":3421319,"name":"Nuuk","latitude":64.18347,"longitude":-51.72157,"timezone":"Mars/Olympus_Mons"}]}""";
        var (service, _, _) = Build(openMeteo: _ => Task.FromResult(Json(body)));

        var candidate = Assert.Single((await service.SearchAsync(World("nuuk"))).Candidates);

        Assert.Null(candidate.TimeZoneId);
    }

    [Fact]
    public async Task A_reference_of_the_wrong_shape_is_dropped_but_the_candidate_kept()
    {
        const string body = """{"results":[{"id":"12a/../x","name":"Somewhere","latitude":1,"longitude":2,"timezone":"Europe/Oslo"}]}""";
        var (service, _, _) = Build(openMeteo: _ => Task.FromResult(Json(body)));

        var candidate = Assert.Single((await service.SearchAsync(World("somewhere"))).Candidates);

        Assert.Null(candidate.SourceReference);
        Assert.Equal(LocationSource.OpenMeteo, candidate.Source);
    }

    [Fact]
    public async Task A_second_identical_search_is_served_from_the_cache()
    {
        var (service, _, openMeteo) = Build();

        await service.SearchAsync(World("Lisbon"));
        await service.SearchAsync(World("  lisbon  "));

        Assert.Single(openMeteo.Requests);
    }

    [Fact]
    public async Task A_partial_result_is_returned_and_not_cached()
    {
        var (service, kartverket, _) = Build(kartverket: request =>
            Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/adresser", StringComparison.Ordinal)
                ? Json(Fixture("kartverket-adresser-storgata-1.json"))
                : new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var first = await service.SearchAsync(Norway("storgata 1"));
        await service.SearchAsync(Norway("storgata 1"));

        Assert.True(first.Partial);
        Assert.All(first.Candidates, c => Assert.Equal(LocationCandidateKind.Address, c.Kind));
        Assert.Equal(4, kartverket.Requests.Count);
    }

    [Fact]
    public async Task Both_Kartverket_halves_failing_is_geocoding_unavailable()
    {
        var (service, _, _) = Build(kartverket: _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("provider secret detail"),
        }));

        var ex = await Assert.ThrowsAsync<GeocodingUnavailableException>(() => service.SearchAsync(Norway("storgata 1")));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("geocoding-unavailable", ex.Code);
        Assert.DoesNotContain("secret", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task A_failure_is_not_cached()
    {
        var calls = 0;
        var (service, _, openMeteo) = Build(openMeteo: _ => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json(Fixture("openmeteo-lisbon.json"))));

        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => service.SearchAsync(World("lisbon")));
        var second = await service.SearchAsync(World("lisbon"));

        Assert.NotEmpty(second.Candidates);
        Assert.Equal(2, openMeteo.Requests.Count);
    }

    private static async Task<HttpResponseMessage> Hang(HttpRequestMessage request)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None).WaitAsync(request.Options.TryGetValue(
            new HttpRequestOptionsKey<CancellationToken>("ct"), out var ct) ? ct : CancellationToken.None);
        return Json("{}");
    }

    // The 3 s budget is the contract: the lower bound proves the call was not abandoned early for some
    // other reason, the upper one that it was not left to the 30 s the stub would take.
    [Fact]
    public async Task A_provider_that_does_not_answer_in_three_seconds_is_geocoding_unavailable()
    {
        var (service, _, _) = Build(openMeteo: Hang);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => service.SearchAsync(World("lisbon")));

        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Kartverket_not_answering_in_three_seconds_is_geocoding_unavailable()
    {
        var (service, _, _) = Build(kartverket: Hang);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => service.SearchAsync(Norway("storgata 1")));

        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_redirect_is_a_failure_not_followed()
    {
        var (service, _, openMeteo) = Build(openMeteo: _ => Task.FromResult(Redirect()));

        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => service.SearchAsync(World("lisbon")));
        Assert.Single(openMeteo.Requests);
    }

    [Fact]
    public async Task A_Kartverket_redirect_is_a_failure_not_followed()
    {
        var (service, kartverket, _) = Build(kartverket: _ => Task.FromResult(Redirect()));

        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => service.SearchAsync(Norway("storgata 1")));
        Assert.Equal(2, kartverket.Requests.Count);
        Assert.All(kartverket.Requests, uri => Assert.Equal("ws.geonorge.no", uri.Host));
    }

    private static HttpResponseMessage Redirect()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        redirect.Headers.Location = new Uri("https://evil.example/");
        return redirect;
    }

    [Fact]
    public async Task A_two_character_Norway_search_maps_the_recorded_Al_responses()
    {
        var (service, _, _) = Build(kartverket: request => Task.FromResult(Json(
            request.RequestUri!.AbsolutePath.StartsWith("/adresser", StringComparison.Ordinal)
                ? Fixture("kartverket-adresser-al.json")
                : Fixture("kartverket-stedsnavn-al.json"))));

        var response = await service.SearchAsync(Norway("Ål"));

        Assert.False(response.Partial);
        Assert.Contains(response.Candidates, c => c.Kind == LocationCandidateKind.Address && c.Name == "Ål idrettspark 1");
        Assert.Contains(response.Candidates, c => c.Kind == LocationCandidateKind.PlaceName && c.Name == "Ål");
        Assert.All(response.Candidates, c => Assert.Equal(LocationSource.Kartverket, c.Source));
    }

    [Fact]
    public async Task Concurrent_identical_searches_share_one_provider_call()
    {
        var gate = new TaskCompletionSource();
        var (service, _, openMeteo) = Build(openMeteo: async _ =>
        {
            await gate.Task;
            return Json(Fixture("openmeteo-lisbon.json"));
        });

        var searches = Enumerable.Range(0, 5).Select(_ => service.SearchAsync(World("lisbon"))).ToList();
        await Task.Delay(100);
        gate.SetResult();
        await Task.WhenAll(searches);

        Assert.Single(openMeteo.Requests);
    }

    [Fact]
    public async Task One_cancelled_caller_does_not_fail_the_others_sharing_the_call()
    {
        var gate = new TaskCompletionSource();
        var (service, _, _) = Build(openMeteo: async _ =>
        {
            await gate.Task;
            return Json(Fixture("openmeteo-lisbon.json"));
        });

        using var cts = new CancellationTokenSource();
        var cancelled = service.SearchAsync(World("lisbon"), cts.Token);
        var other = service.SearchAsync(World("lisbon"));
        await Task.Delay(50);
        cts.Cancel();
        gate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.NotEmpty((await other).Candidates);
    }

    [Theory]
    [InlineData("a&b=c")]
    [InlineData("x#y")]
    [InlineData("star*")]
    [InlineData("~fuzzy")]
    [InlineData("100%")]
    [InlineData("a/b")]
    [InlineData("æøå")]
    [InlineData("hytta 🏔")]
    public async Task The_query_reaches_the_provider_as_one_correctly_encoded_parameter(string query)
    {
        var (service, kartverket, openMeteo) = Build(
            kartverket: _ => Task.FromResult(Json("""{"adresser":[],"navn":[]}""")),
            openMeteo: _ => Task.FromResult(Json("{}")));

        await service.SearchAsync(Norway(query));
        await service.SearchAsync(World(query + " x"));

        var expectedNorway = LocationRules.NormalizeQuery(query);
        foreach (var uri in kartverket.Requests)
        {
            var parameters = System.Web.HttpUtility.ParseQueryString(uri.Query);
            Assert.Equal(expectedNorway, parameters["sok"]);
            Assert.Equal("10", parameters["treffPerSide"]);
        }

        var world = System.Web.HttpUtility.ParseQueryString(Assert.Single(openMeteo.Requests).Query);
        Assert.Equal(LocationRules.NormalizeQuery(query + " x"), world["name"]);
        Assert.Equal("10", world["count"]);
    }

    [Fact]
    public async Task A_response_over_256_KB_is_geocoding_unavailable()
    {
        var huge = """{"results":[""" + string.Join(',', Enumerable.Repeat("""{"name":"x"}""", 30_000)) + "]}";
        var (service, _, _) = Build(openMeteo: _ => Task.FromResult(Json(huge)));

        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => service.SearchAsync(World("lisbon")));
    }

    /// <summary>
    /// A small compressed body that inflates past the cap. In production the handler decompresses before
    /// <c>GeocodingHttp</c> reads; here the content stream IS the inflating stream, which is what the
    /// production client sees after decompression.
    /// </summary>
    [Fact]
    public async Task A_small_gzip_body_that_inflates_past_256_KB_is_geocoding_unavailable()
    {
        var inflated = Encoding.UTF8.GetBytes("""{"results":[],"pad":" """ + new string(' ', 2 * 1024 * 1024) + "\"}");
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzip.WriteAsync(inflated);
        }

        Assert.True(compressed.Length < 64 * 1024);
        var bytes = compressed.ToArray();
        var (service, _, _) = Build(openMeteo: _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress)),
        }));

        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => service.SearchAsync(World("lisbon")));
    }

    /// <summary>
    /// A well-formed body of the wrong SHAPE degrades to "no usable rows", never a 500: TryGetProperty
    /// throws on a non-object, and a number beyond decimal's range would throw in the conversion.
    /// </summary>
    [Theory]
    [InlineData("""{"results":["a string",42,null,[1]]}""")]
    [InlineData("""{"results":[{"name":"Far","latitude":1e300,"longitude":2}]}""")]
    [InlineData("""{"results":[{"name":"Far","latitude":91,"longitude":2}]}""")]
    [InlineData("""{"results":{"not":"an array"}}""")]
    [InlineData("""[1,2,3]""")]
    public async Task A_body_of_the_wrong_shape_yields_no_rows_and_no_exception(string body)
    {
        var (service, _, _) = Build(openMeteo: _ => Task.FromResult(Json(body)));

        var response = await service.SearchAsync(World("shape test"));

        Assert.Empty(response.Candidates);
    }

    [Fact]
    public async Task An_altitude_beyond_range_is_dropped_but_the_place_kept()
    {
        var (service, _, _) = Build(openMeteo: _ => Task.FromResult(Json(
            """{"results":[{"name":"High","latitude":1,"longitude":2,"elevation":1e300,"timezone":"UTC"}]}""")));

        var response = await service.SearchAsync(World("shape test"));

        var candidate = Assert.Single(response.Candidates);
        Assert.Equal("High", candidate.Name);
        Assert.Null(candidate.AltitudeMeters);
    }

    [Theory]
    [InlineData("""{"adresser":[{"adressetekst":"Storgata 1","representasjonspunkt":"here"}]}""")]
    [InlineData("""{"adresser":["x",{"adressetekst":"Storgata 1","representasjonspunkt":{"lat":1e300,"lon":1}}]}""")]
    public async Task A_Kartverket_body_of_the_wrong_shape_yields_no_rows(string body)
    {
        var (service, _, _) = Build(kartverket: request => Task.FromResult(Json(
            request.RequestUri!.AbsolutePath.StartsWith("/adresser", StringComparison.Ordinal) ? body : "{}")));

        var response = await service.SearchAsync(Norway("storgata 1"));

        Assert.Empty(response.Candidates);
        Assert.False(response.Partial);
    }

    [Fact]
    public async Task A_malformed_body_is_geocoding_unavailable()
    {
        var (service, _, _) = Build(openMeteo: _ => Task.FromResult(Json("<html>not json</html>")));

        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => service.SearchAsync(World("lisbon")));
    }

    [Theory]
    [InlineData(LocationSearchRegion.Norway, "å", false)]
    [InlineData(LocationSearchRegion.Norway, "ål", true)]
    [InlineData(LocationSearchRegion.World, "ål", false)]
    [InlineData(LocationSearchRegion.World, "ålx", true)]
    [InlineData(LocationSearchRegion.Norway, "  å  ", false)]
    public async Task The_minimum_length_is_per_region_and_counted_after_normalization(
        LocationSearchRegion region, string query, bool allowed)
    {
        var (service, _, _) = Build(
            kartverket: _ => Task.FromResult(Json("{}")),
            openMeteo: _ => Task.FromResult(Json("{}")));
        var request = new LocationSearchRequest { Query = query, Region = region };

        if (allowed)
        {
            await service.SearchAsync(request);
        }
        else
        {
            await Assert.ThrowsAsync<DomainValidationException>(() => service.SearchAsync(request));
        }
    }

    [Fact]
    public void Normalize_caps_the_candidate_list_at_twenty()
    {
        var raw = Enumerable.Range(0, 30).Select(i => new LocationCandidate
        {
            Name = $"Place {i}",
            Latitude = i,
            Longitude = i,
            TimeZoneId = "Europe/Oslo",
            Source = LocationSource.OpenMeteo,
        });

        Assert.Equal(20, LocationSearchService.Normalize(raw).Count);
    }

    /// <summary>Records each request URI and answers it with the supplied behaviour.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Uri> requests = new();

        public IReadOnlyList<Uri> Requests => [.. requests];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue(request.RequestUri!);
            request.Options.Set(new HttpRequestOptionsKey<CancellationToken>("ct"), cancellationToken);
            return respond(request);
        }
    }
}
