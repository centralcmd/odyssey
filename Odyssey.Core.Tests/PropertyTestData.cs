using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Tests;

/// <summary>Request bodies shared by the property service tests (issue #167).</summary>
internal static class PropertyTestData
{
    public static NewProperty House(string name = "Storgata 14", string currency = "SEK") => new()
    {
        Name = name,
        Description = "Primary residence",
        Type = PropertyType.RealEstate,
        CurrencyCode = currency,
        AcquiredDate = new DateTime(2019, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        RealEstateDetails = new RealEstateDetailsDto
        {
            Kind = RealEstateKind.House,
            AddressLine = "Storgata 14",
            PostalCode = "0155",
            City = "Oslo",
            CountryCode = "no",
            CadastralNumber = "208/451",
            LivingAreaSqm = 142.5m,
            PlotAreaSqm = 410m,
            BuildYear = 1968,
        },
    };

    public static NewProperty Car(string name = "Family car", string currency = "SEK") => new()
    {
        Name = name,
        Description = "Daily driver",
        Type = PropertyType.Vehicle,
        CurrencyCode = currency,
        VehicleDetails = new VehicleDetailsDto
        {
            Kind = VehicleKind.Car,
            RegistrationNumber = " el 12345 ",
            Vin = "yv1xz00000a000001",
            Make = "Volvo",
            Model = "EX90",
            ModelYear = 2023,
            FirstRegisteredDate = new DateTime(2023, 4, 20, 0, 0, 0, DateTimeKind.Utc),
        },
    };
}
