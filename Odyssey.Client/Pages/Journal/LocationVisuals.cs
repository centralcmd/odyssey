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
    ];

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

    public static LocationSourceInfo SourceOf(LocationSource source) =>
        Sources.FirstOrDefault(s => s.Source == source) ?? Sources[0];

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

    // ── Time zones ───────────────────────────────────────────────────────────

    [GeneratedRegex(@"^[A-Za-z0-9_+\-]+(/[A-Za-z0-9_+\-]+){0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex IanaShape();

    private static IReadOnlyList<string>? zoneIds;

    /// <summary>
    /// The IANA ids this browser knows, for the picker. Windows-style ids and bare abbreviations are left
    /// out: the server accepts only an IANA id with a region, or <c>UTC</c>.
    /// </summary>
    public static IReadOnlyList<string> TimeZoneIds => zoneIds ??=
    [
        .. TimeZoneInfo.GetSystemTimeZones()
            .Select(z => z.Id)
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
