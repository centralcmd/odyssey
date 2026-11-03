using System.Net;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Components;

/// <summary>
/// The value of an <c>OdsLocationSelect</c> (issue #383 §4): an existing place by <see cref="Id"/>, a
/// <b>staged</b> new one by <see cref="NewName"/> that the parent creates when its own dialog saves, or a staged
/// Kartverket <see cref="Candidate"/> — in address mode (v5) the record write sends it as fields plus its geocode; in
/// place mode (v6 §3.2a) the parent creates it as a place when its dialog saves. Exactly one of the three is set.
/// </summary>
public sealed record LocationPick(Guid? Id, string? NewName)
{
    public static LocationPick Existing(Guid id) => new(id, null);

    public static LocationPick Staged(string name) => new(null, name);

    /// <summary>A Kartverket address picked in the picker: nothing is created until the record saves.</summary>
    public static LocationPick Address(LocationCandidate candidate) => new(null, null) { Candidate = candidate };

    /// <summary>The staged Kartverket address.</summary>
    public LocationCandidate? Candidate { get; init; }

    public bool IsStaged => Id is null && !string.IsNullOrEmpty(NewName);

    /// <summary>A staged Kartverket address (design system · <c>isStagedAddress</c>).</summary>
    public bool IsStagedAddress => Id is null && Candidate is not null;
}

/// <summary>What an <c>OdsLocationSelect</c> is for (design system · LocationSelect <c>mode</c>).</summary>
public enum LocationSelectMode
{
    /// <summary>The Place of an event, series or journal entry.</summary>
    Place,

    /// <summary>
    /// The one Address field of a contact or property (issue #383 v5 §3.3a): address-complete saved places, the
    /// Kartverket addresses and the hand-entry row, in labelled groups; never a create row.
    /// </summary>
    Address,
}

/// <summary>Which places an <c>OdsLocationSelect</c> offers.</summary>
public enum LocationPickFilter
{
    /// <summary>Every active place.</summary>
    All,

    /// <summary>Only places with Line 1, City and Country — a contact or property address (#380).</summary>
    AddressComplete,
}

/// <summary>
/// What a write of an event, series or journal entry does with its place link (issue #383 §5.2). The
/// server's <c>PUT</c> is a full replacement and an omitted <c>locationId</c> clears the link, so every
/// builder takes one of these as a <b>required</b> argument, and a literal <see langword="null"/> link can
/// only come from <see cref="Clear"/> — the explicit clear-link path.
/// </summary>
public sealed class LocationLink
{
    private LocationLink(LocationLinkKind kind, Guid? id)
    {
        Kind = kind;
        Id = id;
    }

    public LocationLinkKind Kind { get; }

    /// <summary>The <c>locationId</c> the write sends.</summary>
    public Guid? Id { get; }

    /// <summary>Re-send what is stored (null for a record that has no place) — needs no claim (#380 v8).</summary>
    public static LocationLink Keep(Guid? stored) => new(LocationLinkKind.Keep, stored);

    /// <summary>Link a place by id — a change of link, which needs <c>locations.read</c>.</summary>
    public static LocationLink Set(Guid id) => new(LocationLinkKind.Set, id);

    /// <summary>Remove the link.</summary>
    public static LocationLink Clear { get; } = new(LocationLinkKind.Clear, null);

    /// <summary>
    /// The link an editor's draft resolves to. Without the picker (no <c>locations.read</c>) the stored id
    /// is kept whatever the draft says; a staged place must have been created first.
    /// </summary>
    public static LocationLink For(Guid? stored, LocationPick? pick, Guid? createdId, bool pickerShown)
    {
        if (!pickerShown)
            return Keep(stored);
        if (pick is null)
            return stored is null ? Keep(null) : Clear;
        if (pick.Id is { } id)
            return id == stored ? Keep(stored) : Set(id);
        return createdId is { } created
            ? Set(created)
            : throw new InvalidOperationException("A staged place is created before the record is written.");
    }

    public override string ToString() => $"{Kind}({Id})";
}

public enum LocationLinkKind { Keep, Set, Clear }

/// <summary>
/// The place half of an event, series or entry draft (issue #383 §3.2, v6 §3.2a): the stored link, the picker's
/// value, and the id of a staged place once it has been created — or, for a staged Kartverket address that turned
/// out to exist already, the id of that place. A retry after a failed record write reuses <see cref="CreatedId"/>
/// and does not post again.
/// </summary>
public sealed class PlaceDraft
{
    /// <summary>The picker error when a Kartverket address could not be added (issue #383 v6 §3.2a).</summary>
    public const string AddressAddFailed = "Couldn’t add this address — pick a saved place or try again.";

    /// <summary>How many times a Kartverket address's name is retried with <c> (2)</c>, <c> (3)</c>, … (§3.2a).</summary>
    public const int NameSuffixRetries = 5;

    public Guid? StoredId { get; init; }

    /// <summary>The stored link's embed — null without <c>locations.read</c>.</summary>
    public LocationRef? StoredRef { get; init; }

    public LocationPick? Pick { get; set; }

