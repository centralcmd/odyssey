using System.Globalization;
using Odyssey.Dtos.Application;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// The advisories the warnings channel carries (issue #434 §9, closing #421's deferred D1, extended by
/// issue #439): six cost advisories that fire when a resource-shaped setting is set above its shipped
/// default, one correspondence heuristic between the disclosed processor name and the base URL, and
/// three on the file-analysis switch, destination and model.
///
/// <para>
/// <strong>Every one of these is informational and none is a security control.</strong> The controls on
/// the file-analysis surface are the <c>system-settings.security.update</c> claim, the derived audit
/// line, the blocking https-only shape validators on the base URL and the privacy notice, and
/// <c>AllowAutoRedirect = false</c> on the outbound client. An advisory exists so that a change which
/// costs memory, CPU, third-party spend — or which sends the API key somewhere new — says so <em>on the
/// row</em>, instead of being discovered in production.
/// </para>
/// </summary>
internal static class SettingAdvisories
{
    /// <summary>
    /// The cost advisory shape: fires only when the saved value is strictly above the shipped default,
    /// and names both numbers plus what the extra costs.
    ///
    /// <para>
    /// "Above the shipped default" rather than "near the ceiling" on purpose. The default is the value
    /// the system was measured at; anything above it is the administrator taking on a cost, whether or
    /// not there is headroom left to a bound.
    /// </para>
    /// </summary>
    public static Func<SystemSettingsDto, AdvisoryContext, string?> AboveDefault(
        Func<SystemSettingsDto, int> read, int shippedDefault, string cost) =>
        (dto, _) =>
        {
            var value = read(dto);
            return value > shippedDefault
                ? $"Set to {value.ToString("N0", CultureInfo.InvariantCulture)}, above the shipped default "
                  + $"of {shippedDefault.ToString("N0", CultureInfo.InvariantCulture)}. {cost}"
                : null;
        };

    /// <summary>
    /// Flags a disclosed processor name that does not correspond to the host analysis requests actually
    /// go to (issue #421 D1). Re-sourced by issue #439: the host now comes from the
    /// <c>FileAnalysisBaseUrl</c> <em>setting</em> rather than from deploy-time configuration, so the
    /// heuristic keeps working after a repoint that this feature made possible in the first place.
    ///
    /// <para>
    /// <strong>Explicitly a heuristic, and explicitly non-blocking.</strong> Both failure directions
    /// are accepted and stated in the copy: a legitimate Bedrock, Vertex or corporate-gateway
    /// deployment will trip it, and a host like <c>evil-anthropic.example.com</c> will not. It exists
    /// to catch a <em>stale</em> disclosure after someone repoints the base URL, which is the realistic
    /// mistake — not to decide whether a transfer is legitimate.
    /// </para>
    ///
    /// <para>
    /// Only the host is ever echoed, never the path, query or <c>userinfo</c>. The write validator
    /// rejects all three, but a row planted by a restore or a hand edit is not bound by it, so the
    /// parse-to-host happens here rather than being assumed upstream.
    /// </para>
    /// </summary>
    public static string? ProcessorMatchesBaseUrl(SystemSettingsDto dto, AdvisoryContext context)
    {
        if (HostOf(dto.FileAnalysisBaseUrl) is not { } host)
        {
            return null;
        }

        var processor = Normalize(dto.FileAnalysisProcessor);
        if (processor.Length == 0 || Normalize(host).Contains(processor, StringComparison.Ordinal))
        {
            return null;
        }

        return $"The disclosed processor \"{dto.FileAnalysisProcessor}\" does not appear in the host "
             + $"documents are actually sent to ({host}). This is a spelling check, not a verification: "
             + "a gateway or reseller deployment will trip it legitimately, and a lookalike host would "
             + "not. Confirm the consent gate still names the party that receives the data.";
    }

    /// <summary>
    /// Fires whenever the kill switch is ON, naming the processor and region documents will go to
    /// (issue #439).
    ///
    /// <para>
    /// Unlike the cost advisories this is not an "above the default" comparison: <em>every</em> enabled
    /// state is worth stating, because the setting authorises transferring personal data to a third
    /// party. It says so on the row rather than leaving the administrator to infer it from the fact
    /// that a toggle exists — and it names the region, which is the fact that decides whether those
    /// transfers fall under GDPR Art. 44–49.
    /// </para>
    /// </summary>
    public static string? AnalysisEnabled(SystemSettingsDto dto, AdvisoryContext context) =>
        dto.FileAnalysisEnabled
            ? $"Documents will be transferred to {dto.FileAnalysisProcessor} in "
              + $"{dto.FileAnalysisProcessorRegion} on each analysis. Each transfer still requires the "
              + "user's per-document consent."
            : null;

    /// <summary>
    /// Fires when the destination differs from the shipped default's host (issue #439), stating the
    /// consequence the Non-Goal accepted: the configured API key stays a deploy-time secret and travels
    /// to whatever host is set here.
    ///
    /// <para>
    /// <strong>Host only, parsed here.</strong> The expected shape of a gateway URL is
    /// <c>https://apikey:secret@gateway.internal/v1</c>, so a credential-bearing value is the likely
    /// case rather than an edge case. The write validator rejects that form, but this delegate also
    /// runs against whatever a restore or a hand edit left in the row, so it parses rather than trusts.
    /// An unparseable value yields no advisory — there is no host to name, and echoing the raw string
    /// is exactly what must not happen.
    /// </para>
    /// </summary>
    public static string? BaseUrlAwayFromDefault(SystemSettingsDto dto, AdvisoryContext context)
    {
        if (HostOf(dto.FileAnalysisBaseUrl) is not { } host)
        {
            return null;
        }

        if (HostOf(SystemSettingsDefaults.FileAnalysisBaseUrl) is { } shipped
            && string.Equals(host, shipped, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"Analysis requests, including the configured API key, are sent to {host}. Confirm you "
             + "control this host and that the disclosed processor and region below still describe it.";
    }

    /// <summary>
    /// Fires when the model differs from the shipped default (issue #439). Names no value beyond what
    /// the administrator just typed, and states the two things that are easy to get wrong: the change
    /// is not retroactive, and cost and extraction quality both move with it.
    /// </summary>
    public static string? ModelAwayFromDefault(SystemSettingsDto dto, AdvisoryContext context) =>
        string.Equals(dto.FileAnalysisModel, SystemSettingsDefaults.FileAnalysisModel, StringComparison.Ordinal)
            ? null
            : "Analyses already recorded keep the model they ran under; only future analyses use this. "
              + "Per-document cost and extraction quality vary by model.";

    /// <summary>
    /// The host of a stored base URL, or null when it is empty or unparseable. The one place any of
    /// these delegates is allowed to look at the value, so no advisory can reach the path, query or
    /// <c>userinfo</c> even by accident.
    /// </summary>
    private static string? HostOf(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;

    /// <summary>Lower-cased with every non-alphanumeric character stripped, so "Anthropic, Inc." matches "anthropic".</summary>
    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
