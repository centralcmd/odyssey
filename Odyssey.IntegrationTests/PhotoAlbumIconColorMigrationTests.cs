using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// <c>AddPhotoAlbumIconAndColor</c> (issue #404) applied over a database that already holds albums, and the
/// album list's read normalisation on real MariaDB. The list projects the raw columns in SQL and
/// normalises them after materialisation; only a relational provider proves that split translates.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class PhotoAlbumIconColorMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_album_icon_color";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_AddTagColors";

    private const string Subject = "_AddPhotoAlbumIconAndColor";

    [SkippableFact]
    public async Task Existing_albums_read_no_icon_and_no_colour_and_stale_values_normalise_on_detail_and_list()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        var legacyId = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                Assert.Null(await ColumnAsync(context, "Icon"));
                Assert.Null(await ColumnAsync(context, "Color"));

                // Raw SQL: the entity already carries Icon/Color, which the baseline schema does not have.
                var now = DateTime.UtcNow;
                await context.Database.ExecuteSqlAsync(
                    $"INSERT INTO `PhotoAlbums` (`PhotoAlbumId`, `Name`, `CreatedAt`, `UpdatedAt`) VALUES ({legacyId}, {"Legacy"}, {now}, {now})");
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                Assert.True(await MigrationSeam.HasRunAsync(context, Subject));

                foreach (var (name, type) in new[] { ("Icon", "varchar(64)"), ("Color", "varchar(7)") })
                {
                    var column = await ColumnAsync(context, name);
                    Assert.NotNull(column);
                    Assert.Equal(type, column!["COLUMN_TYPE"]?.ToString());
                    Assert.Equal("YES", column["IS_NULLABLE"]?.ToString());
                    Assert.True(column["COLUMN_DEFAULT"] is null or "NULL");
                }

                // CreatedByUserId is a real FK (SET NULL), so the author must exist.
                context.Users.Add(new ApplicationUser { Id = "album-icon-user", UserName = "album@it.test", Email = "album@it.test" });
                await context.SaveChangesAsync();

                var service = new PhotoAlbumService(context, new Limits());
                var legacy = await service.Get(legacyId);
                Assert.Null(legacy!.Icon);
                Assert.Null(legacy.Color);

                var created = await service.Create(new NewPhotoAlbum { Name = "Lofoten", Icon = "landscape", Color = "#006b5a" }, "album-icon-user");
                Assert.Equal("#006B5A", created.Color);

                // A hand-edited row: a retired key and an invalid colour.
                await context.Database.ExecuteSqlAsync(
                    $"UPDATE `PhotoAlbums` SET `Icon` = {"retired_key"}, `Color` = {"#12345Z"} WHERE `PhotoAlbumId` = {legacyId}");
            }

            await using (var context = NewContext())
            {
                var service = new PhotoAlbumService(context, new Limits());
                var detail = await service.Get(legacyId);
                Assert.Null(detail!.Icon);
                Assert.Null(detail.Color);

                var rows = (await service.ListAsync(new AlbumsQueryParams())).Items;
                var legacyRow = Assert.Single(rows, r => r.PhotoAlbumId == legacyId);
                Assert.Null(legacyRow.Icon);
                Assert.Null(legacyRow.Color);
                var lofoten = Assert.Single(rows, r => r.Name == "Lofoten");
                Assert.Equal("landscape", lofoten.Icon);
                Assert.Equal("#006B5A", lofoten.Color);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    private sealed class Limits : IJournalLimitsLookup
    {
        public Task<JournalLimits> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new JournalLimits(
                PhotoMaxLinksPerKind: 50,
                PhotoMaxAlbumMembers: 500,
                JournalEntryMaxLinksPerKind: 50,
                JournalTaskMaxLinksPerKind: 50,
                PhotoMetadataReadBytes: 1024 * 1024,
                PhotoMetadataExtractionTimeoutSeconds: 5,
                CalendarMaxWindowDays: 92,
                CalendarMaxEventDurationDays: 366,
                RecurrenceMaxGeneratedOccurrences: 500,
                IsDegraded: false));
    }

    private static Task<Dictionary<string, object?>?> ColumnAsync(OdysseyContext context, string column) =>
        MigrationSeam.RowAsync(context, $"""
            SELECT COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'PhotoAlbums' AND COLUMN_NAME = '{column}'
            """);

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
