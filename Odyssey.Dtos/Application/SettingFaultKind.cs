namespace Odyssey.Dtos.Application;

/// <summary>
/// Why a system setting's row is not the value being read (issue #437 §5 component 7). Carried on
/// <see cref="SystemSettingsDto.ProjectionFaults"/>, keyed by the <c>SystemSettingsUpdate</c> property
/// name — the same join key <c>Warnings</c> and <c>ApiProblem.Errors</c> use.
///
/// <para>
/// <strong>The members start at 1, deliberately.</strong> A defaulted or zero <c>int</c> is never a
/// valid value in this codebase (see <see cref="Sex"/>, which states the convention). With
/// <c>Unreadable = 0</c>, both <c>GetValueOrDefault</c> and a missing-key <c>TryGetValue</c> would
/// yield the <em>alarming</em> kind for a perfectly healthy field — and the client's existing advisory
/// reader is exactly that miss-path idiom.
/// </para>
///
/// <para>
/// A healthy field has <strong>no entry</strong>; there is no <c>Ok</c> member on the wire. The read
/// path's own outcome vocabulary calls the first case <c>Unparseable</c>; it maps to
/// <see cref="Unreadable"/> here because "unreadable" is what the administrator-facing sentence claims.
/// </para>
/// </summary>
public enum SettingFaultKind
{
    /// <summary>The stored value could not be parsed, so the row shows the shipped default.</summary>
    Unreadable = 1,

    /// <summary>
    /// The stored value parsed but fell outside its <c>SystemSettingsBounds</c> pair, so it is being
    /// read as the nearer bound. Unlike <see cref="Unreadable"/>, this is a claim about
    /// <em>behaviour</em>: the projection and the engine clamp against the same pair.
    /// </summary>
    Clamped = 2,
}
