// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test generated itself — there is no external input — and the deletes are deliberately
// issued outside the services, which is the whole point: the foreign key exists so the engine, and not
// application code, is what resolves it.
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
/// The relational half of issue #146 (AC 13): the <c>ContractFiles.IssuedBy</c> foreign key's
/// <c>SET NULL</c> behaviour and the reference guard's matching statement.
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
    /// Issue #309 AC 9 — the appended <c>Draft</c> ordinal round-trips through the real engine with no
    /// migration: the column is a plain <c>int</c> with no CHECK constraint, so <c>4</c> is stored and
    /// read back as itself. Read raw as well as through EF, so a value converter cannot be what agrees.
    /// </summary>
    [SkippableFact]
    public async Task The_draft_file_type_round_trips_as_four()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid contractFileId;

        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, "doc-attacher");
            var contractId = await SeedContractAsync(context, "Draft lease");
            var link = new ContractFile
            {
                ContractFileId = Guid.NewGuid(),
                ContractId = contractId,
                FileMetadataId = await SeedFileMetadataAsync(context, "doc-attacher"),
                FileType = ContractFileType.Draft,
                AttachedByUserId = "doc-attacher",
                AttachedAtUtc = Anchor,
            };
            context.ContractFiles.Add(link);
            await context.SaveChangesAsync();
            contractFileId = link.ContractFileId;
        }

        await using (var verify = NewContext())
        {
            var row = await verify.ContractFiles.AsNoTracking()
                .SingleAsync(f => f.ContractFileId == contractFileId);
            Assert.Equal(ContractFileType.Draft, row.FileType);

            Assert.Equal(4, Convert.ToInt32(await MigrationSeam.ScalarAsync(verify, $"""
                SELECT `FileType` FROM `ContractFiles` WHERE `ContractFileId` = '{contractFileId}'
                """), CultureInfo.InvariantCulture));
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

    private static async Task<Guid> SeedFileMetadataAsync(OdysseyContext context, string uploaderId)
    {
        var id = Guid.NewGuid();
        await LegacyFileRows.InsertAsync(context, id, Anchor, "agreement.pdf", uploadedByUserId: uploaderId);
        return id;
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
        await fixture.CreateMigratedDatabaseAsync(Database);
    }

    // The seeded files are database-held (seeded below #448): the interceptor gives them the file move's end state just
    // before RemoveLegacyFileBlobStorage's guard, as the worker did between the two releases.
    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .AddInterceptors(new LegacyFileRows.FinishTheFileMoveBeforeTheGuard())
            .Options);

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
