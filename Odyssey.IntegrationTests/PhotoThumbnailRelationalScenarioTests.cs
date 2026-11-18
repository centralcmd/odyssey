// EF1002: the database name interpolated below is a Guid this test generated, not external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Tests.Thumbnails;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Every thumbnail job scenario of <c>Odyssey.Core.Tests</c>, rerun on real MariaDB (issue #362 AC 17, AC 24): the
/// claim, the renewal, the reclaim, the stale worker's give-back and the rename all race on <c>Generation</c> and the
/// rename's one save, which EF InMemory neither enforces nor makes atomic.
/// </summary>
[Collection(MariaDbCollection.Name)]
public sealed class PhotoThumbnailJobRelationalTests(MariaDbFixture fixture) : PhotoThumbnailJobScenarios
{
    private readonly RelationalScenarioDatabase database = new(fixture);

    protected override bool RelationalDatabase => true;

    protected override void UseDatabase(DbContextOptionsBuilder<OdysseyContext> options) => database.Use(options);

    protected override Task EnsureUserAsync(OdysseyContext context, string userId) => AttributionUsers.EnsureAsync(context, userId);
}

/// <summary>The rename and delete scenarios, rerun on real MariaDB.</summary>
[Collection(MariaDbCollection.Name)]
public sealed class PhotoThumbnailRenameRelationalTests(MariaDbFixture fixture) : PhotoThumbnailRenameScenarios
{
    private readonly RelationalScenarioDatabase database = new(fixture);

    protected override bool RelationalDatabase => true;

    protected override void UseDatabase(DbContextOptionsBuilder<OdysseyContext> options) => database.Use(options);

    protected override Task EnsureUserAsync(OdysseyContext context, string userId) => AttributionUsers.EnsureAsync(context, userId);
}

/// <summary>
/// One migrated database per test, created on first use. The skip happens there, inside the test body, so an absent
/// Docker daemon skips the inherited <c>[SkippableFact]</c> cases rather than failing them.
/// </summary>
internal sealed class RelationalScenarioDatabase(MariaDbFixture fixture)
{
    private readonly string name = $"thumbs_{Guid.NewGuid():N}"[..30];
    private bool created;

    public void Use(DbContextOptionsBuilder<OdysseyContext> options)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var version = new MariaDbServerVersion(new Version(11, 4));
        if (!created)
        {
            // The scenarios configure their context synchronously; Task.Run keeps the copy off any test synchronization context.
            Task.Run(() => fixture.CreateMigratedDatabaseAsync(name)).GetAwaiter().GetResult();
            created = true;
        }

        options.UseMySql(fixture.ConnectionStringFor(name), version);
    }
}
