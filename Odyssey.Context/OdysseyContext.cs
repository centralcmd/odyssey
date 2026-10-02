using Odyssey.Context.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Odyssey.Dtos;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;

namespace Odyssey.Context;

/// <summary>
/// The single <see cref="DbContext"/> for the whole application: the former <c>FinanceContext</c>,
/// <c>JournalContext</c> and <c>OdysseyContext</c> merged into one model — finance, journal,
/// tasks, photos, calendars, the contact aggregate, and identity/auth with its profiles,
/// preferences, system settings and legal-acceptance logs.
/// </summary>
/// <remarks>
/// <para>
/// Each merge was about referential integrity, not tidiness. EF cannot declare a relationship whose
/// principal lives in another model, so every reference across a context boundary was a bare key
/// validated by a lookup service and swept by a guard on delete, with nothing stopping a write path
/// that skipped both. Folding finance and journal together turned ten such columns into real foreign
/// keys (see <c>ConfigureCrossModuleForeignKeys</c>); folding identity in turned the twenty-three
/// user-attribution columns into real foreign keys too (see <c>ConfigureUserAttribution</c>),
/// which is what finally lets a user deletion resolve them inside the transaction that deletes the
/// account.
/// </para>
/// <para>
/// The lookup services remain: they still serve read-path projections and the write-time 400/409
/// messages, which a raw FK violation cannot produce, and the EF InMemory provider enforces no
/// foreign keys at all, so they are the only implementation the fast test tiers ever see.
/// </para>
/// </remarks>
public class OdysseyContext : IdentityDbContext<ApplicationUser>
{
    public OdysseyContext(DbContextOptions<OdysseyContext> options) : base(options)
    {
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyNewUserDefaults();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await ApplyNewUserDefaultsAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // The require-admin-approval rule (issue #349) moved from static config (RegistrationOptions,
    // now removed) into a live read of the SystemSetting row — no cache, since registration volume
    // is far below the threshold the cached settings lookups exist to protect, and a stale read here
    // would be a security-relevant gap, not a cosmetic one. Split sync/async so the synchronous
    // SaveChanges() override (this repo's own tests call it directly — NewUserApprovalTests,
    // RegistrationGrantsNoPrivilegeTests, TestContextFactory) gets a genuine synchronous EF query
    // rather than sync-over-async.
    //
    // Both paths start with NewlyAddedUsers(), which short-circuits at zero — so the overwhelming
    // majority of saves through this context, which are domain writes with no ApplicationUser in the
    // change tracker, pay one ChangeTracker scan and nothing else.
    private void ApplyNewUserDefaults()
    {
        var newUsers = NewlyAddedUsers();
        if (newUsers.Count == 0)
        {
            return;
        }

        var requireAdminApproval = GetBoolSetting(
            SystemSettingsKeys.RegistrationRequireAdminApproval,
            SystemSettingsDefaults.RegistrationRequireAdminApproval);
        ApplyAdminApproval(newUsers, requireAdminApproval);
    }

    private async Task ApplyNewUserDefaultsAsync(CancellationToken cancellationToken)
    {
        var newUsers = NewlyAddedUsers();
        if (newUsers.Count == 0)
        {
            return;
        }

        var requireAdminApproval = await GetBoolSettingAsync(
            SystemSettingsKeys.RegistrationRequireAdminApproval,
            SystemSettingsDefaults.RegistrationRequireAdminApproval,
            cancellationToken);
        ApplyAdminApproval(newUsers, requireAdminApproval);
    }

    private List<ApplicationUser> NewlyAddedUsers() =>
        ChangeTracker.Entries<ApplicationUser>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .ToList();

    // With admin approval required, every new account starts disabled (permanent lockout) until an
    // administrator enables it — including the very first one. Registration order confers no privilege
    // and no exemption (issue #290): the initial administrator is seeded out of band by
    // Odyssey.MigrationService's BootstrapAdminSeeder, which clears this lockout on the one account it
    // creates.
    private static void ApplyAdminApproval(IReadOnlyList<ApplicationUser> newUsers, bool requireAdminApproval)
    {
        if (!requireAdminApproval)
        {
            return;
        }

        foreach (var user in newUsers)
        {
            user.LockoutEnabled = true;
            user.LockoutEnd = AccountLockout.DisabledLockoutEnd;
        }
    }

    private bool GetBoolSetting(string key, bool defaultValue) =>
        SystemSettingsReader.GetBool(this, key, defaultValue);

    private Task<bool> GetBoolSettingAsync(string key, bool defaultValue, CancellationToken cancellationToken) =>
        SystemSettingsReader.GetBoolAsync(this, key, defaultValue, cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // One IEntityTypeConfiguration per entity, under ModelConfiguration/ (issue #287 L5). The three
        // key sets below stay here instead: each spans many entities and is reviewed as a set, so
        // scattering it across the per-entity files would hide the one rule each set exists to state.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OdysseyContext).Assembly);

        ConfigureCurrencyReferences(modelBuilder);
        ConfigureCrossModuleForeignKeys(modelBuilder);
        ConfigureUserAttribution(modelBuilder);
    }

    /// <summary>Every currency-code column except the exchange-rate pair (issue #241).</summary>
    private static void ConfigureCurrencyReferences(ModelBuilder modelBuilder)
    {
        // Every currency-code column but the exchange-rate pair, which ExchangeRateConfiguration declares
        // (issue #241). Before these keys a currency could be deleted
        // out from under the rows recorded in it, leaving accounts that could not be edited and
        // budgets and statements that refused updates. RESTRICT, never CASCADE or SET NULL: a currency
        // is reference data, and neither destroying a ledger nor blanking its unit is an acceptable
        // side effect of removing one. CurrencyService.CountDeleteBlockers is the explaining pre-check
        // (and the only enforcement on the InMemory tiers); it names one clause per key declared here.
        // FileAnalysisCandidateTransactions.Currency is deliberately left without a key — it holds
        // unvetted extraction output, validated on import before it becomes a transaction.
        ConfigureCurrencyReference<Account>(modelBuilder, account => account.CurrencyCode);
        ConfigureCurrencyReference<Transaction>(modelBuilder, transaction => transaction.CurrencyCode);
        ConfigureCurrencyReference<Budget>(modelBuilder, budget => budget.BaseCurrencyCode);
        ConfigureCurrencyReference<TaxStatement>(modelBuilder, statement => statement.BaseCurrencyCode);
        ConfigureCurrencyReference<Property>(modelBuilder, property => property.CurrencyCode);
        ConfigureCurrencyReference<AccountEstimate>(modelBuilder, estimate => estimate.CurrencyCode);
        ConfigureCurrencyReference<PropertyEstimate>(modelBuilder, estimate => estimate.CurrencyCode);
        ConfigureCurrencyReference<Term>(modelBuilder, term => term.CurrencyCode);
    }

    /// <summary>The keys that cross between the Finance and Journal modules.</summary>
    private static void ConfigureCrossModuleForeignKeys(ModelBuilder modelBuilder)
    {
        // These were plain Guid columns for as long as finance and journal lived in separate contexts:
        // EF cannot declare a relationship whose principal is in another model, so the integrity was
        // reimplemented in application code (IContactLookup / IContactReferenceGuard / IFileLookup /
        // IPhotoLookup). One context makes them declarable again, and each is given the on-delete
        // behaviour the guard was imitating, so a write path that forgets to call the guard can no
        // longer leave a dangling reference.
        //
        // Declared here rather than as navigations on the entities: the modules stay one-directional in
        // the source (a Finance entity still has no Contact navigation to Include, and Mapster's
        // projections are unchanged), while the database gets the real constraint.

        // Contact ← Finance. Optional references null out with the contact, matching the pre-split
        // ON DELETE SET NULL and what IContactReferenceGuard.ClearAndCascadeReferencesAsync does.
        modelBuilder.Entity<Transaction>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(transaction => transaction.ContactId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Account>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(account => account.CustodianId)
            .OnDelete(DeleteBehavior.SetNull);

        // Issue #217. Explicit SetNull: EF's default for an optional relationship is ClientSetNull, which
        // emits RESTRICT and would make an association contact undeletable.
        modelBuilder.Entity<RealEstateDetails>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(details => details.HomeownerAssociationId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<AccountFile>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(accountFile => accountFile.IssuedBy)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ContractFile>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(contractFile => contractFile.IssuedBy)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<PropertyFile>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(propertyFile => propertyFile.IssuedBy)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<FileAnalysisCandidateTransaction>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(candidate => candidate.MatchedContactId)
            .OnDelete(DeleteBehavior.SetNull);

        // Contact → FileMetadata: the contact's one image (issue #86). SET NULL is explicit and
        // load-bearing — EF's default for an optional relationship is ClientSetNull, which emits
        // RESTRICT, and under RESTRICT deleting an avatar's file from the Files page would fail with a
        // raw FK violation instead of detaching the contact so it falls back to its type glyph.
        //
        // The opposite direction — contact deleted, avatar file deleted — is not expressible as a
        // foreign key at all and lives in ContactService.Delete's transaction, applying the shared
        // release rule. Because the EF InMemory provider enforces no foreign keys, the detach here also
        // needs its application-level counterpart on the file-delete path (ContactAvatarService), or
        // the fast test tiers exercise none of it.
        modelBuilder.Entity<Contact>()
            .HasOne<FileMetadata>()
            .WithMany()
            .HasForeignKey(contact => contact.AvatarFileId)
            .OnDelete(DeleteBehavior.SetNull);

        // A contract party IS its link to the counterparty, so it dies with the contact — the Cascade
        // the FK carried before the split, and what the guard deletes by hand today.
        modelBuilder.Entity<ContractParty>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(party => party.ContactId)
            .OnDelete(DeleteBehavior.Cascade);

        // FileMetadata ← journal/photo. Cascade matches how every in-module attachment row already
        // references the Files store (TransactionFile, AccountFile, TaxStatementFile, …): the link is
        // meaningless without its file. A library Photo is a wrapper around exactly one file, so it goes
        // the same way, sweeping its tag/person/album links and its journal-entry placements with it.
        modelBuilder.Entity<Photo>()
            .HasOne<FileMetadata>()
            .WithMany()
            .HasForeignKey(photo => photo.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<JournalEntryAttachment>()
            .HasOne<FileMetadata>()
            .WithMany()
            .HasForeignKey(attachment => attachment.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<JournalTaskAttachment>()
            .HasOne<FileMetadata>()
            .WithMany()
            .HasForeignKey(attachment => attachment.FileId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>The user-attribution columns, all <c>SET NULL</c> to <c>AspNetUsers</c>.</summary>
    private static void ConfigureUserAttribution(ModelBuilder modelBuilder)
    {
        // Twenty-three columns across seventeen entities, naming the user who created, updated,
        // attached, uploaded, requested or reviewed a row. They were bare strings for as long as identity lived in its own context, so
        // deleting a user left every one of them pointing at an account that no longer existed — the
        // gap UserAdministrationService.DeleteAsync used to record as "data in the other contexts".
        //
        // Every one is SET NULL, and the direction is not a default: this data is SHARED, not
        // user-owned. Restrict would make anyone who has ever created a journal entry, uploaded a file
        // or attached a document permanently undeletable; Cascade would destroy shared records — a
        // household's photos, journal and attachments — because one of the people who touched them left.
        // Nulling the attribution keeps the record and drops only the name, which is what the read path
        // already expects: IUserDisplayNameResolver takes a nullable id and answers "Unknown user".
        // TermsOfServiceVersion.PublishedByUserId is the same shape, and predates this block.
        //
        // Declared without navigations, matching the cross-module keys: the entities keep a plain
        // string and the Mapster projections are unchanged, while the database gets the constraint.
        // The columns are un-annotated for length so EF takes 255 from AspNetUsers.Id; a mismatched
        // width is refused outright by MariaDB (errno 150), which is why the previous 450 had to go.
        DeclareUserAttribution<Calendar>(modelBuilder, nameof(Calendar.CreatedByUserId), nameof(Calendar.UpdatedByUserId));
        DeclareUserAttribution<CalendarEvent>(modelBuilder, nameof(CalendarEvent.CreatedByUserId), nameof(CalendarEvent.UpdatedByUserId));
        DeclareUserAttribution<JournalEntry>(modelBuilder, nameof(JournalEntry.CreatedByUserId), nameof(JournalEntry.UpdatedByUserId));
        DeclareUserAttribution<JournalTask>(modelBuilder, nameof(JournalTask.CreatedByUserId), nameof(JournalTask.UpdatedByUserId));
        DeclareUserAttribution<Photo>(modelBuilder, nameof(Photo.CreatedByUserId), nameof(Photo.UpdatedByUserId));
        DeclareUserAttribution<PhotoAlbum>(modelBuilder, nameof(PhotoAlbum.CreatedByUserId), nameof(PhotoAlbum.UpdatedByUserId));
        DeclareUserAttribution<RecurrencePattern>(modelBuilder, nameof(RecurrencePattern.CreatedByUserId), nameof(RecurrencePattern.UpdatedByUserId));

        DeclareUserAttribution<AccountFile>(modelBuilder, nameof(AccountFile.AttachedByUserId));
        DeclareUserAttribution<ContractFile>(modelBuilder, nameof(ContractFile.AttachedByUserId));
        DeclareUserAttribution<PropertyFile>(modelBuilder, nameof(PropertyFile.AttachedByUserId));
        DeclareUserAttribution<TransactionFile>(modelBuilder, nameof(TransactionFile.AttachedByUserId));
        DeclareUserAttribution<TaxStatementFile>(modelBuilder, nameof(TaxStatementFile.AttachedByUserId));
        DeclareUserAttribution<OwnedEvent>(modelBuilder, nameof(OwnedEvent.CreatedByUserId));
        DeclareUserAttribution<Contract>(modelBuilder, nameof(Contract.CreatedByUserId));

        DeclareUserAttribution<FileMetadata>(modelBuilder, nameof(Odyssey.Context.FileMetadata.UploadedByUserId));
        DeclareUserAttribution<FileAnalysisJob>(modelBuilder, nameof(FileAnalysisJob.RequestedByUserId));
        DeclareUserAttribution<FileAnalysisCandidateTransaction>(modelBuilder, nameof(FileAnalysisCandidateTransaction.ReviewedByUserId));
    }

    /// <summary>
    /// Declares one or more user-attribution columns on <typeparamref name="TEntity"/> as
    /// navigation-less foreign keys to <c>AspNetUsers</c> that null out when the account is deleted.
    /// </summary>
    /// <remarks>
    /// A helper rather than twenty-two hand-written declarations because the on-delete behaviour is
    /// the whole point of the block: one of them silently written as <c>Cascade</c> would delete a
    /// household's shared records the next time somebody removed a user, and a reviewer comparing
    /// twenty-two near-identical statements is exactly who misses that.
    /// </remarks>
    private static void DeclareUserAttribution<TEntity>(
        ModelBuilder modelBuilder,
        params string[] attributionColumns)
        where TEntity : class
    {
        foreach (var column in attributionColumns)
        {
            modelBuilder.Entity<TEntity>()
                .HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(column)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }

    /// <summary>
    /// Declares <paramref name="currencyColumn"/> on <typeparamref name="TEntity"/> as a
    /// navigation-less <c>RESTRICT</c> foreign key to <c>Currencies.CurrencyCode</c> (issue #241).
    /// </summary>
    private static void ConfigureCurrencyReference<TEntity>(
        ModelBuilder modelBuilder,
        System.Linq.Expressions.Expression<Func<TEntity, object?>> currencyColumn)
        where TEntity : class
    {
        modelBuilder.Entity<TEntity>()
            .HasOne<Currency>()
            .WithMany()
            .HasForeignKey(currencyColumn)
            .HasPrincipalKey(currency => currency.CurrencyCode)
            .OnDelete(DeleteBehavior.Restrict);
    }

    // ── Finance ───────────────────────────────────────────────────────────────────────────────
    public DbSet<Account> Accounts { get; set; }
    public DbSet<Term> Terms { get; set; }
    public DbSet<AccountEstimate> AccountEstimates { get; set; }
    public DbSet<AccountSmartTag> AccountSmartTags { get; set; }
    public DbSet<Transaction> Transactions { get; set; }
    public DbSet<Budget> Budgets { get; set; }
    public DbSet<BudgetItem> BudgetItems { get; set; }
    public DbSet<TransactionTag> TransactionTags { get; set; }
    public DbSet<TransactionTagLink> TransactionTagLinks { get; set; }
    public DbSet<Currency> Currencies { get; set; }
    public DbSet<ExchangeRate> ExchangeRates { get; set; }
    public DbSet<TransactionFile> TransactionFiles { get; set; }
    public DbSet<AccountFile> AccountFiles { get; set; }
    public DbSet<FileMetadata> FileMetadata { get; set; }
    public DbSet<FileBlob> FileBlob { get; set; }
    public DbSet<FileAnalysisJob> FileAnalysisJobs { get; set; }
    public DbSet<FileAnalysisCandidateTransaction> FileAnalysisCandidateTransactions { get; set; }
    public DbSet<FileAnalysisCandidateTag> FileAnalysisCandidateTags { get; set; }
    public DbSet<TaxStatement> TaxStatements { get; set; }
    public DbSet<TaxStatementTag> TaxStatementTags { get; set; }
    public DbSet<TaxStatementFile> TaxStatementFiles { get; set; }
    public DbSet<Contract> Contracts { get; set; }
    public DbSet<ContractParty> ContractParties { get; set; }
    public DbSet<ContractFile> ContractFiles { get; set; }
    public DbSet<ContractEvent> ContractEvents { get; set; }
    public DbSet<ContractSmartTag> ContractSmartTags { get; set; }

    // ── Properties (issue #167) ───────────────────────────────────────────────────────────────
    public DbSet<Property> Properties { get; set; }
    public DbSet<RealEstateDetails> RealEstateDetails { get; set; }
    public DbSet<VehicleDetails> VehicleDetails { get; set; }
    public DbSet<PropertyEstimate> PropertyEstimates { get; set; }
    public DbSet<PropertySmartTag> PropertySmartTags { get; set; }
    public DbSet<PropertyFile> PropertyFiles { get; set; }
    public DbSet<PropertyEvent> PropertyEvents { get; set; }

    // ── Journal, tasks, photos, calendars and contacts ────────────────────────────────────────
    public DbSet<JournalEntry> JournalEntries { get; set; }
    public DbSet<JournalTag> JournalTags { get; set; }
    public DbSet<JournalEntryTag> JournalEntryTags { get; set; }
    public DbSet<JournalEntryContact> JournalEntryContacts { get; set; }
    public DbSet<JournalEntryPhoto> JournalEntryPhotos { get; set; }
    public DbSet<JournalEntryAttachment> JournalEntryAttachments { get; set; }
    public DbSet<JournalTask> JournalTasks { get; set; }
    public DbSet<JournalTaskTag> JournalTaskTags { get; set; }
    public DbSet<JournalTaskTagLink> JournalTaskTagLinks { get; set; }
    public DbSet<JournalTaskAttachment> JournalTaskAttachments { get; set; }

    // Photo Library (issue #321), merged in.
    public DbSet<Photo> Photos { get; set; }
    public DbSet<PhotoTag> PhotoTags { get; set; }
    public DbSet<PhotoTagLink> PhotoTagLinks { get; set; }
    public DbSet<PhotoPerson> PhotoPeople { get; set; }
    public DbSet<PhotoAlbum> PhotoAlbums { get; set; }
    public DbSet<PhotoAlbumItem> PhotoAlbumItems { get; set; }

    // Calendar (issue #330), merged in.
    public DbSet<Calendar> Calendars { get; set; }
    public DbSet<CalendarEvent> CalendarEvents { get; set; }
    public DbSet<RecurrencePattern> RecurrencePatterns { get; set; }

    // Contact aggregate (issue #325).
    public DbSet<Contact> Contacts { get; set; }
    public DbSet<PersonDetails> PersonDetails { get; set; }
    public DbSet<OrganizationDetails> OrganizationDetails { get; set; }
    public DbSet<ContactAlias> ContactAliases { get; set; }
    public DbSet<Address> Addresses { get; set; }
    public DbSet<EmailAddress> EmailAddresses { get; set; }
    public DbSet<PhoneNumber> PhoneNumbers { get; set; }

    // ── Identity, profiles, preferences, settings and legal ───────────────────────────────────
    // The seven sets from the former ApplicationContext; IdentityDbContext<ApplicationUser> supplies
    // Users, Roles, UserRoles, UserClaims, UserLogins, UserTokens and RoleClaims on top of these.
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();

    /// <summary>
    /// A user's one profile picture (issue #94). Deliberately NOT in the domain file store: the pair
    /// below shares no relationship with <see cref="FileMetadata"/>/<see cref="FileBlob"/> in either
    /// direction, which is what keeps a face out of reach of every <c>files.*</c> claim and out of the
    /// admin file export. Both are declared out of scope on <c>DataExportTableCoverageTests</c>,
    /// alongside the identity tables they belong with.
    /// </summary>
    public DbSet<UserProfileImage> UserProfileImages => Set<UserProfileImage>();

    /// <inheritdoc cref="UserProfileImages" />
    public DbSet<UserProfileImageBlob> UserProfileImageBlobs => Set<UserProfileImageBlob>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    /// <summary>
    /// Encrypted secret settings (issue #444). A deliberately SEPARATE set from
    /// <see cref="SystemSettings"/>: every enumeration of that one projects onto the read DTO, so
    /// ciphertext sharing the table would ride along on the wire unless somebody remembered a filter.
    /// It carries no <c>HasData</c> seed and no compiled default — an absent row is a secret's correct
    /// initial state, and that is the one place CLAUDE.md's "adding a setting" recipe does not apply.
    /// </summary>
    public DbSet<Secrets.SystemSettingSecret> SystemSettingSecrets => Set<Secrets.SystemSettingSecret>();

    public DbSet<LicenseAcceptance> LicenseAcceptances => Set<LicenseAcceptance>();

    public DbSet<TermsOfServiceVersion> TermsOfServiceVersions => Set<TermsOfServiceVersion>();

    public DbSet<TermsOfServiceAcceptance> TermsOfServiceAcceptances => Set<TermsOfServiceAcceptance>();
}
