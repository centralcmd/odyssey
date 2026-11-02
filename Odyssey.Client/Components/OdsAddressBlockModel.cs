using Odyssey.Dtos.Authorization;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Components;

/// <summary>The two address editors that share the address field (issue #383 §3.3a).</summary>
public enum AddressBlockVariant
{
    /// <summary>A contact address: the address is required; hand entry has six fields.</summary>
    Contact,

    /// <summary>A real-estate property: the address is optional; hand entry has the four fields the property carries.</summary>
    Property,
}

/// <summary>What <c>OdsAddressBlock</c> draws (design system · AddressBlock <c>mode</c>, issue #383 v5).</summary>
public enum AddressBlockMode
{
    /// <summary>The hand-entry group, opened by the picker's <i>Enter an address by hand…</i> row.</summary>
    Hand,

    /// <summary>Under a linked address: the usage line and <b>Edit address</b>.</summary>
    Linked,
}

/// <summary>What an address write sends, for picking the 403 message (issue #383 §5.3).</summary>
public enum AddressWriteKind
{
    /// <summary>Nothing changes: the stored place re-sent by id, or no address at all.</summary>
    None,

    /// <summary>A different saved place, by id.</summary>
    LinkById,

    /// <summary>Fields for a record with no place yet — find-or-create.</summary>
    Typed,

    /// <summary>Fields replacing a stored place — <c>applyTo: ThisRecord</c>, relinking only this record.</summary>
    ThisRecord,
}

/// <summary>The six structured fields of an address (a property uses four of them).</summary>
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

    /// <summary>The four fields a property carries are empty — its hand entry has no Line 2 or Region.</summary>
    public bool InputsEmpty =>
        new[] { Line1, PostalCode, City, CountryCode }.All(v => LocationAddressKey.CleanField(v) is null);

    /// <summary>
    /// Ordinal equality after <see cref="LocationTextCleaner.Clean"/> and whitespace collapse — the rule #380 applies
    /// to decide whether an address changed. A property compares the four fields it carries.
    /// </summary>
    public bool SameAs(AddressFields other, bool propertyFields = false) =>
        Same(Line1, other.Line1) && Same(PostalCode, other.PostalCode) && Same(City, other.City)
        && Same(CountryCode, other.CountryCode)
        && (propertyFields || (Same(Line2, other.Line2) && Same(Region, other.Region)));

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

    public required AddressFields Fields { get; set; }

    /// <summary>The server's <c>OtherUses</c> — this record excluded, over the kinds the caller can read.</summary>
    public int OtherUses { get; set; }

    /// <summary>The per-kind breakdown of the OTHER uses, when the caller could read it (null kinds hidden).</summary>
    public LocationUsage? OthersBreakdown { get; set; }
}

/// <summary>The caller's claims that decide what the address field offers (issue #383 §3.3a).</summary>
/// <param name="CanReadLocations"><c>locations.read</c>: the picker and the usage line.</param>
/// <param name="CanUpdateLocations"><c>locations.update</c>: <b>Edit address</b>.</param>
/// <param name="AllKindsReadable">The four read claims a place's uses span — the usage line's "you can see" rule.</param>
/// <param name="CanCreateLocations"><c>locations.create</c>: the Kartverket group, hand entry by choice and the disclosure.</param>
public sealed record AddressBlockPermissions(
    bool CanReadLocations, bool CanUpdateLocations, bool AllKindsReadable, bool CanCreateLocations = false)
{
    public static AddressBlockPermissions Full { get; } = new(true, true, true, true);

    /// <summary>
    /// From the signed-in principal. "Every kind readable" is the four read claims a place's uses span —
    /// contacts, properties, calendar (events and series) and journal (§3.3 rule 1, SEC F1).
    /// </summary>
    public static AddressBlockPermissions For(System.Security.Claims.ClaimsPrincipal user) => new(
        user.HasPermission(PermissionClaims.LocationsRead),
        user.HasPermission(PermissionClaims.LocationsUpdate),
        user.HasPermission(PermissionClaims.ContactsRead)
        && user.HasPermission(PermissionClaims.PropertiesRead)
        && user.HasPermission(PermissionClaims.CalendarRead)
        && user.HasPermission(PermissionClaims.JournalRead),
        user.HasPermission(PermissionClaims.LocationsCreate));
}

