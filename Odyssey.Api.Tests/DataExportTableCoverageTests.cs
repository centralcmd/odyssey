using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Odyssey.Api.DataExport;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The guard that issue #33 existed for the want of. The data-portability export covers a
/// deliberate LIST of tables, not every table in the context — the contact detail tables have their
/// own vCard export, the journal side has its own surfaces, and identity is out of scope. A list is
/// the right design; what it must not do is grow a hole in silence, which is exactly what happened
/// to insurance: seven tables that were in neither the export nor
/// <see cref="DataExportExclusions.ExcludedTables"/>, so nothing anywhere said they were missing.
/// Contracts, tax statements, subscriptions and the two account side-tables were in the same state.
///
/// So every table on <see cref="OdysseyContext"/> must be accounted for in one of three ways, and a
/// new one fails this test until someone says which. That is the point: the decision is cheap when
/// the table is added and expensive when a data-subject request surfaces it years later.
/// </summary>
public class DataExportTableCoverageTests
{
    /// <summary>
    /// Tables deliberately outside this export, each with the reason it is safe to leave out. These
    /// are not "not yet done" entries — an entry here is a claim that the data is either reachable
    /// through another surface or genuinely out of scope, and <c>outOfScopeDatabases</c> tells the
    /// reader so. Anything that is merely unfinished belongs in the export or in
    /// <see cref="DataExportExclusions.ExcludedTables"/>, not here.
    /// </summary>
    private static readonly Dictionary<string, string> CoveredElsewhere = new(StringComparer.Ordinal)
    {
        // Denormalized onto the row that owns it.
        ["TransactionTagLinks"] = "exported inline as TransactionExport.TransactionTagIds",

        // Contact detail — served by the vCard export, which is the richer representation.
        ["PersonDetails"] = "GET /api/contacts/vcard",
        ["OrganizationDetails"] = "GET /api/contacts/vcard",
        ["ContactAliases"] = "GET /api/contacts/vcard",
        ["Addresses"] = "GET /api/contacts/vcard",
        ["EmailAddresses"] = "GET /api/contacts/vcard",
        ["PhoneNumbers"] = "GET /api/contacts/vcard",

        // Calendars — served by the iCalendar export.
        ["Calendars"] = "CalendarIcsController",
        ["CalendarEvents"] = "CalendarIcsController",
        ["RecurrencePatterns"] = "CalendarIcsController",

        // Journal module — outOfScopeDatabases says so.
        ["JournalEntries"] = "Journal: out of scope",
        ["JournalTags"] = "Journal: out of scope",
        ["JournalEntryTags"] = "Journal: out of scope",
        ["JournalEntryContacts"] = "Journal: out of scope",
        ["JournalEntryPhotos"] = "Journal: out of scope",
        ["JournalEntryAttachments"] = "Journal: out of scope",
        ["JournalTasks"] = "Journal: out of scope",
        ["JournalTaskTags"] = "Journal: out of scope",
        ["JournalTaskTagLinks"] = "Journal: out of scope",
        ["JournalTaskAttachments"] = "Journal: out of scope",
        ["Photos"] = "Journal: out of scope",
        ["PhotoTags"] = "Journal: out of scope",
        ["PhotoTagLinks"] = "Journal: out of scope",
        ["PhotoPeople"] = "Journal: out of scope",
        ["PhotoAlbums"] = "Journal: out of scope",
        ["PhotoAlbumItems"] = "Journal: out of scope",

        // Identity and instance policy — outOfScopeDatabases says so. The secret store is never
        // exported under any circumstances.
        ["Users"] = "Application/Identity: out of scope",
        ["Roles"] = "Application/Identity: out of scope",
        ["UserClaims"] = "Application/Identity: out of scope",
        ["UserRoles"] = "Application/Identity: out of scope",
        ["UserLogins"] = "Application/Identity: out of scope",
        ["UserTokens"] = "Application/Identity: out of scope",
        ["RoleClaims"] = "Application/Identity: out of scope",
        ["UserProfiles"] = "Application/Identity: out of scope",
        ["UserPreferences"] = "Application/Identity: out of scope",
        ["SystemSettings"] = "Application: instance policy, not subject data",
        ["SystemSettingSecrets"] = "Application: encrypted secrets, never exported",
        ["LicenseAcceptances"] = "Application/Legal: out of scope",
        ["TermsOfServiceVersions"] = "Application/Legal: out of scope",
        ["TermsOfServiceAcceptances"] = "Application/Legal: out of scope",
    };

    [Fact]
    public void EveryTable_IsExported_Excluded_OrAccountedForElsewhere()
    {
        var unaccounted = ContextTableNames()
            .Where(table => !ExportedCollectionNames().Contains(table))
            .Where(table => !new DataExportExclusions().ExcludedTables.Contains(table, StringComparer.Ordinal))
            .Where(table => !CoveredElsewhere.ContainsKey(table))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(unaccounted.Count == 0,
            "These tables are in neither the export, nor ExcludedTables, nor the CoveredElsewhere "
            + "list in this test, so nothing tells a reader of the export that they exist: "
            + string.Join(", ", unaccounted)
            + ". Export them, or state the omission — an omission that is stated is a different "
            + "thing from one that is merely absent.");
    }

    /// <summary>
    /// The other direction. An entry in <see cref="CoveredElsewhere"/> that no longer names a real
    /// table is an exemption outliving the thing it excused — and the next reader would take it as
    /// evidence the table is handled. Same shape as <c>MergedProjectNamespaceLayoutTests</c>'s rule
    /// that an allow-list entry must stay a real shadow.
    /// </summary>
    [Fact]
    public void TheCoveredElsewhereList_NamesOnlyRealTables()
    {
        var tables = ContextTableNames();

        var stale = CoveredElsewhere.Keys
            .Where(name => !tables.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "These entries no longer name a DbSet on OdysseyContext, so they excuse nothing: "
            + string.Join(", ", stale));
    }

    /// <summary>
    /// An excluded table must not also be exported. The two lists contradicting each other would
    /// make <c>excludedTables</c> actively misleading rather than merely incomplete.
    /// </summary>
    [Fact]
    public void NoTable_IsBothExportedAndExcluded()
    {
        var both = new DataExportExclusions().ExcludedTables
            .Intersect(ExportedCollectionNames(), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(both);
    }

    private static HashSet<string> ExportedCollectionNames() =>
        typeof(FinanceDatabaseExport)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

    // Includes the DbSets inherited from IdentityDbContext — they are real tables in the same
    // database, so the guard has to make a decision about them too.
    private static HashSet<string> ContextTableNames() =>
        typeof(OdysseyContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
}
