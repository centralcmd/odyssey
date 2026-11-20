using Odyssey.Context;
using Odyssey.TestData.Catalog;
using Odyssey.TestData.Generators;
using CalendarEntity = Odyssey.Context.Calendar;

namespace Odyssey.TestData;

/// <summary>
/// The complete, deterministic demo finance dataset (spec §3.7–§3.11). Building it is a
/// pure function of the fixed catalogs and seed, so it is reproducible. Consumers:
/// the runtime seeder (persists this to MariaDB) and tests (materialize a subset).
/// Currencies, roles and permission claims are reference data and are NOT included here —
/// they already exist; the seeder only references them.
/// </summary>
public sealed class DemoDataSet
{
    public required IReadOnlyList<DemoUser> Users { get; init; }
    public required IReadOnlyList<TransactionTag> Tags { get; init; }
    public required IReadOnlyList<Contact> Contacts { get; init; }
    public required IReadOnlyList<Account> Accounts { get; init; }
    public required IReadOnlyList<AccountEstimate> AccountEstimates { get; init; }
    public required IReadOnlyList<Term> Terms { get; init; }
    public required IReadOnlyList<Budget> Budgets { get; init; }
    public required IReadOnlyList<BudgetItem> BudgetItems { get; init; }
    public required IReadOnlyList<Transaction> Transactions { get; init; }
    public required IReadOnlyList<TransactionTagLink> TransactionTagLinks { get; init; }
    public required IReadOnlyList<ExchangeRate> ExchangeRates { get; init; }
    public required IReadOnlyList<Instrument> Instruments { get; init; }
    public required IReadOnlyList<InstrumentPrice> InstrumentPrices { get; init; }
    public required IReadOnlyList<Contract> Contracts { get; init; }
    public required IReadOnlyList<ContractParty> ContractParties { get; init; }
    public required IReadOnlyList<ContractEvent> ContractEvents { get; init; }
    public required IReadOnlyList<Property> Properties { get; init; }
    public required IReadOnlyList<PropertyEstimate> PropertyEstimates { get; init; }
    public required IReadOnlyList<PropertySmartTag> PropertySmartTags { get; init; }
    public required IReadOnlyList<PropertyEvent> PropertyEvents { get; init; }
    public required IReadOnlyList<TaxStatement> TaxStatements { get; init; }
    public required IReadOnlyList<TaxStatementTag> TaxStatementTags { get; init; }

    // Everything with bytes, and every row that names it, is written by the worker's demo-file seeder rather than by
    // the migrations job: only a host with the file volumes can put a file on the object store, and the links are
    // real foreign keys. The migrations job seeds everything else in this set.

    /// <summary>Every demo Files-store file — tax-statement documents, journal and task attachments, contact images.</summary>
    public required IReadOnlyList<DemoFile> Files { get; init; }

    /// <summary>The seeded contact images (issue #86), each a file of <see cref="Files"/>.</summary>
    public required IReadOnlyList<DemoContactAvatar> ContactAvatars { get; init; }
    public required IReadOnlyList<TaxStatementFile> TaxStatementFiles { get; init; }

    // Journal module (issue #311). The photo and attachment links are written with their photos and files.
    public required IReadOnlyList<JournalTag> JournalTags { get; init; }
    public required IReadOnlyList<JournalTaskTag> JournalTaskTags { get; init; }
    public required IReadOnlyList<JournalEntry> JournalEntries { get; init; }
    public required IReadOnlyList<JournalEntryTag> JournalEntryTags { get; init; }
    public required IReadOnlyList<JournalEntryContact> JournalEntryContacts { get; init; }
    public required IReadOnlyList<JournalEntryPhoto> JournalEntryPhotos { get; init; }
    public required IReadOnlyList<JournalEntryAttachment> JournalEntryAttachments { get; init; }
    public required IReadOnlyList<JournalTask> JournalTasks { get; init; }
    public required IReadOnlyList<JournalTaskTagLink> JournalTaskTagLinks { get; init; }
    public required IReadOnlyList<JournalTaskAttachment> JournalTaskAttachments { get; init; }

    // Photos module (issue #321), now part of the merged OdysseyContext. Each photo owns its original; journal photos
    // are shared library records (no double-seed).
    public required IReadOnlyList<DemoPhoto> Photos { get; init; }
    public required IReadOnlyList<PhotoTag> PhotoTags { get; init; }
    public required IReadOnlyList<PhotoTagLink> PhotoTagLinks { get; init; }
    public required IReadOnlyList<PhotoPerson> PhotoPeople { get; init; }
    public required IReadOnlyList<PhotoAlbum> PhotoAlbums { get; init; }
    public required IReadOnlyList<PhotoAlbumItem> PhotoAlbumItems { get; init; }

