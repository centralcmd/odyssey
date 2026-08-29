using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Writes a <see cref="SystemSetting"/> row into a test factory's database.
///
/// <para>
/// Needed because settings that used to be configuration are not configuration any more (issue #421).
/// A test that sets <c>["Email:PerRecipientLimit"] = "1"</c> in in-memory configuration is now
/// asserting against a key nothing reads — it passes or fails for the wrong reason, and in the throttle
/// case it silently stopped limiting at all. Set the row instead.
/// </para>
///
/// <para>
/// <c>OdysseyApiFactory</c> never calls <c>EnsureCreated</c>, so on normal request paths the
/// <c>HasData</c> seed does not materialise and anything reading a key with no row falls back to its
/// compiled default — which is why most settings tests pass without seeding anything.
/// </para>
///
/// <para>
/// This helper nevertheless calls <c>EnsureCreated</c> before writing, and that is load-bearing.
/// Identity and the domain share one context now, so a bare write here would implicitly create the
/// single in-memory store — and a <c>EnsureCreated</c> later in the same test would then answer
/// "already exists" and seed <em>nothing</em>. A test that set a cap before seeding its fixtures
/// silently lost the 164 reference currencies, and failed with "PremiumCurrencyCode 'USD' is not
/// supported" a long way from the cause. Creating the store here first makes the order not matter.
/// </para>
/// </summary>
internal static class SystemSettingsSeed
{
    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>, replacing any existing row.</summary>
    internal static async Task SetAsync(IServiceProvider services, string key, string value)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var existing = context.SystemSettings.FirstOrDefault(setting => setting.Key == key);
        if (existing is not null)
        {
            existing.Value = value;
        }
        else
        {
            context.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = value,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await context.SaveChangesAsync();
    }
}
