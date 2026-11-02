using Odyssey.Core.Locations.Conversion;
using Odyssey.Context;

namespace Odyssey.MigrationService;

/// <summary>
/// Migrates <see cref="OdysseyContext"/> — the whole schema, identity included. All of it lives under
/// one context now, so a plain migrate applies every migration in timestamp order, and every foreign
/// key (a finance row to a contact, a photo to a file, a journal entry to the user who wrote it) is
/// created inside the same unit as the tables it joins.
/// </summary>
public sealed class OdysseyMigrationService(
    IServiceProvider serviceProvider,
    ILogger<OdysseyMigrationService> logger)
    : IOdysseyMigrationService
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        // Read before migrating: afterwards the accounts it counts no longer exist (issue #218 §9).
        var retirement = await AccountTypeRetirementReport.ReadIfPendingAsync(dbContext, cancellationToken);

        // Staged around the issue #380 location conversion; same scope, so the same context and connection.
        var conversion = scope.ServiceProvider.GetRequiredService<ILocationConversion>();
        await MigrationRunner.MigrateAsync(dbContext, conversion, cancellationToken);

        retirement?.Log(logger);
    }
}
