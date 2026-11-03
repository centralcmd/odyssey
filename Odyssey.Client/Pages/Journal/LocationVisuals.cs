using System.Globalization;
using System.Text.RegularExpressions;
using Odyssey.Client.Components;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>A <see cref="LocationSource"/>'s words and glyph (design system · <c>locations-data.js</c>).</summary>
public sealed record LocationSourceInfo(LocationSource Source, string Label, string Icon, string? Provider);

/// <summary>A <see cref="LocationSearchRegion"/>: which provider receives the query, and its minimum length.</summary>
public sealed record LocationRegionInfo(LocationSearchRegion Region, string Label, string Icon, LocationSource Source)
{
    /// <summary>The per-region rule the server enforces (N12), named once in <see cref="LocationRules"/>.</summary>
    public int MinLength => LocationRules.MinQueryLength(Region);

    /// <summary>"in Norway" / "worldwide" — the phrase every search sentence ends on.</summary>
    public string Phrase => Region == LocationSearchRegion.Norway ? "in Norway" : "worldwide";
}

/// <summary>A <see cref="LocationCandidateKind"/>, always a word with the glyph echoing it.</summary>
public sealed record LocationKindInfo(LocationCandidateKind Kind, string Label, string Icon);

/// <summary>
/// Display rules for the Locations page and dialog (issue #350; design system · <c>Locations.jsx</c>,
/// <c>AddLocationModal.jsx</c>). The limits and the per-region minimum are <see cref="LocationRules"/>'s,
/// shared with the server — nothing here restates a server bound.
/// </summary>
public static partial class LocationVisuals
{
    // The address hue Contacts and Properties give their location_on tile — a picker-style glyph on its
    // soft ground, so it goes through the glyph-lightness cap like every registry colour.
    public const string Accent = "oklch(0.77 0.14 55)";
    public const string AccentSoft = "oklch(0.77 0.14 55 / 0.16)";

    public static readonly IReadOnlyList<LocationSourceInfo> Sources =
    [
        new(LocationSource.Manual, "Entered by hand", "edit_location_alt", null),
        new(LocationSource.Kartverket, "Kartverket", "map", "Kartverket (the Norwegian Mapping Authority)"),
        new(LocationSource.OpenMeteo, "Open-Meteo", "public", "Open-Meteo, in Switzerland"),
        // The four sources the application sets (issue #380); the client never asserts them.
        new(LocationSource.Converted, "Converted from a text location", "text_fields", null),
        new(LocationSource.ContactAddress, "From a contact address", "contact_mail", null),
        new(LocationSource.PropertyAddress, "From a property address", "home_work", null),
        new(LocationSource.Imported, "From an import", "file_download", null),
    ];

    /// <summary>
    /// What an unknown future source reads — never <i>Entered by hand</i>, which would claim a person typed
    /// coordinates nobody typed (issue #383 §3.6, <c>FE</c> m1).
    /// </summary>
    public static readonly LocationSourceInfo OtherSource = new((LocationSource)0, "Other source", "help_outline", null);

    public static readonly IReadOnlyList<LocationRegionInfo> Regions =
    [
        new(LocationSearchRegion.Norway, "Norway", "flag", LocationSource.Kartverket),
        new(LocationSearchRegion.World, "Worldwide", "public", LocationSource.OpenMeteo),
    ];

    public static readonly IReadOnlyList<LocationKindInfo> Kinds =
    [
        new(LocationCandidateKind.Address, "Address", "signpost"),
        new(LocationCandidateKind.PlaceName, "Place name", "landscape"),
        new(LocationCandidateKind.Locality, "Town or area", "location_city"),
    ];

    /// <summary>The "Any status" filter — the shared <see cref="ArchivalStatus"/>, defaulting to Active.</summary>
    public static readonly IReadOnlyList<OdsOption> StatusOptions =
    [
        new(nameof(ArchivalStatus.Active), "Active"),
        new(nameof(ArchivalStatus.Archived), "Archived"),
    ];

    public static readonly IReadOnlyList<string> DefaultStatusFilter = [nameof(ArchivalStatus.Active)];

    // ── Filters (issue #383 §3.6) ────────────────────────────────────────────
    // Two separate two-option multi-selects beside the status filter. None or both selected sends no
    // parameter; exactly one sends true or false.

