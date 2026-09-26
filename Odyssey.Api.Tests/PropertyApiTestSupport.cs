using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Dtos.Finance;

namespace Odyssey.Api.Tests;

/// <summary>
/// Shared paths, request bodies and direct-to-context seeding for the property HTTP suites
/// (issue #167). Seeding bypasses the API on purpose, so a suite testing one endpoint does not depend on
/// another endpoint's claim or behaviour to set up its fixture.
/// </summary>
internal static class PropertyApiTestSupport
{
    public const string PropertiesPath = "/api/properties";
    public const string LimitsPath = "/api/property-limits";
    public const string SummaryPath = "/api/properties/summary";

    public static string PropertyPath(Guid propertyId) => $"{PropertiesPath}/{propertyId}";

    public static string EstimatesPath(Guid propertyId) => $"{PropertyPath(propertyId)}/estimates";

    public static string EstimatePath(Guid propertyId, Guid estimateId) => $"{EstimatesPath(propertyId)}/{estimateId}";

    public static string CurrentEstimatePath(Guid propertyId) => $"{EstimatesPath(propertyId)}/current";

    public static string SmartTagsPath(Guid propertyId) => $"{PropertyPath(propertyId)}/smart-tags";

    public static string SmartTagPath(Guid propertyId, Guid tagId) => $"{SmartTagsPath(propertyId)}/{tagId}";

    public static NewProperty RealEstateBody(string name = "Maple St house", string currencyCode = "USD") => new()
    {
        Name = name,
        Description = "Family home",
        Type = PropertyType.RealEstate,
        CurrencyCode = currencyCode,
        RealEstateDetails = new RealEstateDetailsDto { Kind = RealEstateKind.House, City = "Bergen" },
    };

    public static NewProperty VehicleBody(string name = "Family car", string currencyCode = "USD") => new()
    {
        Name = name,
        Description = "Daily driver",
        Type = PropertyType.Vehicle,
        CurrencyCode = currencyCode,
        VehicleDetails = new VehicleDetailsDto { Kind = VehicleKind.Car, Make = "Volvo" },
    };

    public static NewPropertyEstimate EstimateBody(decimal value, DateTime effectiveFrom, string? currencyCode = null) => new()
    {
        Value = value,
        EffectiveFrom = effectiveFrom,
        CurrencyCode = currencyCode,
    };

    /// <summary>Creates the store first, so the reference currencies are seeded before anything else.</summary>
    public static async Task EnsureCreatedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
    }

    public static async Task<Guid> SeedPropertyAsync(
        WebApplicationFactory<Program> factory,
        string name = "Maple St house",
        PropertyType type = PropertyType.RealEstate,
        string currencyCode = "USD",
        DateTime? acquired = null,
        DateTime? disposed = null,
        bool archived = false,
        string? city = "Bergen",
        string? notes = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var property = new Property
        {
            Name = name,
            Description = $"{name} description",
            Type = type,
            CurrencyCode = currencyCode,
            AcquiredDate = acquired,
            DisposedDate = disposed,
            Notes = notes,
            Archived = archived ? now : null,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (type == PropertyType.RealEstate)
            property.RealEstateDetails = new RealEstateDetails { Kind = RealEstateKind.House, City = city };
        else
            property.VehicleDetails = new VehicleDetails { Kind = VehicleKind.Car, Make = "Volvo" };

        context.Properties.Add(property);
        await context.SaveChangesAsync();
        return property.PropertyId;
    }

    public static async Task<Guid> SeedEstimateAsync(
        WebApplicationFactory<Program> factory, Guid propertyId, decimal value, DateTime effectiveFrom,
        string currencyCode = "USD")
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var estimate = new PropertyEstimate
        {
            PropertyId = propertyId,
            Value = value,
            CurrencyCode = currencyCode,
            EffectiveFrom = effectiveFrom,
            CreatedAtUtc = DateTime.UtcNow,
        };
        context.PropertyEstimates.Add(estimate);
        await context.SaveChangesAsync();
        return estimate.PropertyEstimateId;
    }

    public static async Task<Guid> SeedTagAsync(
        WebApplicationFactory<Program> factory, string name = "Tag", bool archived = false)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var tag = new TransactionTag { Name = name, Archived = archived ? DateTime.UtcNow : null };
        context.TransactionTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.TransactionTagId;
    }

    public static async Task LinkSmartTagAsync(WebApplicationFactory<Program> factory, Guid propertyId, Guid tagId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.PropertySmartTags.Add(new PropertySmartTag
        {
            PropertyId = propertyId,
            TransactionTagId = tagId,
            AddedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Runs <paramref name="read"/> against a fresh scope's context, for asserting on stored rows.</summary>
    public static async Task<T> ReadAsync<T>(
        WebApplicationFactory<Program> factory, Func<OdysseyContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
    }

    public static Task<int> CountAsync<T>(WebApplicationFactory<Program> factory) where T : class =>
        ReadAsync(factory, context => context.Set<T>().AsNoTracking().CountAsync());

    /// <summary>The problem-details <c>detail</c> member, or <c>null</c> when absent.</summary>
    public static async Task<string?> ReadDetailAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
    }

    /// <summary>The keys of the problem-details <c>errors</c> dictionary, in whatever case the server used.</summary>
    public static async Task<IReadOnlyList<string>> ReadErrorKeysAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!document.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object)
            return [];

        return [.. errors.EnumerateObject().Select(member => member.Name)];
    }

    public static async Task<ExistingProperty> GetPropertyAsync(HttpClient client, Guid propertyId) =>
        (await client.GetFromJsonAsync<ExistingProperty>(PropertyPath(propertyId)))!;
}