/// <summary>
/// The geocode a staged Kartverket address carries to the record write (#380 §5.3a). The server uses it only when the
/// write CREATES the place; an existing place with that address is linked unchanged.
/// </summary>
public sealed record AddressGeocode(decimal Latitude, decimal Longitude, string TimeZoneId, string? SourceReference)
{
    public LocationSource Source => LocationSource.Kartverket;
}

/// <summary>
/// What one address write sends (issue #383 §5.1, §3.3a). Built only by <see cref="AddressBlockModel.ToWrite"/> (and
/// <see cref="None"/> for "no address at all"), so the link it carries is always the field's own decision;
/// <c>LocationWriteBuilderLintTests</c> enforces it (frontend review N6).
/// </summary>
public sealed record AddressWrite(
    AddressWriteKind Kind, Guid? LocationId, AddressFields? Fields, AddressApplyTo? ApplyTo, int? ExpectedOtherUses,
    AddressGeocode? Geocode = null)
{
    /// <summary>No address: nothing linked, nothing typed — a vehicle, or a cleared property.</summary>
    public static AddressWrite None { get; } = new(AddressWriteKind.None, null, null, null, null);
}

/// <summary>
/// The address field's state and rules (issue #383 v5 §3.3a; design system · AddressBlock <c>AddressValue</c>,
/// <c>validate</c>, <c>toRequest</c>): the linked or picked saved place, a staged Kartverket address, or the
/// hand-entry fields — and every decision the field and its dialog make from them. Kept apart from the components
/// so what each state sends is unit-tested without rendering.
/// </summary>
/// <remarks>
/// The v4 editor (the Typed/Picked/Unchanged/Changed/Cleared table, the apply-to group, the coordinates warning and
/// the "Also on the saved place" tile) is retired. The UI never sends <c>AllUses</c>: picking or entering a different
/// address relinks only this record, and a change "for everyone" goes through <b>Edit address</b>, which edits the
/// place itself in the location dialog.
/// </remarks>
public sealed class AddressBlockModel
{
    public const string Line1Key = "line1";
    public const string Line2Key = "line2";
    public const string PostalCodeKey = "postalCode";
    public const string CityKey = "city";
    public const string RegionKey = "region";
    public const string CountryCodeKey = "countryCode";

    /// <summary>The picker's own error key (design system · AddressBlock.validate <c>address</c>).</summary>
    public const string AddressKey = "address";

    /// <param name="initialFields">
    /// Fields to start from when there is no stored link — an address the record carries without a place (a legacy
    /// property address #380 did not convert). It opens in hand entry, so it is sent as typed.
    /// </param>
    public AddressBlockModel(
        AddressBlockVariant variant, AddressBlockStored? stored, AddressBlockPermissions permissions, AddressFields? initialFields = null)
    {
        Variant = variant;
        Stored = stored;
        Permissions = permissions;
        LinkedId = stored?.LocationId;
        Fields = stored?.Fields.Copy() ?? initialFields?.Copy() ?? new AddressFields();
        // Without locations.read there is no picker: the fields are the field, as before v5 (issue #383 AC 2).
        Hand = !permissions.CanReadLocations || (stored is null && initialFields is { IsEmpty: false });
    }

    public AddressBlockVariant Variant { get; }

    public AddressBlockStored? Stored { get; }

    public AddressBlockPermissions Permissions { get; }

    /// <summary>The saved place the picker holds — the stored one, or one just picked. Null for none.</summary>
    public Guid? LinkedId { get; private set; }

    /// <summary>A different saved place just picked, for its usage. Null for the stored place or none.</summary>
    public ExistingLocation? PickedPlace { get; private set; }

