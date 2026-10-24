using System.Globalization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

/// <summary>A MET symbol as the client shows it: a Material Icons glyph that only echoes the words.</summary>
public sealed record WeatherSymbolInfo(string? Code, string Label, string Icon, bool Known);

/// <summary>A <see cref="WeatherFetchFailureKind"/> in words, with its explanation.</summary>
public sealed record WeatherFailureInfo(string Label, string Help);

/// <summary>
/// Display rules for weather (issue #348; design system · <c>weather-data.js</c>, <c>Weather.jsx</c>). Units
/// are metric and fixed. The coordinate rounding and the fingerprint are <see cref="WeatherRules"/>'s, shared
/// with the server — nothing here restates a server rule.
/// </summary>
public static class WeatherVisuals
{
    private sealed record Symbol(string Base, string Label, string Icon, string Night);

    private static Symbol S(string b, string label, string icon, string? night = null) => new(b, label, icon, night ?? icon);

    // MET codes are a base name plus an optional _day / _night / _polartwilight variant. Material Icons has
    // no sleet, no light-vs-heavy and few night glyphs, so several bases share a glyph and the words are
    // always rendered beside it or carried as its accessible name.
    private static readonly Dictionary<string, Symbol> Symbols = new Symbol[]
    {
        S("clearsky", "Clear sky", "wb_sunny", "bedtime"),
        S("fair", "Fair", "wb_sunny", "nights_stay"),
        S("partlycloudy", "Partly cloudy", "filter_drama", "nights_stay"),
        S("cloudy", "Cloudy", "cloud"),
        S("fog", "Fog", "foggy"),
        S("lightrainshowers", "Light rain showers", "grain"),
        S("rainshowers", "Rain showers", "water_drop"),
        S("heavyrainshowers", "Heavy rain showers", "umbrella"),
        S("lightrain", "Light rain", "grain"),
        S("rain", "Rain", "water_drop"),
        S("heavyrain", "Heavy rain", "umbrella"),
        S("lightsleetshowers", "Light sleet showers", "cloudy_snowing"),
        S("sleetshowers", "Sleet showers", "cloudy_snowing"),
        S("heavysleetshowers", "Heavy sleet showers", "cloudy_snowing"),
        S("lightsleet", "Light sleet", "cloudy_snowing"),
        S("sleet", "Sleet", "cloudy_snowing"),
        S("heavysleet", "Heavy sleet", "cloudy_snowing"),
        S("lightsnowshowers", "Light snow showers", "sunny_snowing", "snowing"),
        S("snowshowers", "Snow showers", "sunny_snowing", "snowing"),
        S("heavysnowshowers", "Heavy snow showers", "ac_unit"),
        S("lightsnow", "Light snow", "snowing"),
        S("snow", "Snow", "ac_unit"),
        S("heavysnow", "Heavy snow", "severe_cold"),
    }.ToDictionary(s => s.Base, StringComparer.Ordinal);

    private const string Thunder = "andthunder";

    // MET's own legend spells two thunder bases with a doubled "s"; they are real wire codes.
    private static readonly Dictionary<string, string> BaseAliases = new(StringComparer.Ordinal)
    {
        ["lightssleetshowers"] = "lightsleetshowers",
        ["lightssnowshowers"] = "lightsnowshowers",
    };

    /// <summary>
    /// The glyph and words for a MET code. An <c>…andthunder</c> code takes the thunder glyph and adds the
    /// words; an unknown code is the neutral cloud and "Unknown conditions" — never a guess.
    /// </summary>
    public static WeatherSymbolInfo? SymbolOf(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return null;

        var parts = code.Split('_', 2);
        var raw = parts[0];
        var variant = parts.Length > 1 ? parts[1] : null;
        var thunder = raw.EndsWith(Thunder, StringComparison.Ordinal);
        var key = thunder ? raw[..^Thunder.Length] : raw;
        key = BaseAliases.GetValueOrDefault(key, key);
        if (!Symbols.TryGetValue(key, out var symbol))
            return new WeatherSymbolInfo(code, "Unknown conditions", "cloud", false);
        if (thunder)
            return new WeatherSymbolInfo(code, $"{symbol.Label} and thunder", "thunderstorm", true);

        var icon = variant switch
        {
            "night" => symbol.Night,
            "polartwilight" when symbol.Night != symbol.Icon => "wb_twilight",
            _ => symbol.Icon,
        };
        return new WeatherSymbolInfo(code, symbol.Label, icon, true);
    }

    private static readonly Dictionary<WeatherFetchFailureKind, WeatherFailureInfo> Failures = new()
    {
        [WeatherFetchFailureKind.Timeout] = new("Timed out", "MET Norway did not answer in time. Odyssey backs off and retries on its own."),
        [WeatherFetchFailureKind.ProviderRejected] = new("Refused by MET Norway", "MET refused the request (403). Fetching is paused for 6 hours. Check the contact address in System settings."),
        [WeatherFetchFailureKind.RateLimited] = new("Rate limited by MET Norway", "MET asked Odyssey to slow down (429). Fetching is paused for up to 6 hours."),
        [WeatherFetchFailureKind.ProviderError] = new("MET Norway error", "MET returned an error. Odyssey backs off and retries on its own."),
        [WeatherFetchFailureKind.MalformedPayload] = new("Unusable response", "The response held no usable forecast, so nothing was stored."),
        [WeatherFetchFailureKind.NotConfigured] = new("Not set up", "No contact address is set, so nothing is fetched. Add one in System settings → Weather."),
        [WeatherFetchFailureKind.ResponseTooLarge] = new("Response too large", "The response exceeded its size bound, so nothing was stored."),
    };

