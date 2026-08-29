namespace Odyssey.MigrationService;

/// <summary>
/// One step of the migrations job. <see cref="Worker"/> runs the six below in a fixed order that it
/// spells out explicitly, because the order carries meaning the sequence alone would not explain.
/// </summary>
/// <remarks>
/// <para>
/// The six role interfaces exist so <see cref="Worker"/> depends on abstractions its tests can
/// substitute. They are deliberately distinct types rather than one <c>IEnumerable&lt;IMigrationStep&gt;</c>:
/// resolving a collection would make registration order in <c>Program.cs</c> the thing that decides
/// execution order, which is both invisible at the call site and beyond the reach of
/// <c>WorkerTests</c>. Keeping named dependencies leaves the order — and the comments explaining
/// why the bootstrap seed precedes the demo seed — where a reader and a test can both see it.
/// </para>
/// <para>
/// They also keep the implementations <c>sealed</c>. The earlier seam was a <c>public virtual</c>
/// <c>ExecuteAsync</c> on each concrete class, which was the only such member in the backend and
/// obliged every test double to subclass a production type and forward constructor arguments it had
/// no use for.
/// </para>
/// </remarks>
public interface IMigrationStep
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>Migrates <c>OdysseyContext</c> — the whole schema: identity and auth alongside finance,
/// journal, photos, calendars and contacts.</summary>
public interface IOdysseyMigrationService : IMigrationStep;

/// <summary>
/// Reconciles <c>AspNetRoleClaims</c> with the server-side role-to-claim mapping, replacing the
/// positional <c>HasData</c> seed that made every claim addition a hand-written migration.
/// </summary>
public interface IRoleClaimSeeder : IMigrationStep;

/// <summary>
/// Carries an operator's existing configuration into the settings store for settings that used to be
/// config-driven (issue #421 Wave 2). Runs in Production — see the implementation's remarks.
/// </summary>
public interface ISystemSettingsConfigAdoption : IMigrationStep;

/// <summary>Creates the initial administrator from configuration on an empty user table (issue #290).</summary>
public interface IBootstrapAdminSeeder : IMigrationStep;

/// <summary>Seeds the deterministic demo dataset (Development/Testing only, idempotent).</summary>
public interface IDemoDataSeeder : IMigrationStep;

/// <summary>Fails the job unless an enabled administrator exists once seeding is done (issue #290).</summary>
public interface IAdministratorAssertion : IMigrationStep;