    // Calendar module (issue #323), now part of the merged OdysseyContext.
    public required IReadOnlyList<CalendarEntity> Calendars { get; init; }
    public required IReadOnlyList<CalendarEvent> CalendarEvents { get; init; }
    public required IReadOnlyList<RecurrencePattern> RecurrencePatterns { get; init; }

    // Household locations (issue #350): one per source, one archived.
    public required IReadOnlyList<Location> Locations { get; init; }

    /// <summary>
    /// Builds the full dataset. Pass a different <paramref name="anchorDate"/> only when a
    /// test needs to reanchor "today"; the default keeps the dataset deterministic.
    /// </summary>
    public static DemoDataSet Build(DateTime? anchorDate = null)
    {
        // Reanchoring affects transaction stream windows; the default is the fixed spec anchor.
        var anchor = anchorDate ?? DemoDataDefaults.AnchorDate;

        var accounts = Catalog.Accounts.Build();
        var (budgets, budgetItems) = BudgetGenerator.Build();
        var (transactions, tagLinks) = TransactionGenerator.Build(accounts, anchor);
        var (contracts, contractParties, contractTerms, contractEvents) = ContractGenerator.Build(anchor);
        var (taxStatements, taxStatementTags) = TaxStatementGenerator.Build();
        var (properties, propertyEstimates, propertySmartTags) = PropertyGenerator.Build();
        var (taxFiles, taxStatementFiles) = TaxStatementFileGenerator.Build();
        var (instruments, instrumentPrices) = InstrumentGenerator.Build(anchor);

        var journalTags = JournalTagGenerator.Generate(anchor);
        var taskTags = JournalTaskTagGenerator.Generate(anchor);
        var journal = JournalEntryGenerator.Generate(anchor);
        var journalTasks = JournalTaskGenerator.Generate(anchor);
        var photoLibrary = PhotoGenerator.Generate(anchor);
        var calendar = CalendarGenerator.Generate(anchor);

        // The photo library is the single home for all photos: the journal's shared library records plus
        // the standalone demo photos (a journal photo is one library Photo).
        var allPhotos = journal.LibraryPhotos.Concat(photoLibrary.Photos).ToList();

        var contactAvatars = Catalog.Contacts.BuildAvatarFiles();
        var allFiles = taxFiles.Concat(journal.Files).Concat(journalTasks.Files).Concat(contactAvatars.Select(a => a.File)).ToList();

        return new DemoDataSet
        {
            Users = DemoUsers.All,
            Tags = Catalog.Tags.Build(),
            Contacts = Catalog.Contacts.Build(),
            ContactAvatars = contactAvatars,
            Accounts = accounts,
            AccountEstimates = AccountEstimateGenerator.Build(),
            // Every term is owned by a contract (issue #190): ContractGenerator places the account-derived
            // terms from TermGenerator on their resolved contracts alongside the contracts' own fees.
            Terms = contractTerms,
            Budgets = budgets,
            BudgetItems = budgetItems,
            Transactions = transactions,
            TransactionTagLinks = tagLinks,
            ExchangeRates = ExchangeRateGenerator.Build(anchor),
            Instruments = instruments,
            InstrumentPrices = instrumentPrices,
            Contracts = contracts,
            ContractParties = contractParties,
            ContractEvents = contractEvents,
            Properties = properties,
            PropertyEstimates = propertyEstimates,
            PropertySmartTags = propertySmartTags,
            PropertyEvents = PropertyGenerator.BuildEvents(properties),
            TaxStatements = taxStatements,
            TaxStatementTags = taxStatementTags,
            Files = allFiles,
            TaxStatementFiles = taxStatementFiles,
            JournalTags = journalTags,
            JournalTaskTags = taskTags,
            JournalEntries = journal.Entries,
            JournalEntryTags = journal.EntryTags,
            JournalEntryContacts = journal.EntryContacts,
            JournalEntryPhotos = journal.Photos,
            JournalEntryAttachments = journal.Attachments,
            JournalTasks = journalTasks.Items,
            JournalTaskTagLinks = journalTasks.ItemTags,
            JournalTaskAttachments = journalTasks.Attachments,
            Photos = allPhotos,
            PhotoTags = photoLibrary.Tags,
            PhotoTagLinks = photoLibrary.TagLinks,
            PhotoPeople = photoLibrary.People,
            PhotoAlbums = photoLibrary.Albums,
            PhotoAlbumItems = photoLibrary.AlbumItems,
            Calendars = calendar.Calendars,
            CalendarEvents = calendar.CalendarEvents,
            RecurrencePatterns = calendar.RecurrencePatterns,
            Locations = LocationGenerator.Generate(anchor),
        };
    }
}
