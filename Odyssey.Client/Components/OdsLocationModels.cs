using System.Net;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Components;

/// <summary>
/// The value of an <c>OdsLocationSelect</c> (issue #383 §4): an existing place by <see cref="Id"/>, or a
/// <b>staged</b> new one by <see cref="NewName"/> that the parent creates when its own dialog saves.
/// Exactly one of the two is set.
/// </summary>
public sealed record LocationPick(Guid? Id, string? NewName)
{
    public static LocationPick Existing(Guid id) => new(id, null);

    public static LocationPick Staged(string name) => new(null, name);

    public bool IsStaged => Id is null && !string.IsNullOrEmpty(NewName);
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
/// The place half of an event, series or entry draft (issue #383 §3.2): the stored link, the picker's
/// value, and the id of a staged place once it has been created. A retry after a failed record write
/// reuses <see cref="CreatedId"/> and does not post again.
/// </summary>
public sealed class PlaceDraft
{
    public Guid? StoredId { get; init; }

    /// <summary>The stored link's embed — null without <c>locations.read</c>.</summary>
    public LocationRef? StoredRef { get; init; }

    public LocationPick? Pick { get; set; }

    /// <summary>The staged place's id once <see cref="EnsureCreatedAsync"/> created it, and for which name.</summary>
    public Guid? CreatedId => _created is { } c && Pick?.IsStaged == true && c.Name == Pick.NewName ? c.Id : null;

    private (string Name, Guid Id)? _created;

    /// <summary>The picker's field error — a staged create that failed, or a server <c>422</c>/<c>409</c>.</summary>
    public string? Error { get; set; }

    public static PlaceDraft For(Guid? storedId, LocationRef? storedRef) => new()
    {
        StoredId = storedId,
        StoredRef = storedRef,
        Pick = storedId is { } id ? LocationPick.Existing(id) : null,
    };

    /// <summary>The link the record write sends.</summary>
    public LocationLink Link(bool pickerShown) => LocationLink.For(StoredId, Pick, CreatedId, pickerShown);

    /// <summary>
    /// Creates a staged place, if there is one not yet created — immediately before the record write
    /// (issue #383 §3.2). Returns false, with <see cref="Error"/> set, when the create failed: the save
    /// then stops and nothing else is written.
    /// </summary>
    public async Task<bool> EnsureCreatedAsync(ILocationsApiClient locations, bool pickerShown)
    {
        if (!pickerShown || Pick is not { IsStaged: true } pick || CreatedId is not null)
            return true;

        var result = await locations.CreateAsync(new NewLocation { Name = pick.NewName!, Source = LocationSource.Manual });
        if (result.IsSuccess && result.Value is { } created)
        {
            _created = (pick.NewName!, created.LocationId);
            Error = null;
            return true;
        }

        Error = result.Problem?.ErrorFor("name") ?? result.Problem?.Errors.Values.FirstOrDefault()?.FirstOrDefault()
            ?? result.Error ?? "Couldn’t add the place.";
        return false;
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
