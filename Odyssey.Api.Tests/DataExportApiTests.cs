using Odyssey.Dtos;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Odyssey.Api.DataExport;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

public class DataExportApiTests
{
    private const string ActorUserId = "data-export-actor-id";
    private const string ExportPath = "/api/admin/data-export";

    // Recognizable payloads used to prove the excluded data never reaches the export.
    private static readonly byte[] BlobContent = Encoding.UTF8.GetBytes("SECRET-BLOB-CONTENT-MUST-NOT-EXPORT");
    private const string CandidateMarker = "CANDIDATE-DESCRIPTION-MUST-NOT-EXPORT";
    internal const string TaxStatementMarker = "TAX-STATEMENT-NOTES-MUST-EXPORT";

    // The candidate-tag row carries no free text — only two foreign keys, one of which
    // (the tag) is legitimately exported elsewhere. Its parent id is therefore the only
    // value that can prove the row itself did not travel.
    private static readonly Guid CandidateTransactionId =
        Guid.Parse("cadc0de0-0000-4000-8000-000000000001");

    // ── Authorization matrix (spec §3.4 / §10.1) ──────────────────────────────

    [Fact]
    public async Task Export_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ExportPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Export_WithoutDataExportPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ExportPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Export_WithPermissionAndFeatureEnabled_ReturnsJsonAttachment()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ExportPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Matches(@"^odyssey-database-export-\d{8}-\d{6}Z\.json$", disposition.FileName!.Trim('"'));

        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    // ── Streamed, not buffered (issue #395) ───────────────────────────────────

