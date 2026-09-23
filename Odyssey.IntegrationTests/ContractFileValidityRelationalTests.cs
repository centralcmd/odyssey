// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test generated itself — there is no external input — and the deletes and pre-migration
// inserts are deliberately issued outside the services, which is the whole point: the foreign key
// exists so the engine, and not application code, is what resolves it.
#pragma warning disable EF1002

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Xunit;
using ContextContractType = Odyssey.Context.ContractType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #146 (AC 13, 14): the <c>ContractFiles.IssuedBy</c> foreign key's
/// <c>SET NULL</c> behaviour, the reference guard's matching statement, and the migration applying to
/// a database that already holds <c>ContractFile</c> rows.
/// </summary>
/// <remarks>
/// None of this is observable on the fast tiers. The EF InMemory provider enforces no foreign keys at
/// all, so AC 13 would pass vacuously there; and <c>ContactReferenceGuard</c>'s statements are
/// <c>ExecuteUpdateAsync</c>/<c>ExecuteDeleteAsync</c>, which live in
/// <c>EntityFrameworkCore.Relational</c> and <b>throw</b> on InMemory.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractFileValidityRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_file_validity";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_AddContractSignatureDates";

    private const string Subject = "_AddContractFileValidity";

    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The key carries the behaviour §4 names, read back from the engine rather than inferred from the
    /// model. <c>SET NULL</c> is explicit and load-bearing: EF's default for an optional relationship
    /// is <c>ClientSetNull</c>, which emits <c>RESTRICT</c> — under which any contact that ever issued
    /// a contract document would become undeletable.
    /// </summary>
    [SkippableFact]
    public async Task The_issuer_foreign_key_is_SET_NULL_and_its_index_exists()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();

        Assert.Equal("SET NULL", (await ReadDeleteRulesAsync(context))["IssuedBy"]);
        Assert.Equal(["IssuedBy"], await ReadIndexColumnsAsync(context, "IX_ContractFiles_IssuedBy"));
    }

    /// <summary>
    /// AC 13, first half — through <see cref="ContactReferenceGuard"/>'s clear-and-cascade path, which
    /// is what runs inside the contact-delete transaction and is the only implementation the
    /// non-relational tiers would ever see.
    /// </summary>
    [SkippableFact]
    public async Task The_reference_guard_nulls_the_issuer_and_keeps_the_link_row()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid contractFileId;
        Guid contactId;

        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, "doc-attacher");
            contactId = await SeedContactAsync(context);
            var contractId = await SeedContractAsync(context, "Maple St lease");
            contractFileId = await SeedContractFileAsync(context, contractId, "doc-attacher", contactId);

            await new ContactReferenceGuard(context).ClearAndCascadeReferencesAsync(contactId);
        }

        await using (var verify = NewContext())
        {
            var row = await verify.ContractFiles.AsNoTracking()
                .SingleAsync(f => f.ContractFileId == contractFileId);

            Assert.Null(row.IssuedBy);
            // The document is the record; the issuer is an optional annotation on it.
            Assert.Equal(Anchor, row.ValidFrom);
            Assert.True(await verify.Contacts.AnyAsync(c => c.ContactId == contactId));
        }
    }

    /// <summary>
    /// AC 13, second half — with the guard statement bypassed entirely, so the DATABASE is what
    /// resolves the delete. Raw SQL, so no EF fixup and no service sweep can be what nulls the column:
    /// the constraint is observed firing, and the contact delete is neither refused nor cascading.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_the_issuing_contact_directly_nulls_the_column_and_keeps_the_document()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid contractFileId;
        Guid contactId;

        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, "doc-attacher");
            contactId = await SeedContactAsync(context);
            var contractId = await SeedContractAsync(context, "Maple St lease");
            contractFileId = await SeedContractFileAsync(context, contractId, "doc-attacher", contactId);

            await context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM `Contacts` WHERE `ContactId` = '{contactId}'");
        }

        await using (var verify = NewContext())
        {
            var row = await verify.ContractFiles.AsNoTracking()
                .SingleAsync(f => f.ContractFileId == contractFileId);

            Assert.Null(row.IssuedBy);
            Assert.Equal(Anchor, row.ValidFrom);
            Assert.Equal(Anchor.AddYears(1), row.ValidTo);
            Assert.False(await verify.Contacts.AnyAsync(c => c.ContactId == contactId));
        }
    }

    /// <summary>
    /// AC 14 — the migration applies to a database already populated with <c>ContractFile</c> rows.
    /// Every pre-existing row reads back with <c>NULL</c> in all four columns, and its other columns
    /// unchanged: all four are nullable, so <c>NULL</c> throughout is the correct, healthy state for a
    /// document attached before this feature existed. Nothing is seeded and nothing is backfilled.
    /// </summary>
    [SkippableFact]
    public async Task The_migration_applies_over_pre_existing_rows_and_leaves_them_null()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contractFileId = Guid.NewGuid();
        var attachedAt = new DateTime(2025, 11, 3, 14, 22, 5, DateTimeKind.Utc);

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                Assert.False(await ColumnExistsAsync(context, "ValidFrom"));
                Assert.False(await ColumnExistsAsync(context, "IssuedBy"));

                await AttributionUsers.EnsureAsync(context, "doc-attacher");
                var contractId = await SeedBaselineContractAsync(context, "Pre-existing agreement");
                var fileId = await SeedFileMetadataAsync(context, "doc-attacher");

                // Raw SQL, so the row is written exactly as one existed before the columns did.
                await context.Database.ExecuteSqlRawAsync($"""
                    INSERT INTO `ContractFiles`
                        (`ContractFileId`, `ContractId`, `FileMetadataId`, `FileType`,
                         `AttachedByUserId`, `AttachedAtUtc`)
                    VALUES
                        ('{contractFileId}', '{contractId}', '{fileId}', 1, 'doc-attacher',
                         '{attachedAt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}');
                    """);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                Assert.True(await MigrationSeam.HasRunAsync(context, Subject));

                var row = await context.ContractFiles.AsNoTracking()
                    .SingleAsync(f => f.ContractFileId == contractFileId);

                Assert.Null(row.ValidFrom);
                Assert.Null(row.ValidTo);
                Assert.Null(row.IssuedAt);
                Assert.Null(row.IssuedBy);

                // Its other columns are untouched — the migration is purely additive.
                Assert.Equal(ContractFileType.Amendment, row.FileType);
                Assert.Equal("doc-attacher", row.AttachedByUserId);
                Assert.Equal(attachedAt, row.AttachedAtUtc);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedContactAsync(OdysseyContext context)
    {
        var contact = new Contact
        {
            ContactId = Guid.NewGuid(),
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "THE LANDLORD",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "The landlord" },
        };
        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return contact.ContactId;
    }

    private static async Task<Guid> SeedContractAsync(OdysseyContext context, string name)
    {
        var contract = new Contract
        {
            ContractId = Guid.NewGuid(),
            Name = name,
            Type = ContextContractType.Rental,
            StartDate = Anchor,
            CreatedAtUtc = Anchor,
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    /// <summary>
    /// A contract written as raw SQL naming only the columns the baseline has. Through EF it would
    /// name every column the CURRENT model maps, and a column added by a later migration
    /// (<c>ReferenceNumber</c>, issue #181) fails with <c>Unknown column … in 'INSERT INTO'</c> on
    /// a schema that predates it — the trap <c>BaselineContacts</c> records for contacts.
    /// </summary>
    private static async Task<Guid> SeedBaselineContractAsync(OdysseyContext context, string name)
    {
        var contractId = Guid.NewGuid();
        var anchor = Anchor.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `Contracts` (`ContractId`, `Name`, `Type`, `StartDate`, `CreatedAtUtc`) VALUES ({0}, {1}, {2}, {3}, {4})",
            contractId, name, (int)ContextContractType.Rental, anchor, anchor);
        return contractId;
    }

    private static async Task<Guid> SeedFileMetadataAsync(OdysseyContext context, string uploaderId)
    {
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = uploaderId,
            FileName = "agreement.pdf",
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

    private static async Task<Guid> SeedContractFileAsync(
        OdysseyContext context, Guid contractId, string attacherId, Guid issuedBy)
    {
        var link = new ContractFile
        {
            ContractFileId = Guid.NewGuid(),
            ContractId = contractId,
            FileMetadataId = await SeedFileMetadataAsync(context, attacherId),
            FileType = ContractFileType.Signed,
            AttachedByUserId = attacherId,
            AttachedAtUtc = Anchor,
            ValidFrom = Anchor,
            ValidTo = Anchor.AddYears(1),
            IssuedAt = Anchor.AddDays(-14),
            IssuedBy = issuedBy,
        };
        context.ContractFiles.Add(link);
        await context.SaveChangesAsync();
        return link.ContractFileId;
    }

    private static async Task<bool> ColumnExistsAsync(OdysseyContext context, string column) =>
        await MigrationSeam.CountAsync(context, $"""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'ContractFiles' AND COLUMN_NAME = '{column}'
            """) > 0;

    private static async Task<List<string>> ReadIndexColumnsAsync(OdysseyContext context, string indexName)
    {
        var rows = await context.Database
            .SqlQuery<IndexColumn>($"""
                SELECT COLUMN_NAME AS ColumnName
                FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'ContractFiles'
                  AND INDEX_NAME = {indexName}
                ORDER BY SEQ_IN_INDEX
                """)
            .ToListAsync();

        return [.. rows.Select(row => row.ColumnName)];
    }

    private static async Task<Dictionary<string, string>> ReadDeleteRulesAsync(OdysseyContext context)
    {
        var rows = await context.Database
            .SqlQuery<ForeignKeyRule>($"""
                SELECT k.COLUMN_NAME AS ColumnName, r.DELETE_RULE AS DeleteRule
                FROM information_schema.KEY_COLUMN_USAGE k
                JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                  ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA
                 AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                WHERE k.CONSTRAINT_SCHEMA = DATABASE()
                  AND k.TABLE_NAME = 'ContractFiles'
                """)
            .ToListAsync();

        return rows.ToDictionary(row => row.ColumnName, row => row.DeleteRule, StringComparer.Ordinal);
    }

    private sealed record IndexColumn(string ColumnName);

    private sealed record ForeignKeyRule(string ColumnName, string DeleteRule);

    // ── Fixture plumbing ───────────────────────────────────────────────────────

    private async Task MigrateAsync()
    {
        await RecreateAsync();
        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

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
