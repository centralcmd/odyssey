using Odyssey.Dtos.Authorization;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Components;

/// <summary>The two address editors that share <c>OdsAddressBlock</c> (issue #383 §3.3).</summary>
public enum AddressBlockVariant
{
    /// <summary>A contact address: six fields; Line 1, City and Country required for a typed address.</summary>
    Contact,

    /// <summary>A real-estate property: four fields; Line 2 and Region are the place's, shown read-only.</summary>
    Property,
}

/// <summary>The §3.3 state of an address block.</summary>
public enum AddressBlockState
{
    /// <summary>A new address, typed: save sends the fields.</summary>
    Typed,

    /// <summary>A (different) saved place picked: save sends its id.</summary>
    Picked,

    /// <summary>An existing address, unchanged: save sends the stored id.</summary>
    Unchanged,

    /// <summary>An existing address, edited: the apply-to rule applies.</summary>
    Changed,

    /// <summary>An existing PROPERTY address with every field emptied: save unlinks; the place stays.</summary>
    Cleared,
}

/// <summary>What an address write changed, for picking the 403 message (issue #383 §5.3).</summary>
public enum AddressWriteKind { None, LinkById, Typed, ThisRecord, AllUses }

/// <summary>The six structured fields of an address, as typed (a property shows four of them).</summary>
public sealed class AddressFields
{
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? CountryCode { get; set; }

    public AddressFields Copy() => new()
    {
        Line1 = Line1, Line2 = Line2, PostalCode = PostalCode, City = City, Region = Region, CountryCode = CountryCode,
    };

    public static AddressFields From(ExistingLocation l) => new()
    {
        Line1 = l.Line1, Line2 = l.Line2, PostalCode = l.PostalCode, City = l.City, Region = l.Region, CountryCode = l.CountryCode,
    };

    /// <summary>Every field empty after cleaning.</summary>
    public bool IsEmpty =>
        new[] { Line1, Line2, PostalCode, City, Region, CountryCode }.All(v => LocationAddressKey.CleanField(v) is null);

    /// <summary>
    /// The four fields a property edits are empty — its Line 2 and Region belong to the place and are not inputs,
    /// so they do not keep an emptied property address linked.
    /// </summary>
    public bool InputsEmpty =>
        new[] { Line1, PostalCode, City, CountryCode }.All(v => LocationAddressKey.CleanField(v) is null);

    /// <summary>The shared key normalization (#380) — case and whitespace only differ when this is equal.</summary>
    public string Normalized =>
        LocationAddressKey.Normalize(CountryCode, PostalCode, City, Line1, Line2, Region);

    /// <summary>
    /// Ordinal equality after <see cref="LocationTextCleaner.Clean"/> and whitespace collapse — the rule #380
    /// applies to decide whether an address changed.
    /// </summary>
    public bool SameAs(AddressFields other) =>
        Same(Line1, other.Line1) && Same(Line2, other.Line2) && Same(PostalCode, other.PostalCode)
        && Same(City, other.City) && Same(Region, other.Region) && Same(CountryCode, other.CountryCode);

    private static bool Same(string? a, string? b) =>
        string.Equals(LocationAddressKey.CleanField(a), LocationAddressKey.CleanField(b), StringComparison.Ordinal);

    public string? Get(string key) => key switch
    {
        AddressBlockModel.Line1Key => Line1,
        AddressBlockModel.Line2Key => Line2,
        AddressBlockModel.PostalCodeKey => PostalCode,
        AddressBlockModel.CityKey => City,
        AddressBlockModel.RegionKey => Region,
        AddressBlockModel.CountryCodeKey => CountryCode,
        _ => null,
    };

    public void Set(string key, string? value)
    {
        switch (key)
        {
            case AddressBlockModel.Line1Key: Line1 = value; break;
            case AddressBlockModel.Line2Key: Line2 = value; break;
            case AddressBlockModel.PostalCodeKey: PostalCode = value; break;
            case AddressBlockModel.CityKey: City = value; break;
            case AddressBlockModel.RegionKey: Region = value; break;
            case AddressBlockModel.CountryCodeKey: CountryCode = value; break;
        }
    }
}