    public const string HasCoordinatesKey = "HasCoordinates";
    public const string NeedsCoordinatesKey = "NeedsCoordinates";
    public const string UsedKey = "Used";
    public const string NotUsedKey = "NotUsed";

    public static readonly IReadOnlyList<OdsOption> CoordinatesOptions =
    [
        new(HasCoordinatesKey, "Has coordinates"),
        new(NeedsCoordinatesKey, "Needs coordinates"),
    ];

    public static readonly IReadOnlyList<OdsOption> UseOptions =
    [
        new(UsedKey, "Used"),
        new(NotUsedKey, "Not used"),
    ];

    /// <summary>
    /// A two-option multi-select as the query's <c>bool?</c>: exactly one option sends true or false,
    /// none or both sends nothing.
    /// </summary>
    public static bool? FilterValue(IReadOnlyCollection<string>? selected, string trueKey, string falseKey) =>
        selected is null ? null
        : selected.Contains(trueKey) == selected.Contains(falseKey) ? null
        : selected.Contains(trueKey);

    /// <summary>The empty line under a filtered list with no rows.</summary>
    public const string NoFilterMatches = "No places match these filters.";

    public static LocationSourceInfo SourceOf(LocationSource source) =>
        Sources.FirstOrDefault(s => s.Source == source) ?? OtherSource;

    public static LocationRegionInfo RegionOf(LocationSearchRegion region) =>
        Regions.FirstOrDefault(r => r.Region == region) ?? Regions[0];

    public static LocationKindInfo KindOf(LocationCandidateKind kind) =>
        Kinds.FirstOrDefault(k => k.Kind == kind) ?? new(kind, kind.ToString(), "place");

    // ── Coordinates ──────────────────────────────────────────────────────────

    /// <summary>The stored precision: 5 decimals, away from zero — the server's rounding.</summary>
    public static decimal? Round(decimal? value) =>
        value is { } v ? Math.Round(v, LocationRules.CoordinateScale, MidpointRounding.AwayFromZero) : null;

    /// <summary>Whether saving would change the typed value, so the help line previews what is stored.</summary>
    public static bool WillRound(decimal? value) => value is { } v && Round(v) != v;

    public static string FormatCoordinate(decimal value) => value.ToString("F5", CultureInfo.InvariantCulture);

    public static string FormatPair(decimal latitude, decimal longitude) =>
        $"{FormatCoordinate(latitude)}, {FormatCoordinate(longitude)}";

    /// <summary>Hemisphere words, so a sign is never the only cue: <c>59.91273° N · 10.75027° E</c>.</summary>
    public static string FormatHemispheres(decimal latitude, decimal longitude) =>
        $"{FormatCoordinate(Math.Abs(latitude))}° {(latitude >= 0 ? "N" : "S")} · "
        + $"{FormatCoordinate(Math.Abs(longitude))}° {(longitude >= 0 ? "E" : "W")}";

    /// <summary>A place may have no coordinates since issue #380 (they are all-or-none).</summary>
    public const string NoCoordinates = "No coordinates";

    public static string FormatPair(decimal? latitude, decimal? longitude) =>
        latitude is { } lat && longitude is { } lng ? FormatPair(lat, lng) : NoCoordinates;

    public static string FormatHemispheres(decimal? latitude, decimal? longitude) =>
        latitude is { } lat && longitude is { } lng ? FormatHemispheres(lat, lng) : NoCoordinates;

    // ── Time zones ───────────────────────────────────────────────────────────

