using Odyssey.Dtos;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for vCard import's UID-matched update (issue #338 review): the wholesale
/// contact-collection replace now runs inside a transaction opened via
/// <c>Database.CreateExecutionStrategy()</c> rather than one commit per Delete/Create call. EF InMemory
/// can't exercise this at all — it throws on <c>BeginTransactionAsync</c> unless the warning is
/// suppressed, so this is the only tier that proves the transactional code path actually runs (and
/// commits successfully) against a real relational engine, not just that it compiles.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class ContactVCardIntegrationTests(MariaDbFixture fixture)
{
    [SkippableFact]
    public async Task Import_UidMatchedUpdate_CommitsTransactionally_AgainstRealEngine()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = fixture.RelationalConnectionString;

        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;

        await using (var migrating = new OdysseyContext(options))
        {
            await migrating.Database.MigrateAsync();
        }

        await using var context = new OdysseyContext(options);
        var contactService = new ContactService(context, new ContactReferenceGuard(context));
        var vCardService = new ContactVCardService(
            context, contactService, new UnlimitedImportExportLimitsLookup(), NullLogger<ContactVCardService>.Instance);

        var suffix = Guid.NewGuid().ToString("N");
        var created = await contactService.Create(new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            OrganizationDetails = new OrganizationDetailsDto { LegalName = $"Acme {suffix}" },
        });
        await contactService.CreateEmail(created.ContactId,
            new NewEmailAddress { Label = EmailLabel.General, Value = $"old-{suffix}@example.com" });
        var externalUid = (await contactService.Get(created.ContactId))!.ExternalUid;

        var vcf = "BEGIN:VCARD\r\nVERSION:4.0\r\n" +
                  $"UID:{externalUid}\r\nFN:Acme {suffix}\r\nORG:Acme {suffix}\r\n" +
                  $"EMAIL;TYPE=work:new-{suffix}@example.com\r\nEND:VCARD\r\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(vcf));

        var result = await vCardService.ImportAsync(stream, stream.Length, "text/vcard");

        Assert.Equal(1, result.UpdatedCount);
        Assert.Empty(result.Skipped);
        var emails = await contactService.GetEmails(created.ContactId);
        Assert.Equal($"new-{suffix}@example.com", Assert.Single(emails!).Value);
    }

    /// <summary>
    /// Regression coverage for the PR #403 review fix: <c>ContactService.StreamMatchingChunksAsync</c>
    /// originally held one explicit <c>RepeatableRead</c> transaction open across the whole chunked
    /// fetch, which throws <c>InvalidOperationException</c> ("does not support user-initiated
    /// transactions") the moment a query runs inside it, on any <c>OdysseyContext</c> configured with
    /// <c>EnableRetryOnFailure()</c> — exactly how production is configured
    /// (<c>Odyssey.Api/DatabaseExtension.cs</c>). EF InMemory never configures a retrying execution
    /// strategy, so no InMemory-backed test tier could ever have caught this; this is the only tier that
    /// proves the fixed (transaction-free, keyset-snapshot) implementation actually runs against a real
    /// retrying engine. A chunk size of 3 over 7 seeded contacts forces 3 chunks (3+3+1), so this also
    /// proves the fetch genuinely spans more than one chunk, not just the trivial single-chunk case.
    /// </summary>
    [SkippableFact]
    public async Task Export_FilteredContacts_AcrossMultipleChunks_SucceedsAgainstRealEngineWithRetryOnFailure()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = fixture.RelationalConnectionString;

        var migrationOptions = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;
        // The context under test retries, which is what makes the transactional assertion below
        // meaningful — an execution strategy rejects an ambient transaction it did not open itself.
        var retryingOptions = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), o => o.EnableRetryOnFailure())
            .Options;

        await using (var migrating = new OdysseyContext(migrationOptions))
        {
            await migrating.Database.MigrateAsync();
        }

        await using var context = new OdysseyContext(retryingOptions);
        var contactService = new ContactService(context, new ContactReferenceGuard(context));

        var suffix = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 7; i++)
        {
            await contactService.Create(new NewContact
            {
                Type = ContactType.Organization,
                Archived = false,
                OrganizationDetails = new OrganizationDetailsDto { LegalName = $"Chunked {suffix} {i}" },
            });
        }

        var query = new ContactsQueryParams { Search = suffix };
        var chunks = new List<IReadOnlyList<ExistingContact>>();
        await foreach (var chunk in contactService.StreamMatchingChunksAsync(query, chunkSize: 3))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(3, chunks.Count); // 3 + 3 + 1 — proves more than one chunk actually ran
        Assert.Equal(7, chunks.Sum(c => c.Count));
        Assert.Equal(7, chunks.SelectMany(c => c.Select(row => row.ContactId)).Distinct().Count());
    }

    /// <summary>
    /// The label codec against the real engine (issue #47 §16.13, §16.15). The fast tier proves the
    /// codec; this proves the ordinals survive a real <c>int</c> column and a real round trip — and,
    /// more to the point, that the <b>clamp</b> holds inside the import's own transaction, where a
    /// <c>DomainUnprocessableException</c> escaping a catch would roll the whole entry back rather
    /// than merely dropping a property.
    /// </summary>
    [SkippableFact]
    public async Task Import_ClampsPersonTokensOntoAnOrganization_AndRoundTripsTheExtension()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = fixture.RelationalConnectionString;
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;

        await using (var migrating = new OdysseyContext(options))
        {
            await migrating.Database.MigrateAsync();
        }

        await using var context = new OdysseyContext(options);
        var contactService = new ContactService(context, new ContactReferenceGuard(context));
        var vCardService = new ContactVCardService(
            context, contactService, new UnlimitedImportExportLimitsLookup(), NullLogger<ContactVCardService>.Instance);

        var suffix = Guid.NewGuid().ToString("N");

        // What Google and Apple emit for an organization — every token is person-only, and without the
        // clamp all three rows are dropped by the per-property catch.
        var clamped = "BEGIN:VCARD\r\nVERSION:4.0\r\n" +
                      $"UID:clamp-{suffix}\r\nKIND:org\r\nFN:Clamped {suffix}\r\nORG:Clamped {suffix}\r\n" +
                      "TEL;TYPE=home:+47 22 00 00 00\r\nEMAIL;TYPE=work:post@example.com\r\n" +
                      "ADR;TYPE=home:;;Storgata 55;Oslo;;0184;NO\r\nEND:VCARD\r\n";

        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(clamped)))
        {
            var result = await vCardService.ImportAsync(stream, stream.Length, "text/vcard");
            Assert.Equal(1, result.CreatedCount);
            Assert.Empty(result.Skipped);
        }

        var created = Assert.Single(
            (await contactService.ListAsync(new ContactsQueryParams { Search = $"Clamped {suffix}" })).Items);

        Assert.Equal(PhoneLabel.Other, Assert.Single(created.PhoneNumbers).Label);
        Assert.Equal(EmailLabel.Other, Assert.Single(created.EmailAddresses).Label);
        Assert.Equal(AddressLabel.Other, Assert.Single(created.Addresses).Label);

        // Now the finer distinction: an organization label persists, exports with its extension, and
        // re-imports as itself.
        await contactService.CreatePhone(created.ContactId,
            new NewPhoneNumber { Label = PhoneLabel.Claims, Value = "+47 22 00 00 01" });

        var export = await vCardService.ExportOneAsync(created.ContactId);
        Assert.Contains("X-ODYSSEY-LABEL=Claims", export!.Content);

        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(export.Content)))
        {
            var result = await vCardService.ImportAsync(stream, stream.Length, "text/vcard");
            Assert.Equal(1, result.UpdatedCount);
        }

        var reimported = (await contactService.Get(created.ContactId))!;
        Assert.Contains(reimported.PhoneNumbers, p => p.Label == PhoneLabel.Claims);
        Assert.Equal(2, reimported.PhoneNumbers.Count);
    }

    // Matches today's out-of-the-box System Settings defaults (issue #343 §6) — unlimited counts,
    // 64 MB sizes — this test is about the transaction, not the caps.
    private sealed class UnlimitedImportExportLimitsLookup : IImportExportLimitsLookup
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
