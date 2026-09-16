using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Seeds a contact into a schema that is deliberately <b>older than the current model</b> — the state every migration test sits in between <c>MigrateToAsync(baseline)</c> and the
/// <c>MigrateAsync()</c> under test.
/// </summary>
/// <remarks>
/// <para>
/// Raw SQL, for the reason <see cref="InsurancePolicyFileRelocationTests"/> already states about its
/// policy seed: the seed has to describe <b>the schema under test</b>, not whatever the current model
/// happens to be. An EF insert names every column of today's entity, so the moment a later migration
/// adds one — issue #48 added <c>EstablishedDate</c> and <c>DissolvedDate</c> to
/// <c>OrganizationDetails</c> — every baseline seed breaks with
/// <c>Unknown column 'DissolvedDate' in 'INSERT INTO'</c>, in tests that have nothing to do with the
/// change.
/// </para>
/// <para>
/// The columns named below are the ones <c>InitialCreate</c> shipped for each detail table, so this
/// stays valid for any baseline at or after it. A future migration that adds a column is expected to leave this alone; one that
/// makes a NAMED column non-nullable or drops it has genuinely changed the shape these tests seed and
/// should update it.
/// </para>
/// <para>
/// The parent <c>Contacts</c> row gets the same treatment as the detail tables. It went through EF
/// until issue #86 added <c>AvatarFileId</c> to the entity, at which point every baseline seed broke
/// with <c>Unknown column 'AvatarFileId' in 'INSERT INTO'</c> — in migration tests that have nothing
/// to do with contact images, exactly as this file predicted. That is the whole hazard: a column added
/// to a CURRENT entity reaches back into every test seeding an OLDER schema through EF.
/// </para>
/// </remarks>
internal static class BaselineContacts
{
    public static async Task AddOrganizationAsync(
        OdysseyContext context, Guid contactId, string legalName, string? organizationNumber = null)
    {
        await AddContactRowAsync(context, contactId, legalName.ToUpperInvariant(), ContactType.Organization);

        // Parameterised rather than interpolated: EF1002 is an error in this project, and a test seed
        // is not a reason to suppress it. The optional column is switched in the SQL rather than bound
        // as DBNull, which EF has no store mapping for.
        var numberSql = organizationNumber is null ? "NULL" : "{2}";
        object[] parameters = organizationNumber is null
            ? [contactId, legalName]
            : [contactId, legalName, organizationNumber];

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `OrganizationDetails` (`ContactId`, `LegalName`, `OrganizationNumber`, `Website`) "
            + $"VALUES ({{0}}, {{1}}, {numberSql}, NULL)",
            parameters);

        // The contact was tracked WITHOUT its detail sub-record, so a later read through this same
        // context would materialise it from the change tracker with OrganizationDetails null. Detach
        // it so the next query goes to the database, where the raw insert actually landed.
        context.ChangeTracker.Clear();
    }

    /// <summary>
    /// The Person counterpart. <c>PersonDetails</c> gained <c>MiddleName</c> and <c>DateOfDeath</c> in
    /// issue #48, so an EF graph insert at any earlier baseline fails with
    /// <c>Unknown column 'DateOfDeath' in 'INSERT INTO'</c> — the same trap the Organization side hit.
    /// </summary>
    public static async Task AddPersonAsync(
        OdysseyContext context, Guid contactId, string firstName, string lastName)
    {
        await AddContactRowAsync(context, contactId, $"{firstName} {lastName}".ToUpperInvariant(), ContactType.Person);

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `PersonDetails` (`ContactId`, `FirstName`, `LastName`) VALUES ({0}, {1}, {2})",
            contactId, firstName, lastName);

        context.ChangeTracker.Clear();
    }

    /// <summary>
    /// The parent row. Raw SQL for the same reason as the detail sub-records above: it names only the
    /// columns <c>InitialCreate</c> shipped, so a later migration adding one to the <c>Contact</c>
    /// entity cannot reach back and break every test that seeds an older schema.
    /// </summary>
    private static async Task AddContactRowAsync(
        OdysseyContext context, Guid contactId, string normalizedName, ContactType type)
    {
        var now = SqlTimestamp(DateTime.UtcNow);

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `Contacts` "
            + "(`ContactId`, `ExternalUid`, `DisplayName`, `NormalizedName`, `Type`, `Notes`, "
            + "`Archived`, `CreatedAt`, `UpdatedAt`) "
            + "VALUES ({0}, {1}, NULL, {2}, {3}, NULL, NULL, {4}, {4})",
            contactId, $"urn:uuid:{Guid.NewGuid()}", normalizedName, (int)type, now);

        context.ChangeTracker.Clear();
    }

    /// <summary>The timestamp format the raw-SQL seeds in this project use for a DATETIME(6) column.</summary>
    public static string SqlTimestamp(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
}