    [GeneratedRegex(@"^[A-Za-z0-9_+\-]+(/[A-Za-z0-9_+\-]+){0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex IanaShape();

    private static IReadOnlyList<string>? zoneIds;

    /// <summary>
    /// The IANA ids the picker offers: the tzdata catalogue, plus any further IANA-shaped id this browser
    /// knows. Windows-style ids and bare abbreviations are left out: the server accepts only an IANA id with a
    /// region, or <c>UTC</c>.
    /// </summary>
    public static IReadOnlyList<string> TimeZoneIds => zoneIds ??=
    [
        .. IanaZoneCatalogue.Ids
            .Concat(TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id))
            .Where(id => id == "UTC" || (id.Contains('/') && IanaShape().IsMatch(id)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    public static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
    }

    /// <summary>The browser's own zone, offered for a place entered by hand — null when it is not IANA-shaped.</summary>
    public static string? BrowserZone()
    {
        var id = TimeZoneInfo.Local.Id;
        return id == "UTC" || (id.Contains('/') && IanaShape().IsMatch(id)) ? id : null;
    }

    /// <summary><c>UTC+02:00</c> at <paramref name="at"/>, or null for a zone this browser does not know.</summary>
    public static string? UtcOffset(string? id, DateTime at)
    {
        if (FindZone(id) is not { } zone)
            return null;
        var offset = zone.GetUtcOffset(at);
        var sign = offset < TimeSpan.Zero ? "−" : "+";
        return $"UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
    }

    /// <summary>The wall-clock time there, 24-hour: <c>14:05</c>.</summary>
    public static string? LocalTime(string? id, DateTime at) =>
        FindZone(id) is { } zone
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(at, DateTimeKind.Utc), zone).ToString("HH:mm", CultureInfo.InvariantCulture)
            : null;

    /// <summary>The time-zone foot and help line: <c>UTC+02:00 now · 14:05 there</c>.</summary>
    public static string? NowThere(string? id, DateTime at) =>
        UtcOffset(id, at) is { } offset ? $"{offset} now · {LocalTime(id, at)} there" : null;

    public static string LongDate(DateTime value) =>
        value.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    // ── Structured address (issue #380 §4) ───────────────────────────────────

    /// <summary>
    /// The composed structured address — <c>Storgata 1, 0155 Oslo, NO</c> — or null when no field is set.
    /// Postal code and city share one part, as on an envelope.
    /// </summary>
    public static string? ComposeAddress(
        string? line1, string? line2, string? postalCode, string? city, string? region, string? countryCode)
    {
        var town = string.Join(' ', new[] { postalCode, city }.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()));
        var parts = new[] { line1, line2, town, region, countryCode }
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToArray();
        return parts.Length == 0 ? null : string.Join(", ", parts);
    }

    public static string? ComposeAddress(ExistingLocation l) =>
        ComposeAddress(l.Line1, l.Line2, l.PostalCode, l.City, l.Region, l.CountryCode);

    /// <summary>Line 1, City and Country all set — the places a contact or property address may link (#380).</summary>
    public static bool IsAddressComplete(ExistingLocation l) =>
        !string.IsNullOrWhiteSpace(l.Line1) && !string.IsNullOrWhiteSpace(l.City) && !string.IsNullOrWhiteSpace(l.CountryCode);

    /// <summary>
    /// A place as a picker option (issue #383 §6.1): the name, then a muted second line with the composed
    /// address and <i>No coordinates</i> in words; the accessible name is
    /// <c>‹name›, ‹address›, no coordinates, archived</c>, each part only when it applies.
    /// </summary>
    public static OdsOption OptionFor(ExistingLocation l)
    {
        var archived = l.Archived is not null || l.MergedIntoLocationId is not null;
        var address = ComposeAddress(l) ?? (string.IsNullOrWhiteSpace(l.AddressText) ? null : l.AddressText);
        var sub = string.Join(" · ", new[] { address, l.HasCoordinates ? null : NoCoordinates }.Where(v => v is not null));
        return new OdsOption(l.LocationId.ToString(), archived ? $"{l.Name} (archived)" : l.Name)
        {
            Icon = "place",
            Sub = sub.Length == 0 ? null : sub,
            AriaLabel = string.Join(", ", new[] { l.Name, address, l.HasCoordinates ? null : "no coordinates", archived ? "archived" : null }
                .Where(v => v is not null)),
        };
    }

    /// <summary>The trigger label of a staged place: <c>Hytta (new)</c>.</summary>
    public static string StagedLabel(string name) => $"{name} (new)";

    /// <summary>A linked place's name on a read surface: <c>Hytta (archived)</c> in words when archived.</summary>
    public static string PlaceName(LocationRef place) => place.Archived ? $"{place.Name} (archived)" : place.Name;

    // ── Chips ────────────────────────────────────────────────────────────────

    public const string NoCoordinatesIcon = "location_off";
    public const string MergedIcon = "call_merge";

    public static string MergedInto(string target) => $"Merged into {target}";

    // ── Usage copy (issue #383 §3.3, §3.6; #380 decision 9) ─────────────────

    private static readonly (Func<LocationUsage, int?> Count, string One, string Many)[] UsageKinds =
    [
        (u => u.Contacts, "contact", "contacts"),
        (u => u.Properties, "property", "properties"),
        (u => u.CalendarEvents, "event (including repeats)", "events (including repeats)"),
        (u => u.RecurringSeries, "series", "series"),
        (u => u.JournalEntries, "journal entry", "journal entries"),
    ];

    /// <summary>Every kind is readable — no count is null, so a zero means nothing uses it.</summary>
    public static bool AllKindsReadable(LocationUsage? usage) =>
        usage is not null && UsageKinds.All(k => k.Count(usage) is not null);

    /// <summary>The sum of the VISIBLE counts only — a hidden kind's count is never added in.</summary>
    public static int VisibleUses(LocationUsage? usage) =>
        usage is null ? 0 : UsageKinds.Sum(k => k.Count(usage) ?? 0);

    /// <summary>The per-kind breakdown in words, null and zero kinds omitted: <c>2 contacts, 1 property</c>.</summary>
    public static string UsageBreakdown(LocationUsage? usage, string separator = ", ") =>
        usage is null ? string.Empty : string.Join(separator, UsageKinds
            .Select(k => (k.Count(usage), k.One, k.Many))
            .Where(k => k.Item1 is > 0)
            .Select(k => $"{k.Item1} {(k.Item1 == 1 ? k.One : k.Many)}"));

    /// <summary>
    /// The collapsed row's sub-line: <i>Used by N</i> (visible counts only), <i>Used by N you can see</i>
    /// when a kind is hidden, <i>Not used</i> only when every kind is readable, else
    /// <i>Not used in anything you can see</i>.
    /// </summary>
    public static string UsageSubLine(LocationUsage? usage)
    {
        var n = VisibleUses(usage);
        var all = AllKindsReadable(usage);
        return n > 0 ? $"Used by {n}{(all ? "" : " you can see")}"
            : all ? "Not used" : "Not used in anything you can see";
    }

    /// <summary>The expanded record's <i>Used by</i> tile.</summary>
    public static string UsageTile(LocationUsage? usage)
    {
        var n = VisibleUses(usage);
        return n > 0 ? UsageBreakdown(usage, " · ")
            : AllKindsReadable(usage) ? "Not used" : "Not used in anything you can see";
    }

    /// <summary>
    /// The muted line under a shared address on a contact or property record (issue #383 §3.5):
    /// <i>Shared with N other records</i>, or <i>… you can see</i> when the caller lacks a read claim.
    /// </summary>
    public static string SharedWith(int otherUses, bool allReadable) =>
        $"Shared with {otherUses} other record{(otherUses == 1 ? "" : "s")}{(allReadable ? "" : " you can see")}";

    /// <summary>
    /// The address block's usage line (issue #383 §3.3): who else uses the place this record would point at.
    /// </summary>
    public static string AddressUsageLine(int otherUses, LocationUsage? othersBreakdown, bool allReadable, string noun, bool picked)
    {
        if (otherUses > 0)
        {
            var parts = UsageBreakdown(othersBreakdown);
            return $"{SharedWith(otherUses, allReadable)}{(parts.Length > 0 ? $" — {parts}" : "")}";
        }

        return !allReadable ? "No other records you can see use this address"
            : picked ? "No other records use this address"
            : $"Only this {noun} uses this address";
    }

    /// <summary>
    /// The line the location dialog shows when a contact or property's <b>Edit address</b> opened it (issue #383 v5
    /// §3.3a): every use, this record included, or no count when some kinds are hidden from the caller.
    /// </summary>
    public static string SharedUsesLine(int? uses) => uses is { } n
        ? $"Changes apply to every record that uses this address ({n})"
        : "Changes apply to every record that uses this address.";

    // ── Address mode of the place picker (issue #383 v5 §3.3a) ───────────────

    /// <summary>A saved place in the address picker: its second line ends <i>used by N</i> (visible uses only).</summary>
    public static OdsOption AddressOptionFor(ExistingLocation l)
    {
        var address = ComposeAddress(l) ?? (string.IsNullOrWhiteSpace(l.AddressText) ? null : l.AddressText);
        var shown = address is not null && !string.Equals(address, l.Name, StringComparison.Ordinal) ? address : null;
        var used = l.Usage is { } usage ? $"used by {VisibleUses(usage)}" : null;
        var sub = string.Join(" · ", new[] { shown, used, l.HasCoordinates ? null : NoCoordinates }.Where(v => v is not null));
        return new OdsOption(l.LocationId.ToString(), l.Name)
        {
            Icon = "place",
            Sub = sub.Length == 0 ? null : sub,
            AriaLabel = string.Join(", ", new[] { l.Name, shown, used, l.HasCoordinates ? null : "no coordinates" }
                .Where(v => v is not null)),
        };
    }

    /// <summary>A Kartverket address candidate as one line: <c>Storgata 1, 0155 OSLO</c> — provider text, as returned.</summary>
    public static string CandidateAddress(LocationCandidate c)
    {
        var town = string.Join(" ", new[] { c.PostalCode, c.City }.Where(v => !string.IsNullOrWhiteSpace(v)));
        var parts = new[] { c.Line1 ?? c.Name, town }.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
        return parts.Length > 0 ? string.Join(", ", parts) : c.AddressText ?? c.Name;
    }

    /// <summary>A Kartverket address in the picker's list, before it is picked.</summary>
    public static OdsOption CandidateOptionFor(string value, LocationCandidate c)
    {
        var address = CandidateAddress(c);
        return new OdsOption(value, address)
        {
            Icon = "map",
            Group = OdsLocationSelect.KartverketGroup,
            Sub = "New saved place, added when you save",
            AriaLabel = $"{address}, address from Kartverket, new saved place added when you save",
        };
    }

    /// <summary>A picked Kartverket address — staged until Save: <c>Storgata 1, 0155 OSLO (new)</c>.</summary>
    public static OdsOption StagedAddressOptionFor(string value, LocationCandidate c)
    {
        var address = CandidateAddress(c);
        return new OdsOption(value, StagedLabel(address))
        {
            Icon = "add_location_alt",
            Sub = "From Kartverket · added when you save",
            AriaLabel = $"{address}, new saved place from Kartverket, added when you save",
        };
    }

    // ── Record deletes and imports (issue #383 §3.9, §3.10) ──────────────────

    /// <summary>
    /// The one sentence a contact, property, calendar event and journal entry delete confirmation gains
    /// (issue #383 §3.9, SEC F5), verbatim from the design system.
    /// </summary>
    public const string RecordDeleteSentence = "Its address (or place) stays as a saved place on the Locations page.";

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>An ICS import's line: <c>3 places linked · 1 new place added</c>; null when nothing was linked.</summary>
    public static string? PlacesImportLine(int linked, int created) =>
        linked <= 0 && created <= 0 ? null
            : $"{Count(linked, "place linked", "places linked")} · {Count(created, "new place added", "new places added")}";

    /// <summary>
    /// A vCard import's line: <c>2 addresses linked to saved places · 1 new place added</c>. The created
    /// count is null without <c>locations.read</c> (v8-L3) and is then left out.
    /// </summary>
    public static string? AddressesImportLine(int linked, int? created) =>
        linked <= 0 && created is null or 0 ? null
            : Count(linked, "address linked to saved places", "addresses linked to saved places")
              + (created is { } c ? $" · {Count(c, "new place added", "new places added")}" : "");

    /// <summary>The vCard import's permission-skipped <c>ADR</c> line, or null when none were skipped.</summary>
    public static string? SkippedAddressesLine(int skipped) =>
        skipped <= 0 ? null : $"{Count(skipped, "address", "addresses")} skipped — adding places needs permission";

    // ── List ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The credits line under the list: each distinct attribution among the rows shown, once. Empty when
    /// every row was entered by hand, so the line disappears.
    /// </summary>
    public static IReadOnlyList<LocationAttribution> Credits(IEnumerable<ExistingLocation> rows) =>
    [
        .. rows.Select(r => r.Attribution)
            .OfType<LocationAttribution>()
            .DistinctBy(a => a.Text, StringComparer.Ordinal),
    ];
}
