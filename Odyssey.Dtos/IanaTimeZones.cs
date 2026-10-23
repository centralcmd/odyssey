using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Odyssey.Dtos;

/// <summary>
/// The one check for an IANA time-zone id (issue #350 §7.10, amended by N10). Shared by the
/// <see cref="IanaTimeZoneAttribute"/> on request DTOs and by the geocoding candidate normalization, so a
/// candidate the server offers is always one the same server accepts back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape first, then a lookup.</b> The regex admits only letters, digits, <c>_</c>, <c>+</c> and
/// <c>-</c> in at most three <c>/</c>-separated segments, so path characters and <c>..</c> never reach
/// the filesystem-backed <see cref="TimeZoneInfo.TryFindSystemTimeZoneById"/>. An id must also contain a
/// <c>/</c> or be the literal <c>UTC</c>, which keeps out Windows ids and <c>posixrules</c>-style
/// aliases a host might otherwise resolve.
/// </para>
/// <para>
/// <b>No enumeration.</b> <c>TimeZoneInfo.GetSystemTimeZones()</c> is host-dependent — on Linux it omits
/// valid ids such as <c>Etc/GMT+5</c> — so the result of each lookup is memoized instead, in a bounded
/// cache that holds rejections too. Past the bound a lookup still runs; it is only not remembered.
/// </para>
/// </remarks>
public static partial class IanaTimeZones
{
    public const int MaxLength = 64;

    private const int CacheCapacity = 1000;

    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9_+\-]+(/[A-Za-z0-9_+\-]+){0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength || !Shape().IsMatch(id))
        {
            return false;
        }

        if (Cache.TryGetValue(id, out var known))
        {
            return known;
        }

        var valid = (id.Contains('/') || id == "UTC") && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);
        if (Cache.Count < CacheCapacity)
        {
            Cache.TryAdd(id, valid);
        }

        return valid;
    }
}

/// <summary>An IANA time-zone id this host can resolve — see <see cref="IanaTimeZones"/>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class IanaTimeZoneAttribute : ValidationAttribute
{
    public IanaTimeZoneAttribute()
        : base("The {0} field must be an IANA time zone, e.g. Europe/Oslo.")
    {
    }

    // Null is [Required]'s business, not this attribute's.
    public override bool IsValid(object? value) => value is null || (value is string id && IanaTimeZones.IsValid(id));
}
