using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.MigrationService;
using Odyssey.Core.Journal;
using Odyssey.Core.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// Guarded here as well as in the API (issue #451 §1.4), and this is the half that matters most on a
// fresh deployment: this job runs first, the API sits behind `service_completed_successfully`, and it
// is the only host that sees Bootstrap:Admin:* — so a CHANGE_ME bootstrap credential is caught before
// the initial administrator is created rather than after. The Production check lives inside the guard.
builder.Configuration.ThrowIfPlaceholderValues(builder.Environment.EnvironmentName);

builder.Services.AddHostedService<Worker>();

var useInMemoryDatabase = builder.Configuration.GetValue<bool>("UseInMemoryDatabase") || builder.Environment.IsEnvironment("Testing");
if (useInMemoryDatabase)
{
    builder.Services.AddDbContext<OdysseyContext>(options =>
        options.UseInMemoryDatabase("Odyssey"));
}
else
{
    // Pinned MariaDB version (matches docker-compose and the Aspire AppHost). Pinning avoids
    // ServerVersion.AutoDetect, which opens a blocking probe connection at startup.
    var databaseVersion = new MariaDbServerVersion(new Version(11, 4));

    // Retry transient MariaDB failures. The migration/seed paths already wrap their work in an
    // execution strategy (CreateExecutionStrategy), so retries compose correctly here.
    void EnableRetries(MySqlDbContextOptionsBuilder mySql) => mySql.EnableRetryOnFailure();

    // One context, one connection: identity, finance and journal are a single model with real foreign
    // keys between them, so they cannot be pointed at different databases. Resolved eagerly, outside
    // the options lambda, so a missing or blank connection string fails at startup rather than on the
    // first DbContext resolution.
    var odysseyConnectionString = builder.Configuration.GetRequiredConnectionString("OdysseyConnection");
    builder.Services.AddDbContext<OdysseyContext>(options =>
        options.UseMySql(odysseyConnectionString, databaseVersion, EnableRetries));
}

// NO Data Protection key ring here, deliberately (issue #444 §14, revisited). This job briefly had
// one, mounted from the same volume as the API's, so that a config-adoption step could protect a
// secret under the ring the API reads — protecting under an ephemeral ring would have produced a row
// the API could never decrypt. Adoption is gone and nothing else in this job reads or writes a
// protected value, so the ring bought nothing while costing something real: it let a second container
// decrypt every stored credential.
//
// Re-adding it is the correct move IF a step here ever needs to protect a value — but then it must be
// the same directory AND the same SetApplicationName("Odyssey") as Odyssey.Api, or the two derive
// different keys from the same files.

// Identity core (plus the shared password policy) so the seeders can create users via UserManager.
// Shared with the test harnesses so their graph cannot drift from this one — see
// MigrationServiceIdentity. DemoDataSeeder deliberately sidesteps the policy for its documented short
// demo password; see the comment on its user creation.
builder.Services.AddMigrationServiceIdentity();

// Registered against their role interfaces: Worker depends on those, so its steps can be substituted
// without subclassing a production type (see IMigrationStep).
builder.Services.AddTransient<IOdysseyMigrationService, OdysseyMigrationService>();
builder.Services.AddTransient<IRoleClaimSeeder, RoleClaimSeeder>();
builder.Services.AddTransient<IBootstrapAdminSeeder, BootstrapAdminSeeder>();
builder.Services.AddTransient<IDemoDataSeeder, DemoDataSeeder>();
builder.Services.AddTransient<IAdministratorAssertion, AdministratorAssertion>();

// A migration in flight is allowed to finish rather than being torn down by a shutdown signal
// (issue #468). MariaDB commits DDL implicitly, so a migration interrupted between two CREATE TABLEs
// leaves those tables behind with no __EFMigrationsHistory row — a state no later run can recover
// from. MigrationRunner therefore does not pass the host's token to MigrateAsync, and the default
// 30-second shutdown timeout would simply cut the same wound a little later; this gives the DDL room
// to complete. It only stretches how long a *deliberate* stop waits, and only while a migration is
// actually running — the job's ordinary lifetime is seconds.
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromMinutes(5));

var host = builder.Build();
host.Run();