/// <summary>The record's current link, or null for a new address.</summary>
public sealed class AddressBlockStored
{
    public required Guid LocationId { get; init; }

    public required AddressFields Fields { get; init; }

    /// <summary>The server's <c>OtherUses</c> — this record excluded, over the kinds the caller can read.</summary>
    public int OtherUses { get; set; }

    /// <summary>The per-kind breakdown of the OTHER uses, when the caller could read it (null kinds hidden).</summary>
    public LocationUsage? OthersBreakdown { get; set; }

    public bool PlaceHasCoordinates { get; init; }
}

/// <summary>The caller's claims that decide what the block offers (issue #383 §3.3).</summary>
public sealed record AddressBlockPermissions(bool CanReadLocations, bool CanUpdateLocations, bool AllKindsReadable)
{
    public static AddressBlockPermissions Full { get; } = new(true, true, true);

    /// <summary>
    /// From the signed-in principal. "Every kind readable" is the four read claims a place's uses span —
    /// contacts, properties, calendar (events and series) and journal (§3.3 rule 1, SEC F1).
    /// </summary>
    public static AddressBlockPermissions For(System.Security.Claims.ClaimsPrincipal user) => new(
        user.HasPermission(Odyssey.Dtos.Authorization.PermissionClaims.LocationsRead),
        user.HasPermission(Odyssey.Dtos.Authorization.PermissionClaims.LocationsUpdate),
        user.HasPermission(Odyssey.Dtos.Authorization.PermissionClaims.ContactsRead)
        && user.HasPermission(Odyssey.Dtos.Authorization.PermissionClaims.PropertiesRead)
        && user.HasPermission(Odyssey.Dtos.Authorization.PermissionClaims.CalendarRead)
        && user.HasPermission(Odyssey.Dtos.Authorization.PermissionClaims.JournalRead));
}

/// <summary>What one address write sends (issue #383 §5.1).</summary>
public sealed record AddressWrite(
    AddressWriteKind Kind, Guid? LocationId, AddressFields? Fields, AddressApplyTo? ApplyTo, int? ExpectedOtherUses);

/// <summary>
/// The address block's state and rules (issue #383 §3.3, design system · AddressBlock) — the editable value
/// plus every decision the block and its dialog make from it. Kept apart from the component so each state
/// transition and its request are unit-tested without rendering.
/// </summary>
public sealed class AddressBlockModel
{
    public const string Line1Key = "line1";
    public const string Line2Key = "line2";
    public const string PostalCodeKey = "postalCode";
    public const string CityKey = "city";
    public const string RegionKey = "region";
    public const string CountryCodeKey = "countryCode";
    public const string ApplyToKey = "applyTo";
    public const string LocationIdKey = "locationId";

    public const string AllUses = nameof(AddressApplyTo.AllUses);
    public const string ThisRecord = nameof(AddressApplyTo.ThisRecord);

    /// <param name="initialFields">
    /// Fields to start from when there is no stored link — an address the record carries without a place, which
    /// #380 converts away; it is then sent as a typed address.
    /// </param>
    public AddressBlockModel(
        AddressBlockVariant variant, AddressBlockStored? stored, AddressBlockPermissions permissions, AddressFields? initialFields = null)
    {
        Variant = variant;
        Stored = stored;
        Permissions = permissions;
        Fields = stored?.Fields.Copy() ?? initialFields?.Copy() ?? new AddressFields();
    }

    public AddressBlockVariant Variant { get; }

    public AddressBlockStored? Stored { get; }

    public AddressBlockPermissions Permissions { get; }

    public AddressFields Fields { get; private set; }

    /// <summary>The picked place, while it is picked.</summary>
    public ExistingLocation? Picked { get; private set; }

    /// <summary>The apply-to choice: <see cref="AllUses"/>, <see cref="ThisRecord"/>, or nothing.</summary>
    public string? ApplyTo { get; set; }

    /// <summary>The key of the address field the user last changed — the warning joins its describedby.</summary>
    public string? LastChangedField { get; private set; }

