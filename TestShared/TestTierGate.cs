// Linked (not referenced) into Odyssey.IntegrationTests, Odyssey.E2ETests and Odyssey.E2ETests.Api, so
// the three self-skipping fixtures share one decision rule without a project any of them must depend on.
// Every type here is internal: each test assembly compiles its own copy.

namespace Odyssey.Testing;

/// <summary>What a fixture found when it tried to bring its tier up.</summary>
internal enum TierProbe
{
    /// <summary>The prerequisites are present and the tier is usable.</summary>
    Ready,

    /// <summary>
    /// The one skippable condition: the thing the tier runs against is simply ABSENT — no Docker daemon
    /// reachable, or nothing listening where the stack should be.
    /// </summary>
    PrerequisiteMissing,

    /// <summary>
    /// The prerequisite is present but bringing the tier up went wrong — a provisioning step failed, an
    /// image could not be pulled, a stack answers but is broken. Never a skip.
    /// </summary>
    Faulted,
}

/// <summary>What a fixture must do with a <see cref="TierProbe"/>.</summary>
internal enum TierVerdict
{
    Run,
    Skip,
    Fail,
}

/// <summary>
/// Raised from a fixture's <c>InitializeAsync</c> when its tier must fail rather than skip. xUnit then
/// reports every test in the collection as failed with this message, which is the point: a red job
/// instead of a green one that ran nothing (issue #257).
/// </summary>
internal sealed class TestTierUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// The rule deciding whether a tier whose environment is not usable skips or fails (issue #257).
/// </summary>
/// <remarks>
/// <para>
/// Only an ABSENT prerequisite is ever a skip, and only while the tier is not required. Anything that
/// goes wrong once the prerequisite is present is a failure unconditionally, because that is the case a
/// blanket catch used to hide: a provisioning failure, a failed image pull or a slow start on CI skipped
/// the whole tier while the job stayed green.
/// </para>
/// <para>
/// <c>ODYSSEY_REQUIRE_TIER</c> is the opt-in that turns the absent case into a failure too. It is a
/// comma-separated list of tier names — <c>integration</c>, <c>e2e</c>, <c>e2e-api</c>, or <c>all</c> —
/// and an unknown name is itself an error, so a typo in a workflow cannot silently leave a tier
/// unrequired.
/// </para>
/// </remarks>
internal static class TestTierGate
{
    public const string RequireTierEnvVar = "ODYSSEY_REQUIRE_TIER";

    /// <summary><c>Odyssey.IntegrationTests</c> — the Testcontainers-MariaDB tier.</summary>
    public const string Integration = "integration";

    /// <summary><c>Odyssey.E2ETests</c> — the Playwright browser tier.</summary>
    public const string E2E = "e2e";

    /// <summary><c>Odyssey.E2ETests.Api</c> — the API-over-HTTP tier.</summary>
    public const string E2EApi = "e2e-api";

    /// <summary>Shorthand for every tier above.</summary>
    public const string All = "all";

    public static readonly IReadOnlyList<string> KnownTiers = [Integration, E2E, E2EApi];

    /// <summary>
    /// Parses an <c>ODYSSEY_REQUIRE_TIER</c> value. Blank means nothing is required. Throws on an unknown
    /// tier name rather than ignoring it.
    /// </summary>
    public static IReadOnlySet<string> ParseRequiredTiers(string? value)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return required;
        }

        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(entry, All, StringComparison.OrdinalIgnoreCase))
            {
                required.UnionWith(KnownTiers);
            }
            else if (KnownTiers.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                required.Add(entry);
            }
            else
            {
                throw new TestTierUnavailableException(
                    $"{RequireTierEnvVar}='{value}' names an unknown tier '{entry}'. " +
                    $"Known tiers: {string.Join(", ", KnownTiers)}, or '{All}'.");
            }
        }

        return required;
    }

    public static bool IsRequired(string tier, string? requireTierValue) =>
        ParseRequiredTiers(requireTierValue).Contains(tier);

    public static bool IsRequired(string tier) =>
        IsRequired(tier, Environment.GetEnvironmentVariable(RequireTierEnvVar));

    public static TierVerdict Decide(TierProbe probe, bool required) => probe switch
    {
        TierProbe.Ready => TierVerdict.Run,
        TierProbe.PrerequisiteMissing => required ? TierVerdict.Fail : TierVerdict.Skip,
        TierProbe.Faulted => TierVerdict.Fail,
        _ => throw new ArgumentOutOfRangeException(nameof(probe), probe, null),
    };

    /// <summary>
    /// Applies <see cref="Decide"/>: returns <c>null</c> to run, a skip reason to skip, or throws
    /// <see cref="TestTierUnavailableException"/> to fail.
    /// </summary>
    public static string? Resolve(string tier, TierProbe probe, string reason, Exception? cause = null) =>
        Resolve(tier, probe, reason, cause, Environment.GetEnvironmentVariable(RequireTierEnvVar));

    public static string? Resolve(
        string tier, TierProbe probe, string reason, Exception? cause, string? requireTierValue)
    {
        var required = IsRequired(tier, requireTierValue);
        return Decide(probe, required) switch
        {
            TierVerdict.Run => null,
            TierVerdict.Skip => $"{reason} (set {RequireTierEnvVar}={tier} to fail instead of skip)",
            _ when probe == TierProbe.PrerequisiteMissing => throw new TestTierUnavailableException(
                $"Test tier '{tier}' is required ({RequireTierEnvVar}={requireTierValue}) but cannot run: {reason}",
                cause),
            _ => throw Fault(tier, reason, cause),
        };
    }

    /// <summary>
    /// The failure for a tier whose prerequisite is present but which could not be brought up — thrown
    /// whatever <c>ODYSSEY_REQUIRE_TIER</c> says.
    /// </summary>
    public static TestTierUnavailableException Fault(string tier, string reason, Exception? cause = null) =>
        new($"Test tier '{tier}' is present but broken, so it fails rather than skips: {reason}", cause);
}
