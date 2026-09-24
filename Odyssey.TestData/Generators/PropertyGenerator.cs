using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.TestData.Catalog;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic demo properties (issue #167): a small, hand-written set of real-estate and vehicle
/// records with estimate histories and smart tags drawn from the existing tag pool. Fixed values and
/// <see cref="DeterministicGuid"/> ids throughout, so every run yields the same rows.
///
/// <para>
/// Deliberately <b>not</b> derived from the demo accounts of type <c>Property</c>/<c>Vehicle</c>: the
/// two live side by side in v1 and no data moves between them, so a demo property mirroring an account
/// would suggest a link that does not exist.
/// </para>
///
/// <para>
/// Covers every status the list derives — owned, disposed (sold) and archived — and one property with
/// no estimate at all, so the <c>Value</c> sort's nulls-last rule has something to order.
/// </para>
/// </summary>
public static class PropertyGenerator
{
    public const string Cabin = "Mountain cabin";
    public const string Apartment = "City apartment";
    public const string Plot = "Lakeside plot";
    public const string Car = "Electric estate car";
    public const string Boat = "Day cruiser";
    public const string OldCar = "Old hatchback";

    private static readonly DateTime SeededAt = D(2026, 1, 1);

    public static Guid IdFor(string name) => DeterministicGuid.From($"property::{name}");

    public static Guid EstimateIdFor(string name, DateTime effectiveFrom) =>
        DeterministicGuid.From($"property-estimate::{name}@{effectiveFrom:yyyy-MM-dd}");

    public static (List<Property> Properties, List<PropertyEstimate> Estimates, List<PropertySmartTag> SmartTags) Build()
    {
        var properties = new List<Property>
        {
            RealEstate(Cabin, "Family cabin in the mountains", DemoDataDefaults.Currencies.Nok, D(2015, 3, 1),
                new RealEstateDetails
                {
                    Kind = RealEstateKind.Cabin,
                    AddressLine = "Fjellvegen 7",
                    PostalCode = "3580",
                    City = "Geilo",
                    CountryCode = "NO",
                    CadastralNumber = "57/12",
                    LivingAreaSqm = 86.5m,
                    PlotAreaSqm = 1200m,
                    BuildYear = 1994,
                }),
            RealEstate(Apartment, "Rental apartment", DemoDataDefaults.Currencies.Eur, D(2019, 6, 15),
                new RealEstateDetails
                {
                    Kind = RealEstateKind.Apartment,
                    AddressLine = "Example Street 12, 3rd floor",
                    PostalCode = "10115",
                    City = "Berlin",
                    CountryCode = "DE",
                    LivingAreaSqm = 64m,
                    BuildYear = 2008,
                }),
            // No estimate recorded yet — sorts last by value in both directions.
            RealEstate(Plot, "Building plot by the lake", DemoDataDefaults.Currencies.Nok, null,
                new RealEstateDetails
                {
                    Kind = RealEstateKind.Plot,
                    City = "Hamar",
                    CountryCode = "NO",
                    PlotAreaSqm = 950m,
                }),
            Vehicle(Car, "Daily driver", DemoDataDefaults.Currencies.Nok, D(2023, 4, 20),
                new VehicleDetails
                {
                    Kind = VehicleKind.Car,
                    RegistrationNumber = "EL12345",
                    Vin = "YV1XZ00000A000001",
                    Make = "Volvo",
                    Model = "EX90",
                    ModelYear = 2023,
                    FirstRegisteredDate = D(2023, 4, 20),
                }),
            Vehicle(Boat, "Summer boat", DemoDataDefaults.Currencies.Nok, D(2018, 5, 1),
                new VehicleDetails
                {
                    Kind = VehicleKind.Boat,
                    Make = "Askeladden",
                    Model = "C65",
                    ModelYear = 2017,
                }),
        };

        // Sold: a disposed date in the past makes the derived status Disposed.
        var oldCar = Vehicle(OldCar, "Sold when the new car arrived", DemoDataDefaults.Currencies.Nok, D(2012, 8, 1),
            new VehicleDetails
            {
                Kind = VehicleKind.Car,
                RegistrationNumber = "AB98765",
                Make = "Volkswagen",
                Model = "Golf",
                ModelYear = 2011,
                FirstRegisteredDate = D(2011, 11, 1),
            });
        oldCar.DisposedDate = D(2023, 4, 15);
        properties.Add(oldCar);

        // Archived as well, so all three derived statuses are represented.
        properties.Single(p => p.Name == Boat).Archived = D(2025, 10, 1);

        var estimates = new List<PropertyEstimate>();
        AddEstimates(estimates, Cabin, DemoDataDefaults.Currencies.Nok,
            (D(2015, 3, 1), 2_400_000m, "Purchase price."),
            (D(2020, 1, 1), 3_100_000m, "Estate agent valuation."),
            (D(2025, 1, 1), 3_650_000m, "Latest valuation."));
        AddEstimates(estimates, Apartment, DemoDataDefaults.Currencies.Eur,
            (D(2019, 6, 15), 310_000m, "Purchase price."),
            (D(2024, 3, 1), 355_000m, null));
        AddEstimates(estimates, Car, DemoDataDefaults.Currencies.Nok,
            (D(2023, 4, 20), 780_000m, "Purchase price (new)."),
            (D(2025, 6, 1), 590_000m, "Dealer trade-in estimate."));
        AddEstimates(estimates, Boat, DemoDataDefaults.Currencies.Nok,
            (D(2018, 5, 1), 420_000m, "Purchase price."));
        AddEstimates(estimates, OldCar, DemoDataDefaults.Currencies.Nok,
            (D(2012, 8, 1), 210_000m, "Purchase price."),
            (D(2023, 4, 15), 45_000m, "Sale price."));

        var smartTags = new List<PropertySmartTag>();
        AddSmartTags(smartTags, Cabin, Tags.HomeMaintenance, Tags.Utilities, Tags.Insurance);
        AddSmartTags(smartTags, Apartment, Tags.RentalIncome, Tags.HomeMaintenance);
        AddSmartTags(smartTags, Car, Tags.Fuel, Tags.Transportation, Tags.Insurance);

        return (properties, estimates, smartTags);
    }

    private static Property RealEstate(
        string name, string description, string currency, DateTime? acquired, RealEstateDetails details)
    {
        var property = Base(name, description, PropertyType.RealEstate, currency, acquired);
        details.PropertyId = property.PropertyId;
        property.RealEstateDetails = details;
        return property;
    }

    private static Property Vehicle(
        string name, string description, string currency, DateTime? acquired, VehicleDetails details)
    {
        var property = Base(name, description, PropertyType.Vehicle, currency, acquired);
        details.PropertyId = property.PropertyId;
        property.VehicleDetails = details;
        return property;
    }

    private static Property Base(string name, string description, PropertyType type, string currency, DateTime? acquired) => new()
    {
        PropertyId = IdFor(name),
        Name = name,
        Description = description,
        Type = type,
        CurrencyCode = currency,
        AcquiredDate = acquired,
        CreatedAt = SeededAt,
        UpdatedAt = SeededAt,
    };

    private static void AddEstimates(
        List<PropertyEstimate> into, string propertyName, string currency,
        params (DateTime EffectiveFrom, decimal Value, string? Note)[] specs)
    {
        foreach (var spec in specs)
        {
            into.Add(new PropertyEstimate
            {
                PropertyEstimateId = EstimateIdFor(propertyName, spec.EffectiveFrom),
                PropertyId = IdFor(propertyName),
                Value = spec.Value,
                CurrencyCode = currency,
                EffectiveFrom = spec.EffectiveFrom,
                Note = spec.Note,
                CreatedAtUtc = spec.EffectiveFrom,
            });
        }
    }

    private static void AddSmartTags(List<PropertySmartTag> into, string propertyName, params string[] tagNames)
    {
        // A strictly increasing AddedAt per property keeps the "oldest association first" order stable.
        for (var i = 0; i < tagNames.Length; i++)
        {
            into.Add(new PropertySmartTag
            {
                PropertyId = IdFor(propertyName),
                TransactionTagId = Tags.IdFor(tagNames[i]),
                AddedAt = SeededAt.AddMinutes(i),
            });
        }
    }

    private static DateTime D(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