    /// <summary>
    /// The payload is written straight to <c>Response.Body</c>, so there is no <c>byte[]</c> whose
    /// length could be declared up front. A regression to <c>File(payload, …)</c> would set
    /// <c>Content-Length</c> again, which is what this pins.
    /// </summary>
    [Fact]
    public async Task Export_IsStreamed_SoTheResponseDeclaresNoContentLength()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ExportPath, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentLength);
    }

    /// <summary>
    /// A streamed response cannot become a ProblemDetails once it has started, so the document ends
    /// with a completeness sentinel a reader can check. See <c>DataExportStreamingTests</c> for the
    /// failure side of that contract.
    /// </summary>
    [Fact]
    public async Task Export_CompletedDocument_CarriesTheCompletenessSentinel()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);

        Assert.True(document.RootElement.GetProperty("complete").GetBoolean());

        // Last property of the envelope: a truncated body cannot end with it.
        Assert.Equal("complete", document.RootElement.EnumerateObject().Last().Name);
    }

    // ── Envelope shape (spec §5.3 / §10.1.5) ──────────────────────────────────

    [Fact]
    public async Task Export_Envelope_ContainsMetadataAndFinanceDatabase()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("odyssey.database-export.v1", root.GetProperty("format").GetString());
        Assert.Equal(ActorUserId, root.GetProperty("exportedByUserId").GetString());
        Assert.True(root.TryGetProperty("exportedAt", out var exportedAt));
        Assert.NotEqual(default, exportedAt.GetDateTimeOffset());

        var exclusions = root.GetProperty("exclusions");
        Assert.True(exclusions.GetProperty("fileContentsExcluded").GetBoolean());
        Assert.Contains("FileBlob", exclusions.GetProperty("excludedTables").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("FileBlob.Content", exclusions.GetProperty("excludedFields").EnumerateArray().Select(e => e.GetString()));

        // Issue #33: the candidate-TAGS table was in neither the export nor this list — its parent
        // is excluded, so it belongs here rather than being the one member of the group left unsaid.
        Assert.Contains("FileAnalysisCandidateTags", exclusions.GetProperty("excludedTables").EnumerateArray().Select(e => e.GetString()));

        // The export only covers Finance (+ Contacts); it must say so rather than let a reader assume
        // Identity/Journal data is captured elsewhere by this export (architect finding F-10).
        var outOfScope = exclusions.GetProperty("outOfScopeDatabases").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(outOfScope, d => d.Contains("Application", StringComparison.Ordinal));
        Assert.Contains(outOfScope, d => d.Contains("Journal", StringComparison.Ordinal));

        var finance = root.GetProperty("databases").GetProperty("finance");
        foreach (var collection in new[]
                 {
                     "accounts", "terms", "budgets", "budgetItems", "contacts", "currencies",
                     "exchangeRates", "transactions", "transactionTags", "fileMetadata", "accountFiles",
                     "transactionFiles",
                     // Issue #33.
                     "accountEstimates", "accountSmartTags", "taxStatements", "taxStatementTags",
                     "taxStatementFiles",
                     "contracts", "contractParties", "contractFiles",
                     // Issue #166.
                     "contractSmartTags",
                 })
        {
            Assert.Equal(JsonValueKind.Array, finance.GetProperty(collection).ValueKind);
        }
    }

    // ── File metadata included, blob bytes excluded (spec §10.1.6) ─────────────

    [Fact]
    public async Task Export_IncludesFileMetadata_ButExcludesBlobContent()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        var rawJson = await (await client.GetAsync(ExportPath)).Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(rawJson);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var fileMetadata = finance.GetProperty("fileMetadata");
        var file = Assert.Single(fileMetadata.EnumerateArray());

        // Metadata columns present (including the FileBlob *relationship* key)…
        Assert.Equal("statement.pdf", file.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", file.GetProperty("contentType").GetString());
        Assert.True(file.TryGetProperty("sizeBytes", out _));
        Assert.True(file.TryGetProperty("sha256Hash", out _));
        Assert.True(file.TryGetProperty("fileBlobId", out _));

        // …but no blob payload, under any name.
        Assert.False(file.TryGetProperty("content", out _));
        Assert.False(file.TryGetProperty("fileBlob", out _));
        Assert.DoesNotContain("fileBlobs", finance.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain(Convert.ToBase64String(BlobContent), rawJson);
    }

    // ── File-analysis tables excluded entirely (spec §10.1.7) ─────────────────

    [Fact]
    public async Task Export_ExcludesFileAnalysisJobsAndCandidates()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        var rawJson = await (await client.GetAsync(ExportPath)).Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(rawJson);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var collectionNames = finance.EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain(collectionNames, name => name.Contains("analysis", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(collectionNames, name => name.Contains("candidate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(CandidateMarker, rawJson);

        // The candidate-TAGS row is seeded too, so this is a leak check rather than a restatement
        // of the declared list: its parent id must appear nowhere, under any collection name.
        Assert.DoesNotContain(CandidateTransactionId.ToString(), rawJson, StringComparison.OrdinalIgnoreCase);
    }

    // ── Rows reference FKs, not nested objects (spec §10.1.8) ─────────────────

    [Fact]
    public async Task Export_TransactionRow_ReferencesForeignKeysNotNestedObjects()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var transaction = Assert.Single(finance.GetProperty("transactions").EnumerateArray());
        Assert.True(transaction.TryGetProperty("accountId", out var accountId));
        Assert.NotEqual(Guid.Empty, accountId.GetGuid());
        Assert.True(transaction.TryGetProperty("contactId", out _));
        // Multi-tag (issue #181): tags are exported as a flat list of FK ids, not a nested object.
        Assert.True(transaction.TryGetProperty("transactionTagIds", out var transactionTagIds));
        Assert.Equal(JsonValueKind.Array, transactionTagIds.ValueKind);
        Assert.NotEqual(Guid.Empty, Assert.Single(transactionTagIds.EnumerateArray()).GetGuid());

        // Enums serialize as their stored integer (the DB column representation), not a string.
        Assert.Equal(JsonValueKind.Number, transaction.GetProperty("status").ValueKind);

        // No nested navigation graphs.
        Assert.False(transaction.TryGetProperty("account", out _));
        Assert.False(transaction.TryGetProperty("contact", out _));
        Assert.False(transaction.TryGetProperty("transactionTag", out _));
        Assert.False(transaction.TryGetProperty("transactionFiles", out _));
    }

    // ── Account terms included (issue #172) ───────────────────────────────────

    [Fact]
    public async Task Export_IncludesTerms_AsFlatRows()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var term = Assert.Single(finance.GetProperty("terms").EnumerateArray());
        Assert.NotEqual(Guid.Empty, term.GetProperty("termId").GetGuid());
        Assert.NotEqual(Guid.Empty, term.GetProperty("accountId").GetGuid());

        // Enums serialize as their stored integer, not a nested navigation object.
        Assert.Equal(JsonValueKind.Number, term.GetProperty("valueUnit").ValueKind);
        Assert.False(term.TryGetProperty("account", out _));

        // There is no kind column any more: the label is what a term is.
        Assert.False(term.TryGetProperty("termKind", out _));
        Assert.Equal("Interest rate", term.GetProperty("label").GetString());
        Assert.Equal("interest rate", term.GetProperty("labelKey").GetString());
    }

    /// <summary>
    /// A labelled fee exports BOTH its display label and the folded key that carries its series. The
    /// key is derived server-side and never round-trips through a request DTO, so the export is the
    /// only place it is observable — and an export that dropped it could not reproduce the series
    /// membership it encodes.
    /// </summary>
    [Fact]
    public async Task Export_IncludesTermSeriesLabels()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        await AddLabelledFeeAsync(factory, "ATM · Abroad", "atm · abroad");
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var fee = finance.GetProperty("terms").EnumerateArray()
            .Single(t => t.GetProperty("label").GetString() == "ATM · Abroad");

        Assert.Equal("ATM · Abroad", fee.GetProperty("label").GetString());
        Assert.Equal("atm · abroad", fee.GetProperty("labelKey").GetString());
    }

    private static async Task AddLabelledFeeAsync(
        WebApplicationFactory<Program> factory, string label, string labelKey)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var accountId = await context.Accounts.Select(a => a.AccountId).FirstAsync();

        context.Terms.Add(new Term
        {
            TermId = Guid.NewGuid(),
            AccountId = accountId,
            Label = label,
            LabelKey = labelKey,
            ValueUnit = TermValueUnit.Amount,
            Value = 25m,
            CurrencyCode = "USD",
            EffectiveFrom = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// AC 19 (issue #135) — the <c>Terms</c> table carries BOTH owner columns, with exactly one
    /// populated per row. Without the contract column the export would attribute every contract term
    /// to no owner at all: the row would be present, complete in every other respect, and silently
    /// unattached to the agreement whose price it records.
    /// </summary>
    [Fact]
    public async Task Export_Terms_CarryBothOwnerIdsWithExactlyOnePopulated()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        var contractId = await AddContractTermAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");
        var terms = finance.GetProperty("terms").EnumerateArray().ToList();

        // Both owners are represented, so neither column is merely present-and-always-null.
        Assert.Equal(2, terms.Count);

        var contractTerm = terms.Single(t => t.GetProperty("contractId").ValueKind != JsonValueKind.Null);
        Assert.Equal(contractId, contractTerm.GetProperty("contractId").GetGuid());
        Assert.Equal(JsonValueKind.Null, contractTerm.GetProperty("accountId").ValueKind);
        Assert.Equal("Monthly rent", contractTerm.GetProperty("label").GetString());

        var accountTerm = terms.Single(t => t.GetProperty("accountId").ValueKind != JsonValueKind.Null);
        Assert.Equal(JsonValueKind.Null, accountTerm.GetProperty("contractId").ValueKind);

        // The invariant itself, asserted over every row rather than over the two named above.
        Assert.All(terms, term => Assert.True(
            (term.GetProperty("accountId").ValueKind != JsonValueKind.Null)
            ^ (term.GetProperty("contractId").ValueKind != JsonValueKind.Null),
            "every exported term names exactly one owner"));
    }

    private static async Task<Guid> AddContractTermAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var contract = new Contract
        {
            Name = "Maple St lease",
            Type = Odyssey.Context.ContractType.Rental,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = DateTime.UtcNow,
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();

        context.Terms.Add(new Term
        {
            TermId = Guid.NewGuid(),
            ContractId = contract.ContractId,
            Label = "Monthly rent",
            LabelKey = "monthly rent",
            ValueUnit = TermValueUnit.Amount,
            Value = 14500m,
            CurrencyCode = "USD",
            EffectiveFrom = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    // ── Issue #33: the previously-omitted tables ──────────────────────────────

    /// <summary>
    /// A contract party is one-of-two with no kind discriminator on the row, so both nullable
    /// relationship columns are exported and which one is set is what says which kind it is.
    /// </summary>
    [Fact]
    public async Task Export_IncludesContractsAndParties_AsIdsOnly()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var contract = Assert.Single(finance.GetProperty("contracts").EnumerateArray());
        Assert.Equal("Lease", contract.GetProperty("name").GetString());

        var parties = finance.GetProperty("contractParties").EnumerateArray().ToList();
        Assert.Equal(2, parties.Count);
        Assert.All(parties, party =>
        {
            Assert.Equal(contract.GetProperty("contractId").GetGuid(), party.GetProperty("contractId").GetGuid());
            // Role and the term ride along since issue #121: they are columns on the link row, not a
            // resolved name, so exporting them discloses nothing the two target ids did not already.
            Assert.Equal(
                new[] { "contractPartyId", "contractId", "accountId", "contactId", "role", "fromDate", "toDate" }
                    .Order(StringComparer.Ordinal),
                party.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        });

        // Both branches, each with the other column null. Covering only one would let a dropped
        // relationship column pass: the surviving branch would still look correct.
        var institution = Assert.Single(parties, party => party.GetProperty("contactId").ValueKind != JsonValueKind.Null);
        Assert.NotEqual(Guid.Empty, institution.GetProperty("contactId").GetGuid());
        Assert.Equal(JsonValueKind.Null, institution.GetProperty("accountId").ValueKind);

        var accountParty = Assert.Single(parties, party => party.GetProperty("accountId").ValueKind != JsonValueKind.Null);
        Assert.NotEqual(Guid.Empty, accountParty.GetProperty("accountId").GetGuid());
        Assert.Equal(JsonValueKind.Null, accountParty.GetProperty("contactId").ValueKind);

        Assert.Single(finance.GetProperty("contractFiles").EnumerateArray());
    }

    /// <summary>
    /// Issue #181 AC 21. <c>DataExportTableCoverageTests</c> guards TABLE coverage and would pass with
    /// this column missing, so the field is asserted on the export document itself — present with its
    /// value on one contract and present as <c>null</c> on the other.
    /// </summary>
    [Fact]
    public async Task Export_IncludesContractReferenceNumber_OrNull()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.Contracts.AddRange(
                new Contract
                {
                    Name = "Numbered",
                    Type = Odyssey.Context.ContractType.Rental,
                    ReferenceNumber = "AGR-2026/114-B.2",
                    CreatedAtUtc = DateTime.UtcNow,
                },
                new Contract
                {
                    Name = "Unnumbered",
                    Type = Odyssey.Context.ContractType.Rental,
                    CreatedAtUtc = DateTime.UtcNow,
                });
            await context.SaveChangesAsync();
        }
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var contracts = document.RootElement.GetProperty("databases").GetProperty("finance")
            .GetProperty("contracts").EnumerateArray().ToList();

        var numbered = Assert.Single(contracts, c => c.GetProperty("name").GetString() == "Numbered");
        Assert.Equal("AGR-2026/114-B.2", numbered.GetProperty("referenceNumber").GetString());

        var unnumbered = Assert.Single(contracts, c => c.GetProperty("name").GetString() == "Unnumbered");
        Assert.True(unnumbered.TryGetProperty("referenceNumber", out var absent));
        Assert.Equal(JsonValueKind.Null, absent.ValueKind);
    }

    /// <summary>
    /// Issue #138 AC 18's CONTENT half. <c>DataExportTableCoverageTests</c> is a reflection guard that
    /// only proves the table is accounted for somewhere; it passes the moment a collection property
    /// exists and says nothing about what the rows carry. This seeds an event and reads the exported
    /// JSON.
    /// </summary>
    /// <remarks>
    /// <b>All three free-text fields, <c>notes</c> included.</b> The timeline does not render notes,
    /// but that is a presentation rule and not an access one (§4.1) — the field is behind the same
    /// claim, searched by the same term and no more private than the other two. Omitting it here would
    /// make a subject-access response silently incomplete, which is the exact defect this document's
    /// coverage guard exists for (issue #33). The attribution is exported as the RAW column, unlike
    /// the API's read path, matching every other attribution column in the document.
    /// </remarks>
    [Fact]
    public async Task Export_IncludesContractEventsWithAllThreeFreeTextFields()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        var contractId = await AddContractEventAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var contractEvent = Assert.Single(finance.GetProperty("contractEvents").EnumerateArray());
        Assert.Equal(contractId, contractEvent.GetProperty("contractId").GetGuid());
        Assert.Equal("Emailed the landlord", contractEvent.GetProperty("title").GetString());
        Assert.Equal("What was said, at more length.", contractEvent.GetProperty("description").GetString());
        Assert.Equal("Chase this on the 21st.", contractEvent.GetProperty("notes").GetString());
        Assert.Equal((int)Odyssey.Dtos.Finance.ContractEventType.EmailSent, contractEvent.GetProperty("type").GetInt32());
        Assert.Equal("exporting-author", contractEvent.GetProperty("createdByUserId").GetString());
        Assert.Equal(
            (int)Odyssey.Dtos.Finance.ContractEventSource.User,
            contractEvent.GetProperty("source").GetInt32());

        // The whole column set, so a field dropped from the projection fails here rather than going
        // unnoticed — the assertion the three named above cannot make on their own.
        Assert.Equal(
            new[]
            {
                "contractEventId", "contractId", "type", "source", "title", "description", "notes",
                "occurredAt", "createdByUserId", "createdAtUtc",
            }.Order(StringComparer.Ordinal),
            contractEvent.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Issue #154 AC 33 — a system-recorded event is <b>distinguishable</b> from a hand-written one in
    /// the export.
    /// </summary>
    /// <remarks>
    /// Its own test rather than an extra assertion above, because the property being pinned is the
    /// <em>difference</em> between two rows, not the presence of one column. <c>ContractEventsQuery</c>
    /// is a hand-written projection that enumerates every column explicitly, so a new column is omitted
    /// by default rather than included by default, and <c>DataExportTableCoverageTests</c> reflects
    /// over <c>DbSet</c>s rather than columns and would not catch it. The export would otherwise ship a
    /// log in which the two kinds of line are indistinguishable — which is precisely the distinction
    /// this feature exists to create.
    /// </remarks>
    [Fact]
    public async Task Export_DistinguishesSystemRecordedContractEventsFromHandWrittenOnes()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        var contractId = await AddContractEventAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.ContractEvents.Add(new ContractEvent
            {
                ContractEventId = Guid.NewGuid(),
                ContractId = contractId,
                Type = Odyssey.Context.ContractEventType.Paused,
                Source = Odyssey.Context.ContractEventSource.System,
                Title = "Contract paused",
                Description = "Suspended on 1 March 2026.",
                OccurredAt = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc),
                CreatedByUserId = "exporting-author",
                CreatedAtUtc = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc),
            });
            await context.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        using var document = await GetExportDocumentAsync(client);

        var rows = document.RootElement
            .GetProperty("databases").GetProperty("finance")
            .GetProperty("contractEvents").EnumerateArray()
            .ToDictionary(
                row => (Odyssey.Dtos.Finance.ContractEventType)row.GetProperty("type").GetInt32(),
                row => (Odyssey.Dtos.Finance.ContractEventSource)row.GetProperty("source").GetInt32());

        Assert.Equal(
            Odyssey.Dtos.Finance.ContractEventSource.User,
            rows[Odyssey.Dtos.Finance.ContractEventType.EmailSent]);
        Assert.Equal(
            Odyssey.Dtos.Finance.ContractEventSource.System,
            rows[Odyssey.Dtos.Finance.ContractEventType.Paused]);
    }

    private static async Task<Guid> AddContractEventAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var contract = new Contract
        {
            Name = "Maple St lease",
            Type = Odyssey.Context.ContractType.Rental,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = DateTime.UtcNow,
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();

        context.ContractEvents.Add(new ContractEvent
        {
            ContractEventId = Guid.NewGuid(),
            ContractId = contract.ContractId,
            Type = Odyssey.Context.ContractEventType.EmailSent,
            Title = "Emailed the landlord",
            Description = "What was said, at more length.",
            Notes = "Chase this on the 21st.",
            OccurredAt = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc),
            CreatedByUserId = "exporting-author",
            CreatedAtUtc = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    /// <summary>
    /// Tax statements carry declared figures and an assessment — wholly user-authored financial
    /// data, and among the strongest omissions issue #33 found.
    /// </summary>
    [Fact]
    public async Task Export_IncludesTaxStatementsWithTheirDeclaredFigures()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        var rawJson = await (await client.GetAsync(ExportPath)).Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(rawJson);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var statement = Assert.Single(finance.GetProperty("taxStatements").EnumerateArray());
        Assert.Equal(2025, statement.GetProperty("fiscalYear").GetInt32());
        Assert.Equal(1_250_000m, statement.GetProperty("declaredNetWorth").GetDecimal());
        Assert.Equal(96_000m, statement.GetProperty("declaredTotalIncome").GetDecimal());
        Assert.Equal(24_500m, statement.GetProperty("assessedTax").GetDecimal());
        Assert.Contains(TaxStatementMarker, rawJson, StringComparison.Ordinal);

        Assert.Single(finance.GetProperty("taxStatementTags").EnumerateArray());
        Assert.Single(finance.GetProperty("taxStatementFiles").EnumerateArray());
    }

    [Fact]
    public async Task Export_IncludesTheAccountSideTables()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var estimate = Assert.Single(finance.GetProperty("accountEstimates").EnumerateArray());
        Assert.Equal(185_000m, estimate.GetProperty("value").GetDecimal());

        // Composite-keyed: the row is the (account, tag) pair, so there is no id to export.
        var smartTag = Assert.Single(finance.GetProperty("accountSmartTags").EnumerateArray());
        Assert.NotEqual(Guid.Empty, smartTag.GetProperty("accountId").GetGuid());
        Assert.NotEqual(Guid.Empty, smartTag.GetProperty("transactionTagId").GetGuid());

        // Its contract sibling (issue #166), composite-keyed the same way.
        var contractSmartTag = Assert.Single(finance.GetProperty("contractSmartTags").EnumerateArray());
        Assert.NotEqual(Guid.Empty, contractSmartTag.GetProperty("contractId").GetGuid());
        Assert.NotEqual(Guid.Empty, contractSmartTag.GetProperty("transactionTagId").GetGuid());
    }

    // ── Deterministic ordering, every collection (spec §10.1.9) ───────────────

    /// <summary>
    /// The collections and the key columns each is ordered by, with the kind of comparison that
    /// key uses. All 27 of them appear here — ordering was pinned for <c>accounts</c> alone, so a
    /// dropped or wrong <c>OrderBy</c> on any of the other 26 queries passed the whole suite.
    /// Deterministic order is what makes two exports of unchanged data diffable, so it is a
    /// property of the format, not of one table.
    /// </summary>
    public static TheoryData<string, string[], KeyKind> OrderedCollections() => new()
    {
        { "accounts", ["accountId"], KeyKind.Guid },
        { "terms", ["termId"], KeyKind.Guid },
        { "budgets", ["budgetId"], KeyKind.Guid },
        { "budgetItems", ["budgetItemId"], KeyKind.Guid },
        { "contacts", ["contactId"], KeyKind.Guid },
        // The one text-keyed collection. Its ordering was never covered, which is how it
        // came to be left out of an assertion that claimed to cover everything.
        { "currencies", ["currencyCode"], KeyKind.Text },
        { "exchangeRates", ["exchangeRateId"], KeyKind.Guid },
        { "transactions", ["transactionId"], KeyKind.Guid },
        { "transactionTags", ["transactionTagId"], KeyKind.Guid },
        { "fileMetadata", ["id"], KeyKind.Guid },
        { "accountFiles", ["id"], KeyKind.Guid },
        { "transactionFiles", ["id"], KeyKind.Guid },
        { "accountEstimates", ["accountEstimateId"], KeyKind.Guid },
        // Composite-keyed: ordered by both key columns, in that order.
        { "accountSmartTags", ["accountId", "transactionTagId"], KeyKind.Guid },
        { "taxStatements", ["taxStatementId"], KeyKind.Guid },
        { "taxStatementTags", ["id"], KeyKind.Guid },
        { "taxStatementFiles", ["id"], KeyKind.Guid },
        { "contracts", ["contractId"], KeyKind.Guid },
        { "contractParties", ["contractPartyId"], KeyKind.Guid },
        { "contractFiles", ["contractFileId"], KeyKind.Guid },
        // Composite-keyed, like accountSmartTags: ordered by both key columns, in that order.
        { "contractSmartTags", ["contractId", "transactionTagId"], KeyKind.Guid },
    };

    /// <summary>
    /// Rows come back in ascending primary-key order. The fixture seeds each table so that INSERT
    /// order is not already key order — without that a missing <c>OrderBy</c> still returns sorted
    /// rows on the in-memory provider and the assertion proves nothing.
    /// </summary>
    /// <summary>How a collection's key columns compare. A Guid key must NOT be compared as text:
    /// <see cref="Guid.CompareTo(Guid)"/> reads the first three groups in a different byte order
    /// than their printed form, so the two orderings genuinely disagree.</summary>
    public enum KeyKind
    {
        Guid,
        Text,
    }

    [Theory]
    [MemberData(nameof(OrderedCollections))]
    public async Task Export_EveryCollection_IsOrderedByPrimaryKey(
        string collection, string[] keyProperties, KeyKind keyKind)
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        await SeedOutOfOrderRowsAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var rows = finance.GetProperty(collection).EnumerateArray().ToList();

        // Guards the guard: on one row every ordering is trivially correct, so a collection the
        // fixture stopped seeding would pass this silently.
        Assert.True(rows.Count >= 2,
            $"'{collection}' has {rows.Count} row(s); the fixture must seed at least two or this "
            + "assertion cannot fail.");

        for (var i = 1; i < rows.Count; i++)
        {
            Assert.True(CompareKeys(rows[i - 1], rows[i], keyProperties, keyKind) < 0,
                $"'{collection}' is not ordered by {string.Join(" + ", keyProperties)}: row {i - 1} "
                + $"({KeyText(rows[i - 1], keyProperties)}) precedes row {i} "
                + $"({KeyText(rows[i], keyProperties)}).");
        }
    }

    // Compares the way the provider's OrderBy does: Guid.CompareTo for a Guid key, ordinal for a
    // text one. The fixture keeps text keys to ASCII so ordinal and the provider's culture-aware
    // default cannot disagree on them.
    private static int CompareKeys(
        JsonElement left, JsonElement right, string[] keyProperties, KeyKind keyKind)
    {
        foreach (var property in keyProperties)
        {
            var comparison = keyKind switch
            {
                KeyKind.Guid => left.GetProperty(property).GetGuid()
                    .CompareTo(right.GetProperty(property).GetGuid()),
                _ => string.CompareOrdinal(
                    left.GetProperty(property).GetString(), right.GetProperty(property).GetString()),
            };

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static string KeyText(JsonElement row, string[] keyProperties) =>
        string.Join(", ", keyProperties.Select(property => row.GetProperty(property).ToString()));

    /// <summary>
    /// Ids that differ only in their final byte, so <see cref="Guid.CompareTo(Guid)"/> reduces to
    /// comparing <paramref name="sequence"/>. Rows are added highest-first, which is what makes the
    /// stored order differ from key order.
    /// </summary>
    private static Guid OrderingId(int sequence) =>
        Guid.Parse($"00000000-0000-4000-8000-{sequence:D12}");

    /// <summary>
    /// Adds two more rows to every table, inserted in DESCENDING key order. Each child attaches to
    /// the parent of the same sequence number.
    /// </summary>
    private static async Task SeedOutOfOrderRowsAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var accountIds = await context.Accounts.Select(a => a.AccountId).ToListAsync();
        var accountId = accountIds[0];
        var contactId = await context.Contacts.Select(c => c.ContactId).FirstAsync();
        var tagId = await context.TransactionTags.Select(t => t.TransactionTagId).FirstAsync();
        var fileMetadataId = await context.FileMetadata.Select(f => f.Id).FirstAsync();
        var budgetId = await context.Budgets.Select(b => b.BudgetId).FirstAsync();
        var transactionId = await context.Transactions.Select(t => t.TransactionId).FirstAsync();
        var now = DateTime.UtcNow;

        foreach (var sequence in new[] { 3, 2 })
        {
            var id = OrderingId(sequence);

            context.Terms.Add(new Term
            {
                TermId = id, AccountId = accountId, Label = "Interest rate", LabelKey = "interest rate",
                ValueUnit = TermValueUnit.Percentage, Value = 0.01m, EffectiveFrom = now, CreatedAtUtc = now,
            });
            context.AccountEstimates.Add(new AccountEstimate
            {
                AccountEstimateId = id, AccountId = accountId, Value = 1_000m,
                EffectiveFrom = now, CreatedAtUtc = now,
            });
            context.Budgets.Add(new Budget
            {
                BudgetId = id, Name = $"Budget {sequence}", StartDate = now, EndDate = now.AddMonths(1),
            });
            context.BudgetItems.Add(new BudgetItem
            {
                BudgetItemId = id, BudgetId = budgetId, PlannedAmount = 10m, TransactionTagId = id,
            });
            context.Contacts.Add(new Contact
            {
                ContactId = id,
                ExternalUid = $"urn:uuid:{id}",
                NormalizedName = $"ordering {sequence}",
                Type = Odyssey.Dtos.ContactType.Organization,
                OrganizationDetails = new() { LegalName = $"Ordering {sequence}" },
            });
            context.ExchangeRates.Add(new ExchangeRate
            {
                ExchangeRateId = id, FromCurrencyCode = "USD", ToCurrencyCode = "EUR",
                Rate = 0.9m, AsOf = now.AddDays(-sequence), CreatedAt = now,
            });
            context.Transactions.Add(new Transaction
            {
                TransactionId = id, Description = $"Row {sequence}", Amount = 1m,
                TimeStamp = now, AccountId = accountId,
            });
            context.TransactionTags.Add(new TransactionTag { TransactionTagId = id, Name = $"Tag {sequence}" });

            var blobId = Guid.Parse($"00000000-0000-4000-9000-{sequence:D12}");
            context.FileBlob.Add(new FileBlob { Id = blobId, Content = [1, 2, 3] });
            context.FileMetadata.Add(new FileMetadata
            {
                Id = id, FileName = $"file{sequence}.pdf", ContentType = "application/pdf",
                SizeBytes = 3, Sha256Hash = $"hash{sequence}", FileBlobId = blobId, UploadedAtUtc = now,
            });
            context.AccountFiles.Add(new AccountFile
            {
                Id = id, AccountId = accountId, FileMetadataId = fileMetadataId,
                AttachedAtUtc = now, FileType = AccountFileType.Statement,
            });
            context.TransactionFiles.Add(new TransactionFile
            {
                Id = id, TransactionId = transactionId, FileMetadataId = fileMetadataId, AttachedAtUtc = now,
            });

            context.TaxStatements.Add(new TaxStatement
            {
                TaxStatementId = id, Name = $"FY{sequence}", FiscalYear = 2020 + sequence,
                StartDate = now.AddYears(-1), EndDate = now, CreatedAtUtc = now,
            });
            context.TaxStatementTags.Add(new TaxStatementTag
            {
                Id = id, TaxStatementId = id, TransactionTagId = tagId,
                Role = Odyssey.Dtos.Finance.TaxStatementTagRole.Income,
            });
            context.TaxStatementFiles.Add(new TaxStatementFile
            {
                Id = id, TaxStatementId = id, FileMetadataId = fileMetadataId, AttachedAtUtc = now,
            });

            context.Contracts.Add(new Contract
            {
                ContractId = id, Name = $"Contract {sequence}", CreatedAtUtc = now,
            });
            context.ContractParties.Add(new ContractParty
            {
                ContractPartyId = id, ContractId = id, ContactId = contactId,
            });
            context.ContractFiles.Add(new ContractFile
            {
                ContractFileId = id, ContractId = id, FileMetadataId = fileMetadataId, AttachedAtUtc = now,
            });
            // Composite-keyed like AccountSmartTags (issue #166): the inversion comes from the
            // contract, since the loop inserts sequence 3 before sequence 2.
            context.ContractSmartTags.Add(new ContractSmartTag
            {
                ContractId = id, TransactionTagId = tagId, AddedAt = now,
            });
        }

        // Text-keyed, so its inversion comes from the codes. ASCII-only and outside the ISO set the
        // migration seeds, so they neither collide with it nor let ordinal and culture-aware
        // comparison disagree.
        foreach (var code in new[] { "ZZB", "ZZA" })
        {
            context.Currencies.Add(new Currency
            {
                CurrencyCode = code, Name = $"Test {code}", MinorUnits = 2,
            });
        }

        // Composite-keyed, so its inversion comes from the accounts rather than a crafted id: the
        // two remaining accounts are added highest-first.
        foreach (var otherAccountId in accountIds.Skip(1).OrderByDescending(id => id))
        {
            context.AccountSmartTags.Add(new AccountSmartTag
            {
                AccountId = otherAccountId, TransactionTagId = tagId, AddedAt = now,
            });
        }

        await context.SaveChangesAsync();
    }

    // ── Deterministic ordering (spec §10.1.9) ─────────────────────────────────

    [Fact]
    public async Task Export_Collections_AreDeterministicallyOrderedByPrimaryKey()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var accountIds = finance.GetProperty("accounts").EnumerateArray()
            .Select(account => account.GetProperty("accountId").GetGuid())
            .ToList();

        Assert.Equal(3, accountIds.Count);
        Assert.Equal(accountIds.OrderBy(id => id).ToList(), accountIds);
    }

    // ── Cancellation awareness (spec §10.1.10) ────────────────────────────────

    [Fact]
    public async Task WriteExport_WithCancelledToken_Throws()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DataExportService>();
        using var output = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.WriteExportAsync(
                output, service.CreateHeader(ActorUserId), new CancellationToken(canceled: true)));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────


    /// <summary>
    /// AC 18 — the pause stamp is exported for every contract: a stored fact the export silently
    /// omitted would be an incomplete subject-access response. Two rows, because covering only the
    /// paused one would let a projection that hardcodes a value pass.
    /// </summary>
    [Fact]
    public async Task Export_IncludesTheContractPauseStamp_ForEveryContract()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        var pausedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            await context.Database.EnsureCreatedAsync();
            context.Contracts.AddRange(
                new Contract
                {
                    ContractId = Guid.NewGuid(), Name = "Frozen membership",
                    Type = ContractType.Membership, StartDate = DateTime.UtcNow.AddYears(-1),
                    Paused = pausedAt, CreatedAtUtc = DateTime.UtcNow,
                },
                new Contract
                {
                    ContractId = Guid.NewGuid(), Name = "Running lease",
                    Type = ContractType.Rental, StartDate = DateTime.UtcNow.AddYears(-1),
                    CreatedAtUtc = DateTime.UtcNow,
                });
            await context.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        using var document = await GetExportDocumentAsync(client);
        var contracts = document.RootElement.GetProperty("databases").GetProperty("finance")
            .GetProperty("contracts").EnumerateArray().ToList();

        Assert.Equal(2, contracts.Count);
        var frozen = Assert.Single(contracts, c => c.GetProperty("name").GetString() == "Frozen membership");
        Assert.Equal(pausedAt, frozen.GetProperty("paused").GetDateTime());
        var running = Assert.Single(contracts, c => c.GetProperty("name").GetString() == "Running lease");
        Assert.Equal(JsonValueKind.Null, running.GetProperty("paused").ValueKind);
    }

    private static async Task<JsonDocument> GetExportDocumentAsync(HttpClient client)
    {
        var response = await client.GetAsync(ExportPath);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Seeds a representative finance dataset: three accounts (for ordering), one fully-wired
    /// transaction, file metadata backed by a blob, plus the file-analysis job + candidate that
    /// must be excluded.
    /// </summary>
    internal static async Task SeedFinanceAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        var journalContext = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await journalContext.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        context.Accounts.AddRange(
            new Account { AccountId = accountId, Name = "Primary", Description = "Primary account", Opened = DateTime.UtcNow },
            new Account { AccountId = Guid.NewGuid(), Name = "Secondary", Description = "Second account", Opened = DateTime.UtcNow },
            new Account { AccountId = Guid.NewGuid(), Name = "Tertiary", Description = "Third account", Opened = DateTime.UtcNow });

        context.Terms.Add(new Term
        {
            TermId = Guid.NewGuid(),
            AccountId = accountId,
            Label = "Interest rate",
            LabelKey = "interest rate",
            ValueUnit = TermValueUnit.Percentage,
            Value = 0.0325m,
            EffectiveFrom = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        });

        var contactId = Guid.NewGuid();
        journalContext.Contacts.Add(new Contact
        {
            ContactId = contactId,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "acme",
            Type = Odyssey.Dtos.ContactType.Organization,
            OrganizationDetails = new() { LegalName = "Acme" },
        });

        var tagId = Guid.NewGuid();
        context.TransactionTags.Add(new TransactionTag { TransactionTagId = tagId, Name = "Groceries" });

        var transactionId = Guid.NewGuid();
        context.Transactions.Add(new Transaction
        {
            TransactionId = transactionId,
            Description = "Weekly shop",
            Amount = 42.50m,
            TimeStamp = DateTime.UtcNow,
            AccountId = accountId,
            ContactId = contactId,
            TransactionTagLinks = new List<TransactionTagLink>
            {
                new() { TransactionId = transactionId, TransactionTagId = tagId },
            },
        });

        var budgetId = Guid.NewGuid();
        context.Budgets.Add(new Budget
        {
            BudgetId = budgetId,
            Name = "Monthly",
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddMonths(1),
        });
        context.BudgetItems.Add(new BudgetItem
        {
            BudgetItemId = Guid.NewGuid(),
            BudgetId = budgetId,
            PlannedAmount = 300m,
            TransactionTagId = tagId,
        });

        var blobId = Guid.NewGuid();
        var fileMetadataId = Guid.NewGuid();
        context.FileBlob.Add(new FileBlob { Id = blobId, Content = BlobContent });
        context.FileMetadata.Add(new FileMetadata
        {
            Id = fileMetadataId,
            UploadedByUserId = "uploader",
            FileName = "statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = BlobContent.Length,
            Sha256Hash = "abc123",
            FileBlobId = blobId,
            UploadedAtUtc = DateTime.UtcNow,
        });
        context.AccountFiles.Add(new AccountFile
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            FileMetadataId = fileMetadataId,
            AttachedByUserId = "uploader",
            AttachedAtUtc = DateTime.UtcNow,
            FileType = AccountFileType.Statement,
        });

        // ── Issue #33: the tables the export used to omit silently ────────────

        context.AccountEstimates.Add(new AccountEstimate
        {
            AccountEstimateId = Guid.NewGuid(),
            AccountId = accountId,
            Value = 185_000m,
            CurrencyCode = "USD",
            EffectiveFrom = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        });
        context.AccountSmartTags.Add(new AccountSmartTag
        {
            AccountId = accountId,
            TransactionTagId = tagId,
            AddedAt = DateTime.UtcNow,
        });

        var taxStatementId = Guid.NewGuid();
        context.TaxStatements.Add(new TaxStatement
        {
            TaxStatementId = taxStatementId,
            Name = "FY2025",
            FiscalYear = 2025,
            StartDate = DateTime.UtcNow.AddYears(-1),
            EndDate = DateTime.UtcNow,
            DeclaredNetWorth = 1_250_000m,
            DeclaredTotalIncome = 96_000m,
            AssessedTax = 24_500m,
            Notes = TaxStatementMarker,
            CreatedAtUtc = DateTime.UtcNow,
        });
        context.TaxStatementTags.Add(new TaxStatementTag
        {
            Id = Guid.NewGuid(),
            TaxStatementId = taxStatementId,
            TransactionTagId = tagId,
            Role = Odyssey.Dtos.Finance.TaxStatementTagRole.Income,
        });
        context.TaxStatementFiles.Add(new TaxStatementFile
        {
            Id = Guid.NewGuid(),
            TaxStatementId = taxStatementId,
            FileMetadataId = fileMetadataId,
            AttachedByUserId = "uploader",
            AttachedAtUtc = DateTime.UtcNow,
        });

        var contractId = Guid.NewGuid();
        context.Contracts.Add(new Contract
        {
            ContractId = contractId,
            Name = "Lease",
            Type = ContractType.Rental,
            StartDate = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        });
        context.ContractParties.AddRange(
            new ContractParty
            {
                ContractPartyId = Guid.NewGuid(), ContractId = contractId, ContactId = contactId,
            },
            // The other branch of the one-of-two. Exporting only the Institution side would leave a
            // dropped AccountId column passing every test.
            new ContractParty
            {
                ContractPartyId = Guid.NewGuid(), ContractId = contractId, AccountId = accountId,
            });
        context.ContractSmartTags.Add(new ContractSmartTag
        {
            ContractId = contractId,
            TransactionTagId = tagId,
            AddedAt = DateTime.UtcNow,
        });
        context.ContractFiles.Add(new ContractFile
        {
            ContractFileId = Guid.NewGuid(),
            ContractId = contractId,
            FileMetadataId = fileMetadataId,
            AttachedByUserId = "uploader",
            AttachedAtUtc = DateTime.UtcNow,
        });

        // File-analysis records — must NOT appear in the export.
        var jobId = Guid.NewGuid();
        context.FileAnalysisJobs.Add(new FileAnalysisJob
        {
            Id = jobId,
            AccountFileId = Guid.NewGuid(),
            RequestedByUserId = "uploader",
        });
        context.FileAnalysisCandidateTransactions.Add(new FileAnalysisCandidateTransaction
        {
            Id = CandidateTransactionId,
            AnalysisJobId = jobId,
            TransactionDate = DateTime.UtcNow,
            Description = CandidateMarker,
            Amount = 9.99m,
            Currency = "USD",
        });
        context.FileAnalysisCandidateTags.Add(new FileAnalysisCandidateTag
        {
            CandidateTransactionId = CandidateTransactionId,
            TransactionTagId = tagId,
        });

        await context.SaveChangesAsync();
        await journalContext.SaveChangesAsync();
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