    /// <summary>A Kartverket address picked in the picker — staged until Save.</summary>
    public LocationCandidate? Staged { get; private set; }

    /// <summary>The hand-entry group is open in place of the picker.</summary>
    public bool Hand { get; private set; }

    /// <summary>The hand-entry values. Kept across <see cref="SearchInstead"/>, so going back and forth loses nothing.</summary>
    public AddressFields Fields { get; private set; }

    /// <summary>The record noun the copy uses: <c>contact</c> or <c>property</c>.</summary>
    public string Noun => Variant == AddressBlockVariant.Property ? "property" : "contact";

    // ── Transitions ──────────────────────────────────────────────────────────

    /// <summary>A saved place was picked — the stored one, or another (with its usage, when it could be read).</summary>
    public void PickPlace(Guid id, ExistingLocation? place = null)
    {
        LinkedId = id;
        PickedPlace = id == Stored?.LocationId ? null : place;
        Staged = null;
    }

    /// <summary>A Kartverket address was picked: staged, nothing created. Returns the announcement.</summary>
    public string Stage(LocationCandidate candidate)
    {
        Staged = candidate;
        LinkedId = null;
        PickedPlace = null;
        return $"{LocationVisuals.CandidateAddress(candidate)} will be added when you save";
    }

    /// <summary>The picker was cleared — on a property, that unlinks it.</summary>
    public void ClearPick()
    {
        LinkedId = null;
        PickedPlace = null;
        Staged = null;
    }

    /// <summary>
    /// <i>Enter an address by hand…</i>: the hand-entry group opens with the typed text as Line 1. Only Line 1 is
    /// set — what was entered before in the other fields is kept — and with nothing typed, all of it is kept.
    /// </summary>
    public void EnterByHand(string? typed)
    {
        Hand = true;
        Staged = null;
        var text = typed?.Trim();
        if (!string.IsNullOrEmpty(text))
            Fields.Line1 = text;
    }

    /// <summary><i>Search instead</i>: back to the picker, keeping the typed fields until Save or Cancel.</summary>
    public void SearchInstead() => Hand = false;

    /// <summary>A hand-entry field was edited. Country is uppercased as typed.</summary>
    public void Edit(string key, string? value)
    {
        if (key == CountryCodeKey && value is not null)
            value = value.ToUpperInvariant();
        Fields.Set(key, value);
    }

    /// <summary>The place was edited through <b>Edit address</b>: its new fields are what the record now shows.</summary>
    public void PlaceEdited(ExistingLocation place)
    {
        if (Stored is not null && place.LocationId == Stored.LocationId)
            Stored.Fields = AddressFields.From(place);
        if (PickedPlace?.LocationId == place.LocationId)
            PickedPlace = place;
    }

    // ── What the field shows ─────────────────────────────────────────────────

    /// <summary>Whether the picker can be shown — <c>locations.read</c>; without it the fields are the field.</summary>
    public bool CanSearchInstead => Permissions.CanReadLocations;

    /// <summary>The picker's value: a staged address, a saved place, or nothing.</summary>
    public LocationPick? PickerValue =>
        Staged is { } c ? LocationPick.Address(c) : LinkedId is { } id ? LocationPick.Existing(id) : null;

    /// <summary>The picker holds a saved place: the usage line and <b>Edit address</b> show under it.</summary>
    public bool ShowsLinked => !Hand && Staged is null && LinkedId is not null;

    /// <summary><b>Edit address</b> — <c>locations.update</c>, and the place must be readable to edit it.</summary>
    public bool CanEditAddress => ShowsLinked && Permissions.CanUpdateLocations && Permissions.CanReadLocations;

    private bool LinkedIsStored => LinkedId is { } id && id == Stored?.LocationId;

