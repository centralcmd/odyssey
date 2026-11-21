using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Odyssey.TestData;

/// <summary>
/// Whether a host may seed demo data: the one gate both halves of the demo seed read — the migrations job's
/// <c>DemoDataSeeder</c> and the worker's <c>DemoFileSeeder</c>, which writes the demo files and photos.
/// </summary>
/// <remarks>
/// <para>
/// Demo data is seeded only in Development and Testing. Every other environment name — Production, Staging, or
/// anything an operator invents — refuses, and <c>Seed:DemoData=true</c> cannot override that. Inside the two allowed
/// environments the flag still wins, so a developer can turn seeding off.
/// </para>
/// <para>
/// An allow-list, not a Production deny-list (issue #457, item B4). The previous shape refused Production and then let
/// an explicit flag win <em>anywhere else</em>, so <c>ASPNETCORE_ENVIRONMENT=Staging</c> plus <c>Seed:DemoData=true</c> —
/// both things a self-hoster types — seeded four accounts, one of them Admin with lockout disabled, whose shared password
/// is published in this repository's README. A deny-list has to enumerate every environment that must not seed; an
/// allow-list enumerates the two that may, so a name nobody anticipated fails closed.
/// </para>
/// <para>
/// The refusal is not redundant with <c>docker-compose.prod.yml</c> pinning <c>Seed__DemoData: "false"</c>. The base
/// compose file defaults the flag to <c>true</c>, so anyone who runs without the prod overlay — or deploys the image
/// from their own manifest — would otherwise seed.
/// </para>
/// </remarks>
public static class DemoDataGate
{
    public const string FlagKey = "Seed:DemoData";

    public static bool IsEnabled(IHostEnvironment environment, IConfiguration configuration, ILogger logger)
    {
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            // Warn rather than skip silently: an operator who set the flag deliberately is owed the
            // reason it did nothing, and the line doubles as evidence that the gate held.
            if (configuration.GetValue<bool?>(FlagKey) == true)
            {
                logger.LogWarning(
                    "Seed:DemoData=true was ignored: demo data seeds only in Development or Testing, "
                    + "and this host is running as {Environment}. The demo accounts share a password "
                    + "published in the README, so they are never seeded outside those environments.",
                    environment.EnvironmentName);
            }

            return false;
        }

        return configuration.GetValue<bool?>(FlagKey) ?? true;
    }
}
