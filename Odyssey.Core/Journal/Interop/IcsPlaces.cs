using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Locations;
using Odyssey.Core.Locations.Conversion;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// The ICS mapping of a place (issue #380 §8.4), shared by the calendar (<c>LOCATION</c>) and journal
/// (<c>X-ODYSSEY-LOCATION</c>) import and export.
/// </summary>
/// <remarks>
/// <para>
/// <b>Import</b> links or creates a place only when the importer holds both <c>locations.read</c> and
/// <c>locations.create</c> (§7.1.8); otherwise the value stays detail text and <b>no name lookup runs</b>.
/// The value is cleaned; a link-like value (a meeting link, a passcode) stays detail. Otherwise its
/// <c>Name</c> is matched exactly on letters, else its <c>AddressText</c>, archived included (§7.1.9), and a
/// miss creates an <see cref="LocationSource.Imported"/> place. A value of the export's own
/// <c>Name — detail</c> shape whose name matches links that place and keeps the detail.
/// </para>
/// <para>
/// <b>Export</b> writes the linked place's name — with <c> — detail</c> when there is one — only for a
/// <c>locations.read</c> holder, and otherwise the detail alone. It never writes <c>GEO</c>. Ical.Net does
/// the text escaping.
/// </para>
/// </remarks>
internal sealed class IcsPlaces
{
    /// <summary>The separator between a place's name and the detail on export.</summary>
    public const string DetailSeparator = " — ";

    /// <summary>
    /// The calendar's own detail property (issue #516 §8.3): the event's detail text, written beside the composed
    /// <c>LOCATION</c> so an import can tell the place from the detail instead of storing the whole composed text as
    /// the detail and growing it on every round trip.
    /// </summary>
    public const string DetailProperty = "X-ODYSSEY-LOCATION-DETAIL";

    private readonly LocationResolver? resolver;
    private readonly LocationIndex? index;
    private readonly string? userId;

    private IcsPlaces(LocationResolver? resolver, LocationIndex? index, string? userId)
    {
        this.resolver = resolver;
        this.index = index;
        this.userId = userId;
    }

    /// <summary>Rows linked to a place by this import, found or created.</summary>
    public int Linked { get; private set; }

    /// <summary>Places this import created.</summary>
    public int Created { get; private set; }

    /// <summary>Whether this import links places at all.</summary>
    public bool Enabled => index is not null;

    public static async Task<IcsPlaces> ForImportAsync(
        OdysseyContext context, LocationClaims claims, string? userId, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!claims.LocationsRead || !claims.LocationsCreate)
        {
            return new IcsPlaces(null, null, userId);
        }