    public static WeatherFailureInfo? FailureOf(WeatherFetchFailureKind? kind) =>
        kind is { } k ? Failures.GetValueOrDefault(k, new WeatherFailureInfo(k.ToString(), "")) : null;

    /// <summary>A pause from MET: a refresh cannot shorten it.</summary>
    public static bool IsPause(WeatherFetchFailureKind? kind) =>
        kind is WeatherFetchFailureKind.ProviderRejected or WeatherFetchFailureKind.RateLimited;

    // ── Units ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whole degrees with a true minus: <c>−3°</c>; <c>—</c> when unknown.</summary>
    public static string Temp(decimal? value)
    {
        if (value is not { } v)
            return "—";
        var rounded = (int)Math.Round(v, MidpointRounding.AwayFromZero);
        return rounded < 0 ? $"−{-rounded}°" : $"{rounded}°";
    }

    /// <summary>High then low, as the design writes it: <c>12° / 6°</c>.</summary>
    public static string TempRange(decimal? low, decimal? high) => $"{Temp(high)} / {Temp(low)}";

    public static string Mm(decimal? value) =>
        value is { } v ? $"{v.ToString("0.0", CultureInfo.InvariantCulture)} mm" : "—";

    public static string Mps(decimal? value) => value is { } v ? $"{Whole(v)} m/s" : "—";

    public static string Pct(decimal? value) => value is { } v ? $"{Whole(v)} %" : "—";

    public static string Hpa(decimal? value) => value is { } v ? $"{Whole(v)} hPa" : "—";

    private static string Whole(decimal v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static readonly string[] Compass = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    private static readonly Dictionary<string, string> CompassWords = new(StringComparer.Ordinal)
    {
        ["N"] = "north", ["NE"] = "north-east", ["E"] = "east", ["SE"] = "south-east",
        ["S"] = "south", ["SW"] = "south-west", ["W"] = "west", ["NW"] = "north-west",
    };

    /// <summary>The compass point a wind blows FROM, or null when unknown.</summary>
    public static string? CompassOf(decimal? degrees)
    {
        if (degrees is not { } d)
            return null;
        var normalized = ((double)d % 360 + 360) % 360;
        return Compass[(int)Math.Round(normalized / 45, MidpointRounding.AwayFromZero) % 8];
    }

    public static string? CompassWordsOf(decimal? degrees) => CompassOf(degrees) is { } point ? CompassWords[point] : null;

    // ── Dates and times ───────────────────────────────────────────────────────────────────────────

    /// <summary>An instant in the viewer's own clock, <c>07:30</c>. Read shapes carry UTC and no zone.</summary>
    public static string Clock(DateTime? utc) =>
        utc is { } t ? AsUtc(t).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "—";

    /// <summary><c>2 Mar 2026, 07:30</c> in the viewer's clock.</summary>
    public static string Stamp(DateTime? utc) =>
        utc is { } t ? AsUtc(t).ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) : "—";

    public static string Date(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string Date(DateTime value) => DateOnly.FromDateTime(value).ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary><c>Today</c>, <c>Tomorrow</c>, then the short weekday.</summary>
    public static string DayShort(DateOnly date, DateOnly today) =>
        date == today ? "Today" : date == today.AddDays(1) ? "Tomorrow" : date.ToString("ddd", CultureInfo.InvariantCulture);

    /// <summary><c>Sunday 25 Oct</c> — the full date behind the short name.</summary>
    public static string DayLong(DateOnly date) => date.ToString("dddd d MMM", CultureInfo.InvariantCulture);

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // ── Positions ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A position at the weather scale, <c>59.913, 10.750</c>.</summary>
    public static string Pair3(decimal latitude, decimal longitude) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{WeatherRules.RoundCoordinate(latitude):0.000}, {WeatherRules.RoundCoordinate(longitude):0.000}");

    /// <summary>
    /// "Moved", derived from the live location the same way the server derives <c>locationMoved</c>, so an
    /// edit on this page turns the chip and notice on without a re-read.
    /// </summary>
    public static bool Moved(ExistingWeatherLocation row, ExistingLocation location) =>
        WeatherRules.Moved(location.Latitude, location.Longitude, location.AltitudeMeters, location.TimeZoneId,
            row.Site.Latitude, row.Site.Longitude, row.Site.AltitudeMeters, row.Site.TimeZoneId);

    public static string Fingerprint(ExistingLocation location) =>
        WeatherRules.Fingerprint(location.Latitude, location.Longitude, location.AltitudeMeters, location.TimeZoneId);
}
