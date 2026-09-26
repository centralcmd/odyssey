// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test generated itself — there is no external input — and the deletes are deliberately
// issued outside the services, so the ENGINE resolves the foreign keys rather than application code.
#pragma warning disable EF1002

using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextPropertyFileType = Odyssey.Context.PropertyFileType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #210. AC 16: the purely additive migration, the four declared on-delete
/// behaviours observed firing at the engine (property and file <c>CASCADE</c>; issuer and attaching user
/// <c>SET NULL</c>), the property delete leaving the files themselves in place, and the unique
/// <c>(PropertyId, FileMetadataId)</c> index. AC 22: the list query never reads <c>FileBlob</c>.
/// </summary>
/// <remarks>
/// None of this is observable on the fast tiers: the EF InMemory provider enforces no foreign keys and
/// no unique indexes, runs no migrations, never invokes a <see cref="DbCommandInterceptor"/>, and
/// <c>ContactReferenceGuard</c>'s statements are relational-only.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class PropertyFileRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_property_files";
    private const string PreviousMigration = "_AddPropertyContractParties";
    private const string ThisMigration = "_AddPropertyFiles";
    private const string Attacher = "property-doc-attacher";

    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Migration and schema ─────────────────────────────────────────────────

    /// <summary>
    /// The migration applies to a populated database at the previous migration, adds exactly one table,
    /// leaves every existing table's columns and indexes as they were, and <c>Down()</c> removes it again.
    /// </summary>
    [SkippableFact]
    public async Task The_migration_is_purely_additive_and_reversible_over_a_populated_database()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, PreviousMigration);
            await context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO `Properties` (`PropertyId`, `Name`, `Description`, `Type`, `CurrencyCode`, `CreatedAt`, `UpdatedAt`)
                VALUES ('{Guid.NewGuid()}', 'Pre-existing', '', 0, 'USD', '2026-01-01', '2026-01-01')
                """);
        }

        Snapshot before;
        await using (var context = NewContext())
        {
            before = await SnapshotAsync(context);
            Assert.DoesNotContain("PropertyFiles", before.Tables);
            await context.Database.MigrateAsync();
            Assert.True(await MigrationSeam.HasRunAsync(context, ThisMigration));
        }

        await using (var context = NewContext())
        {
            var after = await SnapshotAsync(context);

            Assert.Equal(before.Tables.Append("PropertyFiles").OrderBy(t => t, StringComparer.Ordinal), after.Tables);
            Assert.Equal(before.Columns, after.Columns.Where(c => !c.StartsWith("PropertyFiles.", StringComparison.Ordinal)).ToList());
            Assert.Equal(before.Indexes, after.Indexes.Where(i => !i.StartsWith("PropertyFiles.", StringComparison.Ordinal)).ToList());
            Assert.Equal(1L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `Properties`"));

            await MigrationSeam.MigrateToAsync(context, PreviousMigration);
        }

        await using (var context = NewContext())
        {
            Assert.False(await MigrationSeam.TableExistsAsync(context, "PropertyFiles"));
            Assert.Equal(before.Tables, (await SnapshotAsync(context)).Tables);
        }
    }

    /// <summary>Every foreign key the table carries has the on-delete behaviour issue #210 §4 names.</summary>
    [SkippableFact]
    public async Task Every_foreign_key_carries_the_declared_on_delete_behaviour_and_the_pair_is_unique()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var rules = await context.Database
            .SqlQuery<ForeignKeyRule>($"""
                SELECT k.COLUMN_NAME AS ColumnName, k.REFERENCED_TABLE_NAME AS ReferencedTable, r.DELETE_RULE AS DeleteRule
                FROM information_schema.KEY_COLUMN_USAGE k
                JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                  ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA
                 AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                WHERE k.CONSTRAINT_SCHEMA = DATABASE()
                  AND k.TABLE_NAME = 'PropertyFiles'
                ORDER BY k.COLUMN_NAME
                """)
            .ToListAsync();

        Assert.Equal(
        [
            new ForeignKeyRule("AttachedByUserId", "AspNetUsers", "SET NULL"),
            new ForeignKeyRule("FileMetadataId", "FileMetadata", "CASCADE"),
            new ForeignKeyRule("IssuedBy", "Contacts", "SET NULL"),
            new ForeignKeyRule("PropertyId", "Properties", "CASCADE"),
        ], rules);

        var unique = await context.Database.SqlQuery<string>($"""
            SELECT CONCAT(COLUMN_NAME, ':', NON_UNIQUE) AS Value
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = 'PropertyFiles'
              AND INDEX_NAME = 'IX_PropertyFiles_PropertyId_FileMetadataId'
            ORDER BY SEQ_IN_INDEX
            """).ToListAsync();
        Assert.Equal(["PropertyId:0", "FileMetadataId:0"], unique);
    }

    // ── AC 16: the constraints firing at the engine ──────────────────────────

    /// <summary>
    /// Deleting a property at the ENGINE takes its document links with it, leaves another property's link
    /// to the same file alone, and leaves the file's metadata and bytes in place.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_property_cascades_its_links_and_keeps_the_file_and_its_blob()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid doomed, surviving, fileId;
        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, Attacher);
            doomed = await SeedPropertyAsync(context, "Doomed");
            surviving = await SeedPropertyAsync(context, "Survivor");
            fileId = await SeedFileAsync(context);
            await SeedLinkAsync(context, doomed, fileId);
            await SeedLinkAsync(context, surviving, fileId);

            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `Properties` WHERE `PropertyId` = '{doomed}'");
        }

        await using var verify = NewContext();
        Assert.False(await verify.PropertyFiles.AnyAsync(f => f.PropertyId == doomed));
        Assert.True(await verify.PropertyFiles.AnyAsync(f => f.PropertyId == surviving));
        var metadata = await verify.FileMetadata.AsNoTracking().SingleAsync(f => f.Id == fileId);
        Assert.True(await verify.FileBlob.AnyAsync(b => b.Id == metadata.FileBlobId));
    }

    /// <summary>AC 17 on the real provider: the service delete removes the links and keeps the file.</summary>
    [SkippableFact]
    public async Task The_service_delete_removes_the_links_and_keeps_the_file()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid propertyId, fileId;
        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, Attacher);
            propertyId = await SeedPropertyAsync(context, "Doomed");
            fileId = await SeedFileAsync(context);
            await Service(context).AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = fileId }, Attacher);

            Assert.True(await new PropertyService(context).Delete(propertyId, userId: null));
        }

        await using var verify = NewContext();
        Assert.False(await verify.Properties.AnyAsync());
        Assert.False(await verify.PropertyFiles.AnyAsync());
        Assert.True(await verify.FileMetadata.AnyAsync(f => f.Id == fileId));
        Assert.Equal(1, await verify.FileBlob.CountAsync());
    }

    [SkippableFact]
    public async Task Deleting_the_file_cascades_the_link_and_keeps_the_property()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid propertyId, fileId;
        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, Attacher);
            propertyId = await SeedPropertyAsync(context, "Storgata 14");
            fileId = await SeedFileAsync(context);
            await SeedLinkAsync(context, propertyId, fileId);

            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `FileMetadata` WHERE `Id` = '{fileId}'");
        }

        await using var verify = NewContext();
        Assert.False(await verify.PropertyFiles.AnyAsync());
        Assert.True(await verify.Properties.AnyAsync(p => p.PropertyId == propertyId));
    }

    /// <summary>
    /// The guard statement bypassed entirely, so the DATABASE is what resolves the contact delete: the
    /// issuer is nulled and the document kept, and the delete is neither refused nor cascading.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_the_issuing_contact_directly_nulls_the_issuer_and_keeps_the_document()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid linkId, contactId;
        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, Attacher);
            contactId = await SeedContactAsync(context);
            linkId = await SeedLinkAsync(context, await SeedPropertyAsync(context, "Storgata 14"), await SeedFileAsync(context), contactId);

            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `Contacts` WHERE `ContactId` = '{contactId}'");
        }

        await using var verify = NewContext();
        var row = await verify.PropertyFiles.AsNoTracking().SingleAsync(f => f.PropertyFileId == linkId);
        Assert.Null(row.IssuedBy);
        Assert.Equal(Anchor, row.ValidFrom);
        Assert.Equal(Anchor.AddYears(1), row.ValidTo);
        Assert.False(await verify.Contacts.AnyAsync(c => c.ContactId == contactId));
    }

    /// <summary>
    /// The same through <see cref="ContactReferenceGuard"/>'s clear-and-cascade path, which runs inside the
    /// contact-delete transaction before the contact row goes.
    /// </summary>
    [SkippableFact]
    public async Task The_reference_guard_nulls_the_issuer_and_keeps_the_link_row()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid linkId, contactId;
        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, Attacher);
            contactId = await SeedContactAsync(context);
            linkId = await SeedLinkAsync(context, await SeedPropertyAsync(context, "Storgata 14"), await SeedFileAsync(context), contactId);

            await new ContactReferenceGuard(context).ClearAndCascadeReferencesAsync(contactId);
        }

        await using var verify = NewContext();
        var row = await verify.PropertyFiles.AsNoTracking().SingleAsync(f => f.PropertyFileId == linkId);
        Assert.Null(row.IssuedBy);
        Assert.Equal(Anchor, row.ValidFrom);
        Assert.True(await verify.Contacts.AnyAsync(c => c.ContactId == contactId));
    }

    [SkippableFact]
    public async Task Deleting_the_attaching_user_nulls_the_attribution_and_keeps_the_document()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid linkId;
        await using (var context = NewContext())
        {
            // The file is uploaded by someone else, so the user delete cannot reach the link through the
            // FileMetadata.UploadedByUserId key instead.
            await AttributionUsers.EnsureAsync(context, Attacher, "property-doc-uploader");
            linkId = await SeedLinkAsync(
                context, await SeedPropertyAsync(context, "Storgata 14"), await SeedFileAsync(context, "property-doc-uploader"));

            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `AspNetUsers` WHERE `Id` = '{Attacher}'");
        }

        await using var verify = NewContext();
        var row = await verify.PropertyFiles.AsNoTracking().SingleAsync(f => f.PropertyFileId == linkId);
        Assert.Null(row.AttachedByUserId);
        Assert.Equal(ContextPropertyFileType.Deed, row.FileType);
        Assert.Equal(Anchor, row.AttachedAtUtc);
    }

    /// <summary>
    /// The unique index is the backstop the service pre-check cannot be: a second row for one pair written
    /// straight through EF, past the service, is refused by the engine.
    /// </summary>
    [SkippableFact]
    public async Task The_unique_index_rejects_a_second_link_for_the_same_pair()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid propertyId, fileId;
        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, Attacher);
            propertyId = await SeedPropertyAsync(context, "Storgata 14");
            fileId = await SeedFileAsync(context);
            await SeedLinkAsync(context, propertyId, fileId);
        }

        await using (var context = NewContext())
        {
            context.PropertyFiles.Add(NewLink(propertyId, fileId));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await using var verify = NewContext();
        Assert.Equal(1, await verify.PropertyFiles.CountAsync());
    }

    /// <summary>Two concurrent attaches of one pair race to the unique index; exactly one row lands.</summary>
    [SkippableFact]
    public async Task Two_concurrent_attaches_of_the_same_pair_yield_exactly_one_row()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid propertyId, fileId;
        await using (var seed = NewContext())
        {
            await AttributionUsers.EnsureAsync(seed, Attacher);
            propertyId = await SeedPropertyAsync(seed, "Storgata 14");
            fileId = await SeedFileAsync(seed);
        }

        await using var first = NewContext();
        await using var second = NewContext();

        var outcomes = await Task.WhenAll(
            AttemptAsync(Service(first), propertyId, fileId),
            AttemptAsync(Service(second), propertyId, fileId));

        Assert.Equal(1, outcomes.Count(succeeded => succeeded));
        await using var verify = NewContext();
        Assert.Equal(1, await verify.PropertyFiles.CountAsync());

        static async Task<bool> AttemptAsync(PropertyFileService service, Guid propertyId, Guid fileId)
        {
            try
            {
                await service.AttachFile(propertyId, new AttachPropertyFileRequest { FileMetadataId = fileId }, Attacher);
                return true;
            }
            catch (Exception exception) when (exception is DbUpdateException or DomainConflictException)
            {
                return false;
            }
        }
    }

    // ── AC 22: the list never reads the blob ─────────────────────────────────

    /// <summary>
    /// <see cref="PropertyFileService.GetFiles"/> loads <c>FileMetadata</c> and never the <c>FileBlob</c>
    /// table — asserted on the generated SQL, since a result DTO cannot show whether the bytes were
    /// materialised. The table is matched as a quoted identifier: <c>FileMetadata.FileBlobId</c> is
    /// legitimately selected and contains the table's name as a substring.
    /// </summary>
    [SkippableFact]
    public async Task Listing_a_propertys_documents_issues_no_read_of_FileBlob()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid propertyId;
        await using (var seed = NewContext())
        {
            await AttributionUsers.EnsureAsync(seed, Attacher);
            propertyId = await SeedPropertyAsync(seed, "Storgata 14");
            await SeedLinkAsync(seed, propertyId, await SeedFileAsync(seed));
            await SeedLinkAsync(seed, propertyId, await SeedFileAsync(seed));
        }

        var recorder = new CommandRecorder();
        await using (var context = NewContext(recorder))
        {
            var files = await Service(context).GetFiles(propertyId);

            Assert.Equal(2, files!.Count);
            Assert.All(files, f => Assert.Equal("deed.pdf", f.FileMetadata.FileName));
        }

        // Not vacuous: the recorder saw the list query itself, joined onto the metadata.
        Assert.Contains(recorder.Commands, sql => sql.Contains("`PropertyFiles`", StringComparison.Ordinal)
                                                  && sql.Contains("`FileMetadata`", StringComparison.Ordinal));
        Assert.DoesNotContain(recorder.Commands, sql => sql.Contains("`FileBlob`", StringComparison.Ordinal));

        // …and the detector does see the table when a query does read it.
        var control = new CommandRecorder();
        await using (var context = NewContext(control))
        {
            await context.FileMetadata.Include(f => f.FileBlob).ToListAsync();
        }

        Assert.Contains(control.Commands, sql => sql.Contains("`FileBlob`", StringComparison.Ordinal));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PropertyFileService Service(OdysseyContext context) =>
        new(context, new ContactLookup(context));

    private static async Task<Guid> SeedPropertyAsync(OdysseyContext context, string name) =>
        (await new PropertyService(context).Create(new NewProperty
        {
            Name = name,
            Description = "Residence",
            Type = PropertyType.RealEstate,
            CurrencyCode = "USD",
            RealEstateDetails = new RealEstateDetailsDto { Kind = RealEstateKind.House, City = "Oslo", CountryCode = "NO" },
        }, userId: null)).PropertyId;

    private static async Task<Guid> SeedFileAsync(OdysseyContext context, string uploaderId = Attacher)
    {
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = uploaderId,
            FileName = "deed.pdf",
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            FileBlobId = blob.Id,
            UploadedAtUtc = Anchor,
        };
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(metadata);
        await context.SaveChangesAsync();
        return metadata.Id;
    }

    private static async Task<Guid> SeedContactAsync(OdysseyContext context)
    {
        var contact = new Contact
        {
            ContactId = Guid.NewGuid(),
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "KARTVERKET",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "Kartverket" },
        };
        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return contact.ContactId;
    }

    private static PropertyFile NewLink(Guid propertyId, Guid fileId, Guid? issuedBy = null) => new()
    {
        PropertyFileId = Guid.NewGuid(),
        PropertyId = propertyId,
        FileMetadataId = fileId,
        FileType = ContextPropertyFileType.Deed,
        AttachedByUserId = Attacher,
        AttachedAtUtc = Anchor,
        ValidFrom = Anchor,
        ValidTo = Anchor.AddYears(1),
        IssuedAt = Anchor.AddDays(-14),
        IssuedBy = issuedBy,
    };

    private static async Task<Guid> SeedLinkAsync(OdysseyContext context, Guid propertyId, Guid fileId, Guid? issuedBy = null)
    {
        var link = NewLink(propertyId, fileId, issuedBy);
        context.PropertyFiles.Add(link);
        await context.SaveChangesAsync();
        return link.PropertyFileId;
    }

    private sealed record ForeignKeyRule(string ColumnName, string ReferencedTable, string DeleteRule);

    private sealed record Snapshot(List<string> Tables, List<string> Columns, List<string> Indexes);

    private static async Task<Snapshot> SnapshotAsync(OdysseyContext context)
    {
        var tables = await context.Database.SqlQuery<string>($"""
            SELECT TABLE_NAME AS Value FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> '__EFMigrationsHistory'
            ORDER BY BINARY TABLE_NAME
            """).ToListAsync();
        var columns = await context.Database.SqlQuery<string>($"""
            SELECT CONCAT_WS('.', TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY, IFNULL(COLUMN_DEFAULT, '<null>')) AS Value
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> '__EFMigrationsHistory'
            ORDER BY BINARY TABLE_NAME, ORDINAL_POSITION
            """).ToListAsync();
        var indexes = await context.Database.SqlQuery<string>($"""
            SELECT CONCAT_WS('.', TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX, COLUMN_NAME, NON_UNIQUE) AS Value
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> '__EFMigrationsHistory'
            ORDER BY BINARY TABLE_NAME, BINARY INDEX_NAME, SEQ_IN_INDEX
            """).ToListAsync();

        return new Snapshot(tables, columns, indexes);
    }

    /// <summary>Records the text of every command that reads, so the SQL itself can be asserted on.</summary>
    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> commands = new();

        public IReadOnlyCollection<string> Commands => commands;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            commands.Enqueue(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            commands.Enqueue(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    // ── Fixture plumbing ───────────────────────────────────────────────────────

    private async Task MigrateAsync()
    {
        await RecreateAsync();
        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private OdysseyContext NewContext(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString));
        if (interceptor is not null)
        {
            builder = builder.AddInterceptors(interceptor);
        }

        return new OdysseyContext(builder.Options);
    }

    private async Task RecreateAsync()
    {
        await using var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }
}