        var resolver = new LocationResolver(context, timeProvider);
        return new IcsPlaces(resolver, await resolver.LoadIndexAsync(cancellationToken), userId);
    }

    /// <summary>
    /// What an imported value becomes: a place id and the detail kept beside it, or no place and the value as
    /// detail. Counts the link. Call only for a value the import is about to store.
    /// </summary>
    public (Guid? LocationId, string? Detail) Map(string? value)
    {
        if (index is null || resolver is null)
        {
            return (null, value);
        }

        var cleaned = LocationTextCleaner.Clean(value);
        if (cleaned is null)
        {
            return (null, null);
        }

        if (LinkLikeText.IsLinkLike(cleaned))
        {
            return (null, value);
        }

        Guid locationId;
        string? detail = null;
        if (index.FindByText(cleaned) is { } match)
        {
            locationId = match;
        }
        else if (SplitDetail(cleaned) is { } split && index.FindByText(split.Name) is { } named)
        {
            locationId = named;
            detail = split.Detail;
        }
        else
        {
            var (id, created) = resolver.FindOrCreateByText(index, cleaned, userId);
            locationId = id;
            if (created)
            {
                Created++;
            }
        }

        Linked++;
        return (locationId, detail);
    }

    /// <summary>Saves the import's context through the resolver, which maps a name or key race (§8.1).</summary>
    public Task SaveChangesAsync(OdysseyContext context, CancellationToken cancellationToken) =>
        resolver is not null ? resolver.SaveChangesAsync(cancellationToken) : context.SaveChangesAsync(cancellationToken);

    /// <summary>The names of the linked places, for a <c>locations.read</c> holder only; empty otherwise.</summary>
    public static async Task<Dictionary<Guid, string>> NamesAsync(
        OdysseyContext context, IEnumerable<Guid?> locationIds, bool canReadLocations, CancellationToken cancellationToken)
    {
        var ids = locationIds.OfType<Guid>().Distinct().ToList();
        if (!canReadLocations || ids.Count == 0)
        {
            return [];
        }

        return await context.Locations.AsNoTracking()
            .Where(l => ids.Contains(l.LocationId))
            .ToDictionaryAsync(l => l.LocationId, l => l.Name, cancellationToken);
    }

    /// <summary>The exported text: the linked name (plus the detail) when known, else the detail.</summary>
    public static string? ExportText(Guid? locationId, string? detail, IReadOnlyDictionary<Guid, string> names)
    {
        if (locationId is not { } id || !names.TryGetValue(id, out var name))
        {
            return string.IsNullOrEmpty(detail) ? null : detail;
        }

        return string.IsNullOrEmpty(detail) ? name : name + DetailSeparator + detail;
    }

    /// <summary>
    /// Writes an event's place (issue #516 §8.3): <c>LOCATION</c> as <see cref="ExportText"/> composes it for the
    /// caller, and <see cref="DetailProperty"/> when the detail is non-empty. The one signature every calendar
    /// builder — standalone, recurring and aggregate — calls.
    /// </summary>
    public static void WriteEventPlace(
        Ical.Net.CalendarComponents.CalendarEvent ev, Guid? locationId, string? detail, IReadOnlyDictionary<Guid, string> names)
    {
        ev.Location = ExportText(locationId, detail, names);
        if (!string.IsNullOrEmpty(detail))
        {
            ev.Properties.Add(new Ical.Net.CalendarProperty(DetailProperty, detail));
        }
    }

    /// <summary>
    /// Reads an event's place back (issue #516 §8.3). Without <see cref="DetailProperty"/> (a file from another app)
    /// the result is <see cref="ImportedPlace.Foreign"/>: <c>LOCATION</c> is resolved by the existing rules. With it,
    /// the detail is taken from the property and the place text is <c>LOCATION</c> with a trailing
    /// <c> — detail</c> removed; an empty place text, or one equal to the detail, names no place at all.
    /// </summary>
    public static ImportedPlace ReadEventPlace(Ical.Net.CalendarComponents.CalendarEvent ev)
    {
        var location = ev.Location;
        if (!ev.Properties.ContainsKey(DetailProperty))
        {
            return new ImportedPlace(location, location, HasDetailProperty: false);
        }

        var detail = ev.Properties.Get<string>(DetailProperty);
        if (string.IsNullOrEmpty(detail))
        {
            detail = null;
        }

        var placeText = location?.Trim();
        if (detail is not null && placeText is not null
            && placeText.EndsWith(DetailSeparator + detail, StringComparison.Ordinal))
        {
            placeText = placeText[..^(DetailSeparator.Length + detail.Length)].Trim();
        }

        if (string.IsNullOrEmpty(placeText) || string.Equals(placeText, detail, StringComparison.Ordinal))
        {
            placeText = null;
        }

        return new ImportedPlace(placeText, detail, HasDetailProperty: true);
    }

    private static (string Name, string Detail)? SplitDetail(string value)
    {
        var at = value.IndexOf(DetailSeparator, StringComparison.Ordinal);
        if (at <= 0)
        {
            return null;
        }

        var detail = value[(at + DetailSeparator.Length)..].Trim();
        return detail.Length == 0 ? null : (value[..at].Trim(), detail);
    }
}

/// <summary>
/// A calendar event's place as read from a file (issue #516 §8.3). <see cref="PlaceText"/> is what a place is resolved
/// from — null when the file names no place — and <see cref="Detail"/> is the detail to store.
/// <see cref="HasDetailProperty"/> is false for a file from another app, whose <c>LOCATION</c> is both.
/// </summary>
internal sealed record ImportedPlace(string? PlaceText, string? Detail, bool HasDetailProperty);
