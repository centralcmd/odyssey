using Odyssey.Api;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Locks the refusal that keeps <c>ASPNETCORE_ENVIRONMENT=Testing</c> from becoming a one-variable
/// CSRF kill switch on a deployment (issue #451 Phase 3).
/// </summary>
public class TestingEnvironmentGuardTests
{
    [Fact]
    public void Validate_Throws_WhenTestingEnvironmentIsHostedOnARealServer()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TestingEnvironmentGuard.Validate(
                TestingEnvironmentGuard.EnvironmentName,
                "Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerImpl"));

        // The message has to name the consequence, not just the rule: an operator who reached this is
        // deploying, and needs to know what they were about to turn off.
        Assert.Contains("antiforgery", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Staging", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Throws_WhenNoServerIsRegistered()
    {
        // Absence is not evidence of a test host, so it is refused the same way rather than waved through.
        Assert.Throws<InvalidOperationException>(() =>
            TestingEnvironmentGuard.Validate(TestingEnvironmentGuard.EnvironmentName, serverTypeName: null));
    }

    [Fact]
    public void Validate_Allows_TheInProcessTestHost()
    {
        TestingEnvironmentGuard.Validate(
            TestingEnvironmentGuard.EnvironmentName,
            TestingEnvironmentGuard.TestServerTypeName);
    }

    [Theory]
    [InlineData("testing")]
    [InlineData("TESTING")]
    public void Validate_IsCaseInsensitive_OnTheEnvironmentName(string environmentName)
    {
        // IHostEnvironment.IsEnvironment compares case-insensitively, so the guard must too — otherwise
        // `ASPNETCORE_ENVIRONMENT=testing` gets every weakening branch and none of the protection.
        Assert.Throws<InvalidOperationException>(() =>
            TestingEnvironmentGuard.Validate(environmentName, "Kestrel"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    public void Validate_Allows_EveryOtherEnvironment(string environmentName)
    {
        TestingEnvironmentGuard.Validate(environmentName, "Kestrel");
    }

    /// <summary>
    /// The type name is compared as a string so <c>Odyssey.Api</c> need not reference the test-host
    /// package; this asserts the constant still matches the type the factory actually substitutes, which
    /// a package upgrade could rename out from under it.
    /// </summary>
    [Fact]
    public void TestServerTypeName_MatchesTheTypeWebApplicationFactorySubstitutes()
    {
        Assert.Equal(
            TestingEnvironmentGuard.TestServerTypeName,
            typeof(Microsoft.AspNetCore.TestHost.TestServer).FullName);
    }
}
