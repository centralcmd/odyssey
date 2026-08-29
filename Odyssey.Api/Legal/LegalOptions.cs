namespace Odyssey.Api.Legal;

/// <summary>Constants for the legal-acceptance feature (issue #354 §14).</summary>
/// <remarks>
/// <para>
/// Not a feature toggle — per CLAUDE.md this feature has no kill-switch, and the admin authoring
/// surface is gated by the existing <c>users.manage</c> claim.
/// </para>
/// <para>
/// <strong>No longer a bound options class.</strong> Its one member, <c>PseudonymizationSecret</c>,
/// moved to the encrypted secret store in issue #445 Wave 4 as
/// <c>SecretSettingKeys.LegalPseudonymizationSecret</c>, and the <c>Legal</c> configuration section
/// went with it. What survives is the non-production stand-in, which is not configuration at all: it
/// is a compiled constant the pseudonymizer substitutes outside Production.
/// </para>
/// </remarks>
public static class LegalOptions
{
    // SectionName is gone with the binding it named: no "Legal" configuration section is bound any
    // more, so a constant for its name was a pointer to nothing.

    /// <summary>Non-production stand-in. Deliberately obvious: it is not a secret and must never be used in Production.</summary>
    public const string DevelopmentPseudonymizationSecret = "odyssey-development-pseudonymization-secret-do-not-use-in-production";
}
