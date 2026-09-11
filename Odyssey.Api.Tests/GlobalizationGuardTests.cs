using System.Globalization;
using Odyssey.Api;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The invariant-globalization startup guard (issue #48 §5, AC 42).
/// </summary>
/// <remarks>
/// <para>
/// The condition it catches is invisible everywhere it would normally be caught. A developer machine,
/// CI and both fast test tiers all have ICU, so <c>CompareOptions.IgnoreNonSpace</c> folds accents
/// there; the alpine runtime image ships none, so the process falls back to invariant globalization
/// and the folding silently stops. Contact-alias uniqueness would then accept <c>"Renee"</c> against a
/// stored <c>"Renée"</c> and leave the database's own <c>_ci</c> unique index to reject the insert —
/// a duplicate-key failure on an ordinary write, reproducible only in the built container.
/// </para>
/// <para>
/// Invariant mode is fixed at runtime startup and cannot be flipped in-process, so the guard is split
/// into a measurement and a response. The measurement is pinned here in the healthy direction (and
/// pinned to be a real discriminator, not a comparison that is always equal); the response is asserted
/// directly. What the split cannot cover — that invariant mode really does disable folding — is a
/// property of the runtime, not of this code.
/// </para>
/// </remarks>
public class GlobalizationGuardTests
{
    [Fact]
    public void The_guard_passes_when_ICU_is_available()
    {
        // The test host has ICU, so this is the healthy path end to end.
        GlobalizationGuard.EnsureIcuAvailable();
        Assert.True(GlobalizationGuard.AccentFoldingWorks());
    }

    // The measurement is behaviour-based, not a read of the switch: an app-context switch, a
    // runtimeconfig property and a missing ICU library all reach the same condition by different
    // routes, and only the comparison itself covers all three.
    [Fact]
    public void The_measurement_is_a_real_discriminator()
    {
        Assert.Equal(0, string.Compare("e", "é", CultureInfo.InvariantCulture, CompareOptions.IgnoreNonSpace));
        // If this were also equal, the guard would pass under every condition including the degraded one.
        Assert.NotEqual(0, string.Compare("e", "f", CultureInfo.InvariantCulture, CompareOptions.IgnoreNonSpace));
    }

    [Fact]
    public void The_guard_refuses_when_accent_folding_is_unavailable()
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => GlobalizationGuard.EnsureIcuAvailable(accentFoldingWorks: false));

        // The message has to name the remedy: an operator meeting this has a container that will not
        // start, and the cause — a missing library — is nowhere in the symptom.
        Assert.Contains("icu-libs", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", refusal.Message, StringComparison.Ordinal);
    }

    // The other half of the same contract, and the one most likely to regress silently: a Dockerfile
    // edit that drops either line brings back exactly the condition the guard exists to refuse — and
    // the guard would then take the whole API down rather than degrade, which is the intended trade
    // but only if the image is built to satisfy it.
    [Fact]
    public void The_api_image_installs_ICU_and_pins_the_switch_off()
    {
        var dockerfile = File.ReadAllText(SolutionFile("Odyssey.Api/Dockerfile"));

        Assert.Contains("apk add --no-cache icu-libs", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false", dockerfile, StringComparison.Ordinal);
    }

    // And nothing in the build re-enables it from the other direction — an InvariantGlobalization
    // property in a csproj or runtimeconfig would win regardless of the environment variable.
    [Fact]
    public void No_project_opts_into_invariant_globalization()
    {
        var root = SolutionRoot();
        var offenders = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "Directory.Build.props", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file)
                .Contains("<InvariantGlobalization>true</InvariantGlobalization>", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string SolutionFile(string relativePath) =>
        Path.Combine(SolutionRoot(), relativePath);

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the solution root from the test binary.");
    }
}
