using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The committed model snapshot agrees with the model (issue #52 AC #4).
/// </summary>
/// <remarks>
/// Its own class, deliberately WITHOUT <c>[Collection(MariaDbCollection.Name)]</c>: joining the
/// collection would construct <see cref="MariaDbFixture"/>, which needs a Docker daemon, and this
/// assertion needs none. It lives in this project rather than a fast tier only because it resolves
/// <c>IMigrator</c> through a relational service and so throws on EF InMemory.
/// </remarks>
public class ModelSnapshotDriftTests
{
    /// <summary>
    /// AC #4 — the model snapshot was regenerated with the migration, not left stale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things about this assertion are easy to get wrong, and all three of the obvious spellings
    /// are wrong:
    /// </para>
    /// <para>
    /// <b>The API is synchronous.</b> EF Core is pinned to 9.0.19 (held at 9.x by the Pomelo 9.0.0
    /// floor), which ships <c>HasPendingModelChanges()</c> with no <c>…Async</c> overload — calling
    /// <c>HasPendingModelChangesAsync()</c> is a compile error.
    /// </para>
    /// <para>
    /// <b>It is not a schema comparison.</b> It diffs the migrations assembly's snapshot against the
    /// design-time model and never opens a connection. A test written as "reflect the live schema and
    /// compare" would not catch a stale snapshot, which is the exact defect this exists for.
    /// </para>
    /// <para>
    /// <b>It must not sit behind the Docker gate.</b> It resolves <c>IMigrator</c> through a relational
    /// service, so it throws on EF InMemory and cannot live in the three fast tiers — but it needs no
    /// container. Behind <c>Skip.IfNot(fixture.Available)</c> it would silently never run without
    /// Docker, so it is a plain <c>[Fact]</c> on a pinned server version and a connection string it
    /// never dials.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_model_and_the_committed_snapshot_agree()
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(
                "server=localhost;database=odyssey;user=odyssey;password=odyssey",
                new MySqlServerVersion(new Version(11, 4, 0)))
            .Options;

        using var context = new OdysseyContext(options);

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "OdysseyContextModelSnapshot.cs disagrees with the model. Scaffold a migration with "
            + "`dotnet ef migrations add …` — which regenerates the snapshot — rather than editing "
            + "either by hand; a stale snapshot leaves the model and the schema disagreeing with "
            + "nothing else in the repository to catch it.");
    }
}
