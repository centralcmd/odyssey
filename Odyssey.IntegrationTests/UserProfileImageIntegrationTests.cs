using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Profiles;
using Odyssey.TestData.Fixtures;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for the user profile picture (issue #94, AC 15–21, 40).
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is invisible to the fast tiers, in four ways. EF InMemory enforces <b>no foreign
/// keys at all</b>, so the <c>AspNetUsers → UserProfileImage → UserProfileImageBlob</c> cascade — the
/// mechanism the whole GDPR Art. 17 claim rests on — is unobservable there. It honours no
/// transactions, so "the blob and the metadata commit together" cannot be asserted. It enforces no
/// unique index, so the concurrent <i>first</i> upload that must be a <c>409</c> simply succeeds
/// twice. And it has no column types, so the <c>varchar(255)</c> the FK inherits is not a thing it
/// could disagree about.
/// </para>
/// <para>
/// <b>The two race criteria are separate tests on purpose.</b> Under the update-in-place write shape,
/// the unique index can only fire for two concurrent <i>first</i> uploads and the concurrency token
/// only for two concurrent <i>replaces</i> — so a single combined criterion could be declared passing
/// while the replace race silently lost a write.
/// </para>
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class UserProfileImageIntegrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_profile_images";
    private const string SubjectId = "profile-image-subject";

    // ── AC 15: the cascade is what makes erasure work ─────────────────────────────────────────────

    /// <summary>
    /// AC 15. Deleting the account removes <b>both</b> rows by cascade alone — no purge code anywhere.
    ///
    /// <para>
    /// This is what the inverted FK buys. In the shipped <c>FileMetadata</c>/<c>FileBlob</c> pair the
    /// <i>blob</i> is the principal, so the cascade runs blob → metadata and never the reverse — which
    /// is why <c>ContactAvatarRelease</c> has to remove both rows explicitly. Copying that shape here
    /// would have left every deleted user's facial image bytes as a permanent orphan with no row
    /// pointing at them, and nothing would have said so.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Deleting_the_account_removes_the_image_and_its_blob_by_cascade_alone()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            await SeedUserAsync(context, SubjectId);
            await ServiceFor(context).SetAsync(SubjectId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

            Assert.Equal(1, await context.UserProfileImages.CountAsync());
            Assert.Equal(1, await context.UserProfileImageBlobs.CountAsync());
        }

        await using (var context = new OdysseyContext(options))
        {
            // A BARE user delete — no image code involved at all, which is the point. The real
            // UserAdministrationService.DeleteAsync carries no purge code either, so whatever the
            // database does not cascade is simply left behind.
            var user = await context.Users.SingleAsync(candidate => candidate.Id == SubjectId);
            context.Users.Remove(user);
            await context.SaveChangesAsync();
        }

        await using (var context = new OdysseyContext(options))
        {
            Assert.Equal(0, await context.UserProfileImages.CountAsync());
            Assert.Equal(0, await context.UserProfileImageBlobs.CountAsync());
        }

        await DropAsync();
    }

    // ── AC 16: revalidation is the hot path, so it must not touch the blob ────────────────────────

    /// <summary>
    /// AC 16. Resolving the descriptor — everything a conditional request needs — issues <b>no</b>
    /// query touching <c>UserProfileImageBlobs</c>.
    ///
    /// <para>
    /// Under <c>no-cache</c> a return visit revalidates every time, so a descriptor read that
    /// materialised the <c>LONGBLOB</c> would make every one of those pay full cost for a response
    /// that carries no body. That is the entire reason the blob is a separate table.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Resolving_the_descriptor_issues_no_query_against_the_blob_table()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var seed = new OdysseyContext(options))
        {
            await SeedUserAsync(seed, SubjectId);
            await ServiceFor(seed).SetAsync(SubjectId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        }

        var recorder = new CommandRecorder();
        await using (var context = new OdysseyContext(OptionsFor(fixture.ConnectionStringFor(Database), recorder)))
        {
            var descriptor = await ServiceFor(context).GetDescriptorAsync(SubjectId);
            Assert.NotNull(descriptor);
        }

        Assert.NotEmpty(recorder.Commands);
        Assert.DoesNotContain(
            recorder.Commands,
            sql => sql.Contains("UserProfileImageBlobs", StringComparison.OrdinalIgnoreCase));

        // ...and it DOES join the identity table, which is the cost §10.6 accepts for the disabled
        // check. Asserted so the join is a known cost rather than a surprise on a 30 ms P50 path.
        Assert.Contains(recorder.Commands, sql => sql.Contains("AspNetUsers", StringComparison.OrdinalIgnoreCase));

        await DropAsync();
    }

    // ── AC 17, 18: two races, two mechanisms ──────────────────────────────────────────────────────

    /// <summary>
    /// AC 17 — the <b>unique-index</b> path. Two concurrent first uploads for a user with no picture
    /// leave exactly one row pair, and the loser gets the curated <c>409</c> rather than a raw
    /// duplicate-key error surfacing as a <c>500</c>.
    /// </summary>
    [SkippableFact]
    public async Task Two_concurrent_first_uploads_leave_one_row_pair_and_one_curated_conflict()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var seed = new OdysseyContext(options))
        {
            await SeedUserAsync(seed, SubjectId);
        }

        // Two contexts, so neither can see the other's change tracker — the database arbitrates.
        await using var first = new OdysseyContext(options);
        await using var second = new OdysseyContext(options);

        var outcomes = await Task.WhenAll(
            AttemptAsync(first, ContactImageFixtures.BaselineJpeg(), "image/jpeg"),
            AttemptAsync(second, ContactImageFixtures.BaselinePng(), "image/png"));

        var conflicts = outcomes.OfType<DomainConflictException>().ToList();
        Assert.Single(conflicts);

        // The CURATED message, not GlobalExceptionHandler's generic conflict text — a message written
        // only in a spec would never reach a user.
        Assert.Contains("profile picture", conflicts[0].Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = new OdysseyContext(options);
        Assert.Equal(1, await verify.UserProfileImages.CountAsync());
        Assert.Equal(1, await verify.UserProfileImageBlobs.CountAsync());

        await DropAsync();
    }

    /// <summary>
    /// AC 18 — the <b>concurrency-token</b> path, and a separate test on purpose. Under the
    /// update-in-place write shape the unique index <i>cannot</i> fire here, so a single combined
    /// criterion could be declared passing while this race silently lost a write: without the token
    /// both <c>UPDATE</c>s affect one row, both succeed, and the later simply wins.
    /// </summary>
    [SkippableFact]
    public async Task Two_concurrent_replaces_leave_one_row_pair_and_one_conflict()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var seed = new OdysseyContext(options))
        {
            await SeedUserAsync(seed, SubjectId);
            await ServiceFor(seed).SetAsync(SubjectId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        }

        await using var first = new OdysseyContext(options);
        await using var second = new OdysseyContext(options);

        // Both load the row (and so both capture the SAME original ImageVersion) before either saves.
        await first.UserProfileImages.Include(image => image.Blob).SingleAsync();
        await second.UserProfileImages.Include(image => image.Blob).SingleAsync();

        var outcomes = await Task.WhenAll(
            AttemptAsync(first, ContactImageFixtures.BaselinePng(), "image/png"),
            AttemptAsync(second, ContactImageFixtures.StillWebp(), "image/webp"));

        Assert.Single(outcomes.OfType<DomainConflictException>());

        await using var verify = new OdysseyContext(options);
        Assert.Equal(1, await verify.UserProfileImages.CountAsync());
        Assert.Equal(1, await verify.UserProfileImageBlobs.CountAsync());

        await DropAsync();
    }

    /// <summary>
    /// AC 40. Pairs with AC 18: that one proves the loser is <i>refused</i>; this proves the refusal
    /// did not already write bytes.
    ///
    /// <para>
    /// The blob write shares the metadata write's <c>SaveChangesAsync</c>. Split across two saves, the
    /// loser of a replace race would commit its <b>bytes</b> and then be refused on the
    /// <b>metadata</b> — leaving its image stored under the winner's <c>Sha256Hash</c>. Not a lost
    /// update but a silent integrity break: every subsequent conditional <c>GET</c> would then return
    /// <c>304</c> for content that changed, on a read path whose whole design rests on revalidation
    /// being correct.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task The_loser_of_a_replace_race_leaves_no_bytes_and_the_hash_still_describes_the_image()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var seed = new OdysseyContext(options))
        {
            await SeedUserAsync(seed, SubjectId);
            await ServiceFor(seed).SetAsync(SubjectId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        }

        await using (var first = new OdysseyContext(options))
        await using (var second = new OdysseyContext(options))
        {
            await first.UserProfileImages.Include(image => image.Blob).SingleAsync();
            await second.UserProfileImages.Include(image => image.Blob).SingleAsync();

            await Task.WhenAll(
                AttemptAsync(first, ContactImageFixtures.BaselinePng(), "image/png"),
                AttemptAsync(second, ContactImageFixtures.StillWebp(), "image/webp"));
        }

        await using var verify = new OdysseyContext(options);
        var row = await verify.UserProfileImages.AsNoTracking().SingleAsync();
        var blob = await verify.UserProfileImageBlobs.AsNoTracking().SingleAsync();

        // No orphaned blob content — one pair, keyed together.
        Assert.Equal(row.UserProfileImageId, blob.UserProfileImageId);
        Assert.Equal(1, await verify.UserProfileImageBlobs.CountAsync());

        // And the ETag actually describes the stored bytes. This is the assertion the split-save defect
        // would fail while everything else stayed green.
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blob.Content)).ToLowerInvariant();
        Assert.Equal(actual, row.Sha256Hash);
        Assert.Equal(blob.Content.LongLength, row.SizeBytes);

        await DropAsync();
    }

    // ── AC 19: the ordinary replace path ──────────────────────────────────────────────────────────

    /// <summary>
    /// AC 19. A sequential replace never raises a duplicate-key error.
    ///
    /// <para>
    /// That is the <b>ordering hazard</b>, and it is self-inflicted rather than a race: a replace
    /// written as delete-then-insert touches a row constrained by the unique index on <c>UserId</c>,
    /// and EF does not guarantee DELETE-before-INSERT ordering within one <c>SaveChangesAsync</c>. So
    /// the failure would appear on the ordinary path, for every user, not under contention.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_sequential_replace_never_raises_a_duplicate_key_error()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            await SeedUserAsync(context, SubjectId);
            var service = ServiceFor(context);

            var first = await service.SetAsync(SubjectId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
            var second = await service.SetAsync(SubjectId, ContactImageFixtures.BaselinePng(), "image/png");
            var third = await service.SetAsync(SubjectId, ContactImageFixtures.StillWebp(), "image/webp");

            Assert.NotEqual(first, second);
            Assert.NotEqual(second, third);
        }

        await using (var verify = new OdysseyContext(options))
        {
            var row = await verify.UserProfileImages.AsNoTracking().SingleAsync();
            Assert.Equal("image/webp", row.ContentType);
            Assert.Equal(1, await verify.UserProfileImageBlobs.CountAsync());

            // UploadedAtUtc is PRESERVED across a replace; only UpdatedAtUtc is stamped.
            Assert.True(row.UpdatedAtUtc >= row.UploadedAtUtc);
        }

        await DropAsync();
    }

    // ── AC 20: the separation is a schema fact, not a convention ──────────────────────────────────

    /// <summary>
    /// AC 20. <b>Zero</b> foreign keys between the two identity-image tables and
    /// <c>FileMetadata</c>/<c>FileBlob</c>, in either direction.
    ///
    /// <para>
    /// The whole security boundary is that a <c>files.read</c> holder cannot read a person's face and
    /// <c>files.delete</c> cannot destroy it, and that <c>AdminFileExportService</c> — which enumerates
    /// <c>FileMetadata</c> and pulls matching blobs wholesale — cannot sweep one into an admin export.
    /// A single convenience FK added later would quietly undo that, and nothing else would notice.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task The_identity_image_tables_share_no_foreign_key_with_the_domain_file_store()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using var context = new OdysseyContext(options);

        var identityTables = new[] { "UserProfileImages", "UserProfileImageBlobs" };
        var fileTables = new[] { "FileMetadata", "FileBlob" };

        var crossings = new List<string>();
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            await context.Database.OpenConnectionAsync();
            command.CommandText = """
                SELECT TABLE_NAME, REFERENCED_TABLE_NAME, CONSTRAINT_NAME
                FROM information_schema.KEY_COLUMN_USAGE
                WHERE TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL
                """;

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var table = reader.GetString(0);
                var referenced = reader.GetString(1);

                var identityToFile = identityTables.Contains(table) && fileTables.Contains(referenced);
                var fileToIdentity = fileTables.Contains(table) && identityTables.Contains(referenced);
                if (identityToFile || fileToIdentity)
                {
                    crossings.Add($"{reader.GetString(2)}: {table} → {referenced}");
                }
            }
        }

        Assert.True(crossings.Count == 0,
            "A foreign key crosses the identity-image / domain-file boundary, which is the security "
            + "boundary the separate tables exist for: " + string.Join(", ", crossings));

        await DropAsync();
    }

    // ── AC 21: the FK column is no wider than the key it references ───────────────────────────────

    /// <summary>
    /// AC 21. <c>UserProfileImages.UserId</c> materialises as <c>varchar(255)</c>, matching
    /// <c>AspNetUsers.Id</c> and <c>UserProfiles.UserId</c>.
    ///
    /// <para>
    /// <c>MaxLength(450)</c> is the SQL Server <c>nvarchar(450)</c> Identity convention and does not
    /// apply to this provider. Adding it would make this the only FK-to-<c>AspNetUsers</c> column wider
    /// than the key it references — which on MariaDB also costs index bytes for nothing.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task The_user_id_column_matches_the_identity_key_it_references()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using var context = new OdysseyContext(options);
        await context.Database.OpenConnectionAsync();

        var widths = new Dictionary<string, long?>(StringComparer.Ordinal);
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = """
                SELECT CONCAT(TABLE_NAME, '.', COLUMN_NAME), CHARACTER_MAXIMUM_LENGTH
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND ((TABLE_NAME = 'UserProfileImages' AND COLUMN_NAME = 'UserId')
                    OR (TABLE_NAME = 'UserProfiles' AND COLUMN_NAME = 'UserId')
                    OR (TABLE_NAME = 'AspNetUsers' AND COLUMN_NAME = 'Id'))
                """;

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                widths[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
        }

        Assert.Equal(255, widths["AspNetUsers.Id"]);
        Assert.Equal(255, widths["UserProfiles.UserId"]);
        Assert.Equal(255, widths["UserProfileImages.UserId"]);
        Assert.NotEqual(450, widths["UserProfileImages.UserId"]);

        await DropAsync();
    }

    /// <summary>
    /// The unique index is what turns a concurrent first upload into a <c>409</c> rather than two rows,
    /// so it is asserted directly rather than inferred from the race above passing.
    /// </summary>
    [SkippableFact]
    public async Task The_user_id_column_carries_a_unique_index()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using var context = new OdysseyContext(options);
        await context.Database.OpenConnectionAsync();

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = 'UserProfileImages'
              AND COLUMN_NAME = 'UserId'
              AND NON_UNIQUE = 0
            """;

        Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync()) > 0,
            "UserProfileImages.UserId has no unique index; two concurrent first uploads would leave two rows.");

        await DropAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs one write and hands back the exception it produced, or <c>null</c> on success.</summary>
    private static async Task<Exception?> AttemptAsync(OdysseyContext context, byte[] bytes, string contentType)
    {
        try
        {
            await ServiceFor(context).SetAsync(SubjectId, bytes, contentType);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static UserProfileImageService ServiceFor(OdysseyContext context) =>
        new(context,
            new FixedUploadLimits(64L * 1024 * 1024),
            logger: NullLogger<UserProfileImageService>.Instance);

    /// <summary>
    /// Seeds an identity row, then clears its lockout in a second save: <c>OdysseyContext</c> stamps
    /// the admin-approval sentinel onto every newly-added user, and a disabled subject's picture reads
    /// as absent by design.
    /// </summary>
    private static async Task SeedUserAsync(OdysseyContext context, string userId)
    {
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = $"{userId}@example.com",
            NormalizedUserName = $"{userId}@EXAMPLE.COM",
            Email = $"{userId}@example.com",
            NormalizedEmail = $"{userId}@EXAMPLE.COM",
        };

        context.Users.Add(user);
        await context.SaveChangesAsync();

        user.LockoutEnd = null;
        await context.SaveChangesAsync();
    }

    private async Task<DbContextOptions<OdysseyContext>> MigratedSchemaAsync()
    {
        await DropAsync();

        await using (var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString)))
        {
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        var options = OptionsFor(fixture.ConnectionStringFor(Database));
        await using (var context = new OdysseyContext(options))
        {
            await context.Database.MigrateAsync();
        }

        return options;
    }

    private async Task DropAsync()
    {
        await using var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString));
        await admin.Database.ExecuteSqlRawAsync("DROP DATABASE IF EXISTS `" + Database + "`");
    }

    private static DbContextOptions<OdysseyContext> OptionsFor(
        string connectionString, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));

        return interceptor is null ? builder.Options : builder.AddInterceptors(interceptor).Options;
    }

    private sealed class FixedUploadLimits(long maxBytes) : IUploadLimitsLookup
    {
        public Task<UploadLimits> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new UploadLimits(maxBytes, (int)(maxBytes / (1024 * 1024)), IsDegraded: false));
    }

    /// <summary>Records the SQL every command carries, so "the blob was not read" is an assertion.</summary>
    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result)
        {
            Commands.Add(result.CommandText);
            return base.CommandCreated(eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
