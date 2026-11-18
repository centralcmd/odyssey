using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using System.Net;

namespace Odyssey.Client.Tests;

/// <summary>
/// Shared fixtures for the Unified Locations frontend tests (issue #383): a host that mounts MudBlazor's
/// dialog and popover providers around a fragment, and the place rows the tests pick from.
/// </summary>
internal static class UnifiedLocationsTestKit
{
    public static readonly Guid HyttaId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid StorgataId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    public static ExistingLocation Hytta(bool archived = false, bool coordinates = true, Guid? mergedInto = null) => new()
    {
        LocationId = HyttaId,
        Name = "Hytta",
        Line1 = "Fjellveien 12",
        PostalCode = "2630",
        City = "Ringebu",
        CountryCode = "NO",
        Latitude = coordinates ? 61.52912m : null,
        Longitude = coordinates ? 10.13850m : null,
        TimeZoneId = coordinates ? "Europe/Oslo" : null,
        HasCoordinates = coordinates,
        Archived = archived ? new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) : null,
        MergedIntoLocationId = mergedInto,
        Usage = new LocationUsage { Contacts = 2, Properties = 0, CalendarEvents = 5, RecurringSeries = 0, JournalEntries = 1 },
        Source = LocationSource.Manual,
    };

    public static ExistingLocation Storgata(LocationUsage? usage = null) => new()
    {
        LocationId = StorgataId,
        Name = "Storgata 1",
        Line1 = "Storgata 1",
        Line2 = "Etg. 4",
        PostalCode = "0155",
        City = "Oslo",
        Region = "Oslo",
        CountryCode = "NO",
        HasCoordinates = false,
        Usage = usage ?? new LocationUsage { Contacts = 1, Properties = 1, CalendarEvents = 0, RecurringSeries = 0, JournalEntries = 0 },
        Source = LocationSource.ContactAddress,
    };

    /// <summary>A context with MudBlazor, loose JS, a principal holding <paramref name="permissions"/>.</summary>
    public static BunitContext Context(params string[] permissions)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedInWith(permissions));
        return ctx;
    }

    /// <summary>A locations client answering every list and get with the given places.</summary>
    public static Mock<ILocationsApiClient> Locations(params ExistingLocation[] places)
    {
        var api = new Mock<ILocationsApiClient>();
        api.Setup(a => a.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingLocation>>.Success(
                new PagedResult<ExistingLocation> { Items = [.. places], Offset = 0, Limit = 20, TotalCount = places.Length }, HttpStatusCode.OK));
        api.Setup(a => a.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => places.FirstOrDefault(p => p.LocationId == id) is { } found
                ? ApiResult<ExistingLocation>.Success(found, HttpStatusCode.OK)
                : ApiResult<ExistingLocation>.Failure(HttpStatusCode.NotFound, new ApiProblem { Title = "Not found", Status = 404 }));
        return api;
    }

    /// <summary>The Kartverket credit a search response carries.</summary>
    public static readonly LocationAttribution Kartverket = new() { Text = "© Kartverket", License = "CC BY 4.0", Url = "https://www.kartverket.no" };

    /// <summary>A Kartverket search result — an address unless <c>kind</c> says otherwise.</summary>
    public static LocationCandidate Candidate(
        string line1 = "Storgata 3", string postalCode = "0155", string city = "OSLO",
        LocationCandidateKind kind = LocationCandidateKind.Address, string reference = "kv-1", string? zone = "Europe/Oslo") => new()
    {
        Name = $"{line1}, {postalCode} {city}",
        Kind = kind,
        Line1 = kind == LocationCandidateKind.Address ? line1 : null,
        PostalCode = kind == LocationCandidateKind.Address ? postalCode : null,
        City = kind == LocationCandidateKind.Address ? city : null,
        CountryCode = kind == LocationCandidateKind.Address ? "NO" : null,
        Latitude = 59.91234m,
        Longitude = 10.75012m,
        TimeZoneId = zone,
        Source = LocationSource.Kartverket,
        SourceReference = reference,
    };

    /// <summary>Every location search answers with <paramref name="candidates"/>.</summary>
    public static void Answer(Mock<ILocationsApiClient> api, params LocationCandidate[] candidates) =>
        api.Setup(a => a.SearchAsync(It.IsAny<LocationSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<LocationSearchResponse>.Success(
                new LocationSearchResponse { Region = LocationSearchRegion.Norway, Candidates = candidates, Attribution = Kartverket }, HttpStatusCode.OK));

    public static ApiProblem Problem(int status, string? field = null, string message = "Refused.") => new()
    {
        Title = message,
        Status = status,
        Errors = field is null
            ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [field] = [message] },
    };
}

/// <summary>Mounts MudBlazor's dialog and popover providers, then <see cref="ChildContent"/>.</summary>
public sealed class ProvidersHost : ComponentBase
{
    [Parameter] public RenderFragment? ChildContent { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<MudDialogProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<MudPopoverProvider>(1);
        builder.CloseComponent();
        builder.AddContent(2, ChildContent);
    }
}