    /// <summary>The record noun the copy uses: <c>contact</c> or <c>property</c>.</summary>
    public string Noun => Variant == AddressBlockVariant.Property ? "property" : "contact";

    // ── Transitions ──────────────────────────────────────────────────────────

    /// <summary>
    /// A place was picked: the fields fill from it. Returns the announcement (<i>Address filled from ‹name›</i>).
    /// </summary>
    public string Pick(ExistingLocation place)
    {
        Picked = place;
        Fields = AddressFields.From(place);
        ApplyTo = null;
        return $"Address filled from {place.Name}";
    }

    /// <summary>The picker was cleared: back to the typed fields, keeping the values it filled in.</summary>
    public void ClearPick() => Picked = null;

    /// <summary>
    /// A field was edited. Editing a picked place's field clears the pick; the returned announcement says so
    /// (<i>Place cleared — saving uses the address you type</i>), else null. Country is uppercased as typed.
    /// </summary>
    public string? Edit(string key, string? value)
    {
        if (key == CountryCodeKey && value is not null)
            value = value.ToUpperInvariant();
        Fields.Set(key, value);
        LastChangedField = key;
        if (Picked is null)
            return null;
        var wasDifferent = Stored is null || Picked.LocationId != Stored.LocationId;
        Picked = null;
        return wasDifferent ? "Place cleared — saving uses the address you type" : null;
    }

    // ── State ────────────────────────────────────────────────────────────────

    public AddressBlockState State =>
        Picked is { } p
            ? Stored is not null && p.LocationId == Stored.LocationId ? AddressBlockState.Unchanged : AddressBlockState.Picked
            : Stored is null ? AddressBlockState.Typed
            : Variant == AddressBlockVariant.Property && Fields.InputsEmpty ? AddressBlockState.Cleared
            : Fields.SameAs(Stored.Fields) ? AddressBlockState.Unchanged
            : AddressBlockState.Changed;

    /// <summary>
    /// A change only in case or whitespace — equal under the shared <see cref="LocationAddressKey.Normalize"/>.
    /// Applied in place without asking; the server does the same.
    /// </summary>
    public bool CaseOnly => State == AddressBlockState.Changed && Stored is not null
        && string.Equals(Fields.Normalized, Stored.Fields.Normalized, StringComparison.Ordinal);

    /// <summary>
    /// Whether the apply-to question is asked (§3.3 rule 1): a Changed address that is not case-only, by a
    /// caller who may edit the place, unless nothing else uses it AND every kind is readable.
    /// </summary>
    public bool AsksApplyTo =>
        State == AddressBlockState.Changed && !CaseOnly && Permissions.CanUpdateLocations
        && (!Permissions.CanReadLocations || !(Stored!.OtherUses == 0 && Permissions.AllKindsReadable));

    /// <summary>Whether the save will send <c>AllUses</c>.</summary>
    public bool SendsAllUses =>
        State == AddressBlockState.Changed
        && (CaseOnly || (AsksApplyTo ? ApplyTo == AllUses : Permissions.CanUpdateLocations));

    /// <summary>
    /// The coordinates warning (§3.3 rule 3, decision 8): Changed, the place has coordinates, and the save
    /// will send <c>AllUses</c>. Never for <i>Only this ‹noun›</i>, where the old place keeps them.
    /// </summary>
    public bool ShowsCoordinatesWarning => SendsAllUses && Stored?.PlaceHasCoordinates == true;

    public const string CoordinatesWarning =
        "This place has map coordinates for the old address — check them on the Locations page after saving.";

    public const string GroupAppears = "This address is shared — choose who the change applies to";

