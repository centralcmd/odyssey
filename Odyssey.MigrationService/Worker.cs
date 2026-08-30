using System.Diagnostics;

namespace Odyssey.MigrationService;

// Named dependencies rather than one injected collection, so the order below stays visible at the
// call site and substitutable in tests — see IMigrationStep.
public sealed class Worker(
    IOdysseyMigrationService odysseyMigrationService,
    IRoleClaimSeeder roleClaimSeeder,
    IBootstrapAdminSeeder bootstrapAdminSeeder,
    IDemoDataSeeder demoDataSeeder,
    IAdministratorAssertion administratorAssertion,
    IHostApplicationLifetime hostApplicationLifetime,
    ILogger<Worker> logger)
    : BackgroundService
{
    public const string ActivitySourceName = "DatabaseMigrations";
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("Start migrating databases", ActivityKind.Client);

        try
        {
            // Identity, finance, journal, photos, calendars and contacts are one context now: a single
            // MigrateAsync applies every migration in timestamp order, and every foreign key — the
            // cross-module ones and the user-attribution ones — lands with the tables it references.
            activity?.AddEvent(new ActivityEvent("Start migrating the database (identity + finance + journal + photos + calendar + contacts)."));
            await odysseyMigrationService.ExecuteAsync(cancellationToken);

            // After the migration creates AspNetRoleClaims, and before anything that
            // reasons about authorization. The claims are no longer seeded by the model: reconciling
            // them here on (RoleId, ClaimType, ClaimValue) is what lets a claim be added or removed
            // without a hand-written migration renumbering every row after it.
            activity?.AddEvent(new ActivityEvent("Reconcile role claims with the permission mapping."));
            await roleClaimSeeder.ExecuteAsync(cancellationToken);

            // Before the demo seed, not after (issue #290): the bootstrap seeder is keyed on an empty
            // user table, so running it second would find the demo users already present and silently
            // ignore credentials an operator had explicitly configured.
            activity?.AddEvent(new ActivityEvent("Seed the bootstrap administrator (only on an empty user table)."));
            await bootstrapAdminSeeder.ExecuteAsync(cancellationToken);

            activity?.AddEvent(new ActivityEvent("Seed demo data (Development/Testing only, idempotent)."));
            await demoDataSeeder.ExecuteAsync(cancellationToken);

            // Fail the job rather than let the API come up behind it with nobody able to administer it.
            activity?.AddEvent(new ActivityEvent("Assert an enabled administrator exists."));
            await administratorAssertion.ExecuteAsync(cancellationToken);
        }
        catch (MigrationDriftException drift)
        {
            activity?.AddException(drift);

            // Logged without the stack trace, and separately from the generic case below, because the
            // stack is the least useful part of this particular failure: the message already says what
            // is wrong and how to repair it, and burying that under an EF trace is what made the
            // original occurrence take a DCP-log dig to diagnose (issue #468).
            logger.LogCritical("Migrations job failed: {Reason}", drift.Message);

            Environment.ExitCode = 1;
            throw;
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);

            // Say plainly that the job failed. Without this the only signal is the host's own
            // BackgroundService trace, which reads as an unhandled exception rather than as "the
            // migrations job is down and nothing behind it will start" (issue #468).
            logger.LogCritical(ex, "Migrations job failed; the API will not start behind it.");

            // The host's default BackgroundService behaviour is to log the exception and stop — which on
            // its own leaves the process exit code at 0, so Compose's
            // `depends_on: condition: service_completed_successfully` would happily start the API behind
            // a job that failed. Setting the exit code explicitly is what makes the failure loud
            // (issue #290 §Goals 3): a failed migration or a missing administrator keeps the API down.
            Environment.ExitCode = 1;
            throw;
        }

        hostApplicationLifetime.StopApplication();
    }
}