    /// <summary>The usage line, or null when there is none to show (§3.3 usage copy, visible-kind rule).</summary>
    public string? UsageLine
    {
        get
        {
            if (!ShowsLinked || !Permissions.CanReadLocations)
                return null;
            if (LinkedIsStored)
                return LocationVisuals.AddressUsageLine(
                    Stored!.OtherUses, Stored.OthersBreakdown, Permissions.AllKindsReadable, Noun, picked: false);
            return PickedPlace?.Usage is { } usage
                ? LocationVisuals.AddressUsageLine(LocationVisuals.VisibleUses(usage), usage, Permissions.AllKindsReadable, Noun, picked: true)
                : null;
        }
    }

    /// <summary>Whether the usage line names other records (its glyph is <c>link</c>, else <c>info</c>).</summary>
    public bool UsageIsShared => LinkedIsStored ? Stored!.OtherUses > 0 : LocationVisuals.VisibleUses(PickedPlace?.Usage) > 0;

    /// <summary>
    /// The N in the location dialog's <i>Changes apply to every record that uses this address (N)</i>: every use, this
    /// record included once it is linked — or null when some kinds are hidden, and the line then names no count.
    /// </summary>
    public int? SharedUses =>
        !Permissions.AllKindsReadable ? null
        : LinkedIsStored ? Stored!.OtherUses + 1
        : PickedPlace?.Usage is { } usage ? LocationVisuals.VisibleUses(usage)
        : null;

    /// <summary>A property whose picker was cleared: saving unlinks it, and the place stays.</summary>
    public bool Cleared => Variant == AddressBlockVariant.Property && Stored is not null && !Hand && Staged is null && LinkedId is null;

    /// <summary>The help under the picker.</summary>
    public string? PickerHelp => Cleared ? ClearedHelp : null;

    public const string ClearedHelp = "The address stays as a saved place on the Locations page.";

    public const string AddressRequired = "Choose an address, or enter one by hand.";

    // ── Validation and the request ───────────────────────────────────────────

