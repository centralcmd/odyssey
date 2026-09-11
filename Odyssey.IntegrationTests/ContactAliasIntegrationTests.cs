using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Journal;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for contact aliases (issue #48, AC 3, 10, 12, 13, 15, 16).
/// </summary>
/// <remarks>
/// Everything here is invisible to the fast tiers, and in three different ways. EF InMemory has no
/// collations and no column metadata, so the <c>_ci</c> assertion cannot run there at all. It
/// enforces no foreign keys, so the cascade is unobservable. And its <c>LIKE</c> is
/// case-<b>sensitive</b> while MariaDB's <c>_ci</c> collation is neither case- nor accent-sensitive —
/// which is precisely why the alias and middle-name search arms, which pass a raw un-uppercased
/// pattern, are asserted HERE rather than in <c>Odyssey.Core.Tests</c>.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContactAliasIntegrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contact_aliases";

    /// <summary>
    /// AC 15. The unique index's case- and accent-insensitivity is the column's <b>default</b>
    /// collation, which is inherited from the server image rather than pinned anywhere in
    /// infrastructure — <c>docker/mariadb/init/01-init.sql</c> is a bare
    /// <c>CREATE DATABASE IF NOT EXISTS odyssey</c>. This assertion is what makes an image bump fail a
    /// test instead of silently turning the index case-sensitive and the design inoperative.
    /// </summary>
    [SkippableFact]
    public async Task AliasValue_UsesTheDefaultCaseInsensitiveCollation()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            var collation = await CollationAsync(context, "ContactAliases", "Value");
            Assert.EndsWith("_ci", collation);
            // Explicitly NOT the binary collation Contact.ExternalUid carries. Copying that here
            // would make the index case-sensitive and defeat the whole duplicate design.
            Assert.NotEqual("utf8mb4_bin", collation);
        }

        await DropAsync();
    }

    /// <summary>
    /// AC 16. The composite leads on <c>ContactId</c>, so it serves uniqueness, the correlated
    /// <c>EXISTS</c> behind the search arm <b>and</b> InnoDB's foreign-key-index requirement in one —
    /// but EF's FK-index suppression depends on configuration ordering, so a redundant
    /// <c>IX_ContactAliases_ContactId</c> can appear silently.
    /// </summary>
    [SkippableFact]
    public async Task TheTable_CarriesExactlyOneIndexBesidesThePrimaryKey()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            var indexes = await NonPrimaryIndexNamesAsync(context, "ContactAliases");
            Assert.Equal(["IX_ContactAliases_ContactId_Value"], indexes);

            Assert.Equal(
                ["ContactId", "Value"],
                await IndexColumnsAsync(context, "ContactAliases", "IX_ContactAliases_ContactId_Value"));
            Assert.True(await IsUniqueAsync(context, "ContactAliases", "IX_ContactAliases_ContactId_Value"));
        }

        await DropAsync();
    }

    /// <summary>
    /// AC 3, from the database's side. The service's in-memory pre-check and this index have to agree
    /// on accents; if they did not, the check would pass and the INSERT would fail — a duplicate-key
    /// error on an ordinary write.
    /// </summary>
    [SkippableFact]
    public async Task TheUniqueIndex_RefusesCaseAndAccentVariantsOfTheSameValue()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();
        var contactId = await SeedContactAsync(options, "Renée", "Dubois");

        await using (var context = new OdysseyContext(options))
        {
            context.ContactAliases.Add(new ContactAlias { ContactId = contactId, Value = "Renée" });
            await context.SaveChangesAsync();
        }

        foreach (var variant in new[] { "renée", "Renee", "RENEE" })
        {
            await using var context = new OdysseyContext(options);
            context.ContactAliases.Add(new ContactAlias { ContactId = contactId, Value = variant });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        // Uniqueness is PER CONTACT: a different contact may carry the same alias.
        var otherId = await SeedContactAsync(options, "Renee", "Martin");
        await using (var context = new OdysseyContext(options))
        {
            context.ContactAliases.Add(new ContactAlias { ContactId = otherId, Value = "Renée" });
            await context.SaveChangesAsync();
            Assert.Equal(2, await context.ContactAliases.CountAsync());
        }

        await DropAsync();
    }

    /// <summary>
    /// AC 11's database half: the race the pre-check cannot win. The service catches the 1062 itself
    /// rather than letting it reach <c>GlobalExceptionHandler</c>, which would serialise MariaDB's
    /// <c>Duplicate entry 'xxx-Hansen' for key …</c> — the alias value, frequently a maiden name —
    /// into a log line. Both routes throw the same field-carrying conflict.
    /// </summary>
    [SkippableFact]
    public async Task ConcurrentInsert_SurfacesAsAConflictNamingTheFieldAndNeverEchoesTheValue()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();
        var contactId = await SeedContactAsync(options, "Karoline", "Hansen");

        // Two services on two contexts: each pre-check passes, because neither has seen the other's
        // row. The index arbitrates, and the loser must come back as a 409, not a 500.
        await using var winner = new OdysseyContext(options);
        await using var loser = new OdysseyContext(options);
        var winnerService = new ContactService(winner, new NoopContactReferenceGuard());
        var loserService = new ContactService(loser, new NoopContactReferenceGuard());

        await winnerService.CreateAlias(contactId, new NewContactAlias { Value = "Hansen", Label = "maiden name" });

        var conflict = await Assert.ThrowsAsync<Core.DomainConflictException>(
            () => loserService.CreateAlias(contactId, new NewContactAlias { Value = "Hansen" }));

        Assert.NotNull(conflict.Errors);
        Assert.True(conflict.Errors!.ContainsKey("value"));
        Assert.DoesNotContain("Hansen", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hansen", conflict.Errors["value"][0], StringComparison.OrdinalIgnoreCase);

        await DropAsync();
    }

    /// <summary>
    /// AC 10. The FK is <c>ON DELETE CASCADE</c>, so an erasure removes the alias rows in the same
    /// transaction — no orphan, and no application-code sweep to forget. That is also what makes the
    /// GDPR Art. 17 story hold without a separate retention rule.
    /// </summary>
    [SkippableFact]
    public async Task DeletingAContact_CascadesToItsAliasRows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();
        var contactId = await SeedContactAsync(options, "Karoline", "Hansen");

        await using (var context = new OdysseyContext(options))
        {
            context.ContactAliases.Add(new ContactAlias { ContactId = contactId, Value = "Kari", Label = "nickname" });
            context.ContactAliases.Add(new ContactAlias { ContactId = contactId, Value = "KH" });
            await context.SaveChangesAsync();
        }

        // Raw SQL, so the cascade under test is the DATABASE's, not EF's tracked-graph one.
        await using (var context = new OdysseyContext(options))
        {
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM Contacts WHERE ContactId = {0}", contactId);
        }

        await using (var context = new OdysseyContext(options))
        {
            Assert.Empty(await context.ContactAliases.Where(a => a.ContactId == contactId).ToListAsync());
        }

        await DropAsync();
    }

    /// <summary>
    /// AC 12 and AC 13. Both arms pass a raw, un-uppercased (but still escaped) LIKE pattern and lean
    /// on the <c>_ci</c> collation, so a lower-case term matches a capitalised alias — which is the
    /// half the fast tier cannot assert.
    /// </summary>
    [SkippableFact]
    public async Task Search_MatchesAnAliasAndAMiddleNameCaseInsensitively()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();
        var hansenId = await SeedContactAsync(options, "Karoline", "Hansen", middleName: "Marie");
        await SeedContactAsync(options, "Sam", "Rivera");

        await using (var context = new OdysseyContext(options))
        {
            context.ContactAliases.Add(new ContactAlias { ContactId = hansenId, Value = "Kari", Label = "maiden name" });
            await context.SaveChangesAsync();
        }

        await using (var context = new OdysseyContext(options))
        {
            var service = new ContactService(context, new NoopContactReferenceGuard());

            var byAlias = await service.ListAsync(new ContactsQueryParams { Search = "kari" });
            Assert.Equal(hansenId, Assert.Single(byAlias.Items).ContactId);

            var byMiddleName = await service.ListAsync(new ContactsQueryParams { Search = "marie" });
            Assert.Equal(hansenId, Assert.Single(byMiddleName.Items).ContactId);

            // AC 14 holds on real MariaDB too: the label is not searched.
            Assert.Empty((await service.ListAsync(new ContactsQueryParams { Search = "maiden" })).Items);
        }

        await DropAsync();
    }

    /// <summary>
    /// The four lifecycle columns survive a real round trip as pure dates. They are <c>DateOnly</c>
    /// server-side and exchanged as <c>DateTime?</c> on the wire, so this is the tier that proves no
    /// time component or offset creeps in.
    /// </summary>
    [SkippableFact]
    public async Task LifecycleDates_RoundTripAsPureDates()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using (var context = new OdysseyContext(options))
        {
            var service = new ContactService(context, new NoopContactReferenceGuard());
            await service.Create(new NewContact
            {
                Type = ContactType.Person,
                Archived = false,
                PersonDetails = new PersonDetailsDto
                {
                    FirstName = "Karoline",
                    LastName = "Hansen",
                    DateOfBirth = new DateTime(1951, 9, 2),
                    DateOfDeath = new DateTime(2024, 3, 11),
                },
            });
            await service.Create(new NewContact
            {
                Type = ContactType.Organization,
                Archived = false,
                OrganizationDetails = new OrganizationDetailsDto
                {
                    LegalName = "Pacific Home Insurance Co.",
                    EstablishedDate = new DateTime(1974, 6, 1),
                    DissolvedDate = new DateTime(2023, 6, 30),
                },
            });
        }

        await using (var context = new OdysseyContext(options))
        {
            var person = await context.PersonDetails.AsNoTracking().SingleAsync();
            Assert.Equal(new DateOnly(1951, 9, 2), person.DateOfBirth);
            Assert.Equal(new DateOnly(2024, 3, 11), person.DateOfDeath);

            var org = await context.OrganizationDetails.AsNoTracking().SingleAsync();
            Assert.Equal(new DateOnly(1974, 6, 1), org.EstablishedDate);
            Assert.Equal(new DateOnly(2023, 6, 30), org.DissolvedDate);

            // Non-Goal 3: neither contact was archived by recording a date.
            Assert.All(await context.Contacts.AsNoTracking().ToListAsync(), c => Assert.Null(c.Archived));
        }

        await DropAsync();
    }

    /// <summary>
    /// AC 45's collation caveat, asserted rather than assumed. The dedicated list is ordered by the
    /// column's <c>_ci</c> collation while the inline list is sorted in memory with
    /// <c>OrdinalIgnoreCase</c>, so the two are equivalent <b>modulo collation</b>: they agree on
    /// ASCII and may legitimately differ on accented input.
    /// </summary>
    [SkippableFact]
    public async Task InlineAndDedicatedOrdering_AgreeOnAsciiAndAreBothStable()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();
        var contactId = await SeedContactAsync(options, "Karoline", "Hansen");

        await using (var context = new OdysseyContext(options))
        {
            var service = new ContactService(context, new NoopContactReferenceGuard());
            foreach (var value in new[] { "Zed", "alpha", "Mid", "Émile" })
            {
                await service.CreateAlias(contactId, new NewContactAlias { Value = value });
            }
        }

        await using (var context = new OdysseyContext(options))
        {
            var service = new ContactService(context, new NoopContactReferenceGuard());
            var dedicated = (await service.GetAliases(contactId))!.Select(a => a.Value).ToList();
            var inline = (await service.Get(contactId))!.Aliases.Select(a => a.Value).ToList();

            // Same SET either way — the divergence the caveat allows is ordering, never membership.
            Assert.Equal(dedicated.Order(StringComparer.Ordinal), inline.Order(StringComparer.Ordinal));

            // And the ASCII values keep the same relative order in both.
            Assert.Equal(
                dedicated.Where(v => v.All(char.IsAscii)),
                inline.Where(v => v.All(char.IsAscii)));
        }

        await DropAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The insurance guard is irrelevant to every case here — no seeded contact is named on a policy
    /// — and the real one would only add queries. It is NOT stubbed to bypass a check under test: the
    /// contact delete exercised above goes through raw SQL precisely so the DATABASE's cascade is what
    /// is being observed.
    /// </summary>
    private sealed class NoopContactReferenceGuard : Core.Finance.IContactReferenceGuard
    {
        public Task<Core.Finance.InsuranceLinkBlockers> GetInsuranceLinkBlockersAsync(Guid contactId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Core.Finance.InsuranceLinkBlockers.None);

        public Task<bool> IsReferencedByInsuranceAsync(Guid contactId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task ClearAndCascadeReferencesAsync(Guid contactId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Dtos.Finance.DetachedInsuranceLinks> StageInsuranceLinkDetachAsync(Guid contactId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Dtos.Finance.DetachedInsuranceLinks());
    }

    private static async Task<Guid> SeedContactAsync(
        DbContextOptions<OdysseyContext> options, string first, string last, string? middleName = null)
    {
        await using var context = new OdysseyContext(options);
        var id = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = $"{first} {last}".ToUpperInvariant(),
            Type = ContactType.Person,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            PersonDetails = new PersonDetails
            {
                ContactId = id,
                FirstName = first,
                LastName = last,
                MiddleName = middleName,
            },
        });
        await context.SaveChangesAsync();
        return id;
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

    private static async Task<string> CollationAsync(OdysseyContext context, string table, string column) =>
        (await context.Database
            .SqlQueryRaw<string>(
                "SELECT COLLATION_NAME AS Value FROM INFORMATION_SCHEMA.COLUMNS "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'")
            .ToListAsync())
        .Single();

    private static Task<List<string>> IndexColumnsAsync(OdysseyContext context, string table, string index) =>
        context.Database
            .SqlQueryRaw<string>(
                "SELECT COLUMN_NAME AS Value FROM INFORMATION_SCHEMA.STATISTICS "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND INDEX_NAME = '{index}' "
                + "ORDER BY SEQ_IN_INDEX")
            .ToListAsync();

    private static Task<List<string>> NonPrimaryIndexNamesAsync(OdysseyContext context, string table) =>
        context.Database
            .SqlQueryRaw<string>(
                "SELECT DISTINCT INDEX_NAME AS Value FROM INFORMATION_SCHEMA.STATISTICS "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND INDEX_NAME <> 'PRIMARY' "
                + "ORDER BY INDEX_NAME")
            .ToListAsync();

    private static async Task<bool> IsUniqueAsync(OdysseyContext context, string table, string index) =>
        (await context.Database
            .SqlQueryRaw<int>(
                "SELECT NON_UNIQUE AS Value FROM INFORMATION_SCHEMA.STATISTICS "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND INDEX_NAME = '{index}' "
                + "LIMIT 1")
            .ToListAsync())
        .Single() == 0;

    private static DbContextOptions<OdysseyContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;
}