    /// <summary>
    /// The property's <i>Also on the saved place</i> tile: the linked place's Line 2 and Region, read-only. In
    /// Picked and Unchanged, and in Changed only when the save sends <c>AllUses</c>; hidden in Typed and Cleared.
    /// </summary>
    public string? AlsoOnSavedPlace
    {
        get
        {
            if (Variant != AddressBlockVariant.Property)
                return null;
            var source = State switch
            {
                AddressBlockState.Picked => Picked is { } p ? AddressFields.From(p) : null,
                AddressBlockState.Unchanged => Picked is { } p ? AddressFields.From(p) : Stored?.Fields,
                AddressBlockState.Changed when SendsAllUses => Stored?.Fields,
                _ => null,
            };
            if (source is null)
                return null;
            var parts = new[] { source.Line2, source.Region }.Select(LocationAddressKey.CleanField).Where(v => v is not null).ToArray();
            return parts.Length == 0 ? null : string.Join(" · ", parts);
        }
    }

    /// <summary>The picker's value: the picked place, or the stored one while unchanged.</summary>
    public Guid? PickerId => Picked?.LocationId ?? (State == AddressBlockState.Unchanged ? Stored?.LocationId : null);

    /// <summary>The usage line, or null when there is none to show (§3.3 usage copy).</summary>
    public string? UsageLine
    {
        get
        {
            if (!Permissions.CanReadLocations)
                return null;
            switch (State)
            {
                case AddressBlockState.Picked when Picked?.Usage is { } usage:
                    return LocationVisuals.AddressUsageLine(
                        LocationVisuals.VisibleUses(usage), usage, Permissions.AllKindsReadable, Noun, picked: true);
                case AddressBlockState.Unchanged or AddressBlockState.Changed when Stored is not null:
                    return LocationVisuals.AddressUsageLine(
                        Stored.OtherUses, Stored.OthersBreakdown, Permissions.AllKindsReadable, Noun, picked: false);
                default:
                    return null;
            }
        }
    }

    /// <summary>Whether the usage line names other records (its glyph is <c>link</c>, else <c>info</c>).</summary>
    public bool UsageIsShared => State switch
    {
        AddressBlockState.Picked => LocationVisuals.VisibleUses(Picked?.Usage) > 0,
        _ => Stored?.OtherUses > 0,
    };

    /// <summary>The help under the picker for a typed new address.</summary>
    public string? PickerHelp => Stored is null && Picked is null
        ? "Addresses are shared places. Saving uses the existing place at this address, or adds a new one."
        : null;

    public const string ClearedHelp = "The address stays as a saved place on the Locations page.";

    /// <summary>The two apply-to options, each with its consequence as the description.</summary>
    public IReadOnlyList<OdsRadioOption> ApplyToOptions =>
    [
        new(AllUses, Permissions.CanReadLocations && Permissions.AllKindsReadable && Stored is not null
            ? $"All {Stored.OtherUses + 1} uses of this address"
            : "All uses of this address")
        {
            Description = "Every contact, property and event at this place is updated.",
        },
        new(ThisRecord, $"Only this {Noun}")
        {
            Description = $"This {Noun} moves to the new address; the others keep the old one.",
        },
    ];

    // ── Validation and the request ───────────────────────────────────────────

    /// <summary>Client-side checks on save (§8) — the server stays the enforcement.</summary>
    public Dictionary<string, string> Validate()
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (State is AddressBlockState.Typed or AddressBlockState.Changed)
        {
            if (Variant == AddressBlockVariant.Contact)
            {
                if (LocationAddressKey.CleanField(Fields.Line1) is null)
                    errors[Line1Key] = "Line 1 is required.";
                if (LocationAddressKey.CleanField(Fields.City) is null)
                    errors[CityKey] = "City is required.";
                if (LocationAddressKey.CleanField(Fields.CountryCode) is null)
                    errors[CountryCodeKey] = "Country is required.";
            }

            if (LocationAddressKey.CleanField(Fields.CountryCode) is { } country
                && !System.Text.RegularExpressions.Regex.IsMatch(country, LocationRules.CountryCodePattern))
                errors[CountryCodeKey] = "Two letters, e.g. NO.";
        }