    /// <summary>
    /// The place a staged pick resolved to once <see cref="EnsureCreatedAsync"/> ran — the created place, or the
    /// existing one a staged address already named — for that same pick only.
    /// </summary>
    public Guid? CreatedId => _created is { } c && StagedKey(Pick) is { } key && c.Key == key ? c.Id : null;

    private (string Key, Guid Id)? _created;

    /// <summary>The picker's field error — a staged create that failed, or a server <c>422</c>/<c>409</c>.</summary>
    public string? Error { get; set; }

    /// <summary>What a staged pick is called, for the save's announcement — null when nothing is staged.</summary>
    public string? StagedName => Pick switch
    {
        { IsStaged: true } p => p.NewName,
        { IsStagedAddress: true, Candidate: { } c } => LocationVisuals.CandidateAddress(c),
        _ => null,
    };

    public static PlaceDraft For(Guid? storedId, LocationRef? storedRef) => new()
    {
        StoredId = storedId,
        StoredRef = storedRef,
        Pick = storedId is { } id ? LocationPick.Existing(id) : null,
    };

    /// <summary>The link the record write sends.</summary>
    public LocationLink Link(bool pickerShown) => LocationLink.For(StoredId, Pick, CreatedId, pickerShown);

    // Which staged pick a created id belongs to: a new name, or a Kartverket address by its source reference.
    private static string? StagedKey(LocationPick? pick) => pick switch
    {
        { IsStaged: true } p => "name:" + p.NewName,
        { IsStagedAddress: true, Candidate: { } c } => "addr:" + (c.SourceReference ?? LocationVisuals.CandidateAddress(c)),
        _ => null,
    };

    /// <summary>
    /// Creates a staged place, if there is one not yet created — immediately before the record write
    /// (issue #383 §3.2, §3.2a). Returns false, with <see cref="Error"/> set, when the create failed: the save
    /// then stops and nothing else is written.
    /// </summary>
    public async Task<bool> EnsureCreatedAsync(ILocationsApiClient locations, bool pickerShown)
    {
        if (!pickerShown || CreatedId is not null)
            return true;

        if (Pick is { IsStagedAddress: true, Candidate: { } candidate } address)
            return await CreateAddressAsync(locations, address, candidate);

        if (Pick is not { IsStaged: true } pick)
            return true;

        var result = await locations.CreateAsync(new NewLocation { Name = pick.NewName!, Source = LocationSource.Manual });
        if (result.IsSuccess && result.Value is { } created)
        {
            Resolved(pick, created.LocationId);
            return true;
        }

        Error = FirstError(result) ?? "Couldn’t add the place.";
        return false;
    }

    /// <summary>
    /// A staged Kartverket address (§3.2a): <c>POST /api/locations</c> named by the composed address, with the
    /// structured fields and the geocode. A <c>409</c> keyed <c>line1</c> means the address is already a saved
    /// place, which is found by its address key and linked unchanged — never posted again. A <c>409</c> keyed
    /// <c>name</c> means a different place has the name, so the name is retried with a suffix.
    /// </summary>
    /// <summary>How many places sharing a street line the lookup reads — far beyond the picker's page of 20.</summary>
    internal const int AddressLookupPageSize = 500;

    internal const string AddressArchived =
        "This address is archived on the Locations page — restore it there, or pick another place.";

    private async Task<bool> CreateAddressAsync(ILocationsApiClient locations, LocationPick pick, LocationCandidate candidate)
    {
        var baseName = AddressPlaceName(candidate);
        for (var ordinal = 1; ordinal <= 1 + NameSuffixRetries; ordinal++)
        {
            var result = await locations.CreateAsync(AddressLocation(candidate, SuffixedName(baseName, ordinal)));
            if (result.IsSuccess && result.Value is { } created)
            {
                Resolved(pick, created.LocationId);
                return true;
            }

            if (result.Status == HttpStatusCode.Conflict && result.Problem?.ErrorFor("line1") is not null)
            {
                var found = await FindByAddressAsync(locations, candidate);
                if (found.Id is { } existing)
                {
                    Resolved(pick, existing);
                    return true;
                }

                Error = found.Error ?? AddressAddFailed;
                return false;
            }

            if (result.Status == HttpStatusCode.Conflict && result.Problem?.ErrorFor("name") is not null)
                continue;

            Error = FirstError(result) ?? AddressAddFailed;
            return false;
        }

        Error = AddressAddFailed;
        return false;
    }

    private void Resolved(LocationPick pick, Guid id)
    {
        _created = (StagedKey(pick)!, id);
        Error = null;
    }

    private static string? FirstError(ApiResult<ExistingLocation> result) =>
        result.Problem?.ErrorFor("name") ?? result.Problem?.Errors.Values.FirstOrDefault()?.FirstOrDefault() ?? result.Error;

    /// <summary>The fields of a staged Kartverket address as sent: cleaned, country defaulting to <c>NO</c>.</summary>
    private static (string? Line1, string? PostalCode, string? City, string CountryCode) AddressFieldsOf(LocationCandidate c) => (
        LocationAddressKey.CleanField(c.Line1),
        LocationAddressKey.CleanField(c.PostalCode),
        LocationAddressKey.CleanField(c.City),
        (LocationAddressKey.CleanField(c.CountryCode) ?? "NO").ToUpperInvariant());

