using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos;

namespace Odyssey.Api.Email;

/// <summary>
/// What the send path found when it read one of the four transport settings (issue #8 §5.9, §11.1).
///
/// <para>
/// <strong>Three states, and collapsing the outer two is the bug this type exists to prevent.</strong>
/// An <em>absent</em> row is healthy — it means the setting was never configured, which for the SMTP
/// host is a supported steady state ("mail is off") and for the client base URL means links cannot be
/// composed, both of which the sender already handles. An <em>unusable</em> row is degraded: something
/// is stored, and it is not something this deployment can act on.
/// </para>
/// </summary>
internal enum EmailTransportReadState
{
    /// <summary>No row, or an empty one. The healthy not-configured state.</summary>
    Absent = 1,

    /// <summary>A row that parsed and passed its rule.</summary>
    Valid = 2,

    /// <summary>A row that is present and cannot be used. The send is skipped; nothing is substituted.</summary>
    Unusable = 3,
}

/// <summary>
/// The four SMTP transport settings as the send path sees them (issue #8).
///
/// <para>
/// <strong>Why this is not <see cref="SystemSettingsReader"/>.</strong> That reader is documented to
/// resolve a missing OR UNPARSEABLE value to the caller's compiled default, "so the control degrades
/// instead of disappearing". That is exactly right for a display bound and exactly wrong here. An
/// unparseable <c>EmailUseStartTls</c> would silently resolve to <c>true</c>, and an out-of-range
/// <c>EmailSmtpPort</c> to 587 — a value the administrator did not choose being substituted on the
/// path that decides where a password-reset token goes. Issue #445's central rule is that an
/// unreadable row never resolves to the configured value; issue #11.1 extends it: it never resolves to
/// the compiled default either.
/// </para>
///
/// <para>
/// <strong>The port is the one exception, and it is a clamp rather than a substitution.</strong> A
/// port that parses is a usable number that merely fell outside its pair, so it resolves to the nearer
/// bound — the same treatment <c>IntSetting.Project</c> gives it on the read path, and the same
/// treatment CLAUDE.md prescribes for a bound that is load-bearing. A port that does NOT parse is
/// unusable, and the send is skipped.
/// </para>
/// </summary>
internal sealed record EmailTransportSettings
{
    /// <summary>The relay host, canonicalised. Empty when <see cref="HostState"/> is not Valid.</summary>
    public required string Host { get; init; }

    public required EmailTransportReadState HostState { get; init; }

    public required int Port { get; init; }

    public required EmailTransportReadState PortState { get; init; }

    public required bool UseStartTls { get; init; }

    public required EmailTransportReadState StartTlsState { get; init; }

    /// <summary>The public link origin, canonicalised. Empty when <see cref="ClientBaseUrlState"/> is not Valid.</summary>
    public required string ClientBaseUrl { get; init; }

    public required EmailTransportReadState ClientBaseUrlState { get; init; }

    /// <summary>
    /// Whether mail is configured at all. Deliberately NOT the same question as "is anything faulted":
    /// an absent host is the healthy off state, and the sender's existing "log the link and skip"
    /// branch is the right response to it.
    /// </summary>
    public bool IsConfigured => HostState == EmailTransportReadState.Valid;

    /// <summary>
    /// The keys whose stored value is present and unusable, in a stable order. Non-empty means the
    /// send fails closed — <em>including</em> when the host itself is fine, because a corrupt port or
    /// TLS flag decides how the credential travels, and a corrupt base URL decides where the token
    /// lands.
    /// </summary>
    public IReadOnlyList<string> UnusableKeys
    {
        get
        {
            var keys = new List<string>(4);
            Add(HostState, SystemSettingsKeys.EmailSmtpHost);
            Add(PortState, SystemSettingsKeys.EmailSmtpPort);
            Add(StartTlsState, SystemSettingsKeys.EmailUseStartTls);
            Add(ClientBaseUrlState, SystemSettingsKeys.EmailClientBaseUrl);
            return keys;

            void Add(EmailTransportReadState state, string key)
            {
                if (state == EmailTransportReadState.Unusable)
                {
                    keys.Add(key);
                }
            }
        }
    }
}

