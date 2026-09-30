using DotNet.Testcontainers.Builders;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The skip-or-fail rule for the self-skipping tiers (issue #257). Plain unit tests: they sit outside
/// <see cref="MariaDbCollection"/>, so they run — and must pass — with no Docker at all.
/// </summary>
public sealed class TestTierGateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_value_requires_nothing(string? value)
    {
        Assert.Empty(TestTierGate.ParseRequiredTiers(value));
    }

    [Fact]
    public void Comma_separated_names_are_trimmed_and_case_insensitive()
    {
        var required = TestTierGate.ParseRequiredTiers(" Integration , e2e-api,,");

        Assert.True(required.SetEquals([TestTierGate.Integration, TestTierGate.E2EApi]));
        Assert.True(TestTierGate.IsRequired(TestTierGate.Integration, "INTEGRATION"));
        Assert.False(TestTierGate.IsRequired(TestTierGate.E2E, "integration,e2e-api"));
    }

    [Fact]
    public void All_requires_every_known_tier()
    {
        Assert.True(TestTierGate.ParseRequiredTiers("all").SetEquals(TestTierGate.KnownTiers));
    }

    [Theory]
    [InlineData("integraton")]
    [InlineData("e2e_api")]
    [InlineData("integration,browser")]
    public void An_unknown_tier_name_is_an_error_not_a_silent_no_op(string value)
    {
        var ex = Assert.Throws<TestTierUnavailableException>(() => TestTierGate.ParseRequiredTiers(value));
        Assert.Contains(TestTierGate.RequireTierEnvVar, ex.Message);
    }

    [Fact]
    public void Decision_matrix()
    {
        Assert.Equal(TierVerdict.Run, TestTierGate.Decide(TierProbe.Ready, required: false));
        Assert.Equal(TierVerdict.Run, TestTierGate.Decide(TierProbe.Ready, required: true));
        Assert.Equal(TierVerdict.Skip, TestTierGate.Decide(TierProbe.PrerequisiteMissing, required: false));
        Assert.Equal(TierVerdict.Fail, TestTierGate.Decide(TierProbe.PrerequisiteMissing, required: true));
        Assert.Equal(TierVerdict.Fail, TestTierGate.Decide(TierProbe.Faulted, required: false));
        Assert.Equal(TierVerdict.Fail, TestTierGate.Decide(TierProbe.Faulted, required: true));
    }

    [Fact]
    public void Missing_prerequisite_skips_when_not_required()
    {
        var reason = TestTierGate.Resolve(TestTierGate.Integration, TierProbe.PrerequisiteMissing, "no Docker", null, "e2e");

        Assert.NotNull(reason);
        Assert.Contains("no Docker", reason);
        Assert.Contains($"{TestTierGate.RequireTierEnvVar}={TestTierGate.Integration}", reason);
    }

    [Fact]
    public void Missing_prerequisite_fails_when_required()
    {
        var ex = Assert.Throws<TestTierUnavailableException>(() =>
            TestTierGate.Resolve(TestTierGate.Integration, TierProbe.PrerequisiteMissing, "no Docker", null, "integration"));

        Assert.Contains("required", ex.Message);
        Assert.Contains("no Docker", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("integration")]
    public void A_fault_fails_whether_or_not_the_tier_is_required(string? value)
    {
        var cause = new InvalidOperationException("Database provisioning failed");

        var ex = Assert.Throws<TestTierUnavailableException>(() =>
            TestTierGate.Resolve(TestTierGate.Integration, TierProbe.Faulted, "provisioning", cause, value));

        Assert.Same(cause, ex.InnerException);
    }

    [Fact]
    public void Ready_runs()
    {
        Assert.Null(TestTierGate.Resolve(TestTierGate.Integration, TierProbe.Ready, "ok", null, "integration"));
    }

    [Fact]
    public void Only_DockerUnavailableException_is_a_missing_prerequisite()
    {
        var unavailable = new DockerUnavailableException("Docker is either not running or misconfigured.");

        Assert.Equal(TierProbe.PrerequisiteMissing, MariaDbFixture.ClassifyStartupFailure(unavailable));
        Assert.Equal(
            TierProbe.PrerequisiteMissing,
            MariaDbFixture.ClassifyStartupFailure(new InvalidOperationException("wrapped", unavailable)));
        Assert.Equal(
            TierProbe.PrerequisiteMissing,
            MariaDbFixture.ClassifyStartupFailure(new AggregateException(new TimeoutException(), unavailable)));
    }

    [Fact]
    public void Everything_else_once_Docker_is_reachable_is_a_fault()
    {
        // The provisioning failure the old catch-all turned into a skip (issue #257).
        Assert.Equal(
            TierProbe.Faulted,
            MariaDbFixture.ClassifyStartupFailure(new InvalidOperationException("Database provisioning failed: boom")));
        // An image pull or a daemon error surfaces as an HTTP-level failure from the Docker API client.
        Assert.Equal(
            TierProbe.Faulted,
            MariaDbFixture.ClassifyStartupFailure(new HttpRequestException("pull access denied")));
        // A readiness wait that never completes.
        Assert.Equal(TierProbe.Faulted, MariaDbFixture.ClassifyStartupFailure(new TimeoutException()));
    }
}
