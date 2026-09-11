// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or an
// int this test generated itself — there is no external input — and a pre-migration row holding a
// label the current enum forbids cannot be written through the entity, which is the point of the seam.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// <c>RemapContactMethodLabelsForOrganizations</c> — the migration that <b>establishes</b> issue #47's
/// invariant, "a contact method's label is valid for its contact's type". The rest of the
/// specification assumes no invalid row survives it, so this is not optional cleanup.
/// </summary>
/// <remarks>
/// <para>
/// Unrunnable on the fast tiers by construction: EF InMemory has no schema and no migrations, and the
/// ordinary relational fixtures migrate straight to head — by the time a test could write an invalid
/// row, the migration has already run against an empty database and will never run again.
/// <see cref="MigrationSeam"/> is the <c>migrate to N−1 → seed → migrate to head</c> seam this needs.
/// </para>
/// <para>
/// <b>Which clause catches which direction</b>, stated so a later tidy-up cannot drop one as
/// redundant: a <i>too-permissive</i> migration (misses rows it should fix) is caught by the
/// zero-invalid-rows clause; a <i>too-aggressive</i> one (clobbers rows it should leave) is caught
/// <b>only</b> by the valid-rows-unchanged clause — the first cannot see it, because <c>Other</c> is
/// valid for every type, so it would pass over a database the migration had just damaged.
/// </para>
/// <para>
/// The final predicate is built from <see cref="ContactLabelScope"/> rather than from SQL literals:
/// re-encoding the scope map here would let the test agree with a wrong migration.
/// </para>
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContactMethodLabelRemapMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contact_label_remap";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_DropDeprecatedColumnsAndTuneIndexes";

    private const int OutOfRange = 99;

    [SkippableFact]
    public async Task The_migration_leaves_no_contact_method_label_invalid_for_its_contact_type()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var person = Guid.NewGuid();
        var organization = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedContactAsync(context, person, ContactType.Person);
                await SeedContactAsync(context, organization, ContactType.Organization);
                await SeedTheMatrixAsync(context, person, organization);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                // Clause 1 — zero invalid rows. Catches a migration that missed rows it should fix.
                await AssertNoInvalidRowsAsync(context);

                // Clause 2 — the valid rows survived UNCHANGED. The only clause that can catch a
                // migration that clobbered rows it should have left, since Other is valid everywhere.
                await AssertSurvivorsAsync(context, person, organization);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// Idempotence, asserted rather than assumed: the <c>Up</c> statements target the complement of the
    /// valid set, so their post-state <i>is</i> that complement's complement. Re-running them changes
    /// nothing — which was not true of the earlier <c>WHERE Label IN (1,2)</c> form, where idempotence
    /// was a property of the data rather than of the predicate.
    /// </summary>
    [SkippableFact]
    public async Task Running_the_remap_a_second_time_changes_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var person = Guid.NewGuid();
        var organization = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedContactAsync(context, person, ContactType.Person);
                await SeedContactAsync(context, organization, ContactType.Organization);
                await SeedTheMatrixAsync(context, person, organization);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                var before = await SnapshotAsync(context);

                await ReplayTheRemapAsync(context);

                Assert.Equal(before, await SnapshotAsync(context));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// A contact whose <c>Type</c> is outside <c>{1, 2}</c> is untouched by all six statements. There is
    /// no CHECK constraint, so a hand edit can create one — but such a row has no valid label set to be
    /// measured against, and is data corruption rather than a labelling problem (Non-Goal 7).
    /// </summary>
    [SkippableFact]
    public async Task A_contact_whose_type_is_out_of_range_is_left_alone()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var alien = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedContactAsync(context, alien, ContactType.Person);
                await context.Database.ExecuteSqlRawAsync($"UPDATE `Contacts` SET `Type` = 7 WHERE `ContactId` = '{alien}';");
                await AddPhoneAsync(context, alien, (int)PhoneLabel.Home);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                var labels = await LabelsAsync(context, "PhoneNumbers", alien);
                Assert.Equal([(int)PhoneLabel.Home], labels);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── The seed matrix (§16.12) ─────────────────────────────────────────────

    // Seeded in ALL THREE tables, because a wrong ordinal in any one of the six NOT IN lists has to be
    // catchable. Per table: on the organization, two person-only members and an out-of-range ordinal
    // (all must become Other) plus three valid rows that must survive; on the person, an
    // organization-band member and an out-of-range ordinal (both must become Other) plus three valid
    // rows that must survive.
    private static async Task SeedTheMatrixAsync(OdysseyContext context, Guid person, Guid organization)
    {
        foreach (var label in new[] { (int)AddressLabel.Home, (int)AddressLabel.Work, OutOfRange })
            await AddAddressAsync(context, organization, label);
        foreach (var label in new[] { (int)AddressLabel.Visiting, (int)AddressLabel.Billing, (int)AddressLabel.Other })
            await AddAddressAsync(context, organization, label);

        foreach (var label in new[] { (int)AddressLabel.Visiting, OutOfRange })
            await AddAddressAsync(context, person, label);
        foreach (var label in new[] { (int)AddressLabel.Home, (int)AddressLabel.Work, (int)AddressLabel.Postal })
            await AddAddressAsync(context, person, label);

        foreach (var label in new[] { (int)EmailLabel.Home, (int)EmailLabel.Work, OutOfRange })
            await AddEmailAsync(context, organization, label);
        foreach (var label in new[] { (int)EmailLabel.General, (int)EmailLabel.Claims, (int)EmailLabel.Other })
            await AddEmailAsync(context, organization, label);

        foreach (var label in new[] { (int)EmailLabel.General, OutOfRange })
            await AddEmailAsync(context, person, label);
        foreach (var label in new[] { (int)EmailLabel.Home, (int)EmailLabel.Work, (int)EmailLabel.Other })
            await AddEmailAsync(context, person, label);

        foreach (var label in new[] { (int)PhoneLabel.Home, (int)PhoneLabel.Work, OutOfRange })
            await AddPhoneAsync(context, organization, label);
        foreach (var label in new[] { (int)PhoneLabel.Switchboard, (int)PhoneLabel.Mobile, (int)PhoneLabel.Other })
            await AddPhoneAsync(context, organization, label);

        foreach (var label in new[] { (int)PhoneLabel.Switchboard, OutOfRange })
            await AddPhoneAsync(context, person, label);
        foreach (var label in new[] { (int)PhoneLabel.Home, (int)PhoneLabel.Work, (int)PhoneLabel.Mobile })
            await AddPhoneAsync(context, person, label);
    }

    // ── Assertions ───────────────────────────────────────────────────────────

    private static async Task AssertNoInvalidRowsAsync(OdysseyContext context)
    {
        var types = new[] { ContactType.Person, ContactType.Organization };

        foreach (var type in types)
        {
            await AssertAllValidAsync(
                context, "Addresses", type, [.. ContactLabelScope.AddressLabelsFor(type).Select(l => (int)l)]);
            await AssertAllValidAsync(
                context, "EmailAddresses", type, [.. ContactLabelScope.EmailLabelsFor(type).Select(l => (int)l)]);
            await AssertAllValidAsync(
                context, "PhoneNumbers", type, [.. ContactLabelScope.PhoneLabelsFor(type).Select(l => (int)l)]);
        }
    }

    private static async Task AssertAllValidAsync(
        OdysseyContext context, string table, ContactType type, int[] valid)
    {
        // The predicate is built from ContactLabelScope, never from SQL literals: re-encoding the scope
        // map here would let this agree with a wrong migration.
        var invalid = await ScalarAsync(context, $"""
            SELECT COUNT(*) FROM `{table}` r
            JOIN `Contacts` c ON c.`ContactId` = r.`ContactId`
            WHERE c.`Type` = {(int)type} AND r.`Label` NOT IN ({string.Join(',', valid)});
            """);

        Assert.Equal(0, invalid);
    }

    /// <summary>
    /// The exact label multiset each contact's rows hold after the migration. This is the only clause
    /// that can catch a <i>too-aggressive</i> migration: <c>Other</c> is valid for both types, so the
    /// zero-invalid-rows clause above would pass over a database whose valid rows had just been
    /// clobbered.
    /// </summary>
    private static async Task AssertSurvivorsAsync(OdysseyContext context, Guid person, Guid organization)
    {
        // Organization addresses: Home, Work and 99 became Other; Visiting, Billing and Other survive.
        await AssertLabelsAsync(context, "Addresses", organization,
        [
            (int)AddressLabel.Billing, (int)AddressLabel.Other, (int)AddressLabel.Other,
            (int)AddressLabel.Other, (int)AddressLabel.Other, (int)AddressLabel.Visiting,
        ]);

        // Person addresses: Visiting and 99 became Other; Home, Work and Postal survive.
        await AssertLabelsAsync(context, "Addresses", person,
        [
            (int)AddressLabel.Home, (int)AddressLabel.Work, (int)AddressLabel.Other,
            (int)AddressLabel.Other, (int)AddressLabel.Postal,
        ]);

        await AssertLabelsAsync(context, "EmailAddresses", organization,
        [
            (int)EmailLabel.Other, (int)EmailLabel.Other, (int)EmailLabel.Other,
            (int)EmailLabel.Other, (int)EmailLabel.General, (int)EmailLabel.Claims,
        ]);

        await AssertLabelsAsync(context, "EmailAddresses", person,
        [
            (int)EmailLabel.Home, (int)EmailLabel.Work, (int)EmailLabel.Other,
            (int)EmailLabel.Other, (int)EmailLabel.Other,
        ]);

        await AssertLabelsAsync(context, "PhoneNumbers", organization,
        [
            (int)PhoneLabel.Mobile, (int)PhoneLabel.Other, (int)PhoneLabel.Other,
            (int)PhoneLabel.Other, (int)PhoneLabel.Other, (int)PhoneLabel.Switchboard,
        ]);

        await AssertLabelsAsync(context, "PhoneNumbers", person,
        [
            (int)PhoneLabel.Home, (int)PhoneLabel.Work, (int)PhoneLabel.Mobile,
            (int)PhoneLabel.Other, (int)PhoneLabel.Other,
        ]);
    }

    private static async Task AssertLabelsAsync(
        OdysseyContext context, string table, Guid contactId, int[] expected)
    {
        var actual = await LabelsAsync(context, table, contactId);

        Assert.Equal([.. expected.Order()], [.. actual.Order()]);
    }

    private static async Task<List<int>> LabelsAsync(OdysseyContext context, string table, Guid contactId)
    {
        var labels = new List<int>();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        await context.Database.OpenConnectionAsync();
        command.CommandText = $"SELECT `Label` FROM `{table}` WHERE `ContactId` = '{contactId}';";

        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                labels.Add(reader.GetInt32(0));
        }

        await context.Database.CloseConnectionAsync();
        return labels;
    }

    // ── Seeding at the baseline schema ───────────────────────────────────────

    // Seeded through BaselineContacts rather than as an EF graph: this runs at the Baseline schema,
    // which predates the columns later migrations add to the two detail tables (issue #48 added
    // MiddleName and DateOfDeath to PersonDetails). An EF insert names every column of TODAY's entity,
    // so it breaks on a schema that is deliberately older — which is exactly what a migration test
    // sits on.
    private static Task SeedContactAsync(OdysseyContext context, Guid contactId, ContactType type) =>
        type == ContactType.Person
            ? BaselineContacts.AddPersonAsync(context, contactId, "Ada", "Lovelace")
            : BaselineContacts.AddOrganizationAsync(context, contactId, "Acme");

    // Raw SQL throughout: an out-of-range ordinal — and, once the enum is widened, a label the new
    // scope forbids — cannot be written through the entity, and that is precisely the pre-migration
    // state this has to build.
    private static Task AddAddressAsync(OdysseyContext context, Guid contactId, int label) =>
        context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Addresses` (`Id`, `ContactId`, `Label`, `IsPrimary`, `Line1`, `City`, `CountryCode`)
            VALUES ('{Guid.NewGuid()}', '{contactId}', {label}, 0, 'Storgata 55', 'Oslo', 'NO');
            """);

    private static Task AddEmailAsync(OdysseyContext context, Guid contactId, int label) =>
        context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `EmailAddresses` (`Id`, `ContactId`, `Label`, `IsPrimary`, `Value`)
            VALUES ('{Guid.NewGuid()}', '{contactId}', {label}, 0, 'post-{Guid.NewGuid():N}@example.com');
            """);

    private static Task AddPhoneAsync(OdysseyContext context, Guid contactId, int label) =>
        context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `PhoneNumbers` (`Id`, `ContactId`, `Label`, `IsPrimary`, `Value`)
            VALUES ('{Guid.NewGuid()}', '{contactId}', {label}, 0, '+47 22 00 00 00');
            """);

    // ── Idempotence helpers ──────────────────────────────────────────────────

    // The migration itself will not re-run — its history row is written — so the second pass replays
    // the same six statements directly. Literal ordinals, exactly as the migration writes them: this
    // has to be the statement under test, not a re-derivation of it.
    private static async Task ReplayTheRemapAsync(OdysseyContext context)
    {
        var statements = new[]
        {
            "UPDATE Addresses a JOIN Contacts c ON c.ContactId = a.ContactId SET a.Label = 4 WHERE c.Type = 1 AND a.Label NOT IN (1,2,3,4,5);",
            "UPDATE Addresses a JOIN Contacts c ON c.ContactId = a.ContactId SET a.Label = 4 WHERE c.Type = 2 AND a.Label NOT IN (3,4,5,20,21,22);",
            "UPDATE EmailAddresses e JOIN Contacts c ON c.ContactId = e.ContactId SET e.Label = 3 WHERE c.Type = 1 AND e.Label NOT IN (1,2,3);",
            "UPDATE EmailAddresses e JOIN Contacts c ON c.ContactId = e.ContactId SET e.Label = 3 WHERE c.Type = 2 AND e.Label NOT IN (3,20,21,22,23,24);",
            "UPDATE PhoneNumbers p JOIN Contacts c ON c.ContactId = p.ContactId SET p.Label = 4 WHERE c.Type = 1 AND p.Label NOT IN (1,2,3,4);",
            "UPDATE PhoneNumbers p JOIN Contacts c ON c.ContactId = p.ContactId SET p.Label = 4 WHERE c.Type = 2 AND p.Label NOT IN (3,4,20,21,22,23,24,25,26);",
        };

        foreach (var statement in statements)
            await context.Database.ExecuteSqlRawAsync(statement);
    }

    private static async Task<string> SnapshotAsync(OdysseyContext context)
    {
        var rows = new List<string>();
        foreach (var table in new[] { "Addresses", "EmailAddresses", "PhoneNumbers" })
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            await context.Database.OpenConnectionAsync();
            command.CommandText = $"SELECT `Id`, `Label` FROM `{table}` ORDER BY `Id`;";

            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    rows.Add($"{table}:{reader.GetGuid(0)}={reader.GetInt32(1)}");
            }

            await context.Database.CloseConnectionAsync();
        }

        return string.Join('\n', rows);
    }

    private static async Task<int> ScalarAsync(OdysseyContext context, string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        await context.Database.OpenConnectionAsync();
        command.CommandText = sql;
        var value = Convert.ToInt32(await command.ExecuteScalarAsync());
        await context.Database.CloseConnectionAsync();
        return value;
    }

    // ── Fixture plumbing ─────────────────────────────────────────────────────

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
