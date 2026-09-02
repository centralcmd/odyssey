namespace Odyssey.Dtos.Finance;

/// <summary>
/// Shared validation bounds for the insurance link collections (issue #27). Referenced by the
/// request-DTO data annotations — so an over-length array is rejected by <c>[ApiController]</c> model
/// validation before the service sees it — and by <c>RequestCapCeilings</c>, which refuses an
/// <c>InsuranceMaxLinksPerPolicy</c> setting above the compile-time limit that pre-empts it.
///
/// <para>
/// Mirrors <c>PhotoLimits.MaxLinksPerKind</c>: a compile-time constant, deliberately NOT the settings
/// bound maximum. Pinning it to the bound would make the attribute inert and invert CLAUDE.md's
/// tighten-only rule.
/// </para>
/// </summary>
public static class InsuranceLinkLimits
{
    /// <summary>
    /// Max members in each of a policy's four link collections — insurers, insured accounts, insured
    /// contacts and beneficiaries — counted per collection, not across them.
    /// </summary>
    public const int MaxLinksPerPolicy = 50;
}
