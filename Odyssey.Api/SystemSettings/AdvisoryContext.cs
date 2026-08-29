namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Everything an advisory delegate is allowed to read (issue #434 §5 item 3).
///
/// <para>
/// Deliberately tiny — and, since issue #439, deliberately <em>empty</em>. It used to carry the
/// pre-parsed host of the configured <c>FileAnalysis:BaseUrl</c>, because that value lived in
/// <c>IOptions&lt;FileAnalysisOptions&gt;</c> and an advisory has no way to reach configuration. The
/// base URL is a setting now, so <c>ProcessorMatchesBaseUrl</c> reads it off
/// <see cref="Odyssey.Dtos.Application.SystemSettingsDto"/> like every other value and parses the host
/// itself, under the same host-only rule.
/// </para>
///
/// <para>
/// The type stays rather than the parameter being dropped, for two reasons. The delegate signature
/// <c>Func&lt;SystemSettingsDto, AdvisoryContext, string?&gt;</c> is shared by every advisory, so
/// removing it would touch all of them for no behavioural gain. More importantly it is the designed
/// extension point: it carries no <c>DbContext</c>, no <c>HttpContext</c>, no secrets and no services,
/// which is what keeps <see cref="SystemSettingDescriptor.Advise"/> pure, synchronous and cheap enough
/// to run for every field on every read — and what makes "an advisory can never fail a save" a
/// structural property rather than a promise. The next advisory that genuinely needs ambient input
/// adds it here, where that constraint is enforced.
/// </para>
/// </summary>
internal sealed record AdvisoryContext
{
    /// <summary>The single instance. Nothing distinguishes one context from another today.</summary>
    public static readonly AdvisoryContext Empty = new();
}
