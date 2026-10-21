using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Avatar;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextContractType = Odyssey.Context.ContractType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for converting a contact between Person and Organization (issue #335): the
/// links the conversion keeps survive it with their real foreign keys, the type-change path commits or
/// rolls back as one unit under a retrying execution strategy, and vCard import's nested call joins its
/// ambient transaction instead of opening a second one. None of it is observable on EF InMemory, which
/// honours neither transactions nor the execution strategy.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class ContactConversionIntegrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contact_conversion";

    [SkippableFact]
    public async Task Conversion_keeps_every_untyped_link_pointing_at_the_contact()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var entryId = Guid.NewGuid();
            DateTime updatedAt;

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(new Contact
                {
                    ContactId = contactId,
                    ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                    NormalizedName = "NORTHWIND LABS",
                    Type = ContactType.Organization,
                    OrganizationDetails = new OrganizationDetails { LegalName = "Northwind Labs", OrganizationNumber = "98-7654321" },
                    UpdatedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
                });
                context.Contracts.Add(new Contract
                {
                    ContractId = contractId, Name = "Service agreement", Type = ContextContractType.Other, CreatedAtUtc = DateTime.UtcNow,
                });
                context.Accounts.Add(new Account
                {
                    AccountId = accountId, Name = "Payroll", Description = "", Opened = DateTime.UtcNow, CurrencyCode = "USD", CustodianId = contactId,
                });
                await context.SaveChangesAsync();

                context.ContractParties.Add(new ContractParty
                {
                    ContractPartyId = Guid.NewGuid(), ContractId = contractId, ContactId = contactId, Role = ContextContractPartyRole.Other,
                });
                context.Transactions.Add(new Transaction
                {
                    TransactionId = transactionId, Description = "Salary", Amount = 100m, TimeStamp = DateTime.UtcNow, AccountId = accountId, ContactId = contactId,
                });
                context.JournalEntries.Add(new JournalEntry
                {
                    JournalEntryId = entryId, ExternalUid = $"urn:uuid:{Guid.NewGuid()}", Title = "Met them", Content = "",
                    Contacts = [new JournalEntryContact { JournalEntryContactId = Guid.NewGuid(), ContactId = contactId }],
                });
                await context.SaveChangesAsync();
                updatedAt = (await context.Contacts.AsNoTracking().FirstAsync(c => c.ContactId == contactId)).UpdatedAt;
            }

            await using (var context = New(connectionString))
            {
                var service = new ContactService(context, new ContactReferenceGuard(context));
                var result = await service.ConvertAsync(contactId, new ContactConversionRequest
                {
                    TargetType = ContactType.Person,
                    ExpectedUpdatedAt = updatedAt,
                });

                Assert.Equal(ContactType.Person, result!.Contact.Type);
                Assert.Equal("Northwind", result.Contact.PersonDetails!.FirstName);
            }

            await using (var context = New(connectionString))
            {
                Assert.Equal(1, await context.ContractParties.CountAsync(p => p.ContactId == contactId));
                Assert.Equal(contactId, (await context.Accounts.FirstAsync(a => a.AccountId == accountId)).CustodianId);
                Assert.Equal(contactId, (await context.Transactions.FirstAsync(t => t.TransactionId == transactionId)).ContactId);
                Assert.Equal(1, await context.JournalEntryContacts.CountAsync(j => j.ContactId == contactId));
                Assert.False(await context.OrganizationDetails.AnyAsync(o => o.ContactId == contactId));
                Assert.True(await context.PersonDetails.AnyAsync(p => p.ContactId == contactId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    [SkippableFact]
    public async Task A_blocked_type_changing_put_rolls_back_and_leaves_the_row_untouched()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Person(contactId, "Diego", "Ramos"));
                await context.SaveChangesAsync();
                await SeedPhotoTagAsync(context, contactId);
            }

            await using (var context = New(connectionString))
            {
                var service = new ContactService(context, new ContactReferenceGuard(context));
                await Assert.ThrowsAsync<ContactTypeChangeBlockedException>(() => service.Update(contactId, new NewContact
                {
                    Type = ContactType.Organization,
                    Archived = false,
                    ExternalUid = "urn:uuid:must-not-stick",
                    OrganizationDetails = new OrganizationDetailsDto { LegalName = "Diego Ramos AS" },
                }));

                // The same scoped context carries on: the refusal left nothing tracked to flush.
                var kept = await service.Update(contactId, new NewContact
                {
                    Type = ContactType.Person,
                    Archived = false,
                    PersonDetails = new PersonDetailsDto { FirstName = "Diego", LastName = "Ramirez" },
                });
                Assert.Equal("Ramirez", kept!.PersonDetails!.LastName);
            }

            await using (var context = New(connectionString))
            {
                var row = await context.Contacts.Include(c => c.PersonDetails).AsNoTracking().FirstAsync(c => c.ContactId == contactId);
                Assert.Equal(ContactType.Person, row.Type);
                Assert.NotEqual("urn:uuid:must-not-stick", row.ExternalUid);
                Assert.False(await context.OrganizationDetails.AnyAsync(o => o.ContactId == contactId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The wrapper clears the change tracker when it owns the transaction (issue #335 N1). A row the
    /// scoped context already tracks — mutated, unsaved — must not leak into the write: the attempt
    /// re-reads it, which is what makes a strategy retry replay from the database rather than from the
    /// failed attempt's mutations.
    /// </summary>
    [SkippableFact]
    public async Task An_owned_attempt_rereads_the_row_instead_of_reusing_a_mutated_tracked_entity()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Person(contactId, "Ada", "Lovelace"));
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                var stale = await context.Contacts.FirstAsync(c => c.ContactId == contactId);
                stale.Notes = "left behind by an earlier attempt";

                var service = new ContactService(context, new ContactReferenceGuard(context));
                await service.Update(contactId, new NewContact
                {
                    Type = ContactType.Person,
                    Archived = false,
                    DisplayName = "Countess",
                    PersonDetails = new PersonDetailsDto { FirstName = "Ada", LastName = "Lovelace" },
                });
            }

            await using (var context = New(connectionString))
            {
                var row = await context.Contacts.AsNoTracking().FirstAsync(c => c.ContactId == contactId);
                Assert.Equal("Countess", row.DisplayName);
                Assert.Null(row.Notes);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    [SkippableFact]
    public async Task VCard_retype_joins_the_import_transaction_and_a_blocked_one_rolls_back()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var convertedId = Guid.NewGuid();
            var blockedId = Guid.NewGuid();
            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Person(convertedId, "Grace", "Hopper", "urn:uuid:converted"));
                context.Contacts.Add(Person(blockedId, "Diego", "Ramos", "urn:uuid:blocked"));
                await context.SaveChangesAsync();
                await SeedPhotoTagAsync(context, blockedId);
            }

            await using (var context = New(connectionString))
            {
                var contacts = new ContactService(context, new ContactReferenceGuard(context));
                var vCard = new ContactVCardService(
                    context, contacts,
                    new ContactAvatarService(context, new FileService(context, new FileValidationService()), new FixedUploadLimits(64L * 1024 * 1024)),
                    new UnlimitedLimits(),
                    NullLogger<ContactVCardService>.Instance);

                var vcf =
                    "BEGIN:VCARD\r\nVERSION:4.0\r\nUID:urn:uuid:converted\r\nKIND:org\r\nORG:Hopper Systems\r\nFN:Hopper Systems\r\nEND:VCARD\r\n"
                    + "BEGIN:VCARD\r\nVERSION:4.0\r\nUID:urn:uuid:blocked\r\nKIND:org\r\nORG:Ramos AS\r\nFN:Ramos AS\r\nEND:VCARD\r\n";
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(vcf));
                var outcome = await vCard.ImportWithOutcomeAsync(stream, stream.Length, "text/vcard");

                Assert.Equal([convertedId], outcome.TypeConvertedContactIds);
                Assert.Equal([blockedId], outcome.TypeChangeBlockedContactIds);
            }

            await using (var context = New(connectionString))
            {
                Assert.Equal(ContactType.Organization, (await context.Contacts.AsNoTracking().FirstAsync(c => c.ContactId == convertedId)).Type);
                Assert.Equal(ContactType.Person, (await context.Contacts.AsNoTracking().FirstAsync(c => c.ContactId == blockedId)).Type);
                Assert.True(await context.PersonDetails.AnyAsync(p => p.ContactId == blockedId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Contact Person(Guid id, string first, string last, string? externalUid = null) => new()
    {
        ContactId = id,
        ExternalUid = externalUid ?? $"urn:uuid:{Guid.NewGuid()}",
        NormalizedName = $"{first} {last}".ToUpperInvariant(),
        Type = ContactType.Person,
        PersonDetails = new PersonDetails { FirstName = first, LastName = last },
    };

    /// <summary>A real photo (and its image file) tagging the contact, so the FK chain is genuine.</summary>
    private static async Task SeedPhotoTagAsync(OdysseyContext context, Guid contactId)
    {
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var fileId = Guid.NewGuid();
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(new FileMetadata
        {
            Id = fileId,
            FileName = "tag.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 3,
            Sha256Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blob.Content)),
            FileBlobId = blob.Id,
            FileBlob = blob,
            UploadedAtUtc = DateTime.UtcNow,
        });

        var photoId = Guid.NewGuid();
        context.Photos.Add(new Photo
        {
            PhotoId = photoId,
            FileId = fileId,
            People = [new PhotoPerson { PhotoId = photoId, ContactId = contactId }],
        });
        await context.SaveChangesAsync();
    }

    private async Task<string> MigratedSchemaAsync()
    {
        await DropAsync();

        await using (var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString)))
        {
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        var connectionString = fixture.ConnectionStringFor(Database);
        await using var context = new OdysseyContext(OptionsFor(connectionString));
        await context.Database.MigrateAsync();

        return connectionString;
    }

    private async Task DropAsync()
    {
        await using var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString));
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private static OdysseyContext New(string connectionString) => new(OptionsFor(connectionString));

    /// <summary>
    /// With <c>EnableRetryOnFailure</c>, as production configures it: a bare <c>BeginTransactionAsync</c>
    /// throws under a retrying strategy, so this is what proves the wrapper opens and joins correctly.
    /// </summary>
    private static DbContextOptions<OdysseyContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), o => o.EnableRetryOnFailure())
            .Options;

    private sealed class UnlimitedLimits : IImportExportLimitsLookup
    {
        public Task<ImportExportLimits> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImportExportLimits(
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                2000, 2000, 64L * 1024 * 1024, 64L * 1024 * 1024,
                2000, 2000, 64L * 1024 * 1024, 64L * 1024 * 1024,
                2000, 2000, 64L * 1024 * 1024, 64L * 1024 * 1024,
                20_000, 5_000, 92, 200, 100,
            IsDegraded: false));
    }
}
