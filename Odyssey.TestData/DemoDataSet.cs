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
    public required IReadOnlyList<AccountTerm> AccountTerms { get; init; }
    public required IReadOnlyList<Budget> Budgets { get; init; }
    public required IReadOnlyList<BudgetItem> BudgetItems { get; init; }
    public required IReadOnlyList<Transaction> Transactions { get; init; }
    public required IReadOnlyList<TransactionTagLink> TransactionTagLinks { get; init; }
    public required IReadOnlyList<ExchangeRate> ExchangeRates { get; init; }
    public required IReadOnlyList<InsurancePolicy> InsurancePolicies { get; init; }
    public required IReadOnlyList<PolicyRenewal> PolicyRenewals { get; init; }
    public required IReadOnlyList<InsurancePolicyInsurer> InsurancePolicyInsurers { get; init; }
    public required IReadOnlyList<InsurancePolicyInsuredAccount> InsurancePolicyInsuredAccounts { get; init; }
    public required IReadOnlyList<InsurancePolicyInsuredContact> InsurancePolicyInsuredContacts { get; init; }
    public required IReadOnlyList<InsurancePolicyBeneficiary> InsurancePolicyBeneficiaries { get; init; }
    public required IReadOnlyList<Contract> Contracts { get; init; }
    public required IReadOnlyList<ContractParty> ContractParties { get; init; }
    public required IReadOnlyList<TaxStatement> TaxStatements { get; init; }
    public required IReadOnlyList<TaxStatementTag> TaxStatementTags { get; init; }
    public required IReadOnlyList<FileBlob> FileBlobs { get; init; }
    public required IReadOnlyList<FileMetadata> FileMetadata { get; init; }
    public required IReadOnlyList<TaxStatementFile> TaxStatementFiles { get; init; }
    public required IReadOnlyList<Subscription> Subscriptions { get; init; }

    // Journal module (issue #311). The backing photo/attachment Files-store records are merged into
    // FileBlobs/FileMetadata above, and are real FK principals for these rows — so the seeder has to
    // write them first.
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

    // Photos module (issue #321), now part of the merged OdysseyContext; the backing image Files-store records are
    // merged into FileBlobs/FileMetadata above. Journal photos are shared library records (no double-seed).
    public required IReadOnlyList<Photo> Photos { get; init; }
    public required IReadOnlyList<PhotoTag> PhotoTags { get; init; }
    public required IReadOnlyList<PhotoTagLink> PhotoTagLinks { get; init; }
    public required IReadOnlyList<PhotoPerson> PhotoPeople { get; init; }
    public required IReadOnlyList<PhotoAlbum> PhotoAlbums { get; init; }
    public required IReadOnlyList<PhotoAlbumItem> PhotoAlbumItems { get; init; }

    // Calendar module (issue #323), now part of the merged OdysseyContext.
    public required IReadOnlyList<CalendarEntity> Calendars { get; init; }
    public required IReadOnlyList<CalendarEvent> CalendarEvents { get; init; }
    public required IReadOnlyList<RecurrencePattern> RecurrencePatterns { get; init; }

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
        var insurance = InsurancePolicyGenerator.Build(anchor);
        var (contracts, contractParties) = ContractGenerator.Build(anchor);
        var (taxStatements, taxStatementTags) = TaxStatementGenerator.Build();
        var (fileBlobs, fileMetadata, taxStatementFiles) = TaxStatementFileGenerator.Build();
        var subscriptions = SubscriptionGenerator.Build(anchor);

        var journalTags = JournalTagGenerator.Generate(anchor);
        var taskTags = JournalTaskTagGenerator.Generate(anchor);
        var journal = JournalEntryGenerator.Generate(anchor);
        var journalTasks = JournalTaskGenerator.Generate(anchor);
        var photoLibrary = PhotoGenerator.Generate(anchor);
        var calendar = CalendarGenerator.Generate(anchor);

        // Journal/photo/attachment generators create their own Files-store records; merge them into the
        // finance Files store (soft-referenced by FileId from the journal/photo tables).
        var allFileBlobs = fileBlobs.Concat(journal.FileBlobs).Concat(journalTasks.FileBlobs).Concat(photoLibrary.FileBlobs).ToList();
        var allFileMetadata = fileMetadata.Concat(journal.FileMetadata).Concat(journalTasks.FileMetadata).Concat(photoLibrary.FileMetadata).ToList();

        // The photo library is the single home for all photos: the journal's shared library records plus
        // the standalone demo photos, deduped by FileId (a journal photo is one library Photo).
        var allPhotos = journal.LibraryPhotos.Concat(photoLibrary.Photos).ToList();

        return new DemoDataSet
        {
            Users = DemoUsers.All,
            Tags = Catalog.Tags.Build(),
            Contacts = Catalog.Contacts.Build(),
            Accounts = accounts,
            AccountEstimates = AccountEstimateGenerator.Build(),
            AccountTerms = AccountTermGenerator.Build(),
            Budgets = budgets,
            BudgetItems = budgetItems,
            Transactions = transactions,
            TransactionTagLinks = tagLinks,
            ExchangeRates = ExchangeRateGenerator.Build(anchor),
            InsurancePolicies = insurance.Policies,
            PolicyRenewals = insurance.Renewals,
            InsurancePolicyInsurers = insurance.Insurers,
            InsurancePolicyInsuredAccounts = insurance.InsuredAccounts,
            InsurancePolicyInsuredContacts = insurance.InsuredContacts,
            InsurancePolicyBeneficiaries = insurance.Beneficiaries,
            Contracts = contracts,
            ContractParties = contractParties,
            TaxStatements = taxStatements,
            TaxStatementTags = taxStatementTags,
            FileBlobs = allFileBlobs,
            FileMetadata = allFileMetadata,
            TaxStatementFiles = taxStatementFiles,
            Subscriptions = subscriptions,
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
        };
    }
}