        if (AsksApplyTo && string.IsNullOrEmpty(ApplyTo))
            errors[ApplyToKey] = "Choose who this change applies to";
        return errors;
    }

    /// <summary>What the save sends for the address (§5.1), given the state.</summary>
    public AddressWrite ToWrite()
    {
        switch (State)
        {
            case AddressBlockState.Picked:
                return new(AddressWriteKind.LinkById, Picked!.LocationId, null, null, null);
            case AddressBlockState.Unchanged:
                return new(AddressWriteKind.None, Stored!.LocationId, null, null, null);
            case AddressBlockState.Cleared:
                return new(AddressWriteKind.None, null, null, null, null);
            case AddressBlockState.Typed:
                return new(AddressWriteKind.Typed, null, Cleaned(), null, null);
            default:
                var allUses = SendsAllUses;
                return new(
                    allUses ? AddressWriteKind.AllUses : AddressWriteKind.ThisRecord,
                    null,
                    Cleaned(),
                    allUses ? AddressApplyTo.AllUses : AddressApplyTo.ThisRecord,
                    Stored!.OtherUses);
        }
    }

    /// <summary>The fields as sent: cleaned, whitespace collapsed, empty as null, country upper-cased.</summary>
    private AddressFields Cleaned() => new()
    {
        Line1 = LocationAddressKey.CleanField(Fields.Line1),
        Line2 = Variant == AddressBlockVariant.Property ? null : LocationAddressKey.CleanField(Fields.Line2),
        PostalCode = LocationAddressKey.CleanField(Fields.PostalCode),
        City = LocationAddressKey.CleanField(Fields.City),
        Region = Variant == AddressBlockVariant.Property ? null : LocationAddressKey.CleanField(Fields.Region),
        CountryCode = LocationAddressKey.CleanField(Fields.CountryCode)?.ToUpperInvariant(),
    };

    // ── Server replies (issue #383 §5.3, §7) ─────────────────────────────────

    /// <summary>The 403 message, chosen from the request that was sent (#380's 403 carries no reason).</summary>
    public static string ForbiddenMessage(AddressWriteKind kind, string noun) => kind switch
    {
        AddressWriteKind.LinkById => "You don’t have permission to choose a saved place.",
        AddressWriteKind.Typed => "Adding a new address needs permission to create locations.",
        AddressWriteKind.ThisRecord => $"Changing this address for only this {noun} needs permission to create locations.",
        AddressWriteKind.AllUses => $"You can’t change this address for everyone. Choose “Only this {noun}”.",
        _ => "You don’t have permission to make this change.",
    };

    /// <summary>The <c>409</c> keyed <c>line1</c>: another place already has the typed address (SEC F8).</summary>
    public static string DuplicateAddressMessage(string noun, bool canUpdateLocations) => canUpdateLocations
        ? $"Another saved place already has this address. Merge them on the Locations page, or choose “Only this {noun}”."
        : $"Another saved place already has this address. Choose “Only this {noun}”.";

    public const string UsesChangedAlert = "Someone changed who uses this address. Check the count and choose again.";

    public const string ContactsNeedFullAddress = "Contacts use this place, so it needs Line 1, City and Country.";

    public const string PlaceNotAddressComplete = "This place has no full address. Pick another or enter the address.";

    public const string PlaceUnusable = "That place can’t be used here — pick another.";

    public const string ContactAlreadyHasAddress = "This contact already has this address.";

    /// <summary>
    /// The picker's 422 <c>locationId</c> message, chosen from the picked place itself: an incomplete address
    /// (no Line 1, City or Country) is the one refusal the user can fix by typing, so it says so; anything else
    /// (archived, gone) asks for another place. Never read from the server's prose, which is not a contract —
    /// the coupling <c>LocationMergeDialog.SourceRefusal</c> avoids for the same reason (frontend review N3).
    /// </summary>
    public string LocationIdMessage =>
        Picked is { } place && !LocationVisuals.IsAddressComplete(place) ? PlaceNotAddressComplete : PlaceUnusable;

    /// <summary>A <c>409 applyTo</c>: the usage is reloaded and the group asked again with no selection.</summary>
    public void UsesChanged(int otherUses, LocationUsage? breakdown)
    {
        if (Stored is null)
            return;
        Stored.OtherUses = otherUses;
        Stored.OthersBreakdown = breakdown;
        ApplyTo = null;
    }
}
