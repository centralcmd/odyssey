using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Odyssey.Dtos;

/// <summary>One allow-list grant: a host, and a port when the entry names one (issue #470 §7.9).</summary>
/// <param name="Host">A lower-case DNS name or a canonical IP literal (an IPv6 literal unbracketed).</param>
/// <param name="Port">The one extra port granted, or <c>null</c> for 80 and 443 only.</param>
public sealed record NewsPrivateHostGrant(string Host, int? Port);

/// <summary>
/// The shape rule for <c>NewsPrivateHostAllowList</c> (issue #470 §7.9, §11.1): a comma-separated list of at most
/// <see cref="MaxEntries"/> entries, each <c>host</c> or <c>host:port</c> (an IPv6 literal bracketed, as
/// <c>[fd00::1]:8080</c>), at most <see cref="MaxLength"/> characters in all. Empty is the shipped value and blocks
/// every private address.
///
/// <para>
/// <strong>Why it lives here.</strong> The <c>EmailSmtpHostRule</c> reason: <c>Odyssey.Dtos</c> has zero project
/// references and is reachable from the WebAssembly client, so the settings page validates with the same delegate the
/// server's descriptor does, and the worker parses with the same <see cref="Parse"/>. The rule decides only the SHAPE;
/// which addresses a grant may open (never a hard-denied one, and only for the feed's own host) is the egress policy's.
/// </para>
/// </summary>
public static class NewsPrivateHostAllowListRule
{
    public const int MaxLength = 2048;

    public const int MaxEntries = 32;

    /// <summary>The maximum length of one host name, a fully-qualified DNS name's own limit.</summary>
    public const int MaxHostLength = 255;

    private const int MaxLabelLength = 63;

    /// <summary>Returns an error message, or null when <paramref name="value"/> is acceptable.</summary>
    public static string? Validate(string value) =>
        TryParse(value, out _, out var error) ? null : error;

    /// <summary>The canonical stored form: each grant canonicalised, duplicates dropped, joined by <c>", "</c>.</summary>
    public static string? Canonicalize(string value) =>
        TryParse(value, out var grants, out _) ? Format(grants) : null;

    /// <summary>
    /// The grants of a stored value. A value that does not parse yields no grant at all — a degraded read must never
    /// open an address, so the caller treats it exactly as empty.
    /// </summary>
    public static IReadOnlyList<NewsPrivateHostGrant> Parse(string? value) =>
        value is not null && TryParse(value, out var grants, out _) ? grants : [];

    /// <summary>Formats grants in the stored spelling.</summary>
    public static string Format(IEnumerable<NewsPrivateHostGrant> grants) =>
        string.Join(", ", grants.Select(FormatGrant));

    private static string FormatGrant(NewsPrivateHostGrant grant)
    {
        var host = grant.Host.Contains(':', StringComparison.Ordinal) ? $"[{grant.Host}]" : grant.Host;
        return grant.Port is { } port ? $"{host}:{port.ToString(CultureInfo.InvariantCulture)}" : host;
    }

    private static bool TryParse(string value, out IReadOnlyList<NewsPrivateHostGrant> grants, out string? error)
    {
        grants = [];
        error = null;
        if (value is null)
        {
            error = "must be a list of hosts.";
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            error = $"must be at most {MaxLength} characters.";
            return false;
        }

        if (trimmed.Length == 0)
        {
            return true;
        }

        var parsed = new List<NewsPrivateHostGrant>();
        foreach (var raw in trimmed.Split(','))
        {
            var entry = raw.Trim();
            if (!TryParseEntry(entry, out var grant))
            {
                error = "each entry must be a host name or IP address, optionally followed by :port, separated by commas "
                    + "— no scheme, path or credentials.";
                return false;
            }

            if (!parsed.Contains(grant))
            {
                parsed.Add(grant);
            }
        }

        if (parsed.Count > MaxEntries)
        {
            error = $"must list at most {MaxEntries} hosts.";
            return false;
        }

        grants = parsed;
        return true;
    }

    private static bool TryParseEntry(string entry, out NewsPrivateHostGrant grant)
    {
        grant = null!;
        if (entry.Length == 0
            || entry.Contains("://", StringComparison.Ordinal)
            || entry.IndexOfAny(['/', '@', '\\', ' ', '\t']) >= 0)
        {
            return false;
        }

        string host;
        string? portText = null;
        if (entry.StartsWith('['))
        {
            var close = entry.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            var inner = entry[1..close];
            if (!IPAddress.TryParse(inner, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            host = v6.ToString();
            var rest = entry[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return false;
                }

                portText = rest[1..];
            }
        }
        else
        {
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            var name = colon < 0 ? entry : entry[..colon];
            if (colon >= 0)
            {
                portText = entry[(colon + 1)..];
                if (portText.Contains(':', StringComparison.Ordinal))
                {
                    // An unbracketed IPv6 literal: the bracketed spelling is the one that cannot be misread.
                    return false;
                }
            }

            if (!TryCanonicalHost(name, out host))
            {
                return false;
            }
        }

        int? port = null;
        if (portText is not null)
        {
            if (portText.Length is 0 or > 5 || !portText.All(char.IsAsciiDigit)
                || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number is < 1 or > 65535)
            {
                return false;
            }

            port = number;
        }

        grant = new NewsPrivateHostGrant(host, port);
        return true;
    }

    private static bool TryCanonicalHost(string name, out string host)
    {
        host = string.Empty;
        var lowered = name.ToLowerInvariant();
        if (lowered.EndsWith('.'))
        {
            lowered = lowered[..^1];
        }

        if (lowered.Length is 0 or > MaxHostLength)
        {
            return false;
        }

        if (IPAddress.TryParse(lowered, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork)
        {
            // IPAddress.TryParse accepts shorthand such as "10.1"; only the dotted quad is a literal here.
            if (lowered.Count(c => c == '.') != 3)
            {
                return false;
            }

            host = literal.ToString();
            return true;
        }

        foreach (var label in lowered.Split('.'))
        {
            if (label.Length is 0 or > MaxLabelLength || label.StartsWith('-') || label.EndsWith('-')
                || !label.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        // An all-numeric name that is not a dotted quad ("10.1", "167772161") is an address shorthand, not a host.
        if (lowered.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            return false;
        }

        host = lowered;
        return true;
    }
}