    /// <summary>A staged address's place name: the composed address, from the client's one composer.</summary>
    internal static string AddressPlaceName(LocationCandidate c)
    {
        var (line1, postalCode, city, country) = AddressFieldsOf(c);
        return LocationVisuals.ComposeAddress(line1, null, postalCode, city, null, country) ?? c.Name;
    }

    /// <summary>The <paramref name="ordinal"/>-th name: the base, then <c> (2)</c>, <c> (3)</c>, …, cut so it fits.</summary>
    internal static string SuffixedName(string baseName, int ordinal)
    {
        var suffix = ordinal <= 1 ? string.Empty : $" ({ordinal})";
        var room = LocationRules.NameMaxLength - suffix.Length;
        var cut = baseName.Length <= room ? baseName
            : baseName[..(char.IsHighSurrogate(baseName[room - 1]) ? room - 1 : room)].TrimEnd();
        return cut + suffix;
    }

    /// <summary>The request for a staged address. The geocode is all-or-none, as the server requires.</summary>
    internal static NewLocation AddressLocation(LocationCandidate c, string name)
    {
        var (line1, postalCode, city, country) = AddressFieldsOf(c);
        var geocoded = !string.IsNullOrWhiteSpace(c.TimeZoneId);
        return new NewLocation
        {
            Name = name,
            Line1 = line1,
            PostalCode = postalCode,
            City = city,
            CountryCode = country,
            Latitude = geocoded ? c.Latitude : null,
            Longitude = geocoded ? c.Longitude : null,
            TimeZoneId = geocoded ? c.TimeZoneId : null,
            Source = LocationSource.Kartverket,
            SourceReference = c.SourceReference,
        };
    }

    /// <summary>The address key of a staged address — the shared <see cref="LocationAddressKey"/> over its fields.</summary>
    internal static string? AddressKeyOf(LocationCandidate c)
    {
        var (line1, postalCode, city, country) = AddressFieldsOf(c);
        return LocationAddressKey.Compute(country, postalCode, city, line1, null, null);
    }

    /// <summary>
    /// The saved place that already has the staged address (§3.2a): every place matching
    /// <c>GET /api/locations?search=‹line1›</c> (one wide page, so a common street name cannot push it past the
    /// picker's 20), keeping
    /// the one whose address key equals the candidate's. A live place is linked. A merged-away match forwards
    /// to its survivor, which is what the server's own find-or-create does. An archived match is a dead end,
    /// because a new link to an archived place is refused, so it is named rather than linked.
    /// </summary>
    private static async Task<(Guid? Id, string? Error)> FindByAddressAsync(ILocationsApiClient locations, LocationCandidate candidate)
    {
        if (AddressKeyOf(candidate) is not { } key || AddressFieldsOf(candidate).Line1 is not { } line1)
            return (null, null);
        var result = await locations.ListAsync(1, AddressLookupPageSize, line1);
        if (!result.IsSuccess || result.Value is null)
            return (null, null);
        var matches = result.Value.Items
            .Where(l => LocationAddressKey.Compute(l.CountryCode, l.PostalCode, l.City, l.Line1, l.Line2, l.Region) == key)
            .ToList();
        if (matches.FirstOrDefault(l => l.Archived is null && l.MergedIntoLocationId is null) is { } live)
            return (live.LocationId, null);
        if (matches.FirstOrDefault(l => l.MergedIntoLocationId is not null) is { MergedIntoLocationId: { } survivor })
            return (survivor, null);
        return matches.Count > 0 ? (null, AddressArchived) : (null, null);
    }
}

/// <summary>The record write's outcome for an editor with a place picker (issue #383 §5.3, §7).</summary>
public static class PlaceWriteOutcome
{
    public const string ForbiddenLink = "You don’t have permission to choose a saved place.";

    public const string UnusablePlace = "That place can’t be used here — pick another.";

    /// <summary>
    /// True on success (with the success toast). A <c>422</c> keyed <c>locationId</c>, or a <c>403</c> on a
    /// changed link, lands on the picker as a field error; anything else is toasted. The dialog keeps its values.
    /// </summary>
    public static bool PlacedOrToast(
        this ApiResult result, ISnackbar snackbar, string failureLead, string successMessage, PlaceDraft place, LocationLink link)
    {
        if (result.IsSuccess)
        {
            place.Error = null;
            snackbar.Add(successMessage, Severity.Success);
            return true;
        }

        if (result.Problem?.ErrorFor("locationId") is not null)
        {
            place.Error = UnusablePlace;
            return false;
        }

        // #380's 403 carries no reason, so the message is chosen from the request: a changed link by id.
        if (result.Status == HttpStatusCode.Forbidden && link.Kind == LocationLinkKind.Set)
        {
            place.Error = ForbiddenLink;
            return false;
        }

        return result.Toast(snackbar, failureLead);
    }
}
