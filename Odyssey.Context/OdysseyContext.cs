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
/// keys (see <b>Cross-module foreign keys</b> below); folding identity in turned the twenty-three
/// user-attribution columns into real foreign keys too (see <b>User attribution foreign keys</b>),
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
    // is far below the threshold the Insurance-field cache exists to protect, and a stale read here
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

        // ── Finance ───────────────────────────────────────────────────────────────────────────────
        modelBuilder.Entity<TransactionFile>(entity =>
        {
            entity.Property(tf => tf.Type)
                .IsRequired()
                .HasDefaultValue(TransactionFileType.Other)
                .HasSentinel(TransactionFileType.Other)
                .HasConversion<int>();

            entity.ToTable(tb => tb.HasCheckConstraint(
                "CK_TransactionFiles_Type_AllowedValues",
                "`Type` IN (0, 1, 2, 3, 4, 5, 6)"));
        });

        modelBuilder.Entity<FileAnalysisJob>(entity =>
        {
            entity.Property(j => j.Status)
                .IsRequired()
                .HasDefaultValue(FileAnalysisJobStatus.New)
                .HasSentinel(FileAnalysisJobStatus.New)
                .HasConversion<int>();

            entity.Property(j => j.AnalyzerProvider)
                .IsRequired()
                .HasDefaultValue(AnalyzerProvider.None)
                .HasSentinel(AnalyzerProvider.None)
                .HasConversion<int>();

            entity.Property(j => j.MatchStatus)
                .IsRequired()
                .HasDefaultValue(FileAnalysisMatchStatus.NotRun)
                .HasSentinel(FileAnalysisMatchStatus.NotRun)
                .HasConversion<int>();
        });

        modelBuilder.Entity<FileAnalysisCandidateTransaction>(entity =>
        {
            entity.Property(ct => ct.ReviewStatus)
                .IsRequired()
                .HasDefaultValue(CandidateTransactionReviewStatus.Pending)
                .HasSentinel(CandidateTransactionReviewStatus.Pending)
                .HasConversion<int>();

            entity.Property(ct => ct.MatchMethod)
                .IsRequired()
                .HasDefaultValue(MatchMethod.None)
                .HasSentinel(MatchMethod.None)
                .HasConversion<int>();

            // MatchedContactId's FK to Contact is declared with the other cross-module keys below.
        });

        modelBuilder.Entity<FileAnalysisCandidateTag>(entity =>
        {
            // Cascade from the candidate (its match set is meaningless once it's gone); cascade from
            // the tag too — a deleted tag drops its candidate links.
            entity.HasOne(ct => ct.CandidateTransaction)
                .WithMany(c => c.MatchedTags)
                .HasForeignKey(ct => ct.CandidateTransactionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ct => ct.TransactionTag)
                .WithMany()
                .HasForeignKey(ct => ct.TransactionTagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccountTerm>(entity =>
        {
            // Cascade-delete a term history along with its parent account: the timeline is
            // meaningless once the account is gone, and terms are only reachable through it.
            entity.HasOne(term => term.Account)
                .WithMany(account => account.AccountTerms)
                .HasForeignKey(term => term.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccountEstimate>(entity =>
        {
            // Cascade-delete an estimate history along with its parent account: the timeline is
            // meaningless once the account is gone, and estimates are only reachable through it.
            entity.HasOne(estimate => estimate.Account)
                .WithMany(account => account.AccountEstimates)
                .HasForeignKey(estimate => estimate.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccountSmartTag>(entity =>
        {
            // Composite key: one association per (account, tag) pair, no surrogate id.
            entity.HasKey(smartTag => new { smartTag.AccountId, smartTag.TransactionTagId });

            // Cascade-delete an account's smart-tag links along with the account itself: the saved
            // filter is meaningless once the account is gone, and links are only reachable through it.
            entity.HasOne(smartTag => smartTag.Account)
                .WithMany(account => account.SmartTags)
                .HasForeignKey(smartTag => smartTag.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict on the tag side so an in-use tag cannot be hard-deleted out from under an
            // account's smart-tag configuration (mirrors the TransactionTagLink precedent).
            entity.HasOne(smartTag => smartTag.TransactionTag)
                .WithMany(tag => tag.AccountSmartTags)
                .HasForeignKey(smartTag => smartTag.TransactionTagId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExchangeRate>(entity =>
        {
            // 1 unit of From = Rate units of To; decimal(18,8) keeps the precision the
            // ISO minor-unit range never needs to round prematurely during conversion.
            entity.Property(rate => rate.Rate)
                .HasPrecision(18, 8);

            // FK to the currency table for both ends of the pair. No cascade delete: rate rows are
            // never removed as a side effect of another change (only in-place Rate/AsOf corrections
            // or an explicit delete), so a currency should not be removable out from under them by
            // accident.
            entity.HasOne<Currency>()
                .WithMany()
                .HasForeignKey(rate => rate.FromCurrencyCode)
                .HasPrincipalKey(currency => currency.CurrencyCode)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Currency>()
                .WithMany()
                .HasForeignKey(rate => rate.ToCurrencyCode)
                .HasPrincipalKey(currency => currency.CurrencyCode)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaxStatement>(entity =>
        {
            entity.Property(s => s.Status)
                .IsRequired()
                .HasDefaultValue(TaxStatementStatus.New)
                .HasSentinel(TaxStatementStatus.New)
                .HasConversion<int>();
        });

        modelBuilder.Entity<Transaction>()
            .HasMany(transaction => transaction.TransactionTags)
            .WithMany(tag => tag.Transactions)
            .UsingEntity<TransactionTagLink>(
                right => right
                    .HasOne(link => link.TransactionTag)
                    .WithMany(tag => tag.TransactionTagLinks)
                    .HasForeignKey(link => link.TransactionTagId)
                    // Restrict so an in-use tag cannot be hard-deleted (matches the prior single-tag behavior).
                    .OnDelete(DeleteBehavior.Restrict),
                left => left
                    .HasOne(link => link.Transaction)
                    .WithMany(transaction => transaction.TransactionTagLinks)
                    .HasForeignKey(link => link.TransactionId)
                    // Cascade so a transaction's tag links die with it.
                    .OnDelete(DeleteBehavior.Cascade),
                join =>
                {
                    join.HasKey(link => link.Id);
                    join.HasIndex(link => new { link.TransactionId, link.TransactionTagId }).IsUnique();
                });

        // Non-unique performance indexes backing the server-side list sort/filter (issue #277).
        // Only the missing ones are added; Files/TaxStatements/ExchangeRates are already indexed.
        modelBuilder.Entity<Transaction>().HasIndex(transaction => transaction.TimeStamp);
        modelBuilder.Entity<Account>().HasIndex(account => account.Name);

        modelBuilder.Entity<TaxStatementTag>(entity =>
        {
            entity.Property(t => t.Role)
                .IsRequired()
                .HasConversion<int>();
        });

        modelBuilder.Entity<InsurancePolicy>(entity =>
        {
            entity.Property(p => p.Type)
                .IsRequired()
                .HasDefaultValue(InsurancePolicyType.Other)
                .HasSentinel(InsurancePolicyType.Other)
                .HasConversion<int>();
        });

        modelBuilder.Entity<InsurancePolicyFile>(entity =>
        {
            entity.Property(f => f.FileType)
                .IsRequired()
                .HasDefaultValue(PolicyFileType.Other)
                .HasSentinel(PolicyFileType.Other)
                .HasConversion<int>();
        });

        modelBuilder.Entity<PolicyRenewalFile>(entity =>
        {
            entity.Property(f => f.FileType)
                .IsRequired()
                .HasDefaultValue(PolicyFileType.Other)
                .HasSentinel(PolicyFileType.Other)
                .HasConversion<int>();
        });

        modelBuilder.Entity<Contract>(entity =>
        {
            entity.Property(c => c.Type)
                .IsRequired()
                .HasDefaultValue(ContractType.Other)
                .HasSentinel(ContractType.Other)
                .HasConversion<int>();
        });

        modelBuilder.Entity<ContractParty>(entity =>
        {
            // One-of-three (XOR) invariant: a party links to exactly one target (issue #174 §6).
            // The service layer is the real guard (returns 400); this CHECK is a DB backstop declared
            // on the model so it lands in the snapshot. The app only runs on MariaDB, which honours it.
            entity.ToTable(tb => tb.HasCheckConstraint(
                "CK_ContractParties_ExactlyOneTarget",
                "((`AccountId` IS NOT NULL) + (`ContactId` IS NOT NULL) + (`InsurancePolicyId` IS NOT NULL)) = 1"));
        });

        modelBuilder.Entity<ContractFile>(entity =>
        {
            entity.Property(f => f.FileType)
                .IsRequired()
                .HasDefaultValue(ContractFileType.Other)
                .HasSentinel(ContractFileType.Other)
                .HasConversion<int>();
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.Property(s => s.Interval)
                .IsRequired()
                .HasDefaultValue(BillingInterval.Monthly)
                .HasSentinel(BillingInterval.Monthly)
                .HasConversion<int>();

            // Cadence multiplier, always >= 1; DB default 1 so a value-omitting insert reads as
            // "every unit" rather than an invalid 0.
            entity.Property(s => s.IntervalCount)
                .IsRequired()
                .HasDefaultValue(1);
        });
        // ── Journal, tasks, photos, calendars and contacts ────────────────────────────────────────
        modelBuilder.Entity<JournalEntryTag>(entity =>
        {
            // Surrogate Guid PK (every entity owns its own id); the natural key is a unique index.
            entity.HasIndex(link => new { link.JournalEntryId, link.JournalTagId }).IsUnique();

            // Cascade so an entry's tag links die with it.
            entity.HasOne(link => link.JournalEntry)
                .WithMany(journalEntry => journalEntry.EntryTags)
                .HasForeignKey(link => link.JournalEntryId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict so an in-use tag cannot be hard-deleted out from under an entry.
            entity.HasOne(link => link.JournalTag)
                .WithMany(tag => tag.EntryTags)
                .HasForeignKey(link => link.JournalTagId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JournalEntryContact>(entity =>
        {
            entity.HasIndex(link => new { link.JournalEntryId, link.ContactId }).IsUnique();

            entity.HasOne(link => link.JournalEntry)
                .WithMany(journalEntry => journalEntry.Contacts)
                .HasForeignKey(link => link.JournalEntryId)
                .OnDelete(DeleteBehavior.Cascade);

            // Contact now lives in this context (moved from Finance): the link is a real FK. Cascade so
            // an entry's contact links die with the contact, matching the other link-row conventions.
            entity.HasOne<Contact>()
                .WithMany()
                .HasForeignKey(link => link.ContactId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JournalEntryPhoto>(entity =>
        {
            entity.HasOne(photo => photo.JournalEntry)
                .WithMany(journalEntry => journalEntry.Photos)
                .HasForeignKey(photo => photo.JournalEntryId)
                .OnDelete(DeleteBehavior.Cascade);

            // Photo Library unification (issue #321 v4, Phase C): one library photo per position in an
            // entry. The 1:1 FileId↔Photo mapping preserves the old (JournalEntryId, FileId) uniqueness.
            entity.HasIndex(photo => new { photo.JournalEntryId, photo.PhotoId }).IsUnique();

            // Now that Photos and Journal share one context, PhotoId is a real FK (no inverse nav on
            // Photo). Cascade matches the other link rows into Photo (PhotoAlbumItem/PhotoPerson/
            // PhotoTagLink): deleting a library photo sweeps its journal-entry links. Two independent
            // incoming cascades on JournalEntryPhotos (from JournalEntries and Photos) is valid on
            // MariaDB/InnoDB.
            entity.HasOne<Photo>()
                .WithMany()
                .HasForeignKey(photo => photo.PhotoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JournalEntryAttachment>(entity =>
        {
            entity.HasOne(attachment => attachment.JournalEntry)
                .WithMany(journalEntry => journalEntry.Attachments)
                .HasForeignKey(attachment => attachment.JournalEntryId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(attachment => new { attachment.JournalEntryId, attachment.FileId }).IsUnique();
        });


        modelBuilder.Entity<JournalTaskTagLink>(entity =>
        {
            entity.HasIndex(link => new { link.JournalTaskId, link.JournalTaskTagId }).IsUnique();

            // Cascade so a task's tag links die with it.
            entity.HasOne(link => link.JournalTask)
                .WithMany(item => item.ItemTags)
                .HasForeignKey(link => link.JournalTaskId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict so an in-use tag cannot be hard-deleted out from under a task.
            entity.HasOne(link => link.JournalTaskTag)
                .WithMany(tag => tag.ItemTags)
                .HasForeignKey(link => link.JournalTaskTagId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JournalTaskAttachment>(entity =>
        {
            entity.HasOne(attachment => attachment.JournalTask)
                .WithMany(item => item.Attachments)
                .HasForeignKey(attachment => attachment.JournalTaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(attachment => new { attachment.JournalTaskId, attachment.FileId }).IsUnique();
        });

        // ── Photo Library (issue #321), merged into this context ──────────────────────────────────
        // Photo.FileId and PhotoPerson.ContactId are both real FKs; Photo.FileId is declared with the
        // other cross-module keys below.
        modelBuilder.Entity<PhotoTagLink>(entity =>
        {
            entity.HasIndex(link => new { link.PhotoId, link.PhotoTagId }).IsUnique();

            // Cascade so a photo's tag links die with it.
            entity.HasOne(link => link.Photo)
                .WithMany(photo => photo.Tags)
                .HasForeignKey(link => link.PhotoId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict so an in-use tag cannot be hard-deleted out from under a photo.
            entity.HasOne(link => link.PhotoTag)
                .WithMany(tag => tag.PhotoTags)
                .HasForeignKey(link => link.PhotoTagId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PhotoPerson>(entity =>
        {
            entity.HasIndex(link => new { link.PhotoId, link.ContactId }).IsUnique();

            entity.HasOne(link => link.Photo)
                .WithMany(photo => photo.People)
                .HasForeignKey(link => link.PhotoId)
                .OnDelete(DeleteBehavior.Cascade);

            // Contact moved into this context: the person link is a real FK. Cascade so a photo's person
            // links die with the contact.
            entity.HasOne<Contact>()
                .WithMany()
                .HasForeignKey(link => link.ContactId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Contact aggregate (issue #325) ────────────────────────────────────────────────────────
        // Contacts are a Journal-domain concept, referenced from both halves of the model. The
        // aggregate's internal relationships are declared here; the Finance references to it are
        // declared with the other cross-module keys below.
        modelBuilder.Entity<Contact>(entity =>
        {
            entity.Property(c => c.Type).HasConversion<int>();

            // 1:1 detail sub-records sharing the parent PK; cascade-delete with the contact.
            entity.HasOne(c => c.PersonDetails)
                .WithOne(p => p.Contact)
                .HasForeignKey<PersonDetails>(p => p.ContactId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.OrganizationDetails)
                .WithOne(o => o.Contact)
                .HasForeignKey<OrganizationDetails>(o => o.ContactId)
                .OnDelete(DeleteBehavior.Cascade);

            // n:1 contact collections; cascade-delete with the contact.
            entity.HasMany(c => c.Addresses)
                .WithOne(a => a.Contact)
                .HasForeignKey(a => a.ContactId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.EmailAddresses)
                .WithOne(e => e.Contact)
                .HasForeignKey(e => e.ContactId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.PhoneNumbers)
                .WithOne(p => p.Contact)
                .HasForeignKey(p => p.ContactId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PersonDetails>(entity =>
        {
            entity.Property(p => p.RelationshipType).HasConversion<int>();
            entity.Property(p => p.Sex).HasConversion<int>();
        });

        modelBuilder.Entity<Address>().Property(a => a.Label).HasConversion<int>();
        modelBuilder.Entity<EmailAddress>().Property(e => e.Label).HasConversion<int>();
        modelBuilder.Entity<PhoneNumber>().Property(p => p.Label).HasConversion<int>();

        modelBuilder.Entity<PhotoAlbumItem>(entity =>
        {
            entity.HasIndex(item => new { item.PhotoAlbumId, item.PhotoId }).IsUnique();

            entity.HasOne(item => item.PhotoAlbum)
                .WithMany(album => album.Items)
                .HasForeignKey(item => item.PhotoAlbumId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(item => item.Photo)
                .WithMany(photo => photo.Albums)
                .HasForeignKey(item => item.PhotoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PhotoAlbum>(entity =>
        {
            // Optional cover photo: a real in-context FK that nulls out when the referenced photo is
            // deleted, rather than dangling (§6). No inverse navigation on Photo.
            entity.HasOne(album => album.CoverPhoto)
                .WithMany()
                .HasForeignKey(album => album.CoverPhotoId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Calendar (issue #330), merged into this context ───────────────────────────────────────
        modelBuilder.Entity<CalendarEvent>(entity =>
        {
            // Cascade so a calendar's events die with it. In normal operation the service layer
            // blocks DELETE /api/calendars/{id} with 409 while the calendar has any events or
            // patterns — this cascade exists purely as a DB-level safety net.
            entity.HasOne(calendarEvent => calendarEvent.Calendar)
                .WithMany(calendar => calendar.Events)
                .HasForeignKey(calendarEvent => calendarEvent.CalendarId)
                .OnDelete(DeleteBehavior.Cascade);

            // SetNull: only relevant when a pattern is deleted directly (not via a calendar
            // cascade). RecurrencePatternService hard-deletes future generated events itself and
            // relies on this to detach (not destroy) past/current ones.
            entity.HasOne(calendarEvent => calendarEvent.RecurrencePattern)
                .WithMany(pattern => pattern.GeneratedEvents)
                .HasForeignKey(calendarEvent => calendarEvent.RecurrencePatternId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RecurrencePattern>(entity =>
        {
            entity.HasOne(pattern => pattern.Calendar)
                .WithMany(calendar => calendar.RecurrencePatterns)
                .HasForeignKey(pattern => pattern.CalendarId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ExternalUid is the row's external identity anchor (issues #337/#339). VTODO/VJOURNAL/vCard UIDs
        // are case-sensitive, and the import matches them with StringComparer.Ordinal — so the unique index
        // must be case-sensitive too. A binary collation makes DB uniqueness agree with that matching
        // (the default utf8mb4 collation is case-insensitive, which would reject case-variant UIDs).
        modelBuilder.Entity<JournalTask>()
            .Property(task => task.ExternalUid)
            .UseCollation("utf8mb4_bin");

        modelBuilder.Entity<JournalEntry>()
            .Property(entry => entry.ExternalUid)
            .UseCollation("utf8mb4_bin");

        // ── Cross-module foreign keys ─────────────────────────────────────────────────────────────
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

        modelBuilder.Entity<Subscription>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(subscription => subscription.ContactId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Account>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(account => account.CustodianId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<AccountFile>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(accountFile => accountFile.IssuedBy)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<FileAnalysisCandidateTransaction>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(candidate => candidate.MatchedContactId)
            .OnDelete(DeleteBehavior.SetNull);

        // A contract party IS its link to the counterparty, so it dies with the contact — the Cascade
        // the FK carried before the split, and what the guard deletes by hand today.
        modelBuilder.Entity<ContractParty>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(party => party.ContactId)
            .OnDelete(DeleteBehavior.Cascade);

        // The insurer is required, so it restricts instead: a contact still named on a policy cannot be
        // deleted. ContactService keeps calling IContactReferenceGuard.IsReferencedAsInsurerAsync first
        // so the caller still gets a 409 explaining why, rather than a raw FK violation surfacing as 500.
        modelBuilder.Entity<InsurancePolicy>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(policy => policy.InsurerId)
            .OnDelete(DeleteBehavior.Restrict);

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

        // ── Identity, profiles, preferences, settings and legal ───────────────────────────────────
        // The former ApplicationContext's model, folded in unchanged. IdentityDbContext's own
        // configuration has already run via base.OnModelCreating at the top of this method.

        // 1:1 with ApplicationUser: own Guid PK + separate UserId FK with a unique index enforcing the
        // one-profile-per-user rule; cascade delete removes the profile with the user (issue #316 §6).
        modelBuilder.Entity<UserProfile>(entity =>
        {
            entity.HasOne<ApplicationUser>()
                .WithOne()
                .HasForeignKey<UserProfile>(profile => profile.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Migration-seeded singleton-per-key rows (issue #349): Key is the primary key (a natural
        // key), so seeding is a plain HasData insert, not the positional-id hand-written-migration
        // dance the permission-claim seeds below need. GET assembles the DTO from these five rows and
        // never writes, so a fixed UpdatedAt/null UpdatedBy at seed time is the correct "nobody has
        // touched this yet" starting state.
        var seededAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        modelBuilder.Entity<SystemSetting>().HasData(
            new SystemSetting { Key = SystemSettingsKeys.RequireTwoFactor, Value = "false", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.RegistrationRequireAdminApproval, Value = "true", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailRequireConfirmation, Value = "true", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.InsuranceExpiringSoonWindowDays, Value = "30", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.InsuranceMaxSummaryPolicies, Value = "1000", UpdatedAt = seededAt },
            // Import/export volume caps (issue #343 §6/§15) — seeded to today's effective values so
            // out-of-the-box behavior is unchanged. The two vCard count caps seed "unlimited" (today's
            // effective int.MaxValue); the three ICS surfaces keep their existing 2,000-derived count
            // defaults. All eight size (MB) fields below are seeded at 64, a later unification of what
            // was originally a per-surface split (see SystemSettingsKeys' doc comment).
            new SystemSetting { Key = SystemSettingsKeys.ContactVCardMaxExportRows, Value = SystemSettingsDefaults.ContactVCardMaxExportRows, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContactVCardMaxImportEntries, Value = SystemSettingsDefaults.ContactVCardMaxImportEntries, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContactVCardMaxImportMegabytes, Value = "64", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxExportEvents, Value = "2000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxImportEvents, Value = "2000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxImportMegabytes, Value = "64", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.TaskIcsMaxImportTasks, Value = "2000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.TaskIcsMaxImportMegabytes, Value = "64", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.JournalIcsMaxExportRows, Value = "2000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.JournalIcsMaxImportEntries, Value = "2000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.JournalIcsMaxImportMegabytes, Value = "64", UpdatedAt = seededAt },
            // Export-side follow-up (post-#343): a "maximum export file size" per surface, plus a Tasks
            // export row cap (Tasks previously had none — see SystemSettingsKeys' doc comment).
            new SystemSetting { Key = SystemSettingsKeys.ContactVCardMaxExportMegabytes, Value = "64", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxExportMegabytes, Value = "64", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.TaskIcsMaxExportTasks, Value = "2000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.TaskIcsMaxExportMegabytes, Value = "64", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.JournalIcsMaxExportMegabytes, Value = "64", UpdatedAt = seededAt },
            // AI file-analysis policy and processor disclosure (issue #421 Wave 1). Values mirror
            // today's effective behaviour, with one correction: MaxFutureTransactionDays was 90 in
            // appsettings.json and 30 on FileAnalysisOptions, and 90 is what ran.
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisProcessor, Value = SystemSettingsDefaults.FileAnalysisProcessor, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisProcessorRegion, Value = SystemSettingsDefaults.FileAnalysisProcessorRegion, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisLawfulBasis, Value = SystemSettingsDefaults.FileAnalysisLawfulBasis, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisPrivacyNoticeUrl, Value = SystemSettingsDefaults.FileAnalysisPrivacyNoticeUrl, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisMaxFutureTransactionDays, Value = "90", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisMatchAutoLinkThreshold, Value = "0.6", UpdatedAt = seededAt },
            // Transactional email (issue #421 Wave 2). FromAddress/FromName had live environment
            // plumbing, so SystemSettingsConfigAdoption carries an operator's configured value over
            // these seeded defaults on upgrade — a compile-time seed alone would silently change the
            // sender identity of every outgoing message.
            new SystemSetting { Key = SystemSettingsKeys.EmailFromAddress, Value = SystemSettingsDefaults.EmailFromAddress, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailFromName, Value = SystemSettingsDefaults.EmailFromName, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailPerRecipientLimit, Value = "3", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailPerRecipientWindowMinutes, Value = "60", UpdatedAt = seededAt },
            // Per-request defensive caps (issue #421 Wave 3). These had no appsettings entry and no
            // environment plumbing at all — they were POCO defaults and two `private const`s — so no
            // config-adoption entry is needed: there was never a configured value to carry over.
            new SystemSetting { Key = SystemSettingsKeys.ContractMaxPartiesPerContract, Value = "25", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContractMaxFilesPerContract, Value = "50", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContractMaxSummaryContracts, Value = "1000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.InsuranceMaxRenewalsPerPolicy, Value = "100", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.InsuranceMaxFilesPerParent, Value = "50", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.PhotoMaxLinksPerKind, Value = "50", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.PhotoMaxAlbumMembers, Value = "1000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.JournalEntryMaxLinksPerKind, Value = "50", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.JournalTaskMaxLinksPerKind, Value = "50", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileStorageMaxUploadMegabytes, Value = "64", UpdatedAt = seededAt },
            // The last compiled-in tuning constants (issue #434). Every seed is today's effective
            // value, so a default install is behaviourally identical after the migration — with the
            // single deliberate exception of the two Wave 3 ICS link caps, which start being honoured
            // on the import path where a hardcoded 50 used to win.
            //
            // Only the three FileAnalysis keys have a config-adoption entry: they are the only ones
            // that ever had a documented configuration surface. Ten were `const` and two were POCO
            // defaults on a section with no appsettings entry, so there was never a configured value
            // to carry over — adopting one that never had a surface would let a stray environment
            // variable start overriding an administrator's saved setting.
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisMaxTokens, Value = "8096", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisMatchMaxVocabulary, Value = "500", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisMatchTimeoutSeconds, Value = "60", UpdatedAt = seededAt },
            // Bytes on the options class, MEGABYTES here — matching the nine existing size settings.
            new SystemSetting { Key = SystemSettingsKeys.PhotoMetadataReadMegabytes, Value = "8", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.PhotoMetadataExtractionTimeoutSeconds, Value = "5", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarMaxWindowDays, Value = "92", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarMaxEventDurationDays, Value = "366", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxAggregateExportRows, Value = "20000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxAggregateOccurrences, Value = "5000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxAggregateExportWindowDays, Value = "92", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.RecurrenceMaxGeneratedOccurrences, Value = "1000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContactVCardMaxRepeatablePropertiesPerEntry, Value = "200", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ImportMaxSamplesPerSkipReason, Value = "100", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailMaxTrackedRecipients, Value = "20000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.AccountMaxSmartTagsPerAccount, Value = "20", UpdatedAt = seededAt },
            // The file-analysis kill switch, model and destination (issue #439). Seeded to today's
            // effective values, so a default install is behaviourally identical: analysis OFF,
            // claude-sonnet-5, api.anthropic.com.
            //
            // UpdatedBy is left null by the seed, and that null is load-bearing: it is what tells
            // SystemSettingsConfigAdoption no administrator owns the row yet, so an operator's
            // configured FILE_ANALYSIS_ENABLED / _MODEL / _BASE_URL can still be carried across on
            // upgrade. InsertData is a compile-time constant and cannot see an environment variable,
            // which is precisely why all three need an adoption entry as well as a seed.
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisEnabled, Value = "false", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisModel, Value = SystemSettingsDefaults.FileAnalysisModel, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisBaseUrl, Value = SystemSettingsDefaults.FileAnalysisBaseUrl, UpdatedAt = seededAt },
            // The Subscriptions summary limits (issue #437). The first two seed the `private const`s
            // they replace exactly, so a default install is behaviourally identical; the third is the
            // one behaviour change — the summary's fetch was unbounded and is now capped at 1000,
            // matching InsuranceMaxSummaryPolicies and ContractMaxSummaryContracts.
            //
            // No SystemSettingsConfigAdoption entry for any of them, unlike the three above: none ever
            // had an appsettings.json key or environment plumbing, so there is no configured value to
            // carry across — and adopting a key that never had a surface would let a stray environment
            // variable start overriding an administrator's saved setting.
            new SystemSetting { Key = SystemSettingsKeys.SubscriptionRenewalWindowDays, Value = "45", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.SubscriptionMaxSummaryRenewals, Value = "6", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.SubscriptionMaxSummarySubscriptions, Value = "1000", UpdatedAt = seededAt }
        );

        // Preferences are keyed by user id and share this context, so the link is a real FK: cascade
        // delete removes a user's persisted UI state with the user instead of an application-level purge.
        modelBuilder.Entity<UserPreference>(entity =>
        {
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(preference => preference.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Legal acceptance records (issue #354 §6). The two acceptance logs intentionally carry no FK to
        // AspNetUsers — they are compliance records that outlive the account, pseudonymized rather than
        // deleted with it. That stays true now that identity shares this context: they are the deliberate
        // exception to the user-attribution keys declared below, and must not be "fixed" into one. The
        // ToS version link is a real FK with Restrict: nothing deletes a version, and if something ever
        // tried, failing loudly is the correct outcome for an acceptance record.
        modelBuilder.Entity<TermsOfServiceVersion>(entity =>
        {
            entity.Property(version => version.Content).HasColumnType("longtext");

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(version => version.PublishedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TermsOfServiceAcceptance>(entity =>
        {
            entity.HasOne<TermsOfServiceVersion>()
                .WithMany()
                .HasForeignKey(acceptance => acceptance.TermsOfServiceVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IdentityRole>().HasData(
            new IdentityRole
            {
                Id = RoleDefinitions.AdminId,
                Name = RoleDefinitions.Admin,
                NormalizedName = RoleDefinitions.Admin.ToLowerInvariant(),
                ConcurrencyStamp = RoleDefinitions.AdminConcurrencyStamp
            },
            new IdentityRole
            {
                Id = RoleDefinitions.OwnerId,
                Name = RoleDefinitions.Owner,
                NormalizedName = RoleDefinitions.Owner.ToLowerInvariant(),
                ConcurrencyStamp = RoleDefinitions.OwnerConcurrencyStamp
            },
            new IdentityRole
            {
                Id = RoleDefinitions.UserId,
                Name = RoleDefinitions.User,
                NormalizedName = RoleDefinitions.User.ToLowerInvariant(),
                ConcurrencyStamp = RoleDefinitions.UserConcurrencyStamp
            },
            new IdentityRole
            {
                Id = RoleDefinitions.GuestId,
                Name = RoleDefinitions.Guest,
                NormalizedName = RoleDefinitions.Guest.ToLowerInvariant(),
                ConcurrencyStamp = RoleDefinitions.GuestConcurrencyStamp
            }
        );

        // Role CLAIMS are deliberately not seeded here. IdentityRoleClaim.Id is an int identity, so a
        // HasData seed has to assign ids positionally — and a counter running across all four role
        // lists means adding one claim renumbers every claim after it, scaffolding a migration full of
        // UpdateData/InsertData that renumbers unchanged rows. The repo's workaround was to hand-write
        // a raw-SQL migration per claim addition at fresh out-of-band ids and strip the renumbering out
        // of the scaffold, which left the model snapshot and every real database disagreeing on ids by
        // design. RoleClaimSeeder in Odyssey.MigrationService now reconciles the rows at runtime,
        // matching on (RoleId, ClaimType, ClaimValue) and letting the database assign ids, so adding a
        // claim needs no migration at all. Roles above stay here: their ids are fixed GUIDs.

        // ── User attribution foreign keys ─────────────────────────────────────────────────────────
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
        // TermsOfServiceVersion.PublishedByUserId above is the same shape, and predates this block.
        //
        // Declared without navigations, matching the cross-module keys above: the entities keep a plain
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
        DeclareUserAttribution<TransactionFile>(modelBuilder, nameof(TransactionFile.AttachedByUserId));
        DeclareUserAttribution<TaxStatementFile>(modelBuilder, nameof(TaxStatementFile.AttachedByUserId));
        DeclareUserAttribution<InsurancePolicyFile>(modelBuilder, nameof(InsurancePolicyFile.AttachedByUserId));
        DeclareUserAttribution<PolicyRenewalFile>(modelBuilder, nameof(PolicyRenewalFile.AttachedByUserId));

        DeclareUserAttribution<FileMetadata>(modelBuilder, nameof(Odyssey.Context.FileMetadata.UploadedByUserId));
        DeclareUserAttribution<FileAnalysisJob>(modelBuilder, nameof(FileAnalysisJob.RequestedByUserId));
        DeclareUserAttribution<FileAnalysisCandidateTransaction>(modelBuilder, nameof(FileAnalysisCandidateTransaction.ReviewedByUserId));

        modelBuilder.Entity<Currency>().HasData(
            new Currency { CurrencyCode = "AED", Name = "UAE Dirham", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "AFN", Name = "Afghani", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "AMD", Name = "Armenian Dram", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "AOA", Name = "Kwanza", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "ARS", Name = "Argentine Peso", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "AUD", Name = "Australian Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "AWG", Name = "Aruban Florin", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "AZN", Name = "Azerbaijan Manat", MinorUnits = 2, Symbol = "₼", Archived = null },
            new Currency { CurrencyCode = "BAM", Name = "Convertible Mark", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "BBD", Name = "Barbados Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "BDT", Name = "Taka", MinorUnits = 2, Symbol = "৳", Archived = null },
            new Currency { CurrencyCode = "BHD", Name = "Bahraini Dinar", MinorUnits = 3, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "BIF", Name = "Burundi Franc", MinorUnits = 0, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "BMD", Name = "Bermudian Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "BND", Name = "Brunei Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "BOB", Name = "Boliviano", MinorUnits = 2, Symbol = "Bs.", Archived = null },
            new Currency { CurrencyCode = "BOV", Name = "Mvdol", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "BRL", Name = "Brazilian Real", MinorUnits = 2, Symbol = "R$", Archived = null },
            new Currency { CurrencyCode = "BSD", Name = "Bahamian Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "BTN", Name = "Ngultrum", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "BWP", Name = "Pula", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "BYN", Name = "Belarusian Ruble", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "BZD", Name = "Belize Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "CAD", Name = "Canadian Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "CDF", Name = "Congolese Franc", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "CHE", Name = "WIR Euro", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "CHF", Name = "Swiss Franc", MinorUnits = 2, Symbol = "CHF", Archived = null },
            new Currency { CurrencyCode = "CHW", Name = "WIR Franc", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "CLF", Name = "Unidad de Fomento", MinorUnits = 4, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "CLP", Name = "Chilean Peso", MinorUnits = 0, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "CNY", Name = "Yuan Renminbi", MinorUnits = 2, Symbol = "¥", Archived = null },
            new Currency { CurrencyCode = "COP", Name = "Colombian Peso", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "COU", Name = "Unidad de Valor Real", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "CRC", Name = "Costa Rican Colon", MinorUnits = 2, Symbol = "₡", Archived = null },
            new Currency { CurrencyCode = "CUP", Name = "Cuban Peso", MinorUnits = 2, Symbol = "₱", Archived = null },
            new Currency { CurrencyCode = "CVE", Name = "Cape Verde Escudo", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "CZK", Name = "Czech Koruna", MinorUnits = 2, Symbol = "Kč", Archived = null },
            new Currency { CurrencyCode = "DJF", Name = "Djibouti Franc", MinorUnits = 0, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "DKK", Name = "Danish Krone", MinorUnits = 2, Symbol = "kr", Archived = null },
            new Currency { CurrencyCode = "DOP", Name = "Dominican Peso", MinorUnits = 2, Symbol = "RD$", Archived = null },
            new Currency { CurrencyCode = "DZD", Name = "Algerian Dinar", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "EGP", Name = "Egyptian Pound", MinorUnits = 2, Symbol = "£", Archived = null },
            new Currency { CurrencyCode = "ERN", Name = "Nakfa", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "ETB", Name = "Ethiopian Birr", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "FJD", Name = "Fiji Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "FKP", Name = "Falkland Islands Pound", MinorUnits = 2, Symbol = "£", Archived = null },
            new Currency { CurrencyCode = "GBP", Name = "Pound Sterling", MinorUnits = 2, Symbol = "£", Archived = null },
            new Currency { CurrencyCode = "GEL", Name = "Lari", MinorUnits = 2, Symbol = "₾", Archived = null },
            new Currency { CurrencyCode = "GHS", Name = "Ghana Cedi", MinorUnits = 2, Symbol = "₵", Archived = null },
            new Currency { CurrencyCode = "GIP", Name = "Gibraltar Pound", MinorUnits = 2, Symbol = "£", Archived = null },
            new Currency { CurrencyCode = "GMD", Name = "Dalasi", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "GNF", Name = "Guinean Franc", MinorUnits = 0, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "GTQ", Name = "Quetzal", MinorUnits = 2, Symbol = "Q", Archived = null },
            new Currency { CurrencyCode = "GYD", Name = "Guyana Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "HKD", Name = "Hong Kong Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "HNL", Name = "Lempira", MinorUnits = 2, Symbol = "L", Archived = null },
            new Currency { CurrencyCode = "HTG", Name = "Gourde", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "HUF", Name = "Forint", MinorUnits = 2, Symbol = "Ft", Archived = null },
            new Currency { CurrencyCode = "IDR", Name = "Rupiah", MinorUnits = 2, Symbol = "Rp", Archived = null },
            new Currency { CurrencyCode = "ILS", Name = "New Israeli Sheqel", MinorUnits = 2, Symbol = "₪", Archived = null },
            new Currency { CurrencyCode = "INR", Name = "Indian Rupee", MinorUnits = 2, Symbol = "₹", Archived = null },
            new Currency { CurrencyCode = "IQD", Name = "Iraqi Dinar", MinorUnits = 3, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "IRR", Name = "Iranian Rial", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "ISK", Name = "Iceland Krona", MinorUnits = 0, Symbol = "kr", Archived = null },
            new Currency { CurrencyCode = "JMD", Name = "Jamaican Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "JOD", Name = "Jordanian Dinar", MinorUnits = 3, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "JPY", Name = "Yen", MinorUnits = 0, Symbol = "¥", Archived = null },
            new Currency { CurrencyCode = "KES", Name = "Kenyan Shilling", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "KGS", Name = "Som", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "KHR", Name = "Riel", MinorUnits = 2, Symbol = "៛", Archived = null },
            new Currency { CurrencyCode = "KMF", Name = "Comorian Franc", MinorUnits = 0, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "KPW", Name = "North Korean Won", MinorUnits = 0, Symbol = "₩", Archived = null },
            new Currency { CurrencyCode = "KRW", Name = "Won", MinorUnits = 0, Symbol = "₩", Archived = null },
            new Currency { CurrencyCode = "KWD", Name = "Kuwaiti Dinar", MinorUnits = 3, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "KYD", Name = "Cayman Islands Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "KZT", Name = "Tenge", MinorUnits = 2, Symbol = "₸", Archived = null },
            new Currency { CurrencyCode = "LAK", Name = "Lao Kip", MinorUnits = 0, Symbol = "₭", Archived = null },
            new Currency { CurrencyCode = "LBP", Name = "Lebanese Pound", MinorUnits = 0, Symbol = "£", Archived = null },
            new Currency { CurrencyCode = "LKR", Name = "Sri Lanka Rupee", MinorUnits = 2, Symbol = "₨", Archived = null },
            new Currency { CurrencyCode = "LRD", Name = "Liberian Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "LSL", Name = "Loti", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "LYD", Name = "Libyan Dinar", MinorUnits = 3, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "MAD", Name = "Moroccan Dirham", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "MDL", Name = "Moldovan Leu", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "MGA", Name = "Malagasy Ariary", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "MMK", Name = "Kyat", MinorUnits = 2, Symbol = "Ks", Archived = null },
            new Currency { CurrencyCode = "MNT", Name = "Tugrik", MinorUnits = 2, Symbol = "₮", Archived = null },
            new Currency { CurrencyCode = "MOP", Name = "Pataca", MinorUnits = 2, Symbol = "MOP$", Archived = null },
            new Currency { CurrencyCode = "MRU", Name = "Ouguiya", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "MUR", Name = "Mauritius Rupee", MinorUnits = 2, Symbol = "₨", Archived = null },
            new Currency { CurrencyCode = "MVR", Name = "Rufiyaa", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "MWK", Name = "Malawi Kwacha", MinorUnits = 2, Symbol = "MK", Archived = null },
            new Currency { CurrencyCode = "MXN", Name = "Mexican Peso", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "MXV", Name = "Mexican Unidad de Inversion (UDI)", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "MYR", Name = "Malaysian Ringgit", MinorUnits = 2, Symbol = "RM", Archived = null },
            new Currency { CurrencyCode = "MZN", Name = "Mozambique Metical", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "NAD", Name = "Namibia Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "NGN", Name = "Naira", MinorUnits = 2, Symbol = "₦", Archived = null },
            new Currency { CurrencyCode = "NIO", Name = "Cordoba Oro", MinorUnits = 2, Symbol = "C$", Archived = null },
            new Currency { CurrencyCode = "NOK", Name = "Norwegian Krone", MinorUnits = 2, Symbol = "kr", Archived = null },
            new Currency { CurrencyCode = "NPR", Name = "Nepalese Rupee", MinorUnits = 2, Symbol = "₨", Archived = null },
            new Currency { CurrencyCode = "NZD", Name = "New Zealand Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "OMR", Name = "Rial Omani", MinorUnits = 3, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "PAB", Name = "Balboa", MinorUnits = 2, Symbol = "B/.", Archived = null },
            new Currency { CurrencyCode = "PEN", Name = "Sol", MinorUnits = 2, Symbol = "S/", Archived = null },
            new Currency { CurrencyCode = "PGK", Name = "Kina", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "PHP", Name = "Philippine Peso", MinorUnits = 2, Symbol = "₱", Archived = null },
            new Currency { CurrencyCode = "PKR", Name = "Pakistan Rupee", MinorUnits = 2, Symbol = "₨", Archived = null },
            new Currency { CurrencyCode = "PLN", Name = "Zloty", MinorUnits = 2, Symbol = "zł", Archived = null },
            new Currency { CurrencyCode = "PYG", Name = "Guarani", MinorUnits = 0, Symbol = "₲", Archived = null },
            new Currency { CurrencyCode = "QAR", Name = "Qatari Rial", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "RON", Name = "Romanian Leu", MinorUnits = 2, Symbol = "lei", Archived = null },
            new Currency { CurrencyCode = "RSD", Name = "Serbian Dinar", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "RUB", Name = "Russian Ruble", MinorUnits = 2, Symbol = "₽", Archived = null },
            new Currency { CurrencyCode = "RWF", Name = "Rwanda Franc", MinorUnits = 0, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "SAR", Name = "Saudi Riyal", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "SBD", Name = "Solomon Islands Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "SCR", Name = "Seychelles Rupee", MinorUnits = 2, Symbol = "₨", Archived = null },
            new Currency { CurrencyCode = "SDG", Name = "Sudanese Pound", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "SEK", Name = "Swedish Krona", MinorUnits = 2, Symbol = "kr", Archived = null },
            new Currency { CurrencyCode = "SGD", Name = "Singapore Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "SHP", Name = "Saint Helena Pound", MinorUnits = 2, Symbol = "£", Archived = null },
            new Currency { CurrencyCode = "SLE", Name = "Leone", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "SOS", Name = "Somali Shilling", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "SRD", Name = "Surinam Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "SSP", Name = "South Sudanese Pound", MinorUnits = 2, Symbol = "£", Archived = null },
            new Currency { CurrencyCode = "STN", Name = "Dobra", MinorUnits = 2, Symbol = "Db", Archived = null },
            new Currency { CurrencyCode = "SVC", Name = "El Salvador Colon", MinorUnits = 2, Symbol = "₡", Archived = null },
            new Currency { CurrencyCode = "SYP", Name = "Syrian Pound", MinorUnits = 2, Symbol = "£", Archived = null },
            new Currency { CurrencyCode = "SZL", Name = "Lilangeni", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "THB", Name = "Baht", MinorUnits = 2, Symbol = "฿", Archived = null },
            new Currency { CurrencyCode = "TJS", Name = "Somoni", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "TMT", Name = "Turkmenistan New Manat", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "TND", Name = "Tunisian Dinar", MinorUnits = 3, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "TOP", Name = "Pa'anga", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "TRY", Name = "Turkish Lira", MinorUnits = 2, Symbol = "₺", Archived = null },
            new Currency { CurrencyCode = "TTD", Name = "Trinidad and Tobago Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "TWD", Name = "New Taiwan Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "TZS", Name = "Tanzanian Shilling", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "UGX", Name = "Uganda Shilling", MinorUnits = 0, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "USD", Name = "US Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "USN", Name = "US Dollar (Next Day)", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "UYI", Name = "Uruguay Peso en Unidades Indexadas (UI)", MinorUnits = 0, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "UYU", Name = "Peso Uruguayo", MinorUnits = 2, Symbol = "$U", Archived = null },
            new Currency { CurrencyCode = "UYW", Name = "Unidad Previsional", MinorUnits = 4, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "UZS", Name = "Uzbekistan Sum", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "VED", Name = "Bolivar Digital", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "VND", Name = "Dong", MinorUnits = 0, Symbol = "₫", Archived = null },
            new Currency { CurrencyCode = "VUV", Name = "Vatu", MinorUnits = 0, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "WST", Name = "Tala", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "XAF", Name = "CFA Franc BEAC", MinorUnits = 0, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "XCD", Name = "East Caribbean Dollar", MinorUnits = 2, Symbol = "$", Archived = null },
            new Currency { CurrencyCode = "XCG", Name = "Caribbean Guilder", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "XOF", Name = "CFA Franc BCEAO", MinorUnits = 0, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "XPF", Name = "CFP Franc", MinorUnits = 0, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "YER", Name = "Yemeni Rial", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "ZAR", Name = "Rand", MinorUnits = 2, Symbol = "R", Archived = null },
            new Currency { CurrencyCode = "ZMW", Name = "Zambian Kwacha", MinorUnits = 2, Symbol = "ZK", Archived = null },
            new Currency { CurrencyCode = "ZWG", Name = "Zimbabwe Gold", MinorUnits = 2, Symbol = null, Archived = null },
            new Currency { CurrencyCode = "EUR", Name = "Euro", MinorUnits = 2, Symbol = "€", Archived = null },
            new Currency { CurrencyCode = "BGN", Name = "Bulgarian Lev", MinorUnits = 2, Symbol = "лв", Archived = null },
            new Currency { CurrencyCode = "MKD", Name = "Macedonian Denar", MinorUnits = 2, Symbol = "ден", Archived = null },
            new Currency { CurrencyCode = "ALL", Name = "Albanian Lek", MinorUnits = 2, Symbol = "L", Archived = null },
            new Currency { CurrencyCode = "UAH", Name = "Ukrainian Hryvnia", MinorUnits = 2, Symbol = "₴", Archived = null }
        );
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

    // ── Finance ───────────────────────────────────────────────────────────────────────────────
    public DbSet<Account> Accounts { get; set; }
    public DbSet<AccountTerm> AccountTerms { get; set; }
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
    public DbSet<InsurancePolicy> InsurancePolicies { get; set; }
    public DbSet<PolicyRenewal> PolicyRenewals { get; set; }
    public DbSet<InsurancePolicyFile> InsurancePolicyFiles { get; set; }
    public DbSet<PolicyRenewalFile> PolicyRenewalFiles { get; set; }
    public DbSet<Contract> Contracts { get; set; }
    public DbSet<ContractParty> ContractParties { get; set; }
    public DbSet<ContractFile> ContractFiles { get; set; }
    public DbSet<Subscription> Subscriptions { get; set; }

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
    public DbSet<Address> Addresses { get; set; }
    public DbSet<EmailAddress> EmailAddresses { get; set; }
    public DbSet<PhoneNumber> PhoneNumbers { get; set; }

    // ── Identity, profiles, preferences, settings and legal ───────────────────────────────────
    // The seven sets from the former ApplicationContext; IdentityDbContext<ApplicationUser> supplies
    // Users, Roles, UserRoles, UserClaims, UserLogins, UserTokens and RoleClaims on top of these.
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();

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
