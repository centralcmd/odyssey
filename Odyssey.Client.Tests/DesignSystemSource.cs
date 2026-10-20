using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Locates a design-system file for the parity checks that compare the client with the Odyssey
/// Design System artifact (<c>.claude/design-system-artifact.md</c>).
///
/// <para>
/// The artifact is the only copy of the design system and CI cannot read it, so these checks are
/// opt-in: they run when <see cref="Variable"/> names a downloaded copy of the artifact's
/// <c>project/</c> folder, and skip otherwise. The <c>odyssey-design-system-changes</c> skill sets it
/// on every sync. An absent copy is the one skippable case; a copy that is named but missing a file
/// fails, so a renamed artifact file surfaces as a failure rather than a silent skip.
/// </para>
/// </summary>
internal static class DesignSystemSource
{
    public const string Variable = "ODYSSEY_DESIGN_SYSTEM_DIR";

    /// <summary>Absolute path of <paramref name="relativePath"/> inside the downloaded <c>project/</c> folder.</summary>
    public static string File(string relativePath)
    {
        var root = Environment.GetEnvironmentVariable(Variable);
        Skip.If(
            string.IsNullOrWhiteSpace(root),
            $"Design-system parity check: set {Variable} to a downloaded copy of the artifact's project/ folder to run it.");

        Assert.True(Directory.Exists(root), $"{Variable} names '{root}', which does not exist.");

        var path = Path.Combine(root!, relativePath);
        Assert.True(System.IO.File.Exists(path), $"The design-system artifact has no {relativePath} (looked in {root}).");
        return path;
    }
}
