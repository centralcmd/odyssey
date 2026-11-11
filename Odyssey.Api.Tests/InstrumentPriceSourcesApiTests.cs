using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Core.Finance.Instruments;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.TestShared.InstrumentPrices;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The price-source administration surface (issue #446 §5 endpoints 16–18): the status projection, the binding
/// replace and its validation, and the claim gates. The test-only fake source is registered so the catalogue is
/// not empty, as it will be once Phase 2 ships a real source.
/// </summary>
public class InstrumentPriceSourcesApiTests
{
    private const string Route = "/api/system-settings/instrument-price-sources";

    private static readonly string[] Admin =
    [
        PermissionClaims.InstrumentsRead, PermissionClaims.InstrumentsCreate, PermissionClaims.InstrumentsUpdate,
        PermissionClaims.InstrumentPricesRead, PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate,
    ];

    private sealed class ApiFactory(IReadOnlyCollection<string> permissions, bool withSource = true, OdysseyApiFactory? sharing = null)
        : OdysseyApiFactory(
            permissions,
            "price-sources-actor",
            configureServices: services =>
            {
                if (withSource)
                {
                    services.AddInstrumentPriceSource<FakePrimarySource>();
                }
            },
            sharingStoreWith: sharing);

    private static async Task<ExistingInstrument> CreateAsync(HttpClient client, InstrumentType type = InstrumentType.Stock)
    {
        var response = await client.PostAsJsonAsync("/api/instruments", type == InstrumentType.Other
            ? new NewInstrument { Symbol = "OTH", MicCode = "XOSL", Name = "Other", Type = type, CurrencyCode = "NOK" }
            : new NewInstrument { Symbol = "EQNR", MicCode = "XOSL", Name = "Equinor", Type = type, CurrencyCode = "NOK" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExistingInstrument>())!;
    }

    private static InstrumentPriceBindingsUpdate Bindings(params (string Key, string Symbol)[] rows) => new()
    {
        Bindings = rows.Select(r => new InstrumentPriceBindingInput { SourceKey = r.Key, SourceSymbol = r.Symbol, Enabled = true }).ToList(),
    };

    [Fact]
    public async Task Status_lists_the_registered_source_and_says_only_whether_it_is_configured()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<InstrumentPriceSourcesStatus>(Route);

        // The production host registers Tiingo (issue #447); the test adds the fake beside it.
        Assert.Equal(["fake-primary", "tiingo"], status!.Sources.Select(s => s.Key).Order(StringComparer.Ordinal));
        var source = status.Sources.Single(s => s.Key == FakePrimarySource.DeclaredKey);
        Assert.False(source.Configured);
        Assert.Equal(new FakePrimarySource().Limits.MaxRequestsPerDay, source.MaxRequestsPerDay);
        Assert.Equal(InstrumentPriceBindingLimits.MaxBindingsPerInstrument, status.MaxBindingsPerInstrument);
        Assert.Null(status.NextRunAtUtc);
    }

    [Fact]
    public async Task The_production_catalogue_is_exactly_tiingo_and_an_unregistered_key_is_refused()
    {
        await using var factory = new ApiFactory(Admin, withSource: false);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client);

        var tiingo = Assert.Single((await client.GetFromJsonAsync<InstrumentPriceSourcesStatus>(Route))!.Sources);
        Assert.Equal("tiingo", tiingo.Key);
        Assert.False(tiingo.Configured);
        Assert.Equal(600, tiingo.MaxRequestsPerDay);

        var response = await client.PutAsJsonAsync($"{Route}/bindings/{instrument.InstrumentId}", Bindings(("fake-primary", "EQNR.OL")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // The real source is bindable through the same list replace.
        var bound = await client.PutAsJsonAsync($"{Route}/bindings/{instrument.InstrumentId}", Bindings(("tiingo", "BRK-B")));
        Assert.Equal(HttpStatusCode.OK, bound.StatusCode);
    }

    [Fact]
    public async Task Replace_stores_the_list_in_priority_order_and_an_empty_list_clears_it()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client);

        var put = await client.PutAsJsonAsync($"{Route}/bindings/{instrument.InstrumentId}", Bindings((FakePrimarySource.DeclaredKey, "EQNR.OL")));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var stored = Assert.Single((await put.Content.ReadFromJsonAsync<List<ExistingInstrumentPriceBinding>>())!);
        Assert.Equal(1, stored.Priority);
        Assert.Equal("EQNR.OL", stored.SourceSymbol);
        Assert.True(stored.SourceRegistered);

        var listed = await client.GetFromJsonAsync<List<ExistingInstrumentPriceBinding>>($"{Route}/bindings/{instrument.InstrumentId}");
        Assert.Single(listed!);

        var cleared = await client.PutAsJsonAsync($"{Route}/bindings/{instrument.InstrumentId}", Bindings());
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty((await cleared.Content.ReadFromJsonAsync<List<ExistingInstrumentPriceBinding>>())!);
    }

    [Theory]
    [InlineData("unknown-source", "EQNR.OL", "bindings[0].sourceKey")]
    [InlineData("fake-primary|dup", "EQNR.OL", "bindings[1].sourceKey")]
    [InlineData("fake-primary", "EQ..NR", "bindings[0].sourceSymbol")]
    public async Task Replace_refuses_an_unknown_duplicate_or_malformed_binding(string keys, string symbol, string field)
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client);
        var rows = keys.Split('|').Select(k => (k == "dup" ? FakePrimarySource.DeclaredKey : k, symbol)).ToArray();

        var response = await client.PutAsJsonAsync($"{Route}/bindings/{instrument.InstrumentId}", Bindings(rows));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out _), $"Expected an error keyed {field}: {errors}");
    }

    [Fact]
    public async Task Replace_refuses_a_source_that_does_not_price_the_instruments_type()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client, InstrumentType.Other);

        var response = await client.PutAsJsonAsync($"{Route}/bindings/{instrument.InstrumentId}", Bindings((FakePrimarySource.DeclaredKey, "OTH")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Replace_beyond_the_binding_cap_is_a_400()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client);
        var rows = Enumerable.Range(0, InstrumentPriceBindingLimits.MaxBindingsPerInstrument + 1)
            .Select(_ => (FakePrimarySource.DeclaredKey, "EQNR.OL")).ToArray();

        var response = await client.PutAsJsonAsync($"{Route}/bindings/{instrument.InstrumentId}", Bindings(rows));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_archived_instrument_may_have_its_bindings_cleared_but_not_added()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();
        var instrument = await CreateAsync(client);
        await client.PutAsJsonAsync($"{Route}/bindings/{instrument.InstrumentId}", Bindings((FakePrimarySource.DeclaredKey, "EQNR.OL")));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/instruments/{instrument.InstrumentId}/archive", null)).StatusCode);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PutAsJsonAsync($"{Route}/bindings/{instrument.InstrumentId}", Bindings((FakePrimarySource.DeclaredKey, "EQNR.OL")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Route}/bindings/{instrument.InstrumentId}", Bindings())).StatusCode);
    }

    [Fact]
    public async Task An_unknown_instrument_is_a_404()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/bindings/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/bindings/{Guid.NewGuid()}", Bindings((FakePrimarySource.DeclaredKey, "X")))).StatusCode);
    }

    [Fact]
    public async Task The_surface_needs_system_settings_read_and_the_write_also_needs_update()
    {
        await using var admin = new ApiFactory(Admin);
        using var adminClient = admin.CreateClient();
        var instrument = await CreateAsync(adminClient);

        // An Owner: every instrument claim, no system-settings claim.
        await using var owner = new ApiFactory(
            [PermissionClaims.InstrumentsRead, PermissionClaims.InstrumentsUpdate, PermissionClaims.InstrumentPricesRead], sharing: admin);
        using var ownerClient = owner.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.GetAsync($"{Route}/bindings/{instrument.InstrumentId}")).StatusCode);

        // A settings reader who cannot write.
        await using var reader = new ApiFactory([PermissionClaims.InstrumentsRead, PermissionClaims.SystemSettingsRead], sharing: admin);
        using var readerClient = reader.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await readerClient.PutAsJsonAsync($"{Route}/bindings/{instrument.InstrumentId}", Bindings((FakePrimarySource.DeclaredKey, "EQNR.OL")))).StatusCode);

        // The bindings embed instrument fields, so they need instruments.read as well.
        await using var noInstruments = new ApiFactory([PermissionClaims.SystemSettingsRead], sharing: admin);
        using var noInstrumentsClient = noInstruments.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await noInstrumentsClient.GetAsync($"{Route}/bindings/{instrument.InstrumentId}")).StatusCode);
    }
}