    /// <summary>Client-side checks on save (§8) — the server stays the enforcement.</summary>
    public Dictionary<string, string> Validate()
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Hand)
        {
            // An emptied property address unlinks it; nothing is required.
            if (Variant == AddressBlockVariant.Property && Fields.InputsEmpty)
                return errors;
            if (LocationAddressKey.CleanField(Fields.Line1) is null)
                errors[Line1Key] = "Line 1 is required.";
            if (LocationAddressKey.CleanField(Fields.City) is null)
                errors[CityKey] = "City is required.";
            if (LocationAddressKey.CleanField(Fields.CountryCode) is not { } country)
                errors[CountryCodeKey] = "Country is required.";
            else if (!System.Text.RegularExpressions.Regex.IsMatch(country, LocationRules.CountryCodePattern))
                errors[CountryCodeKey] = "Two letters, e.g. NO.";
        }
        else if (Variant == AddressBlockVariant.Contact && LinkedId is null && Staged is null)
        {
            errors[AddressKey] = AddressRequired;
        }

        return errors;
    }

    /// <summary>
    /// What the save sends for the address (#380 §5.3a; design system · AddressBlock.toRequest): a saved place by id;
    /// a staged Kartverket address as its fields plus its geocode; hand entry as the fields; nothing for a cleared
    /// property. A fields write on a record that already has a place sends <c>applyTo: ThisRecord</c> and the
    /// <c>expectedOtherUses</c> shown — the record moves to the new address and the old place is left as it is.
    /// </summary>
    public AddressWrite ToWrite()
    {
        if (Hand)
        {
            var propertyFields = Variant == AddressBlockVariant.Property;
            if (propertyFields && Fields.InputsEmpty)
                return AddressWrite.None;
            if (Stored is not null && Fields.SameAs(Stored.Fields, propertyFields))
                return new(AddressWriteKind.None, Stored.LocationId, null, null, null);
            return FieldsWrite(Cleaned(Fields), null);
        }

        if (Staged is { } c)
        {
            var fields = Cleaned(new AddressFields { Line1 = c.Line1, PostalCode = c.PostalCode, City = c.City, CountryCode = c.CountryCode ?? "NO" });
            var geocode = c.TimeZoneId is { } zone ? new AddressGeocode(c.Latitude, c.Longitude, zone, c.SourceReference) : null;
            return FieldsWrite(fields, geocode);
        }

        if (LinkedId is { } id)
            return id == Stored?.LocationId
                ? new(AddressWriteKind.None, id, null, null, null)
                : new(AddressWriteKind.LinkById, id, null, null, null);

        return AddressWrite.None;
    }

    private AddressWrite FieldsWrite(AddressFields fields, AddressGeocode? geocode) => Stored is null
        ? new(AddressWriteKind.Typed, null, fields, null, null, geocode)
        : new(AddressWriteKind.ThisRecord, null, fields, AddressApplyTo.ThisRecord, Stored.OtherUses, geocode);

    /// <summary>The fields as sent: cleaned, whitespace collapsed, empty as null, country upper-cased.</summary>
    private AddressFields Cleaned(AddressFields source) => new()
    {
        Line1 = LocationAddressKey.CleanField(source.Line1),
        Line2 = Variant == AddressBlockVariant.Property ? null : LocationAddressKey.CleanField(source.Line2),
        PostalCode = LocationAddressKey.CleanField(source.PostalCode),
        City = LocationAddressKey.CleanField(source.City),
        Region = Variant == AddressBlockVariant.Property ? null : LocationAddressKey.CleanField(source.Region),
        CountryCode = LocationAddressKey.CleanField(source.CountryCode)?.ToUpperInvariant(),
    };

    // ── Server replies (issue #383 §5.3, §7) ─────────────────────────────────

    /// <summary>The 403 message, chosen from the request that was sent (#380's 403 carries no reason).</summary>
    public static string ForbiddenMessage(AddressWriteKind kind, string noun) => kind switch
    {
        AddressWriteKind.LinkById => "You don’t have permission to choose a saved place.",
        AddressWriteKind.Typed => "Adding a new address needs permission to create locations.",
        AddressWriteKind.ThisRecord => $"Moving this {noun} to a new address needs permission to create locations.",
        _ => "You don’t have permission to make this change.",
    };

    /// <summary>The <c>409</c> keyed <c>line1</c>: another place already has the address (SEC F8).</summary>
    public static string DuplicateAddressMessage(bool canUpdateLocations) => canUpdateLocations
        ? "Another saved place already has this address. Merge them on the Locations page, or pick that place."
        : "Another saved place already has this address. Pick that place instead.";

    /// <summary>A <c>409</c> keyed <c>applyTo</c>: the uses changed under the user (a case-only change is edited in place).</summary>
    public const string UsesChangedAlert = "Someone changed who uses this address. Check it and save again.";

    public const string ContactsNeedFullAddress = "Contacts use this place, so it needs Line 1, City and Country.";

    public const string PlaceNotAddressComplete = "This place has no full address. Pick another or enter the address.";

    public const string PlaceUnusable = "That place can’t be used here — pick another.";

    public const string ContactAlreadyHasAddress = "This contact already has this address.";

    /// <summary>A <c>422</c> keyed <c>latitude</c> or <c>source</c> on a staged Kartverket address.</summary>
    public const string StagedAddressUnusable = "That address can’t be used — search again or enter it by hand.";

    /// <summary>
    /// The picker's 422 <c>locationId</c> message, chosen from the picked place itself: an incomplete address is the
    /// one refusal the user can fix by entering it, so it says so; anything else (archived, gone) asks for another
    /// place. Never read from the server's prose, which is not a contract (frontend review N3).
    /// </summary>
    public string LocationIdMessage =>
        PickedPlace is { } place && !LocationVisuals.IsAddressComplete(place) ? PlaceNotAddressComplete : PlaceUnusable;

    /// <summary>A <c>409 applyTo</c>: the usage is reloaded so the line shows the count the server now has.</summary>
    public void UsesChanged(int otherUses, LocationUsage? breakdown)
    {
        if (Stored is null)
            return;
        Stored.OtherUses = otherUses;
        Stored.OthersBreakdown = breakdown;
    }
}
