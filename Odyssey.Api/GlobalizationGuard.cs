using System.Globalization;

namespace Odyssey.Api;

/// <summary>
/// The startup assertion behind accent-insensitive comparison (issue #48 §5, AC 42).
///
/// <para>
/// <c>ContactService</c> compares alias values with
/// <c>CompareOptions.IgnoreCase | IgnoreNonSpace</c>, which is culture-aware and ICU-backed, so that
/// the service's duplicate pre-check, the <c>utf8mb4_*_ci</c> unique index and the accent-sensitive
/// EF InMemory provider all agree. Under <b>invariant globalization</b> — what .NET falls back to on
/// an image with no ICU, or when <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c> is set — that folding
/// silently stops. The check then passes <c>"Renee"</c> against a stored <c>"Renée"</c> and the
/// index rejects the insert, turning an ordinary write into a duplicate-key failure.
/// </para>
///
/// <para>
/// <b>Why a startup guard rather than a test.</b> The failure mode is invisible everywhere it would
/// be caught: a developer machine, CI and both fast test tiers all have ICU, so the degradation
/// appears only in the built container. A loud refusal to start is the only signal that reaches the
/// place it actually happens.
/// </para>
/// </summary>
internal static class GlobalizationGuard
{
    /// <summary>
    /// Throws when the process is running with invariant globalization.
    ///
    /// <para>
    /// Detected by <b>behaviour</b>, not by reading the switch: an app-context switch, a runtime
    /// config property and a missing ICU library all produce the same condition through different
    /// routes, and only the comparison itself covers all three. <c>"e"</c> and <c>"é"</c> compare
    /// equal under <c>IgnoreNonSpace</c> exactly when accent folding works.
    /// </para>
    /// </summary>
    public static void EnsureIcuAvailable() => EnsureIcuAvailable(AccentFoldingWorks());

    /// <summary>
    /// Whether <c>IgnoreNonSpace</c> actually folds accents in this process — the single fact the
    /// guard turns on. Public so a test can pin that it is a real discriminator rather than a
    /// comparison that is always equal.
    /// </summary>
    public static bool AccentFoldingWorks() =>
        string.Compare("e", "é", CultureInfo.InvariantCulture, CompareOptions.IgnoreNonSpace) == 0;

    /// <summary>
    /// The guard's RESPONSE, split from its measurement so both halves are testable. Invariant mode is
    /// fixed at runtime startup and cannot be flipped in-process, so a test can pin the measurement
    /// only in the healthy direction; this overload is how the refusal itself is asserted.
    /// </summary>
    internal static void EnsureIcuAvailable(bool accentFoldingWorks)
    {
        if (accentFoldingWorks)
        {
            return;
        }

        throw new InvalidOperationException(
            "Globalization is running in invariant mode, so accent-insensitive comparison is unavailable. "
            + "Contact-alias uniqueness would then disagree with the database's case- and accent-insensitive "
            + "unique index, turning an ordinary write into a duplicate-key failure. Install ICU in the runtime "
            + "image (apk add icu-libs icu-data-full) and ensure DOTNET_SYSTEM_GLOBALIZATION_INVARIANT is false "
            + "and InvariantGlobalization is not set in the project or runtimeconfig.");
    }
}
