using Odyssey.Core;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos.Finance;
using Odyssey.MigrationService;
using Odyssey.Dtos;
using Odyssey.TestData;
using Xunit;
using CalendarEntity = Odyssey.Context.Calendar;
using AccountType = Odyssey.Context.AccountType;
using AccountFileType = Odyssey.Context.AccountFileType;
using AnalyzerProvider = Odyssey.Context.AnalyzerProvider;
using JobStatus = Odyssey.Context.FileAnalysisJobStatus;
using ReviewStatus = Odyssey.Context.CandidateTransactionReviewStatus;
using TransactionStatus = Odyssey.Dtos.Finance.TransactionStatus;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Integration tests against a real MariaDB engine — the subset of behaviour EF InMemory cannot
/// verify: that the actual migrations apply, that FK cascade is enforced at the database, and
/// that decimal/datetime columns round-trip at full precision. Also runs the demo seeder end to
/// end to confirm referential integrity on a relational store.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class RelationalIntegrationTests(MariaDbFixture fixture)
{
    [SkippableFact]
    public async Task Migrations_apply_and_seeder_persists_with_referential_integrity()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        await using var provider = BuildSeederProvider();
        using (var scope = provider.CreateScope())
        {
            // Identity + finance + journal + photos + calendars + contacts are one context now;
            // a single MigrateAsync creates every table.
            await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.MigrateAsync();
        }

        var seeder = new DemoDataSeeder(
            provider,
            SeedEnabledConfiguration(),
            new TestHostEnvironment(),
            provider.GetRequiredService<ILogger<DemoDataSeeder>>());
        await seeder.ExecuteAsync(CancellationToken.None);

        var expected = DemoDataSet.Build();
        using var assertScope = provider.CreateScope();
        var finance = assertScope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var users = assertScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        Assert.Equal(expected.Accounts.Count, await finance.Accounts.CountAsync());
        Assert.Equal(expected.Transactions.Count, await finance.Transactions.CountAsync());
        Assert.Equal(expected.Contracts.Count, await finance.Contracts.CountAsync());
        Assert.Equal(expected.ContractParties.Count, await finance.ContractParties.CountAsync());
        Assert.Equal(expected.Subscriptions.Count, await finance.Subscriptions.CountAsync());
        Assert.Equal(DemoUsers.All.Count, await users.Users.CountAsync());

        // Journal module (issue #311): schema migrates and the seeder persists the journal graph.
        var journal = assertScope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(expected.JournalEntries.Count, await journal.JournalEntries.CountAsync());
        Assert.Equal(expected.JournalEntryTags.Count, await journal.JournalEntryTags.CountAsync());
        Assert.Equal(expected.JournalEntryContacts.Count, await journal.JournalEntryContacts.CountAsync());
        Assert.Equal(expected.JournalEntryPhotos.Count, await journal.JournalEntryPhotos.CountAsync());
        Assert.Equal(expected.JournalEntryAttachments.Count, await journal.JournalEntryAttachments.CountAsync());
        Assert.Equal(expected.JournalTasks.Count, await journal.JournalTasks.CountAsync());
        Assert.Equal(expected.JournalTaskTagLinks.Count, await journal.JournalTaskTagLinks.CountAsync());
        Assert.Equal(expected.JournalTags.Count, await journal.JournalTags.CountAsync());
        Assert.Equal(expected.JournalTaskTags.Count, await journal.JournalTaskTags.CountAsync());

        // Photos module (issue #321), now part of the merged OdysseyContext: the seeder persists the photo graph.
        var photos = journal;
        Assert.Equal(expected.Photos.Count, await photos.Photos.CountAsync());
        Assert.Equal(expected.PhotoTags.Count, await photos.PhotoTags.CountAsync());
        Assert.Equal(expected.PhotoTagLinks.Count, await photos.PhotoTagLinks.CountAsync());
        Assert.Equal(expected.PhotoPeople.Count, await photos.PhotoPeople.CountAsync());
        Assert.Equal(expected.PhotoAlbums.Count, await photos.PhotoAlbums.CountAsync());
        Assert.Equal(expected.PhotoAlbumItems.Count, await photos.PhotoAlbumItems.CountAsync());
        // Every seeded journal photo now links a library Photo that exists (unification backfill/seed).
        var libraryPhotoIds = (await photos.Photos.Select(p => p.PhotoId).ToListAsync()).ToHashSet();
        var journalPhotoIds = await journal.JournalEntryPhotos.Select(link => link.PhotoId).ToListAsync();
        Assert.All(journalPhotoIds, id => Assert.Contains(id, libraryPhotoIds));

        // Calendar module (issue #323), now part of the merged OdysseyContext: the seeder persists the calendar graph.
        var calendar = journal;
        Assert.Equal(expected.Calendars.Count, await calendar.Calendars.CountAsync());
        Assert.Equal(expected.CalendarEvents.Count, await calendar.CalendarEvents.CountAsync());
        Assert.Equal(expected.RecurrencePatterns.Count, await calendar.RecurrencePatterns.CountAsync());

        // Every transaction/account currency resolves to a seeded currency (FK + reference data).
        var orphanCurrencies = await finance.Transactions
            .CountAsync(transaction => !finance.Currencies.Any(currency => currency.CurrencyCode == transaction.CurrencyCode));
        Assert.Equal(0, orphanCurrencies);
    }

    [SkippableFact]
    public async Task Deleting_an_account_cascades_to_its_transactions()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();

        var accountId = Guid.NewGuid();
        await using (var context = NewRelationalContext())
        {
            context.Accounts.Add(new Account
            {
                AccountId = accountId,
                Name = "Cascade Test",
                Description = "cascade",
                Opened = DateTime.UtcNow,
                AccountType = AccountType.CheckingAccount,
                CurrencyCode = "USD",
            });
            context.Transactions.Add(new Transaction
            {
                TransactionId = Guid.NewGuid(),
                Description = "child",
                Amount = 10m,
                TimeStamp = DateTime.UtcNow,
                AccountId = accountId,
                CurrencyCode = "USD",
                Status = TransactionStatus.New,
                StatusChangedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        // Delete the principal directly at the database; the FK's ON DELETE CASCADE (created by the
        // migration) must remove the dependent rows. On InMemory this constraint does not exist.
        await using (var context = NewRelationalContext())
        {
            await context.Accounts.Where(account => account.AccountId == accountId).ExecuteDeleteAsync();
        }

        await using (var context = NewRelationalContext())
        {
            Assert.Equal(0, await context.Transactions.CountAsync(transaction => transaction.AccountId == accountId));
        }
    }

    [SkippableFact]
    public async Task Deleting_a_custodian_contact_sets_the_account_link_to_null()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();
        await EnsureRelationalSchemaAsync();

        var accountId = Guid.NewGuid();
        var custodianId = Guid.NewGuid();

        // Account.CustodianId is a real FK to Contact with ON DELETE SET NULL.
        await using (var journal = NewRelationalContext())
        {
            journal.Contacts.Add(NewOrganizationContact(custodianId, "Custodian Bank", "CUSTODIAN BANK"));
            await journal.SaveChangesAsync();
        }
        await using (var context = NewRelationalContext())
        {
            context.Accounts.Add(new Account
            {
                AccountId = accountId,
                Name = "Held Account",
                Description = "held",
                Opened = DateTime.UtcNow,
                AccountType = AccountType.InvestmentAccount,
                CurrencyCode = "USD",
                CustodianId = custodianId,
            });
            await context.SaveChangesAsync();
        }

        // The service path: ContactService.Delete → ContactReferenceGuard nulls the account link before
        // removing the contact, so the FK's SET NULL never has to fire. The DB-level half is covered by
        // Deleting_a_contact_at_the_database_applies_the_cross_module_on_delete_behaviours.
        await using (var journal = NewRelationalContext())
        await using (var finance = NewRelationalContext())
        {
            var service = new ContactService(journal, new ContactReferenceGuard(finance), TimeProvider.System, new ContactMutationLock(finance));
            await service.Delete(custodianId);
        }

        await using (var context = NewRelationalContext())
        {
            var account = await context.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == accountId);
            Assert.Null(account.CustodianId);

            // cleanup so this database stays reusable
            await context.Accounts.Where(a => a.AccountId == accountId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// The cross-module references are real foreign keys again now that finance and journal share one
    /// context, so the on-delete behaviours belong to the database rather than to
    /// <c>ContactReferenceGuard</c>. The guard is still in front of them on the service path (it turns
    /// the insurer RESTRICT into a 409, and it is the only implementation the InMemory tiers see), which
    /// is what the service-level tests above cover — this one deletes the contact row directly, so only
    /// the constraint can be what nulls and cascades.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_contact_at_the_database_applies_the_cross_module_on_delete_behaviours()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();
        await EnsureRelationalSchemaAsync();

        var contactId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var partyId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var accountFileId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();

        await using (var context = NewRelationalContext())
        {
            context.Contacts.Add(NewOrganizationContact(contactId, "Constrained Co", "CONSTRAINED CO"));
            context.Accounts.Add(new Account
            {
                AccountId = accountId,
                Name = "Custodied",
                Description = "custodied",
                Opened = DateTime.UtcNow,
                AccountType = AccountType.CheckingAccount,
                CurrencyCode = "USD",
                CustodianId = contactId,
            });
            context.Contracts.Add(new Contract
            {
                ContractId = contractId,
                Name = "Party contract",
                Type = Odyssey.Context.ContractType.Rental,
                StartDate = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();

            context.Transactions.Add(new Transaction
            {
                TransactionId = transactionId,
                AccountId = accountId,
                Amount = -25m,
                Description = "Constrained purchase",
                TimeStamp = DateTime.UtcNow,
                ContactId = contactId,
            });
            context.Subscriptions.Add(new Subscription
            {
                SubscriptionId = subscriptionId,
                Name = "Constrained subscription",
                Amount = 9m,
                CurrencyCode = "USD",
                Interval = Odyssey.Context.BillingInterval.Monthly,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
                FirstBillingDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedAtUtc = DateTime.UtcNow,
                ContactId = contactId,
            });
            context.ContractParties.Add(new ContractParty
            {
                ContractPartyId = partyId,
                ContractId = contractId,
                ContactId = contactId,
            });
            context.FileBlob.Add(new FileBlob { Id = blobId, Content = [1, 2, 3] });
            context.FileMetadata.Add(new FileMetadata
            {
                Id = fileId,
                UploadedByUserId = "integration-user",
                FileName = "issued.pdf",
                ContentType = "application/pdf",
                SizeBytes = 3,
                Sha256Hash = Guid.NewGuid().ToString("N"),
                FileBlobId = blobId,
                UploadedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();

            // AccountFile.IssuedBy and FileAnalysisCandidateTransaction.MatchedContactId are the two
            // SET NULL links that only ever had service-path coverage. The candidate hangs off a job,
            // which hangs off the account file, so seeding the issuer covers both in one chain.
            context.AccountFiles.Add(new AccountFile
            {
                Id = accountFileId,
                AccountId = accountId,
                FileMetadataId = fileId,
                AttachedByUserId = "integration-user",
                AttachedAtUtc = DateTime.UtcNow,
                FileType = AccountFileType.Statement,
                IssuedBy = contactId,
            });
            await context.SaveChangesAsync();

            var job = new FileAnalysisJob
            {
                AccountFileId = accountFileId,
                RequestedByUserId = "integration-user",
                Status = Odyssey.Context.FileAnalysisJobStatus.Completed,
                MatchStatus = Odyssey.Context.FileAnalysisMatchStatus.Completed,
                AnalyzerProvider = AnalyzerProvider.Claude,
            };
            context.FileAnalysisJobs.Add(job);
            await context.SaveChangesAsync();

            context.FileAnalysisCandidateTransactions.Add(new FileAnalysisCandidateTransaction
            {
                Id = candidateId,
                AnalysisJobId = job.Id,
                TransactionDate = DateTime.UtcNow,
                Description = "Constrained candidate",
                Amount = -25m,
                Currency = "USD",
                MatchedContactId = contactId,
                MerchantMatchConfidence = 0.9m,
                MatchMethod = Odyssey.Context.MatchMethod.Llm,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewRelationalContext())
        {
            await context.Contacts.Where(c => c.ContactId == contactId).ExecuteDeleteAsync();
        }

        await using (var context = NewRelationalContext())
        {
            // SET NULL: the rows survive with the reference cleared.
            Assert.Null((await context.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == accountId)).CustodianId);
            Assert.Null((await context.Transactions.AsNoTracking().SingleAsync(x => x.TransactionId == transactionId)).ContactId);
            Assert.Null((await context.Subscriptions.AsNoTracking().SingleAsync(s => s.SubscriptionId == subscriptionId)).ContactId);
            Assert.Null((await context.AccountFiles.AsNoTracking().SingleAsync(f => f.Id == accountFileId)).IssuedBy);
            Assert.Null((await context.FileAnalysisCandidateTransactions.AsNoTracking()
                .SingleAsync(c => c.Id == candidateId)).MatchedContactId);

            // CASCADE: a contract party is its link to the counterparty, so it goes; the contract stays.
            Assert.False(await context.ContractParties.AnyAsync(party => party.ContractPartyId == partyId));
            Assert.True(await context.Contracts.AnyAsync(c => c.ContractId == contractId));

            // cleanup
            await context.FileAnalysisJobs.Where(j => j.AccountFileId == accountFileId).ExecuteDeleteAsync();
            await context.AccountFiles.Where(f => f.Id == accountFileId).ExecuteDeleteAsync();
            await context.FileMetadata.Where(f => f.Id == fileId).ExecuteDeleteAsync();
            await context.FileBlob.Where(b => b.Id == blobId).ExecuteDeleteAsync();
            await context.Transactions.Where(x => x.TransactionId == transactionId).ExecuteDeleteAsync();
            await context.Subscriptions.Where(s => s.SubscriptionId == subscriptionId).ExecuteDeleteAsync();
            await context.Contracts.Where(c => c.ContractId == contractId).ExecuteDeleteAsync();
            await context.Accounts.Where(a => a.AccountId == accountId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// The RESTRICT case, at the database rather than through the guard. It is the one on-delete class
    /// that <em>refuses</em> rather than nulling or cascading, and the service test above proves only
    /// that <c>ContactService</c> declines first — it returns the same 409 it returned before the
    /// contexts merged, so it would still pass with no foreign key at all. A write path that legitimately
    /// bypasses <c>ContactReferenceGuard</c> — a repair script, a bulk admin operation — has nothing but
    /// this constraint standing between it and an insurance policy naming a contact that no longer exists.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_an_insurer_contact_at_the_database_is_refused_by_the_constraint()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();
        await EnsureRelationalSchemaAsync();

        var insurerId = Guid.NewGuid();
        var policyId = Guid.NewGuid();

        await using (var context = NewRelationalContext())
        {
            context.Contacts.Add(NewOrganizationContact(insurerId, "Restricted Insurer", "RESTRICTED INSURER"));
            await context.SaveChangesAsync();

            context.InsurancePolicies.Add(new InsurancePolicy
            {
                InsurancePolicyId = policyId,
                Name = "Restricting cover",
                Type = Odyssey.Context.InsurancePolicyType.Home,
                InsurerId = insurerId,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewRelationalContext())
        {
            await Assert.ThrowsAsync<MySqlException>(() =>
                context.Contacts.Where(c => c.ContactId == insurerId).ExecuteDeleteAsync());
        }

        await using (var context = NewRelationalContext())
        {
            // Refused, not partially applied: both rows are still there and still joined.
            Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == insurerId));
            Assert.Equal(
                insurerId,
                (await context.InsurancePolicies.AsNoTracking().SingleAsync(p => p.InsurancePolicyId == policyId)).InsurerId);

            // cleanup, policy first — the constraint under test refuses the other order.
            await context.InsurancePolicies.Where(p => p.InsurancePolicyId == policyId).ExecuteDeleteAsync();
            await context.Contacts.Where(c => c.ContactId == insurerId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// The half no application-code guard could ever provide: a write naming a contact that does not
    /// exist is refused at insert time. While the reference was a bare Guid, a caller that skipped
    /// <c>IContactLookup</c> simply stored a dangling id and nothing noticed.
    /// </summary>
    [SkippableFact]
    public async Task A_finance_row_naming_an_unknown_contact_is_refused()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();

        var accountId = Guid.NewGuid();
        await using (var context = NewRelationalContext())
        {
            context.Accounts.Add(new Account
            {
                AccountId = accountId,
                Name = "Dangling test",
                Description = "dangling",
                Opened = DateTime.UtcNow,
                AccountType = AccountType.CheckingAccount,
                CurrencyCode = "USD",
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewRelationalContext())
        {
            context.Transactions.Add(new Transaction
            {
                TransactionId = Guid.NewGuid(),
                AccountId = accountId,
                Amount = -5m,
                Description = "Points at nobody",
                TimeStamp = DateTime.UtcNow,
                ContactId = Guid.NewGuid(),
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await using (var context = NewRelationalContext())
        {
            await context.Accounts.Where(a => a.AccountId == accountId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// The other direction across the old boundary: a library <see cref="Photo"/> wraps exactly one row
    /// in the Files store, so deleting the file takes the library record — and everything hanging off
    /// it — with it, rather than leaving a photo whose image is gone.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_file_cascades_the_library_photo_that_wraps_it()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();
        await EnsureRelationalSchemaAsync();

        var blobId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var photoId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var tagLinkId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var entryAttachmentId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var taskAttachmentId = Guid.NewGuid();

        await using (var context = NewRelationalContext())
        {
            context.FileBlob.Add(new FileBlob { Id = blobId, Content = [1, 2, 3] });
            context.FileMetadata.Add(new FileMetadata
            {
                Id = fileId,
                UploadedByUserId = "integration-user",
                FileName = "library.jpg",
                ContentType = "image/jpeg",
                SizeBytes = 3,
                Sha256Hash = Guid.NewGuid().ToString("N"),
                FileBlobId = blobId,
                UploadedAtUtc = DateTime.UtcNow,
            });
            context.Photos.Add(new Photo
            {
                PhotoId = photoId,
                FileId = fileId,
                CreatedByUserId = "integration-user",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            context.PhotoTags.Add(new PhotoTag
            {
                PhotoTagId = tagId,
                Name = $"cascade-{Guid.NewGuid():N}",
            });
            await context.SaveChangesAsync();

            context.PhotoTagLinks.Add(new PhotoTagLink
            {
                PhotoTagLinkId = tagLinkId,
                PhotoId = photoId,
                PhotoTagId = tagId,
            });

            // The other two file links the merge made real. They cascade for the same reason every
            // in-module attachment row does — the link is meaningless without its file — and the PR
            // that added them proved only the Photo one, which is why they are seeded against the very
            // same file here rather than in a test of their own.
            context.JournalEntries.Add(new JournalEntry
            {
                JournalEntryId = entryId,
                ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                Title = "Cascade entry",
                Content = "cascade",
                EntryDate = DateTime.UtcNow,
                CreatedByUserId = "integration-user",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            context.JournalTasks.Add(NewJournalTaskRow("Cascade task", DateTime.UtcNow, position: 0));
            await context.SaveChangesAsync();

            var seededTaskId = await context.JournalTasks
                .Where(task => task.Title == "Cascade task")
                .Select(task => task.JournalTaskId)
                .SingleAsync();
            taskId = seededTaskId;

            context.JournalEntryAttachments.Add(new JournalEntryAttachment
            {
                JournalEntryAttachmentId = entryAttachmentId,
                JournalEntryId = entryId,
                FileId = fileId,
                CreatedAt = DateTime.UtcNow,
            });
            context.JournalTaskAttachments.Add(new JournalTaskAttachment
            {
                JournalTaskAttachmentId = taskAttachmentId,
                JournalTaskId = taskId,
                FileId = fileId,
                CreatedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewRelationalContext())
        {
            await context.FileMetadata.Where(f => f.Id == fileId).ExecuteDeleteAsync();
        }

        await using (var context = NewRelationalContext())
        {
            Assert.False(await context.Photos.AnyAsync(photo => photo.PhotoId == photoId));
            Assert.False(await context.PhotoTagLinks.AnyAsync(link => link.PhotoTagLinkId == tagLinkId));
            Assert.False(await context.JournalEntryAttachments
                .AnyAsync(attachment => attachment.JournalEntryAttachmentId == entryAttachmentId));
            Assert.False(await context.JournalTaskAttachments
                .AnyAsync(attachment => attachment.JournalTaskAttachmentId == taskAttachmentId));

            // The tag, the entry and the task are separate aggregates: only the links die with the file.
            Assert.True(await context.PhotoTags.AnyAsync(tag => tag.PhotoTagId == tagId));
            Assert.True(await context.JournalEntries.AnyAsync(entry => entry.JournalEntryId == entryId));
            Assert.True(await context.JournalTasks.AnyAsync(task => task.JournalTaskId == taskId));

            // cleanup
            await context.JournalEntries.Where(entry => entry.JournalEntryId == entryId).ExecuteDeleteAsync();
            await context.JournalTasks.Where(task => task.JournalTaskId == taskId).ExecuteDeleteAsync();
            await context.PhotoTags.Where(tag => tag.PhotoTagId == tagId).ExecuteDeleteAsync();
            await context.FileBlob.Where(b => b.Id == blobId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Deleting_a_matched_contact_sets_the_candidate_link_to_null()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();
        await EnsureRelationalSchemaAsync();

        var accountId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();

        await using (var journal = NewRelationalContext())
        {
            journal.Contacts.Add(NewOrganizationContact(contactId, "Amazon", "AMAZON"));
            await journal.SaveChangesAsync();
        }
        await using (var context = NewRelationalContext())
        {
            context.Accounts.Add(new Account
            {
                AccountId = accountId,
                Name = "Checking",
                Description = "primary",
                Opened = DateTime.UtcNow,
                AccountType = AccountType.CheckingAccount,
                CurrencyCode = "USD",
            });
            context.FileBlob.Add(new FileBlob { Id = blobId, Content = [1, 2, 3] });
            context.FileMetadata.Add(new FileMetadata
            {
                Id = fileId,
                UploadedByUserId = "user-1",
                FileName = "statement.pdf",
                ContentType = "application/pdf",
                SizeBytes = 3,
                Sha256Hash = "hash",
                FileBlobId = blobId,
                UploadedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();

            var accountFile = new AccountFile
            {
                AccountId = accountId,
                FileMetadataId = fileId,
                AttachedByUserId = "user-1",
                AttachedAtUtc = DateTime.UtcNow,
                FileType = AccountFileType.Statement,
            };
            context.AccountFiles.Add(accountFile);
            await context.SaveChangesAsync();

            var job = new FileAnalysisJob
            {
                AccountFileId = accountFile.Id,
                RequestedByUserId = "user-1",
                Status = Odyssey.Context.FileAnalysisJobStatus.Completed,
                MatchStatus = Odyssey.Context.FileAnalysisMatchStatus.Completed,
                AnalyzerProvider = AnalyzerProvider.Claude,
            };
            context.FileAnalysisJobs.Add(job);
            await context.SaveChangesAsync();

            context.FileAnalysisCandidateTransactions.Add(new FileAnalysisCandidateTransaction
            {
                Id = candidateId,
                AnalysisJobId = job.Id,
                TransactionDate = DateTime.UtcNow,
                Description = "AMZN Mktp",
                Amount = -10m,
                Currency = "USD",
                MatchedContactId = contactId,
                MerchantMatchConfidence = 0.95m,
                MatchMethod = Odyssey.Context.MatchMethod.Llm,
            });
            await context.SaveChangesAsync();
        }

        // Deleting the matched contact must SET NULL the candidate link. ContactService.Delete →
        // ContactReferenceGuard nulls MatchedContactId before removing the contact; the FK behind it
        // would do the same.
        await using (var journal = NewRelationalContext())
        await using (var finance = NewRelationalContext())
        {
            var service = new ContactService(journal, new ContactReferenceGuard(finance), TimeProvider.System, new ContactMutationLock(finance));
            await service.Delete(contactId);
        }

        await using (var context = NewRelationalContext())
        {
            var candidate = await context.FileAnalysisCandidateTransactions
                .AsNoTracking().SingleAsync(c => c.Id == candidateId);
            Assert.Null(candidate.MatchedContactId);

            // cleanup so this database stays reusable
            await context.FileAnalysisJobs.Where(j => j.AccountFile!.AccountId == accountId).ExecuteDeleteAsync();
            await context.AccountFiles.Where(af => af.AccountId == accountId).ExecuteDeleteAsync();
            await context.FileMetadata.Where(f => f.Id == fileId).ExecuteDeleteAsync();
            await context.FileBlob.Where(b => b.Id == blobId).ExecuteDeleteAsync();
            await context.Accounts.Where(a => a.AccountId == accountId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Deleting_an_insured_account_sets_the_policy_link_to_null()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();

        var insurerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        await EnsureRelationalSchemaAsync();
        await using (var journal = NewRelationalContext())
        {
            journal.Contacts.Add(NewOrganizationContact(insurerId, "Insurer Co", "INSURER CO"));
            await journal.SaveChangesAsync();
        }
        await using (var context = NewRelationalContext())
        {
            context.Accounts.Add(new Account
            {
                AccountId = accountId,
                Name = "Insured Home",
                Description = "asset",
                Opened = DateTime.UtcNow,
                AccountType = AccountType.Property,
                CurrencyCode = "USD",
            });
            context.InsurancePolicies.Add(new InsurancePolicy
            {
                InsurancePolicyId = policyId,
                Name = "Home cover",
                Type = Odyssey.Context.InsurancePolicyType.Home,
                InsurerId = insurerId,
                InsuredAccountId = accountId,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        // Deleting the insured account must SET NULL the policy link (DeleteBehavior.SetNull), not
        // cascade-delete or block the policy. This Account→InsurancePolicy FK is still a real Finance FK
        // (only the Contact link moved), so it remains a DB-level assertion. InMemory cannot enforce this.
        await using (var context = NewRelationalContext())
        {
            await context.Accounts.Where(a => a.AccountId == accountId).ExecuteDeleteAsync();
        }

        await using (var context = NewRelationalContext())
        {
            var policy = await context.InsurancePolicies.AsNoTracking().SingleAsync(p => p.InsurancePolicyId == policyId);
            Assert.Null(policy.InsuredAccountId);

            // cleanup
            await context.InsurancePolicies.Where(p => p.InsurancePolicyId == policyId).ExecuteDeleteAsync();
        }
        await using (var journal = NewRelationalContext())
        {
            await journal.Contacts.Where(c => c.ContactId == insurerId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Deleting_an_insurance_policy_cascades_renewals_and_file_joins_but_keeps_blobs()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();

        var insurerId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var renewalId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        var fileMetadataId = Guid.NewGuid();
        await EnsureRelationalSchemaAsync();
        await using (var journal = NewRelationalContext())
        {
            journal.Contacts.Add(NewOrganizationContact(insurerId, "Insurer Co", "INSURER CO"));
            await journal.SaveChangesAsync();
        }
        await using (var context = NewRelationalContext())
        {
            var blob = new FileBlob { Id = blobId, Content = [1, 2, 3] };
            context.FileBlob.Add(blob);
            context.FileMetadata.Add(new FileMetadata
            {
                Id = fileMetadataId,
                UploadedByUserId = "integration-user",
                FileName = "contract.pdf",
                ContentType = "application/pdf",
                SizeBytes = 3,
                Sha256Hash = Guid.NewGuid().ToString("N"),
                UploadedAtUtc = DateTime.UtcNow,
                FileBlobId = blobId,
                FileBlob = blob,
            });
            context.InsurancePolicies.Add(new InsurancePolicy
            {
                InsurancePolicyId = policyId,
                Name = "Home cover",
                Type = Odyssey.Context.InsurancePolicyType.Home,
                InsurerId = insurerId,
                CreatedAtUtc = DateTime.UtcNow,
            });
            context.PolicyRenewals.Add(new PolicyRenewal
            {
                PolicyRenewalId = renewalId,
                InsurancePolicyId = policyId,
                FromDate = DateTime.UtcNow,
                ToDate = DateTime.UtcNow.AddYears(1),
                Premium = 100m,
                PremiumCurrencyCode = "USD",
                CoverageAmount = 1000m,
                CoverageCurrencyCode = "USD",
                CreatedAtUtc = DateTime.UtcNow,
            });
            context.InsurancePolicyFiles.Add(new InsurancePolicyFile
            {
                Id = Guid.NewGuid(),
                InsurancePolicyId = policyId,
                FileMetadataId = fileMetadataId,
                FileType = Odyssey.Context.PolicyFileType.Contract,
                AttachedByUserId = "integration-user",
                AttachedAtUtc = DateTime.UtcNow,
            });
            context.PolicyRenewalFiles.Add(new PolicyRenewalFile
            {
                Id = Guid.NewGuid(),
                PolicyRenewalId = renewalId,
                FileMetadataId = fileMetadataId,
                FileType = Odyssey.Context.PolicyFileType.Invoice,
                AttachedByUserId = "integration-user",
                AttachedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        // Delete the policy directly at the database; ON DELETE CASCADE must remove the renewal and
        // both file-join rows (the renewal join via PolicyRenewal's own cascade), while the shared
        // FileMetadata/blob — owned by the files API — survive. InMemory enforces none of this.
        await using (var context = NewRelationalContext())
        {
            await context.InsurancePolicies.Where(p => p.InsurancePolicyId == policyId).ExecuteDeleteAsync();
        }

        await using (var context = NewRelationalContext())
        {
            Assert.Equal(0, await context.PolicyRenewals.CountAsync(r => r.InsurancePolicyId == policyId));
            Assert.Equal(0, await context.InsurancePolicyFiles.CountAsync(f => f.InsurancePolicyId == policyId));
            Assert.Equal(0, await context.PolicyRenewalFiles.CountAsync(f => f.PolicyRenewalId == renewalId));
            Assert.True(await context.FileMetadata.AnyAsync(f => f.Id == fileMetadataId));

            // cleanup
            await context.FileMetadata.Where(f => f.Id == fileMetadataId).ExecuteDeleteAsync();
            await context.FileBlob.Where(b => b.Id == blobId).ExecuteDeleteAsync();
        }
        await using (var journal = NewRelationalContext())
        {
            await journal.Contacts.Where(c => c.ContactId == insurerId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Deleting_an_insurer_contact_in_use_is_blocked_by_the_service()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();
        await EnsureRelationalSchemaAsync();

        var insurerId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        await using (var journal = NewRelationalContext())
        {
            journal.Contacts.Add(NewOrganizationContact(insurerId, "In-Use Insurer", "IN-USE INSURER"));
            await journal.SaveChangesAsync();
        }
        await using (var context = NewRelationalContext())
        {
            context.InsurancePolicies.Add(new InsurancePolicy
            {
                InsurancePolicyId = policyId,
                Name = "Active cover",
                Type = Odyssey.Context.InsurancePolicyType.Home,
                InsurerId = insurerId,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        // InsurancePolicy.InsurerId is a real ON DELETE RESTRICT FK again now that finance and journal
        // share one context. The service check runs in front of it so the caller gets a
        // DomainConflictException (409) rather than a raw FK violation; both the policy and the contact
        // must remain either way.
        await using (var journal = NewRelationalContext())
        await using (var finance = NewRelationalContext())
        {
            var service = new ContactService(journal, new ContactReferenceGuard(finance), TimeProvider.System, new ContactMutationLock(finance));
            await Assert.ThrowsAsync<DomainConflictException>(() => service.Delete(insurerId));
        }

        await using (var context = NewRelationalContext())
        {
            Assert.True(await context.InsurancePolicies.AnyAsync(p => p.InsurancePolicyId == policyId));
        }
        await using (var journal = NewRelationalContext())
        {
            Assert.True(await journal.Contacts.AnyAsync(c => c.ContactId == insurerId));
        }

        // Cleanup, policy first: the RESTRICT FK refuses the contact while the policy still names it —
        // which is the constraint under test, so the order here is part of the assertion.
        await using (var context = NewRelationalContext())
        {
            await context.InsurancePolicies.Where(p => p.InsurancePolicyId == policyId).ExecuteDeleteAsync();
        }
        await using (var journal = NewRelationalContext())
        {
            await journal.Contacts.Where(c => c.ContactId == insurerId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Decimal_and_datetime_columns_round_trip_at_full_precision()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();

        var accountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        const decimal amount = 12345.678901m;                                  // 6 decimal places
        var timestamp = new DateTime(2024, 3, 15, 10, 30, 45, DateTimeKind.Utc).AddTicks(1234560); // microseconds

        await using (var context = NewRelationalContext())
        {
            context.Accounts.Add(new Account
            {
                AccountId = accountId,
                Name = "Precision Test",
                Description = "precision",
                Opened = DateTime.UtcNow,
                AccountType = AccountType.SavingsAccount,
                CurrencyCode = "USD",
            });
            context.Transactions.Add(new Transaction
            {
                TransactionId = transactionId,
                Description = "precise",
                Amount = amount,
                TimeStamp = timestamp,
                AccountId = accountId,
                CurrencyCode = "USD",
                Status = TransactionStatus.New,
                StatusChangedAt = timestamp,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewRelationalContext())
        {
            var stored = await context.Transactions.AsNoTracking().SingleAsync(t => t.TransactionId == transactionId);
            Assert.Equal(amount, stored.Amount);
            Assert.Equal(timestamp, stored.TimeStamp);

            // cleanup so this database stays reusable
            await context.Accounts.Where(a => a.AccountId == accountId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task CalendarEvent_utc_datetime_columns_round_trip_at_full_precision()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();

        // Calendar's DateTime fields are UTC datetime(6) (spec §6) — the same fidelity question the
        // Finance decimal/datetime test above answers, checked for the Calendar module's own schema.
        var calendarId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var start = new DateTime(2026, 3, 15, 10, 30, 45, DateTimeKind.Utc).AddTicks(1234560); // microseconds
        var end = start.AddHours(1);

        await using (var context = NewRelationalContext())
        {
            context.Calendars.Add(new CalendarEntity
            {
                CalendarId = calendarId,
                Name = $"Round-trip {calendarId}",
                Color = "#1976D2",
                CreatedByUserId = "integration-user",
                CreatedAt = start,
                UpdatedAt = start,
            });
            context.CalendarEvents.Add(new CalendarEvent
            {
                CalendarEventId = eventId,
                CalendarId = calendarId,
                Title = "Precision",
                StartDateTime = start,
                EndDateTime = end,
                CreatedByUserId = "integration-user",
                CreatedAt = start,
                UpdatedAt = start,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewRelationalContext())
        {
            var stored = await context.CalendarEvents.AsNoTracking().SingleAsync(e => e.CalendarEventId == eventId);
            Assert.Equal(start, stored.StartDateTime);
            Assert.Equal(end, stored.EndDateTime);

            // cleanup so this database stays reusable
            await context.Calendars.Where(c => c.CalendarId == calendarId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Deleting_a_linked_contact_sets_the_subscription_link_to_null()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();
        await EnsureRelationalSchemaAsync();

        var contactId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        await using (var journal = NewRelationalContext())
        {
            journal.Contacts.Add(NewOrganizationContact(contactId, "Streaming Co", "STREAMING CO"));
            await journal.SaveChangesAsync();
        }
        await using (var context = NewRelationalContext())
        {
            context.Subscriptions.Add(new Subscription
            {
                SubscriptionId = subscriptionId,
                Name = "Streaming",
                ContactId = contactId,
                StartDate = new DateOnly(2026, 1, 1),
                Amount = 9.99m,
                CurrencyCode = "USD",
                Interval = Odyssey.Context.BillingInterval.Monthly,
                FirstBillingDate = new DateOnly(2026, 1, 15),
                CreatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        // Deleting the linked contact must SET NULL the subscription link. ContactService.Delete →
        // ContactReferenceGuard nulls the subscription's ContactId before removing the contact; the FK
        // behind it would do the same.
        await using (var journal = NewRelationalContext())
        await using (var finance = NewRelationalContext())
        {
            var service = new ContactService(journal, new ContactReferenceGuard(finance), TimeProvider.System, new ContactMutationLock(finance));
            await service.Delete(contactId);
        }

        await using (var context = NewRelationalContext())
        {
            var subscription = await context.Subscriptions.AsNoTracking().SingleAsync(s => s.SubscriptionId == subscriptionId);
            Assert.Null(subscription.ContactId);

            // cleanup
            await context.Subscriptions.Where(s => s.SubscriptionId == subscriptionId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Subscription_date_columns_are_real_dates_and_round_trip_with_no_time()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();

        // This is the schema's first DateOnly use, so assert both that the columns are real MariaDB
        // `date` columns AND that the values round-trip with no time component. Kept separate from the
        // decimal-fidelity test on purpose (§Rollout / architect finding #6).
        var subscriptionId = Guid.NewGuid();
        var startDate = new DateOnly(2026, 3, 15);
        var endDate = new DateOnly(2027, 3, 14);
        var firstBillingDate = new DateOnly(2026, 1, 31);

        await using (var context = NewRelationalContext())
        {
            context.Subscriptions.Add(new Subscription
            {
                SubscriptionId = subscriptionId,
                Name = "Date Round-trip",
                StartDate = startDate,
                EndDate = endDate,
                Amount = 1m,
                CurrencyCode = "USD",
                Interval = Odyssey.Context.BillingInterval.Yearly,
                FirstBillingDate = firstBillingDate,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewRelationalContext())
        {
            // The three date fields land in real `date` columns (not datetime), so no time is stored.
            var columnTypes = await context.Database
                .SqlQueryRaw<string>(
                    "SELECT DATA_TYPE AS Value FROM INFORMATION_SCHEMA.COLUMNS " +
                    "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Subscriptions' " +
                    "AND COLUMN_NAME IN ('StartDate', 'EndDate', 'FirstBillingDate')")
                .ToListAsync();
            Assert.Equal(3, columnTypes.Count);
            Assert.All(columnTypes, type => Assert.Equal("date", type));

            var stored = await context.Subscriptions.AsNoTracking().SingleAsync(s => s.SubscriptionId == subscriptionId);
            Assert.Equal(startDate, stored.StartDate);
            Assert.Equal(endDate, stored.EndDate);
            Assert.Equal(firstBillingDate, stored.FirstBillingDate);

            // cleanup so this database stays reusable
            await context.Subscriptions.Where(s => s.SubscriptionId == subscriptionId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Contract_party_check_constraint_rejects_zero_or_multiple_targets()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();

        var contractId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        await EnsureRelationalSchemaAsync();
        await using (var journal = NewRelationalContext())
        {
            journal.Contacts.Add(NewOrganizationContact(contactId, "Party Co", "PARTY CO"));
            await journal.SaveChangesAsync();
        }
        await using (var context = NewRelationalContext())
        {
            context.Accounts.Add(new Account
            {
                AccountId = accountId,
                Name = "Party Account",
                Description = "party",
                Opened = DateTime.UtcNow,
                AccountType = AccountType.CheckingAccount,
                CurrencyCode = "USD",
            });
            context.Contracts.Add(new Contract
            {
                ContractId = contractId,
                Name = "XOR contract",
                Type = Odyssey.Context.ContractType.Service,
                StartDate = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        // Zero targets → the CK_ContractParties_ExactlyOneTarget CHECK rejects the insert. InMemory
        // cannot enforce this (§16 #3); the service-layer 400 is the real guard, this is the backstop.
        await using (var context = NewRelationalContext())
        {
            context.ContractParties.Add(new ContractParty { ContractPartyId = Guid.NewGuid(), ContractId = contractId });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        // Two targets → also rejected.
        await using (var context = NewRelationalContext())
        {
            context.ContractParties.Add(new ContractParty
            {
                ContractPartyId = Guid.NewGuid(),
                ContractId = contractId,
                AccountId = accountId,
                ContactId = contactId,
            });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        // Exactly one → accepted.
        await using (var context = NewRelationalContext())
        {
            context.ContractParties.Add(new ContractParty
            {
                ContractPartyId = Guid.NewGuid(),
                ContractId = contractId,
                AccountId = accountId,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewRelationalContext())
        {
            // cleanup
            await context.Contracts.Where(c => c.ContractId == contractId).ExecuteDeleteAsync();
            await context.Accounts.Where(a => a.AccountId == accountId).ExecuteDeleteAsync();
        }
        await using (var journal = NewRelationalContext())
        {
            await journal.Contacts.Where(c => c.ContactId == contactId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Deleting_a_contract_cascades_party_and_file_links_but_keeps_targets_and_blobs()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();

        var contractId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        var fileMetadataId = Guid.NewGuid();
        await using (var context = NewRelationalContext())
        {
            context.Accounts.Add(new Account
            {
                AccountId = accountId,
                Name = "Linked Account",
                Description = "linked",
                Opened = DateTime.UtcNow,
                AccountType = AccountType.CheckingAccount,
                CurrencyCode = "USD",
            });
            var blob = new FileBlob { Id = blobId, Content = [1, 2, 3] };
            context.FileBlob.Add(blob);
            context.FileMetadata.Add(new FileMetadata
            {
                Id = fileMetadataId,
                UploadedByUserId = "integration-user",
                FileName = "contract.pdf",
                ContentType = "application/pdf",
                SizeBytes = 3,
                Sha256Hash = Guid.NewGuid().ToString("N"),
                UploadedAtUtc = DateTime.UtcNow,
                FileBlobId = blobId,
                FileBlob = blob,
            });
            context.Contracts.Add(new Contract
            {
                ContractId = contractId,
                Name = "Cascade contract",
                Type = Odyssey.Context.ContractType.Employment,
                StartDate = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
            });
            context.ContractParties.Add(new ContractParty
            {
                ContractPartyId = Guid.NewGuid(),
                ContractId = contractId,
                AccountId = accountId,
            });
            context.ContractFiles.Add(new ContractFile
            {
                ContractFileId = Guid.NewGuid(),
                ContractId = contractId,
                FileMetadataId = fileMetadataId,
                FileType = Odyssey.Context.ContractFileType.Signed,
                AttachedByUserId = "integration-user",
                AttachedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        // Delete the contract at the database; ON DELETE CASCADE removes the party + file link rows,
        // while the account, FileMetadata and blob survive (§16 #10).
        await using (var context = NewRelationalContext())
        {
            await context.Contracts.Where(c => c.ContractId == contractId).ExecuteDeleteAsync();
        }

        await using (var context = NewRelationalContext())
        {
            Assert.Equal(0, await context.ContractParties.CountAsync(p => p.ContractId == contractId));
            Assert.Equal(0, await context.ContractFiles.CountAsync(f => f.ContractId == contractId));
            Assert.True(await context.Accounts.AnyAsync(a => a.AccountId == accountId));
            Assert.True(await context.FileMetadata.AnyAsync(f => f.Id == fileMetadataId));

            // cleanup
            await context.FileMetadata.Where(f => f.Id == fileMetadataId).ExecuteDeleteAsync();
            await context.FileBlob.Where(b => b.Id == blobId).ExecuteDeleteAsync();
            await context.Accounts.Where(a => a.AccountId == accountId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Deleting_a_target_cascades_to_the_contract_party_link_only()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();
        await EnsureRelationalSchemaAsync();

        // A contract with two parties: a contact (which we will delete) and an account (which must
        // survive). Deleting the contact must remove only its party link row, leaving the contract
        // and the other party intact. ContactService.Delete → ContactReferenceGuard deletes the
        // contract-party rows for that contact, matching the ON DELETE CASCADE on the FK behind it.
        var contractId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var contactPartyId = Guid.NewGuid();
        var accountPartyId = Guid.NewGuid();
        await using (var journal = NewRelationalContext())
        {
            journal.Contacts.Add(NewOrganizationContact(contactId, "Cascade Co", "CASCADE CO"));
            await journal.SaveChangesAsync();
        }
        await using (var context = NewRelationalContext())
        {
            context.Accounts.Add(new Account
            {
                AccountId = accountId,
                Name = "Other Party Account",
                Description = "kept",
                Opened = DateTime.UtcNow,
                AccountType = AccountType.CheckingAccount,
                CurrencyCode = "USD",
            });
            context.Contracts.Add(new Contract
            {
                ContractId = contractId,
                Name = "Cascade contract",
                Type = Odyssey.Context.ContractType.Rental,
                StartDate = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
            });
            context.ContractParties.Add(new ContractParty
            {
                ContractPartyId = contactPartyId,
                ContractId = contractId,
                ContactId = contactId,
            });
            context.ContractParties.Add(new ContractParty
            {
                ContractPartyId = accountPartyId,
                ContractId = contractId,
                AccountId = accountId,
            });
            await context.SaveChangesAsync();
        }

        // Delete the contact through the service; the guard cascades the contract-party rows for it.
        await using (var journal = NewRelationalContext())
        await using (var finance = NewRelationalContext())
        {
            var service = new ContactService(journal, new ContactReferenceGuard(finance), TimeProvider.System, new ContactMutationLock(finance));
            await service.Delete(contactId);
        }

        await using (var context = NewRelationalContext())
        {
            // The contact's party link is gone; the contract and the account party survive.
            Assert.False(await context.ContractParties.AnyAsync(p => p.ContractPartyId == contactPartyId));
            Assert.True(await context.ContractParties.AnyAsync(p => p.ContractPartyId == accountPartyId));
            Assert.True(await context.Contracts.AnyAsync(c => c.ContractId == contractId));
            Assert.True(await context.Accounts.AnyAsync(a => a.AccountId == accountId));

            // cleanup
            await context.Contracts.Where(c => c.ContractId == contractId).ExecuteDeleteAsync();
            await context.Accounts.Where(a => a.AccountId == accountId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Deleting_a_contact_cascades_to_its_journal_entry_links()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();

        // JournalEntryContact.ContactId → Contact is now a real FK (Cascade) inside OdysseyContext.
        // Deleting the Contact row directly at the database must remove the link row — the new
        // intra-context FK the move introduced, which InMemory cannot enforce.
        var contactId = Guid.NewGuid();
        var journalEntryId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        await using (var context = NewRelationalContext())
        {
            context.Contacts.Add(NewOrganizationContact(contactId, "Linked Contact", "LINKED CONTACT"));
            context.JournalEntries.Add(new JournalEntry
            {
                JournalEntryId = journalEntryId,
                ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                Title = "Entry",
                Content = "body",
                EntryDate = DateTime.UtcNow,
                CreatedByUserId = "integration-user",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            context.JournalEntryContacts.Add(new JournalEntryContact
            {
                JournalEntryContactId = linkId,
                JournalEntryId = journalEntryId,
                ContactId = contactId,
            });
            await context.SaveChangesAsync();
        }

        // Delete the principal directly at the database (bypassing the service), proving DB cascade.
        await using (var context = NewRelationalContext())
        {
            await context.Contacts.Where(c => c.ContactId == contactId).ExecuteDeleteAsync();
        }

        await using (var context = NewRelationalContext())
        {
            Assert.False(await context.JournalEntryContacts.AnyAsync(l => l.JournalEntryContactId == linkId));
            Assert.True(await context.JournalEntries.AnyAsync(e => e.JournalEntryId == journalEntryId));

            // cleanup
            await context.JournalEntries.Where(e => e.JournalEntryId == journalEntryId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Deleting_a_contact_cascades_to_its_photo_person_links()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();

        // PhotoPerson.ContactId → Contact is a real FK (Cascade). Deleting the Contact row directly at
        // the database must remove the person link — a DB-level guarantee, not a service-layer one.
        var contactId = Guid.NewGuid();
        var photoId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        await using (var context = NewRelationalContext())
        {
            context.Contacts.Add(NewOrganizationContact(contactId, "Pictured Contact", "PICTURED CONTACT"));
            // Photo.FileId is a real FK to the Files store now, so the library record needs a real file
            // behind it rather than an arbitrary Guid.
            context.FileBlob.Add(new FileBlob { Id = blobId, Content = [1, 2, 3] });
            context.FileMetadata.Add(new FileMetadata
            {
                Id = fileId,
                UploadedByUserId = "integration-user",
                FileName = "pictured.jpg",
                ContentType = "image/jpeg",
                SizeBytes = 3,
                Sha256Hash = Guid.NewGuid().ToString("N"),
                UploadedAtUtc = DateTime.UtcNow,
                FileBlobId = blobId,
            });
            context.Photos.Add(new Photo
            {
                PhotoId = photoId,
                FileId = fileId,
                CreatedByUserId = "integration-user",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            context.PhotoPeople.Add(new PhotoPerson
            {
                PhotoPersonId = linkId,
                PhotoId = photoId,
                ContactId = contactId,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewRelationalContext())
        {
            await context.Contacts.Where(c => c.ContactId == contactId).ExecuteDeleteAsync();
        }

        await using (var context = NewRelationalContext())
        {
            Assert.False(await context.PhotoPeople.AnyAsync(l => l.PhotoPersonId == linkId));
            Assert.True(await context.Photos.AnyAsync(p => p.PhotoId == photoId));

            // cleanup — deleting the file cascades the library photo with it.
            await context.FileMetadata.Where(f => f.Id == fileId).ExecuteDeleteAsync();
            await context.FileBlob.Where(b => b.Id == blobId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Resumable_review_query_translates_and_reduces_to_latest_per_file()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await EnsureRelationalSchemaAsync();

        var accountId = Guid.NewGuid();
        var fileWithPending = Guid.NewGuid();
        var fileAllReviewed = Guid.NewGuid();
        Guid latestJobId;

        await using (var context = NewRelationalContext())
        {
            context.Accounts.Add(new Account
            {
                AccountId = accountId,
                Name = "Resumable",
                Description = "resumable",
                Opened = DateTime.UtcNow,
                AccountType = AccountType.CheckingAccount,
                CurrencyCode = "USD",
            });

            // File 1 — two completed jobs; the later one still has pending candidates, so it is the
            // resumable winner (and proves the latest-per-file reduction picks it over the older job).
            var af1 = AddRelationalStatement(context, accountId, fileWithPending, "stmt-1.pdf");
            AddRelationalJob(context, af1, new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc), pending: 2, reviewed: 0);
            latestJobId = AddRelationalJob(context, af1, new DateTime(2026, 6, 20, 9, 0, 0, DateTimeKind.Utc), pending: 5, reviewed: 1);

            // File 2 — a completed job whose candidates are all reviewed → not resumable (must be absent).
            var af2 = AddRelationalStatement(context, accountId, fileAllReviewed, "stmt-2.pdf");
            AddRelationalJob(context, af2, DateTime.UtcNow, pending: 0, reviewed: 3);

            await context.SaveChangesAsync();
        }

        // Exercise the REAL service query against MariaDB — what InMemory cannot prove: the SQL-projected
        // conditional COUNT, the .Where(PendingCount > 0) filter over that aggregate, and the
        // latest-per-file reduction all translate and execute on a relational engine.
        IReadOnlyList<ResumableAnalysisSummary> summaries;
        await using (var context = NewRelationalContext())
        await using (var journal = NewRelationalContext())
        {
            // FileAnalysisService now takes an IContactLookup (Contact moved to OdysseyContext). The
            // resumable-jobs read never resolves a contact, so any lookup wired over the same DB works.
            var service = new FileAnalysisService(
                context,
                new NoopAnalysisProvider(),
                new ContactLookup(journal),
                Options.Create(new FileAnalysisOptions { Enabled = true }),
                // The resumable-jobs read consults no setting; a defaults-only lookup is enough.
                new StubFileAnalysisSettingsLookup(),
                NullLogger<FileAnalysisService>.Instance);
            summaries = await service.GetResumableJobsAsync(accountId);
        }

        var summary = Assert.Single(summaries); // the all-reviewed file is uniformly absent
        Assert.Equal(fileWithPending, summary.FileId);
        Assert.Equal(latestJobId, summary.AnalysisJobId);
        Assert.Equal(6, summary.CandidateCount);
        Assert.Equal(5, summary.PendingCount);

        // cleanup so the shared database stays reusable (account delete cascades account-files → jobs →
        // candidates; the file metadata/blobs are referenced, not owned, so remove them explicitly).
        await using (var context = NewRelationalContext())
        {
            var fileIds = new[] { fileWithPending, fileAllReviewed };
            var blobIds = await context.FileMetadata
                .Where(f => fileIds.Contains(f.Id)).Select(f => f.FileBlobId).ToListAsync();
            await context.Accounts.Where(a => a.AccountId == accountId).ExecuteDeleteAsync();
            await context.FileMetadata.Where(f => fileIds.Contains(f.Id)).ExecuteDeleteAsync();
            await context.FileBlob.Where(b => blobIds.Contains(b.Id)).ExecuteDeleteAsync();
        }
    }

    // Seeds a statement file (blob + metadata + account-file join), returning the account-file id.
    private static Guid AddRelationalStatement(OdysseyContext context, Guid accountId, Guid fileMetadataId, string name)
    {
        var blobId = Guid.NewGuid();
        context.FileBlob.Add(new FileBlob { Id = blobId, Content = [1, 2, 3] });
        context.FileMetadata.Add(new FileMetadata
        {
            Id = fileMetadataId,
            UploadedByUserId = "integration-user",
            FileName = name,
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = blobId,
        });

        var accountFileId = Guid.NewGuid();
        context.AccountFiles.Add(new AccountFile
        {
            Id = accountFileId,
            AccountId = accountId,
            FileMetadataId = fileMetadataId,
            AttachedByUserId = "integration-user",
            AttachedAtUtc = DateTime.UtcNow,
            FileType = AccountFileType.Statement,
        });
        return accountFileId;
    }

    // Seeds a Completed job with the given pending/reviewed candidate counts, returning the job id.
    private static Guid AddRelationalJob(OdysseyContext context, Guid accountFileId, DateTime startedAt, int pending, int reviewed)
    {
        var jobId = Guid.NewGuid();
        context.FileAnalysisJobs.Add(new FileAnalysisJob
        {
            Id = jobId,
            AccountFileId = accountFileId,
            RequestedByUserId = "integration-user",
            Status = JobStatus.Completed,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddSeconds(5),
            AnalyzerProvider = AnalyzerProvider.Claude,
            ConsentRecorded = true,
        });

        void AddCandidate(ReviewStatus status) => context.FileAnalysisCandidateTransactions.Add(
            new FileAnalysisCandidateTransaction
            {
                Id = Guid.NewGuid(),
                AnalysisJobId = jobId,
                TransactionDate = startedAt,
                Description = "Candidate",
                Amount = -1m,
                Currency = "USD",
                ReviewStatus = status,
            });
        for (var i = 0; i < pending; i++) AddCandidate(ReviewStatus.Pending);
        for (var i = 0; i < reviewed; i++) AddCandidate(ReviewStatus.Accepted);
        return jobId;
    }

    // The resumable-jobs read never calls the provider — a no-op stand-in keeps the service constructible.
    private sealed class NoopAnalysisProvider : IFileAnalysisProvider
    {
        public Task<List<ExtractedTransaction>> ExtractTransactionsAsync(
            byte[] fileContent, string contentType, string accountCurrencyCode,
            string promptTemplate, FileAnalysisTarget target, int maxTokens,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<ExtractedTransaction>());

        public Task<List<MatchedCandidate>> MatchTransactionsAsync(
            IReadOnlyList<MatchCandidateInput> candidates,
            IReadOnlyList<VocabularyEntry> contactVocabulary,
            IReadOnlyList<VocabularyEntry> tagVocabulary,
            FileAnalysisTarget target,
            int maxTokens,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<MatchedCandidate>());
    }

    [SkippableFact]
    public async Task Task_list_sort_keys_translate_to_sql_on_mariadb()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var context = NewRelationalContext())
        {
            await context.Database.MigrateAsync();
            await context.JournalTasks.ExecuteDeleteAsync();
            context.JournalTasks.AddRange(
                NewJournalTaskRow("backlog", baseTime, position: 0),
                NewJournalTaskRow("doing", baseTime, position: 1, startedAt: baseTime),
                NewJournalTaskRow("done", baseTime, position: 0, startedAt: baseTime, completedAt: baseTime),
                NewJournalTaskRow("archived", baseTime, position: 0, archived: baseTime));
            await context.SaveChangesAsync();
        }

        await using (var context = NewRelationalContext())
        {
            var service = new JournalTaskService(context, new NoopFileLookup(), new StubJournalLimitsLookup());
            var allStatuses = Enum.GetValues<JournalTaskStatus>();

            // Every sort key must translate to SQL and execute on the real engine. Regression guard for
            // a derived sort key expressed as a CLR helper method, which the InMemory tier cannot catch.
            foreach (var sortBy in Enum.GetValues<JournalTaskSortBy>())
            {
                var page = await service.ListAsync(
                    new JournalTasksQueryParams { SortBy = sortBy, Statuses = allStatuses });
                Assert.Equal(4, page.TotalCount);
            }

            // The derived-status sort orders Backlog < Doing < Done < Archived.
            var byStatus = await service.ListAsync(new JournalTasksQueryParams
            {
                SortBy = JournalTaskSortBy.Status,
                SortDir = SortDirection.Asc,
                Statuses = allStatuses,
            });
            var ranks = byStatus.Items.Select(item => (int)item.Status).ToList();
            Assert.Equal(ranks.OrderBy(rank => rank).ToList(), ranks);
        }
    }

    [SkippableFact]
    public async Task Seeded_role_claims_exactly_match_PermissionClaims()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        // Claim *identity* is (RoleId, ClaimType, ClaimValue), never Id: IdentityRoleClaim.Id is an int
        // identity the database assigns, and nothing may depend on which numbers the rows land at. This
        // runs the real RoleClaimSeeder against a real, freshly-migrated MariaDB and proves each role
        // ends up with exactly the claims RolePermissions says it should — a claim reconciled onto the
        // wrong role, or dropped entirely, would otherwise only surface as a 403 in production.
        var connectionString = fixture.ConnectionStringFor("odyssey_roleclaims_it");
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;

        await using var context = new OdysseyContext(options);
        await context.Database.MigrateAsync();

        // The production seeder, resolved the way the migrations job resolves it, rather than a copy of
        // its logic — a reimplementation here would agree with itself while disagreeing with the job.
        await using var provider = new ServiceCollection()
            .AddDbContext<OdysseyContext>(builder =>
                builder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)))
            .BuildServiceProvider();
        await new RoleClaimSeeder(provider, NullLogger<RoleClaimSeeder>.Instance)
            .ExecuteAsync(CancellationToken.None);

        var byRole = await context.RoleClaims
            .Where(c => c.ClaimType == PermissionClaims.Type)
            .GroupBy(c => c.RoleId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(c => c.ClaimValue!).ToHashSet());

        AssertRoleClaims(RoleDefinitions.AdminId, RolePermissions.AdminClaims);
        AssertRoleClaims(RoleDefinitions.OwnerId, RolePermissions.OwnerClaims);
        AssertRoleClaims(RoleDefinitions.UserId, RolePermissions.UserClaims);
        AssertRoleClaims(RoleDefinitions.GuestId, RolePermissions.GuestClaims);

        void AssertRoleClaims(string roleId, IReadOnlyCollection<string> expectedClaims)
        {
            Assert.True(byRole.TryGetValue(roleId, out var actual),
                $"Role {roleId} has no seeded permission claims at all.");
            var expected = expectedClaims.ToHashSet();
            var missing = expected.Except(actual!).ToList();
            var unexpected = actual!.Except(expected).ToList();
            Assert.True(missing.Count == 0 && unexpected.Count == 0,
                $"Role {roleId}: missing [{string.Join(", ", missing)}], unexpected [{string.Join(", ", unexpected)}]");
        }
    }

    [SkippableFact]
    public async Task UserProfile_enforces_unique_1to1_and_cascade_delete_on_mariadb()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        // A private schema this test fully owns, so seeding AspNetUsers here never disturbs the shared
        // demo dataset the seeder test asserts exact counts against.
        var connectionString = fixture.ConnectionStringFor("odyssey_userprofile_it");

        OdysseyContext NewContext() => new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options);

        await using (var context = NewContext())
        {
            await context.Database.MigrateAsync();
        }

        const string userId = "profile-it-user";
        await using (var context = NewContext())
        {
            context.Users.Add(new ApplicationUser { Id = userId, UserName = "p@it.test", Email = "p@it.test" });
            context.UserProfiles.Add(new UserProfile { UserId = userId, FirstName = "Ada", LastName = "Lindqvist" });
            await context.SaveChangesAsync();
        }

        // The unique index on UserId enforces the 1:1 — a second profile for the same user is rejected
        // by the database (InMemory cannot represent this).
        await using (var context = NewContext())
        {
            context.UserProfiles.Add(new UserProfile { UserId = userId, FirstName = "Dup", LastName = "Licate" });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        // The FK cascade removes the profile row when its user is deleted.
        await using (var context = NewContext())
        {
            var user = await context.Users.SingleAsync(u => u.Id == userId);
            context.Users.Remove(user);
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            Assert.False(await context.UserProfiles.AnyAsync(p => p.UserId == userId));
        }
    }

    [SkippableFact]
    public async Task UserPreference_enforces_user_foreign_key_and_cascade_delete_on_mariadb()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        // UserPreference moved into OdysseyContext so UserId could become a real FK. Neither the
        // constraint nor its cascade is representable on EF InMemory, so this is the tier that proves it.
        // A private schema keeps the seeded AspNetUsers rows out of the shared demo dataset.
        var connectionString = fixture.ConnectionStringFor("odyssey_userpreference_it");

        OdysseyContext NewContext() => new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options);

        await using (var context = NewContext())
        {
            await context.Database.MigrateAsync();
        }

        const string userId = "preference-it-user";
        await using (var context = NewContext())
        {
            context.Users.Add(new ApplicationUser { Id = userId, UserName = "pref@it.test", Email = "pref@it.test" });
            context.UserPreferences.Add(NewPreferenceRow(userId, "accounts-page"));
            context.UserPreferences.Add(NewPreferenceRow(userId, "transactions-page"));
            await context.SaveChangesAsync();
        }

        // An orphan preference is now rejected by the database — the whole point of the merge.
        await using (var context = NewContext())
        {
            context.UserPreferences.Add(NewPreferenceRow("no-such-user", "accounts-page"));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        // The unique (UserId, Key) index still holds after the re-homed migrations.
        await using (var context = NewContext())
        {
            context.UserPreferences.Add(NewPreferenceRow(userId, "accounts-page"));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        // Deleting the user cascades its preferences away, replacing the old application-level purge.
        await using (var context = NewContext())
        {
            var user = await context.Users.SingleAsync(u => u.Id == userId);
            context.Users.Remove(user);
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            Assert.False(await context.UserPreferences.AnyAsync(p => p.UserId == userId));
        }
    }

    private static UserPreference NewPreferenceRow(string userId, string key) => new()
    {
        UserPreferenceId = Guid.NewGuid(),
        UserId = userId,
        Key = key,
        PreferencesJson = "{}",
        UpdatedAt = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc),
    };


    // A minimal organization Contact for the contact-reference tests.
    private static Contact NewOrganizationContact(Guid id, string legalName, string normalizedName) => new()
    {
        ContactId = id,
        ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
        OrganizationDetails = new() { LegalName = legalName },
        NormalizedName = normalizedName,
        Type = ContactType.Organization,
    };

    private static JournalTask NewJournalTaskRow(
        string title, DateTime createdAt, int position,
        DateTime? startedAt = null, DateTime? completedAt = null, DateTime? archived = null) => new()
    {
        ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
        Title = title,
        CreatedByUserId = "integration-user",
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        Position = position,
        StartedAt = startedAt,
        CompletedAt = completedAt,
        Archived = archived,
    };

    private sealed class NoopFileLookup : IFileLookup
    {
        public Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlySet<Guid>> ExistingImageIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    }


    private async Task EnsureRelationalSchemaAsync()
    {
        await using var context = NewRelationalContext();
        await context.Database.MigrateAsync();

        // The attribution columns these tests write are real foreign keys to AspNetUsers now, so the
        // principals have to exist before a photo, file or journal row can name them.
        await AttributionUsers.EnsureAsync(context, "integration-user", "user-1");
    }

    private OdysseyContext NewRelationalContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.RelationalConnectionString, ServerVersion.AutoDetect(fixture.RelationalConnectionString))
            .Options);

    private ServiceProvider BuildSeederProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OdysseyContext>(options =>
            options.UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString)));
        services.AddDbContext<OdysseyContext>(options =>
            options.UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString)));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<OdysseyContext>();

        // Photos-module services the demo seeder / journal backfill depend on (issue #321).
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IFileLookup, FileLookup>();
        services.AddScoped<IImageContentReader, ImageContentReader>();
        services.AddSingleton<Odyssey.Core.Journal.IPhotoMetadataExtractor, Odyssey.Core.Journal.PhotoMetadataExtractor>();
        services.AddScoped<Odyssey.Core.Journal.PhotoMetadataService>();
        services.AddScoped<Odyssey.Core.Journal.IPhotoLookup, Odyssey.Core.Journal.PhotoLookup>();
        return services.BuildServiceProvider();
    }

    private static IConfiguration SeedEnabledConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Seed:DemoData"] = "true" })
            .Build();

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Odyssey.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }


    /// <summary>
    /// Defaults-only <see cref="IFileAnalysisSettingsLookup"/> for the relational fixtures (issue #421
    /// Wave 1). The paths exercised here read no file-analysis setting, so the values only have to parse.
    /// </summary>
    private sealed class StubFileAnalysisSettingsLookup : IFileAnalysisSettingsLookup
    {
        public Task<FileAnalysisSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FileAnalysisSettings(
                "Anthropic", "United States", "Consent · GDPR Art. 6(1)(a)",
                "https://www.anthropic.com/legal/privacy", 90, 0.60m,
                MaxTokens: 8096, MatchMaxVocabulary: 500, MatchTimeoutSeconds: 60,
                Model: "claude-sonnet-5", BaseUrl: "https://api.anthropic.com", IsDegraded: false));

        // The kill switch (issue #439). These fixtures exercise what analysis does, so it reads on.
        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
