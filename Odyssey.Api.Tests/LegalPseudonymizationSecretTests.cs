using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Legal;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The pseudonymization secret after issue #445 Wave 4, which moved it out of
/// <c>Legal:PseudonymizationSecret</c> and into the encrypted secret store.
///
/// <para>
/// This replaces <c>LegalOptionsValidationTests</c>, whose subject — a <c>ValidateOnStart</c> gate on
/// a bound options class — no longer exists. The gate is gone deliberately, and its removal is
/// asserted here rather than left implicit: a credential an administrator enters through the UI cannot
/// be a precondition for the UI coming up, and the value now lives in a database this process has not
/// necessarily migrated when options are validated. What replaces it is a throw at the point of use,
/// inside the deletion's own transaction — so a deletion that cannot pseudonymise rolls back and
/// leaves the acceptance rows intact and attributable, which is the recoverable outcome.
/// </para>
/// </summary>
public class LegalPseudonymizationSecretTests
{
    private const string Subject = "target@example.com";

    /// <summary>The stored value is used, and normalisation still holds across it.</summary>
    [Fact]
    public async Task AStoredSecret_IsTheOneUsed_AndTheDigestStaysCaseInsensitive()
    {
        var stored = Create("Production", reader => reader.Found(
            SecretSettingKeys.LegalPseudonymizationSecret, "a-real-production-secret"));

        var expected = await stored.PseudonymizeAsync(Subject);

        Assert.Equal(64, expected.Length);
        Assert.Equal(expected, await stored.PseudonymizeAsync(Subject.ToUpperInvariant()));

        // …and it really is keyed by the stored value: a different one gives a different pseudonym.
        var other = Create("Production", reader => reader.Found(
            SecretSettingKeys.LegalPseudonymizationSecret, "a-different-secret"));
        Assert.NotEqual(expected, await other.PseudonymizeAsync(Subject));
    }

    /// <summary>
    /// Outside Production an unset secret still substitutes the documented placeholder, so the
    /// dev/Compose stack's delete flow works out of the box with nothing configured — byte-identical
    /// to the behaviour before the migration.
    /// </summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public async Task OutsideProduction_AnUnsetSecret_FallsBackToTheDevelopmentValue(string environment)
    {
        var pseudonymizer = Create(environment);
        var reference = Create(environment, reader => reader.Found(
            SecretSettingKeys.LegalPseudonymizationSecret, LegalOptions.DevelopmentPseudonymizationSecret));

        Assert.Equal(await reference.PseudonymizeAsync(Subject), await pseudonymizer.PseudonymizeAsync(Subject));
    }

    /// <summary>
    /// In Production an unset secret throws, naming the remedy. Not a substituted value: a pseudonym
    /// derived from the development placeholder would look correct, be permanently wrong, and be
    /// indistinguishable from a real one afterwards.
    /// </summary>
    [Fact]
    public async Task InProduction_AnUnsetSecret_Throws()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create("Production").PseudonymizeAsync(Subject));

        Assert.Contains(SecretSettingKeys.LegalPseudonymizationSecret, exception.Message, StringComparison.Ordinal);
        Assert.Contains("System settings", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AC 12, the rule the whole issue exists for. An <c>Unreadable</c> row throws in EVERY
    /// environment — including the ones that substitute a development value for an ABSENT row. The two
    /// states are not the same: an absent row means "not configured yet", an unreadable one means "a
    /// value is stored and this server cannot open it", and quietly deriving from the placeholder
    /// there would write pseudonyms nobody can ever re-derive.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task AnUnreadableSecret_Throws_InEveryEnvironment(string environment)
    {
        var pseudonymizer = Create(environment, reader =>
            reader.Unreadable(SecretSettingKeys.LegalPseudonymizationSecret));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pseudonymizer.PseudonymizeAsync(Subject));

        Assert.Contains("cannot be decrypted", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            LegalOptions.DevelopmentPseudonymizationSecret, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 12 as a source fact rather than a behaviour: there is nothing left to fall back TO. The
    /// pseudonymizer does not read <c>IConfiguration</c> or <c>IOptions</c> at all, so no future edit
    /// can reintroduce the fallback by accident — it would have to add a dependency first.
    /// </summary>
    [Fact]
    public void ThePseudonymizer_TakesNoConfigurationDependency()
    {
        var parameters = typeof(LegalPseudonymizer).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(parameters, parameter =>
            parameter.ParameterType.Name.Contains("IConfiguration", StringComparison.Ordinal)
            || parameter.ParameterType.Name.Contains("IOptions", StringComparison.Ordinal));
    }

    /// <summary>
    /// The removed startup gate, asserted rather than assumed: a Production host boots with no
    /// pseudonymization secret configured anywhere. Before Wave 4 this threw at
    /// <c>CreateClient()</c>.
    /// </summary>
    [Fact]
    public void InProduction_TheAppStarts_WithNoPseudonymizationSecretConfigured()
    {
        using var factory = new ProductionFactory();

        Assert.Null(Record.Exception(() => factory.CreateClient()));
    }

    private static LegalPseudonymizer Create(
        string environment, Action<StubSecretSettingsReader>? configure = null)
    {
        var reader = new StubSecretSettingsReader();
        configure?.Invoke(reader);

        var services = new ServiceCollection();
        services.AddSingleton<ISecretSettingsReader>(reader);

        return new LegalPseudonymizer(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StubHostEnvironment(environment));
    }

    /// <summary>
    /// Boots the real app in Production. Not <see cref="OdysseyApiFactory"/>: that one pins the Testing
    /// environment, which is exactly the variable under test. See
    /// <c>AntiforgeryEnforcementTests.EnforcedFactory</c> for why the in-memory flag has to be an
    /// environment variable — <c>AddDatabases</c> reads configuration before <c>Build()</c>.
    /// </summary>
    private sealed class ProductionFactory : WebApplicationFactory<Program>
    {
        public ProductionFactory() =>
            Environment.SetEnvironmentVariable("UseInMemoryDatabase", "true");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["UseInMemoryDatabase"] = "true",
                    // Production used to have a second startup requirement — a configured relay
                    // (issue #405) — which had to be supplied here or its validator failed first and
                    // this test measured the wrong thing. Issue #8 removed that gate along with the
                    // setting, so there is nothing left to satisfy.
                }));
        }
    }
}
