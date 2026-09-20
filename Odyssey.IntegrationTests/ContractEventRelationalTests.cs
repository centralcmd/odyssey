// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test generated itself — there is no external input — and the deletes are deliberately
// issued outside the services, which is the whole point: the foreign keys exist so the engine, and not
// application code, is what resolves them.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;
using Odyssey.Dtos;
using ContextContractType = Odyssey.Context.ContractType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #138 (AC 12, 13, 14, 15): the two foreign keys on
/// <c>ContractEvents</c> and the index the read path is served by.
/// </summary>
/// <remarks>
/// None of this is observable on the fast tiers — the EF InMemory provider enforces no foreign keys at
/// all, so a cascade there is whatever application code happens to do. The contract cascade has an
/// application-code twin, <c>ContractService.Delete</c>'s <c>.Include(c =&gt; c.Events)</c>, guarded on
/// that tier by
/// <c>ContractEventsApiTests.DeleteContract_RemovesItsEventsAndLeavesAnotherContractsUntouched</c>;
/// this is the other half, where the database does the work. The two are not redundant — either one
/// alone leaves the other tier free to regress silently. The user-attribution key is additionally
/// covered by name in <c>UserAttributionForeignKeyTests</c>, which reads <c>information_schema</c> over
/// the whole set at once; here it is observed firing.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractEventRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_events";

    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// AC 15 — the migration applies cleanly and the table it creates has the shape §4 names: one
    /// composite index on the read path's two columns, and no unique index (the same event type may
    /// legitimately occur many times on one contract, and there is no natural key).
    /// </summary>
    [SkippableFact]
    public async Task The_migration_creates_the_read_path_index_and_no_unique_one()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();

        Assert.Equal(
            ["ContractId", "OccurredAt"],
            await ReadIndexColumnsAsync(context, "IX_ContractEvents_ContractId_OccurredAt"));

        Assert.Empty(await ReadUniqueIndexNamesAsync(context));
    }

    /// <summary>
    /// AC 15 — both foreign keys carry the on-delete behaviour §4 names, read back from the engine
    /// rather than inferred from the model. CASCADE for the owner (an event is an owned child and dies
    /// with its contract, matching <c>ContractParty</c> and <c>ContractFile</c>); SET NULL for the
    /// author (the event is shared data that must survive its author's departure).
    /// </summary>
    [SkippableFact]
    public async Task Both_foreign_keys_carry_the_declared_on_delete_behaviour()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var rules = await ReadDeleteRulesAsync(context);

        Assert.Equal("CASCADE", rules["ContractId"]);
        Assert.Equal("SET NULL", rules["CreatedByUserId"]);
    }

    /// <summary>AC 12 — deleting the contract takes its whole log with it, and nothing else's.</summary>
    [SkippableFact]
    public async Task Deleting_a_contract_cascades_its_events_and_leaves_other_contracts_alone()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid doomedEvent, survivingEvent;

        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, "event-author");
            var doomedContract = await SeedContractAsync(context, "Maple St lease");
            var survivingContract = await SeedContractAsync(context, "Survivor");

            doomedEvent = await SeedEventAsync(context, doomedContract, "Signed", "event-author");
            survivingEvent = await SeedEventAsync(context, survivingContract, "Also signed", "event-author");

            await context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM `Contracts` WHERE `ContractId` = '{doomedContract}'");
        }

        await using (var verify = NewContext())
        {
            var remaining = await verify.ContractEvents.AsNoTracking()
                .Select(e => e.ContractEventId).ToListAsync();

            Assert.DoesNotContain(doomedEvent, remaining);
            Assert.Contains(survivingEvent, remaining);
        }
    }

    /// <summary>
    /// AC 13 — deleting the AUTHOR keeps the event and drops only the name. RESTRICT would make
    /// anyone who has ever recorded an event permanently undeletable, and CASCADE would destroy the
    /// household's shared record because one of the people who wrote in it left.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_the_author_nulls_the_attribution_and_keeps_the_event()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        const string AuthorId = "departing-author";
        Guid eventId;

        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, AuthorId);
            var contractId = await SeedContractAsync(context, "Maple St lease");
            eventId = await SeedEventAsync(context, contractId, "Rent renegotiated", AuthorId);

            // Raw SQL, so nothing in application code — no EF fixup, no service sweep — can be what
            // nulls the column. The constraint is observed firing, not declared.
            await context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM `AspNetUsers` WHERE `Id` = '{AuthorId}'");
        }

        await using (var verify = NewContext())
        {
            var stored = await verify.ContractEvents.AsNoTracking()
                .SingleAsync(e => e.ContractEventId == eventId);

            Assert.Equal("Rent renegotiated", stored.Title);
            Assert.Null(stored.CreatedByUserId);
        }
    }

    /// <summary>
    /// AC 14 — a contact an event merely NAMES in its free text is not a link, so deleting it succeeds
    /// and the event is untouched. Drafts v1–v4 gave an event four <c>RESTRICT</c> contact/account
    /// columns; v5 removed them, and with them every blocker and every change to the two delete paths.
    /// </summary>
    /// <remarks>
    /// This tier, and not <c>Odyssey.Api.Tests</c>, because a full contact delete is relational-only:
    /// <c>ContactReferenceGuard.ClearAndCascadeReferencesAsync</c> is written in
    /// <c>ExecuteDeleteAsync</c>, which throws on the EF InMemory provider. Deleting the row directly
    /// is the stronger assertion anyway — it shows the engine itself raises no constraint.
    /// </remarks>
    [SkippableFact]
    public async Task Deleting_a_contact_an_event_only_mentions_in_prose_is_not_blocked()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid eventId;

        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, "event-author");
            var contractId = await SeedContractAsync(context, "Maple St lease");
            var contactId = Guid.NewGuid();
            context.Contacts.Add(new Contact
            {
                ContactId = contactId,
                ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                NormalizedName = "THE LANDLORD",
                Type = ContactType.Organization,
                OrganizationDetails = new() { LegalName = "The landlord" },
            });
            await context.SaveChangesAsync();

            eventId = await SeedEventAsync(
                context, contractId, $"Emailed the landlord (contact {contactId})", "event-author");

            await context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM `Contacts` WHERE `ContactId` = '{contactId}'");
        }

        await using (var verify = NewContext())
        {
            Assert.True(await verify.ContractEvents.AsNoTracking().AnyAsync(e => e.ContractEventId == eventId));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private static async Task<Guid> SeedEventAsync(
        OdysseyContext context, Guid contractId, string title, string authorId)
    {
        var entity = new ContractEvent
        {
            ContractEventId = Guid.NewGuid(),
            ContractId = contractId,
            Type = ContractEventType.Signed,
            Title = title,
            Description = "Seeded for the relational assertions.",
            Notes = "Working note, exported and readable like the other two.",
            OccurredAt = Anchor,
            CreatedByUserId = authorId,
            CreatedAtUtc = Anchor,
        };
        context.ContractEvents.Add(entity);
        await context.SaveChangesAsync();
        return entity.ContractEventId;
    }

    private static async Task<List<string>> ReadIndexColumnsAsync(OdysseyContext context, string indexName)
    {
        var rows = await context.Database
            .SqlQuery<IndexColumn>($"""
                SELECT COLUMN_NAME AS ColumnName
                FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'ContractEvents'
                  AND INDEX_NAME = {indexName}
                ORDER BY SEQ_IN_INDEX
                """)
            .ToListAsync();

        return [.. rows.Select(row => row.ColumnName)];
    }

    /// <summary>
    /// Every unique index on the table, excluding the primary key — which MariaDB reports as an index
    /// named <c>PRIMARY</c> and which is not the kind of uniqueness §4 rules out.
    /// </summary>
    private static async Task<List<string>> ReadUniqueIndexNamesAsync(OdysseyContext context)
    {
        var rows = await context.Database
            .SqlQuery<IndexName>($"""
                SELECT DISTINCT INDEX_NAME AS Name
                FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'ContractEvents'
                  AND NON_UNIQUE = 0
                  AND INDEX_NAME <> 'PRIMARY'
                """)
            .ToListAsync();

        return [.. rows.Select(row => row.Name)];
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
                  AND k.TABLE_NAME = 'ContractEvents'
                """)
            .ToListAsync();

        return rows.ToDictionary(row => row.ColumnName, row => row.DeleteRule, StringComparer.Ordinal);
    }

    private sealed record IndexColumn(string ColumnName);

    private sealed record IndexName(string Name);

    private sealed record ForeignKeyRule(string ColumnName, string DeleteRule);

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
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