/// <summary>
/// Reads the four transport settings live and uncached on the send path, failing closed on anything it
/// cannot use (issue #8 §5.9).
///
/// <para>
/// <strong>It shares the registry's rules rather than restating them.</strong> The same
/// <see cref="EmailSmtpHostRule"/>, the same <see cref="EmailClientBaseUrlRule"/> and the same
/// <see cref="SystemSettingsBounds"/> pair the descriptors use. It differs from
/// <c>SystemSettingDescriptor.Project</c> in exactly one respect — what it does with a non-<c>Ok</c>
/// outcome — so the send path and the write path cannot drift apart on the fields that decide where a
/// credential and a reset token travel. A guard test asserts the two accept and reject the same
/// values.
/// </para>
/// </summary>
internal static class EmailTransportSettingsReader
{
    public static async Task<EmailTransportSettings> ReadAsync(
        OdysseyContext context, CancellationToken cancellationToken = default)
    {
        // One query for all four, not four round trips: SendAsync tests the host before the permit
        // check and DeliverAsync tests it again, so per-field live reads would let a single send
        // observe two different values across a concurrent admin write — the same reasoning
        // ReadSettingsAsync already applies to the sender identity and the throttle.
        var keys = new[]
        {
            SystemSettingsKeys.EmailSmtpHost,
            SystemSettingsKeys.EmailSmtpPort,
            SystemSettingsKeys.EmailUseStartTls,
            SystemSettingsKeys.EmailClientBaseUrl,
        };

        var rows = await context.SystemSettings.AsNoTracking()
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

        var (host, hostState) = ReadRule(rows, SystemSettingsKeys.EmailSmtpHost, EmailSmtpHostRule.Canonicalize);
        var (baseUrl, baseUrlState) = ReadRule(
            rows, SystemSettingsKeys.EmailClientBaseUrl, EmailClientBaseUrlRule.Canonicalize);
        var (port, portState) = ReadPort(rows);
        var (startTls, startTlsState) = ReadStartTls(rows);

        return new EmailTransportSettings
        {
            Host = host,
            HostState = hostState,
            Port = port,
            PortState = portState,
            UseStartTls = startTls,
            StartTlsState = startTlsState,
            ClientBaseUrl = baseUrl,
            ClientBaseUrlState = baseUrlState,
        };
    }

    /// <summary>
    /// A string setting re-validated through its own canonicaliser. Blank is <c>Absent</c> — the
    /// healthy not-configured state, which is what distinguishes "mail is off" from "mail is broken"
    /// and is why an empty dictionary cannot be the return type.
    /// </summary>
    private static (string Value, EmailTransportReadState State) ReadRule(
        IReadOnlyDictionary<string, string> rows, string key, Func<string, string?> canonicalize)
    {
        if (!rows.TryGetValue(key, out var stored) || string.IsNullOrWhiteSpace(stored))
        {
            return (string.Empty, EmailTransportReadState.Absent);
        }

        // Re-validated on READ, not merely on write: a row planted by a hand edit or a restore never
        // passed the PUT path's validator, and this is the field issue #8 §10.2 calls the weakest
        // point in the whole feature.
        return canonicalize(stored) is { } canonical
            ? (canonical, EmailTransportReadState.Valid)
            : (string.Empty, EmailTransportReadState.Unusable);
    }

    private static (int Value, EmailTransportReadState State) ReadPort(IReadOnlyDictionary<string, string> rows)
    {
        if (!rows.TryGetValue(SystemSettingsKeys.EmailSmtpPort, out var stored)
            || string.IsNullOrWhiteSpace(stored))
        {
            // A missing port with a configured host is not a fault: 587 is the shipped default and
            // the seed writes it, so this is the pre-seed database rather than a corrupt one.
            return (SystemSettingsDefaults.EmailSmtpPort, EmailTransportReadState.Absent);
        }

        if (!int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return (0, EmailTransportReadState.Unusable);
        }

        // CLAMPED, not refused: a parseable port is a usable number that fell outside its pair, and
        // the read path clamps for the same reason IntSetting.Project does — a row written by a hand
        // edit or a restore never passed [Range]. "0" is this case, not the unusable one.
        return (
            Math.Clamp(parsed, SystemSettingsBounds.EmailSmtpPortMin, SystemSettingsBounds.EmailSmtpPortMax),
            EmailTransportReadState.Valid);
    }

    private static (bool Value, EmailTransportReadState State) ReadStartTls(IReadOnlyDictionary<string, string> rows)
    {
        if (!rows.TryGetValue(SystemSettingsKeys.EmailUseStartTls, out var stored)
            || string.IsNullOrWhiteSpace(stored))
        {
            return (SystemSettingsDefaults.EmailUseStartTls, EmailTransportReadState.Absent);
        }

        // The one place the difference from SystemSettingsReader is starkest. Its GetBoolAsync would
        // resolve "yes" to the compiled `true` and connect with STARTTLS — which looks like the safe
        // direction until the stored value was `false` for an implicit-TLS relay on 465, where the
        // handshake then fails in a way nobody can read off the setting. Refusing says what is wrong.
        return bool.TryParse(stored, out var parsed)
            ? (parsed, EmailTransportReadState.Valid)
            : (false, EmailTransportReadState.Unusable);
    }
}
