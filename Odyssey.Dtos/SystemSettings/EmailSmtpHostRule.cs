namespace Odyssey.Dtos;

/// <summary>
/// The shape rule for the admin-editable SMTP relay host (issue #8) — validation, canonicalisation,
/// and the projection every echo of the value goes through.
///
/// <para>
/// <strong>Why it lives here.</strong> Same reason as <see cref="FileAnalysisBaseUrlRule"/>:
/// <c>Odyssey.Dtos</c> has zero project references and is reachable from both halves of the stack,
/// including the WebAssembly client, so the client catalogue applies the same constraint the server's
/// <c>PUT</c> path does rather than a hand-copied approximation of it. It is also the rule the
/// <em>send</em> path re-applies (issue #8 §5.9) — the write validator constrains only what an
/// administrator can submit, and says nothing about a row planted by a restore or a hand edit.
/// </para>
///
/// <para>
/// <strong>Empty is valid and means "not configured".</strong> That is not a loophole in a security
/// rule, it is the only spelling of "turn mail off" available: <c>null</c> on
/// <c>SystemSettingsUpdate</c> already means "leave unchanged", so rejecting <c>""</c> — which is what
/// <c>StringSetting</c> does for every other key — would make configuring mail a one-way door.
/// <c>StringSetting.AllowEmpty</c> short-circuits before this rule is ever called, so an
/// implementation here may assume a non-empty input.
/// </para>
///
/// <para>
/// <strong>Why CR, LF and NUL are rejected.</strong> Not SMTP command injection — MailKit's
/// <c>ConnectAsync</c> takes the host as a parameter and composes no command line from it. The real
/// reasons are log forging (the host is written verbatim into audit lines and send diagnostics) and
/// keeping a value that cannot be a host out of connection errors. The general control-character ban
/// <c>StringSetting</c> already applies covers all three; this rule is what additionally refuses a
/// scheme, a path, a port or <c>userinfo</c> — every one of which would be silently ignored by
/// <c>ConnectAsync</c>, so accepting one would give a <c>200</c> on save for a value that does not do
/// what it reads as.
/// </para>
///
/// <para>
/// Private, loopback and link-local hosts are <strong>allowed</strong>. An internal relay is a
/// legitimate and common deployment, the same trusted-administrator posture
/// <see cref="FileAnalysisBaseUrlRule"/> takes.
/// </para>
/// </summary>
public static class EmailSmtpHostRule
{
    /// <summary>The maximum stored length — a fully-qualified DNS name's own limit.</summary>
    public const int MaxLength = 255;

    /// <summary>The maximum length of one dot-separated DNS label.</summary>
    public const int MaxLabelLength = 63;

    /// <summary>Returns an error message, or null when <paramref name="value"/> is acceptable.</summary>
    public static string? Validate(string value) =>
        TryCanonicalize(value, out _)
            ? null
            : "must be a host name or IP address only — no scheme, port, path or credentials.";

    /// <summary>
    /// The canonical stored form: trimmed, lowercased, with a single trailing dot stripped. Returns
    /// null when the value is not usable.
    ///
    /// <para>
    /// Canonicalising is what keeps <c>SMTP.Example.Net</c> and <c>smtp.example.net</c> from being two
    /// distinct stored values — otherwise a <c>GET</c>→<c>PUT</c> round trip of unchanged data stops
    /// being a no-op, emits a spurious audit line, and (uniquely dangerous here) would read as a host
    /// CHANGE and clear the stored relay credential for a save that changed nothing.
    /// </para>
    /// </summary>
    public static string? Canonicalize(string value) =>
        TryCanonicalize(value, out var canonical) ? canonical : null;

    /// <summary>
    /// What an audit line or an advisory echoes. For a host the whole value <em>is</em> the host, so
    /// this is the identity on anything valid — it exists so a value that is NOT valid (one planted by
    /// a restore, carrying <c>user:pass@</c>) cannot reach the log.
    ///
    /// <para>
    /// Applied to the OLD value as well as the new one, which is the whole point: the write validator
    /// never saw the value being replaced.
    /// </para>
    /// </summary>
    public static string Host(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // A distinct token from Unparseable: an empty host is the healthy "not configured" state,
            // and an audit line reading "(unparseable) -> smtp.example.net" would misreport the most
            // common transition there is — the first time mail is configured at all.
            return NotConfigured;
        }

        return TryCanonicalize(value, out var canonical) ? canonical : FileAnalysisBaseUrlRule.Unparseable;
    }

    /// <summary>What <see cref="Host"/> logs for the empty, healthy, not-yet-configured value.</summary>
    public const string NotConfigured = "(not configured)";

    private static bool TryCanonicalize(string value, out string canonical)
    {
        canonical = string.Empty;

        if (value is null)
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxLength)
        {
            return false;
        }

        // Rejected explicitly rather than left to the host-character check below, so the error names
        // what the administrator actually typed. Every one of these is silently discarded by
        // ConnectAsync, which takes a bare host.
        if (trimmed.Contains("://", StringComparison.Ordinal)
            || trimmed.Contains('/', StringComparison.Ordinal)
            || trimmed.Contains('@', StringComparison.Ordinal)
            || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        // An IPv6 literal is the one shape that legitimately contains colons, and it arrives bracketed
        // ("[::1]"). Anywhere else a colon is an embedded port.
        var bracketed = trimmed.StartsWith('[') && trimmed.EndsWith(']');
        if (bracketed)
        {
            var inner = trimmed[1..^1];
            if (!System.Net.IPAddress.TryParse(inner, out var address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return false;
            }

            canonical = $"[{address}]";
            return true;
        }

        if (trimmed.Contains(':', StringComparison.Ordinal))
        {
            // An UNBRACKETED IPv6 literal lands here too, and is refused on purpose: MailKit's host
            // parameter cannot disambiguate it from host:port either, so accepting one would store a
            // value that does not connect. The bracketed form above is the spelling that works.
            return false;
        }

        var lowered = trimmed.ToLowerInvariant();

        // A single trailing dot is the fully-qualified form and is legal; it is stripped so the
        // absolute and relative spellings of one host are not two stored values. Two dots are not.
        if (lowered.EndsWith('.'))
        {
            lowered = lowered[..^1];
            if (lowered.Length == 0 || lowered.EndsWith('.'))
            {
                return false;
            }
        }

        if (System.Net.IPAddress.TryParse(lowered, out var literal))
        {
            canonical = literal.ToString();
            return true;
        }

        foreach (var label in lowered.Split('.'))
        {
            if (label.Length is 0 or > MaxLabelLength)
            {
                return false;
            }

            if (label.StartsWith('-') || label.EndsWith('-'))
            {
                return false;
            }

            // Deliberately ASCII-only. An internationalised host reaches MailKit as its punycode form
            // or not at all, so accepting the Unicode spelling would store a value that resolves
            // differently from the one displayed — and the audit line would carry a homograph.
            foreach (var c in label)
            {
                var ok = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
                if (!ok)
                {
                    return false;
                }
            }
        }

        canonical = lowered;
        return true;
    }
}
