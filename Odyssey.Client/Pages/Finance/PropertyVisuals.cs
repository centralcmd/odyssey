using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>A property kind's picker label and glyph.</summary>
public sealed record PropertyKindInfo(string Key, string Label, string Icon);

/// <summary>A derived property status's label, chip tone, glyph and whether its chip leads with a dot.</summary>
public sealed record PropertyStatusInfo(PropertyStatus Status, string Label, string Icon, OdsChipTone Tone, bool Dot);

/// <summary>
/// The kind and status vocabularies of the Properties page (issue #167), as the design system's
/// <c>properties-data.js</c> declares them. The type registry itself is
/// <see cref="OdsTypeRegistries.PropertyTypes"/>; these two carry no colour of their own, so they live
/// beside the page rather than in the shared registry.
/// </summary>
public static class PropertyVisuals
{
    /// <summary>RealEstateKind, in ordinal order.</summary>
    public static readonly IReadOnlyList<PropertyKindInfo> RealEstateKinds =
    [
        new(nameof(RealEstateKind.House), "House", "house"),
        new(nameof(RealEstateKind.Apartment), "Apartment", "apartment"),
        new(nameof(RealEstateKind.Cabin), "Cabin", "cabin"),
        new(nameof(RealEstateKind.Plot), "Plot", "landscape"),
        new(nameof(RealEstateKind.Commercial), "Commercial", "storefront"),
        new(nameof(RealEstateKind.Other), "Other", "home_work"),
    ];

    /// <summary>VehicleKind, in ordinal order.</summary>
    public static readonly IReadOnlyList<PropertyKindInfo> VehicleKinds =
    [
        new(nameof(VehicleKind.Car), "Car", "directions_car"),
        new(nameof(VehicleKind.Motorcycle), "Motorcycle", "two_wheeler"),
        new(nameof(VehicleKind.Boat), "Boat", "sailing"),
        new(nameof(VehicleKind.Trailer), "Trailer", "rv_hookup"),
        new(nameof(VehicleKind.Other), "Other", "commute"),
    ];

    /// <summary>The derived statuses, in the order the filter and the overview list them.</summary>
    public static readonly IReadOnlyList<PropertyStatusInfo> Statuses =
    [
        new(PropertyStatus.Owned, "Owned", "check_circle", OdsChipTone.Income, Dot: true),
        new(PropertyStatus.Disposed, "Disposed", "output", OdsChipTone.Outline, Dot: true),
        new(PropertyStatus.Archived, "Archived", "inventory_2", OdsChipTone.Outline, Dot: false),
    ];

    public static readonly IReadOnlyList<OdsOption> StatusOptions =
        [.. Statuses.Select(s => new OdsOption(s.Status.ToString(), s.Label))];

    public static IReadOnlyList<OdsOption> KindOptions(PropertyType type) =>
        [.. (type == PropertyType.Vehicle ? VehicleKinds : RealEstateKinds)
            .Select(k => new OdsOption(k.Key, k.Label) { Icon = k.Icon })];

    public static PropertyStatusInfo StatusOf(PropertyStatus status) =>
        Statuses.FirstOrDefault(s => s.Status == status) ?? Statuses[0];

    /// <summary>
    /// The property's kind. A detail row the server did not send (it always sends the one matching
    /// the type) reads as that type's <c>Other</c> rather than throwing.
    /// </summary>
    public static PropertyKindInfo KindOf(ExistingProperty property) => property.Type == PropertyType.Vehicle
        ? VehicleKinds.FirstOrDefault(k => k.Key == property.VehicleDetails?.Kind.ToString()) ?? VehicleKinds[^1]
        : RealEstateKinds.FirstOrDefault(k => k.Key == property.RealEstateDetails?.Kind.ToString()) ?? RealEstateKinds[^1];

    /// <summary>The one-line address: street, then postal code and city, then country.</summary>
    public static string? AddressText(RealEstateDetailsDto? details)
    {
        if (details is null)
            return null;

        var town = string.Join(" ", new[] { details.PostalCode, details.City }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var parts = new[] { details.AddressLine, town, details.CountryCode }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>
    /// The registration number / VIN as the server stores it: whitespace removed, uppercased. Shown
    /// under the field as it is typed, so the saved form is never a surprise.
    /// </summary>
    public static string NormalizePlate(string? value) =>
        string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
}

/// <summary>Builds the PUT body for a write that changes one thing on an existing property.</summary>
public static class PropertyWrites
{
    /// <summary>
    /// The property as it stands, with <paramref name="archived"/> applied. PUT is a full replacement,
    /// so every other field — the detail sub-object included — is carried forward from the record, or
    /// a one-click Archive would clear whatever it omitted.
    /// </summary>
    public static NewProperty WithArchived(ExistingProperty property, bool archived) => new()
    {
        Name = property.Name,
        Description = property.Description,
        Type = property.Type,
        CurrencyCode = property.CurrencyCode,
        AcquiredDate = property.AcquiredDate,
        DisposedDate = property.DisposedDate,
        Notes = property.Notes,
        Archived = archived,
        RealEstateDetails = property.Type == PropertyType.RealEstate ? property.RealEstateDetails : null,
        VehicleDetails = property.Type == PropertyType.Vehicle ? property.VehicleDetails : null,
    };
}
