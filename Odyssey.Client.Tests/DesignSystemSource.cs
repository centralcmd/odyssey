using Odyssey.Testing;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Locates a design-system file for the parity checks that compare the client with the Odyssey
/// Design System artifact (<c>.claude/design-system-artifact.md</c>).
///
/// <para>
/// The artifact is the only copy of the design system and CI cannot read it, so these checks are
/// opt-in: they run when <see cref="Variable"/> names a downloaded copy of the artifact's
/// <c>project/</c> folder, and skip otherwise. An absent copy is the one skippable case, and it is the
/// <c>design-system</c> tier of <see cref="TestTierGate"/>, so <c>ODYSSEY_REQUIRE_TIER=design-system</c>
/// turns it into a failure: the <c>odyssey-design-system-changes</c> skill sets both on every sync. A
/// copy that is named but missing a file always fails, so a renamed artifact file surfaces rather than
/// skipping silently.
/// </para>
/// </summary>
internal static class DesignSystemSource
{
    public const string Variable = "ODYSSEY_DESIGN_SYSTEM_DIR";

    /// <summary>Absolute path of <paramref name="relativePath"/> inside the downloaded <c>project/</c> folder.</summary>
    public static string File(string relativePath)
    {
        var root = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(root))
        {
            Skip.If(true, TestTierGate.Resolve(
                TestTierGate.DesignSystem,
                TierProbe.PrerequisiteMissing,
                $"Design-system parity check: set {Variable} to a downloaded copy of the artifact's project/ folder to run it"));
        }

        Assert.True(Directory.Exists(root), $"{Variable} names '{root}', which does not exist.");

        var path = Path.Combine(root!, relativePath);
        Assert.True(System.IO.File.Exists(path), $"The design-system artifact has no {relativePath} (looked in {root}).");
        return path;
    }
}
