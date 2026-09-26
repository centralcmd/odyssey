using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.TestData.Catalog;
using PropertyEventType = Odyssey.Context.PropertyEventType;
using ContractEventSource = Odyssey.Context.ContractEventSource;

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

    public static Guid EventIdFor(string name, string title) =>
        DeterministicGuid.From($"property-event::{name}::{title}");

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

    /// <summary>
    /// Deterministic event logs (issue #209 AC 20): both property types, both sources, and one property
    /// (the plot) with no log at all so the empty state is reachable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>system</b> rows are the ones <c>PropertyService</c> would have recorded for the seeded
    /// lifecycle fields — an <c>Acquired</c> row per acquired date, <c>Disposed</c> for the sold car and
    /// <c>Archived</c> for the boat — worded as <c>PropertyEventCatalogue</c> words them. That wording is
    /// restated here for the <c>ContractGenerator.SeededSystemDescription</c> reason: this project
    /// references <c>Odyssey.Context</c> alone, and a drift is cosmetic in a demo database.
    /// </para>
    /// <para>
    /// No title, description or note names a person, an address, a registration number or a VIN: the
    /// demo shows what the catalogue guarantees and what a careful user would write.
    /// </para>
    /// </remarks>
    public static List<PropertyEvent> BuildEvents(IReadOnlyList<Property> properties)
    {
        var events = new List<PropertyEvent>();
        var admin = DemoUsers.All.First(user => user.Role == "Admin").Id;
        var owner = DemoUsers.All.First(user => user.Role == "Owner").Id;

        foreach (var property in properties)
        {
            if (property.AcquiredDate is { } acquired)
                AddSystem(events, property.Name, PropertyEventType.Acquired, "Property acquired", "Acquired on", acquired, admin);
            if (property.DisposedDate is { } disposed)
                AddSystem(events, property.Name, PropertyEventType.Disposed, "Property disposed of", "Disposed of on", disposed, admin);
            if (property.Archived is { } archived)
                AddSystem(events, property.Name, PropertyEventType.Archived, "Property archived", "Archived on", archived, owner);
        }

        AddUser(events, Cabin, PropertyEventType.Maintenance, "Chimney swept", null, null, D(2024, 10, 3), owner);
        AddUser(events, Cabin, PropertyEventType.Renovation, "New bathroom",
            "Full bathroom renovation with floor heating.", "Keep the warranty papers in the cabin binder.",
            D(2021, 5, 17), admin);
        AddUser(events, Cabin, PropertyEventType.TaxAssessed, "Municipal property tax assessed", null, null, D(2025, 2, 10), null);
        AddUser(events, Cabin, PropertyEventType.Damage, "Storm damage to the roof",
            "Two rows of shingles lost on the north side.", null, D(2025, 11, 22), owner);
        AddUser(events, Cabin, PropertyEventType.Repair, "Roof repaired", null, null, D(2025, 12, 5), owner);
        AddUser(events, Apartment, PropertyEventType.TenancyStarted, "New tenancy started",
            "Two-year lease.", null, D(2024, 8, 1), admin);
        AddUser(events, Apartment, PropertyEventType.Inspection, "Annual inspection", null, null, D(2025, 7, 14), admin);
        AddUser(events, Car, PropertyEventType.TyreChange, "Winter tyres on",
            "Studded, front left worn to 5 mm.", "Next change mid-April.", D(2025, 10, 28), owner);
        AddUser(events, Car, PropertyEventType.TyreChange, "Summer tyres on", null, null, D(2025, 4, 12), owner);
        AddUser(events, Car, PropertyEventType.Serviced, "First workshop service", null, null, D(2024, 4, 18), owner);
        AddUser(events, Car, PropertyEventType.InsuranceChanged, "Insurance renewed", null, null, D(2025, 4, 20), null);
        AddUser(events, OldCar, PropertyEventType.PeriodicInspection, "Periodic inspection passed", null, null, D(2021, 9, 30), admin);
        AddUser(events, Boat, PropertyEventType.Maintenance, "Hull cleaned and antifouled", null, null, D(2024, 5, 2), owner);

        return events;
    }

    private static void AddSystem(
        List<PropertyEvent> into, string name, PropertyEventType type, string title, string verb,
        DateTime occurredAt, string? author) =>
        into.Add(new PropertyEvent
        {
            EventId = EventIdFor(name, title),
            PropertyId = IdFor(name),
            Type = type,
            Source = ContractEventSource.System,
            Title = title,
            Description = $"{verb} {occurredAt.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}.",
            OccurredAt = occurredAt,
            CreatedByUserId = author,
            CreatedAtUtc = occurredAt < SeededAt ? SeededAt : occurredAt,
        });

    private static void AddUser(
        List<PropertyEvent> into, string name, PropertyEventType type, string title, string? description,
        string? notes, DateTime occurredAt, string? author) =>
        into.Add(new PropertyEvent
        {
            EventId = EventIdFor(name, title),
            PropertyId = IdFor(name),
            Type = type,
            Source = ContractEventSource.User,
            Title = title,
            Description = description,
            Notes = notes,
            OccurredAt = occurredAt,
            CreatedByUserId = author,
            // Recorded shortly after it happened, so the two dates on the row are visibly different facts.
            CreatedAtUtc = occurredAt.AddHours(3),
        });

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
