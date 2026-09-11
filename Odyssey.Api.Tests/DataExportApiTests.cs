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
                     "accounts", "accountTerms", "budgets", "budgetItems", "contacts", "currencies",
                     "exchangeRates", "transactions", "transactionTags", "fileMetadata", "accountFiles",
                     "transactionFiles",
                     // Issue #33.
                     "accountEstimates", "accountSmartTags", "taxStatements", "taxStatementTags",
                     "taxStatementFiles", "insurancePolicies", "insurancePolicyInsurers",
                     "insurancePolicyInsuredAccounts", "insurancePolicyInsuredContacts",
                     "insurancePolicyBeneficiaries", "policyRenewals", "policyRenewalFiles",
                     "contracts", "contractParties", "contractFiles", "subscriptions",
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
    public async Task Export_IncludesAccountTerms_AsFlatRows()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var term = Assert.Single(finance.GetProperty("accountTerms").EnumerateArray());
        Assert.NotEqual(Guid.Empty, term.GetProperty("accountTermId").GetGuid());
        Assert.NotEqual(Guid.Empty, term.GetProperty("accountId").GetGuid());

        // Enums serialize as their stored integer, not a nested navigation object.
        Assert.Equal(JsonValueKind.Number, term.GetProperty("termKind").ValueKind);
        Assert.Equal(JsonValueKind.Number, term.GetProperty("valueUnit").ValueKind);
        Assert.False(term.TryGetProperty("account", out _));

        // The series columns are exported, and an unlabelled rate exports both as null — the unnamed
        // series is a value the export has to state, not a column it may omit.
        Assert.Equal(JsonValueKind.Null, term.GetProperty("label").ValueKind);
        Assert.Equal(JsonValueKind.Null, term.GetProperty("labelKey").ValueKind);
    }

    /// <summary>
    /// A labelled fee exports BOTH its display label and the folded key that carries its series. The
    /// key is derived server-side and never round-trips through a request DTO, so the export is the
    /// only place it is observable — and an export that dropped it could not reproduce the series
    /// membership it encodes.
    /// </summary>
    [Fact]
    public async Task Export_IncludesAccountTermSeriesLabels()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        await AddLabelledFeeAsync(factory, "ATM · Abroad", "atm · abroad");
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var fee = finance.GetProperty("accountTerms").EnumerateArray()
            .Single(t => t.GetProperty("label").ValueKind != JsonValueKind.Null);

        Assert.Equal("ATM · Abroad", fee.GetProperty("label").GetString());
        Assert.Equal("atm · abroad", fee.GetProperty("labelKey").GetString());
    }

    private static async Task AddLabelledFeeAsync(
        WebApplicationFactory<Program> factory, string label, string labelKey)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var accountId = await context.Accounts.Select(a => a.AccountId).FirstAsync();

        context.AccountTerms.Add(new AccountTerm
        {
            AccountTermId = Guid.NewGuid(),
            AccountId = accountId,
            TermKind = TermKind.Fee,
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

    // ── Issue #33: the previously-omitted tables ──────────────────────────────

    /// <summary>
    /// The policy header and its renewals. Before issue #33 an export of a workspace holding
    /// insurance said nothing about it — not in the data, and not in <c>excludedTables</c> either.
    /// </summary>
    [Fact]
    public async Task Export_IncludesInsurancePoliciesAndRenewals()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var policy = Assert.Single(finance.GetProperty("insurancePolicies").EnumerateArray());
        Assert.Equal("Life cover", policy.GetProperty("name").GetString());
        Assert.Equal("POL-123", policy.GetProperty("policyNumber").GetString());
        Assert.Equal(JsonValueKind.Number, policy.GetProperty("type").ValueKind);

        var renewal = Assert.Single(finance.GetProperty("policyRenewals").EnumerateArray());
        Assert.Equal(policy.GetProperty("insurancePolicyId").GetGuid(), renewal.GetProperty("insurancePolicyId").GetGuid());
        Assert.Equal(420m, renewal.GetProperty("premium").GetDecimal());
        Assert.Equal(500_000m, renewal.GetProperty("coverageAmount").GetDecimal());

        Assert.Single(finance.GetProperty("policyRenewalFiles").EnumerateArray());
    }

    /// <summary>
    /// The decision issue #33 flagged as the one worth thinking about: a link row is a
    /// <c>(policy, contact)</c> pair, so it discloses a relationship between two people rather than
    /// a field of one record. It exports the relationship COLUMNS and nothing else — the same
    /// posture the read path takes for an archived link, where the id survives and the name does
    /// not. A resolved name here would make the export disclose more than the API it mirrors.
    /// </summary>
    [Theory]
    [InlineData("insurancePolicyInsurers", "contactId")]
    [InlineData("insurancePolicyInsuredAccounts", "accountId")]
    [InlineData("insurancePolicyInsuredContacts", "contactId")]
    public async Task Export_InsurancePartyLink_CarriesIdsOnly_NeverAResolvedName(
        string collection, string targetIdProperty)
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var link = Assert.Single(finance.GetProperty(collection).EnumerateArray());

        Assert.NotEqual(Guid.Empty, link.GetProperty("insurancePolicyId").GetGuid());
        Assert.NotEqual(Guid.Empty, link.GetProperty(targetIdProperty).GetGuid());

        // The whole row, named: an added name/displayName column fails here rather than shipping.
        Assert.Equal(
            new[] { "id", "insurancePolicyId", targetIdProperty, "fromDate", "toDate" }.Order(StringComparer.Ordinal),
            link.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The beneficiary table is the one that diverges: it carries its own attribution columns, and
    /// they travel with it. Same ids-only rule for the target.
    /// </summary>
    [Fact]
    public async Task Export_BeneficiaryLink_CarriesIdsAndAttribution_ButNoName()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var beneficiary = Assert.Single(finance.GetProperty("insurancePolicyBeneficiaries").EnumerateArray());

        Assert.NotEqual(Guid.Empty, beneficiary.GetProperty("contactId").GetGuid());
        Assert.Equal("designator", beneficiary.GetProperty("createdByUserId").GetString());
        Assert.Equal(
            new[] { "id", "insurancePolicyId", "contactId", "fromDate", "toDate", "createdByUserId", "createdAtUtc" }
                .Order(StringComparer.Ordinal),
            beneficiary.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

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
            Assert.Equal(
                new[] { "contractPartyId", "contractId", "accountId", "contactId" }.Order(StringComparer.Ordinal),
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
    /// Tax statements carry declared figures and an assessment — wholly user-authored financial
    /// data, and the strongest omission after insurance.
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
    public async Task Export_IncludesSubscriptionsAndAccountSideTables()
    {
        await using var factory = new ApiFactory([PermissionClaims.DataExport]);
        await SeedFinanceAsync(factory);
        using var client = factory.CreateClient();

        using var document = await GetExportDocumentAsync(client);
        var finance = document.RootElement.GetProperty("databases").GetProperty("finance");

        var subscription = Assert.Single(finance.GetProperty("subscriptions").EnumerateArray());
        Assert.Equal("Streaming", subscription.GetProperty("name").GetString());
        Assert.Equal(12.99m, subscription.GetProperty("amount").GetDecimal());
        Assert.Equal(JsonValueKind.Number, subscription.GetProperty("interval").ValueKind);

        var estimate = Assert.Single(finance.GetProperty("accountEstimates").EnumerateArray());
        Assert.Equal(185_000m, estimate.GetProperty("value").GetDecimal());

        // Composite-keyed: the row is the (account, tag) pair, so there is no id to export.
        var smartTag = Assert.Single(finance.GetProperty("accountSmartTags").EnumerateArray());
        Assert.NotEqual(Guid.Empty, smartTag.GetProperty("accountId").GetGuid());
        Assert.NotEqual(Guid.Empty, smartTag.GetProperty("transactionTagId").GetGuid());
    }

    // ── Deterministic ordering, every collection (spec §10.1.9) ───────────────

    /// <summary>
    /// The collections and the key columns each is ordered by, with the kind of comparison that
    /// key uses. All 28 of them appear here — ordering was pinned for <c>accounts</c> alone, so a
    /// dropped or wrong <c>OrderBy</c> on any of the other 27 queries passed the whole suite.
    /// Deterministic order is what makes two exports of unchanged data diffable, so it is a
    /// property of the format, not of one table.
    /// </summary>
    public static TheoryData<string, string[], KeyKind> OrderedCollections() => new()
    {
        { "accounts", ["accountId"], KeyKind.Guid },
        { "accountTerms", ["accountTermId"], KeyKind.Guid },
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
        { "insurancePolicies", ["insurancePolicyId"], KeyKind.Guid },
        { "insurancePolicyInsurers", ["id"], KeyKind.Guid },
        { "insurancePolicyInsuredAccounts", ["id"], KeyKind.Guid },
        { "insurancePolicyInsuredContacts", ["id"], KeyKind.Guid },
        { "insurancePolicyBeneficiaries", ["id"], KeyKind.Guid },
        { "policyRenewals", ["policyRenewalId"], KeyKind.Guid },
        { "policyRenewalFiles", ["id"], KeyKind.Guid },
        { "contracts", ["contractId"], KeyKind.Guid },
        { "contractParties", ["contractPartyId"], KeyKind.Guid },
        { "contractFiles", ["contractFileId"], KeyKind.Guid },
        { "subscriptions", ["subscriptionId"], KeyKind.Guid },
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
    /// the parent of the same sequence number, which keeps the unique index on the four insurance
    /// party tables (policy + target) satisfied.
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

            context.AccountTerms.Add(new AccountTerm
            {
                AccountTermId = id, AccountId = accountId, TermKind = TermKind.InterestRate,
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
                BudgetItemId = id, BudgetId = budgetId, Name = $"Item {sequence}", PlannedAmount = 10m,
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

            context.InsurancePolicies.Add(new InsurancePolicy
            {
                InsurancePolicyId = id, Name = $"Policy {sequence}", CreatedAtUtc = now,
            });
            context.InsurancePolicyInsurers.Add(new InsurancePolicyInsurer
            {
                Id = id, InsurancePolicyId = id, ContactId = contactId,
            });
            context.InsurancePolicyInsuredAccounts.Add(new InsurancePolicyInsuredAccount
            {
                Id = id, InsurancePolicyId = id, AccountId = accountId,
            });
            context.InsurancePolicyInsuredContacts.Add(new InsurancePolicyInsuredContact
            {
                Id = id, InsurancePolicyId = id, ContactId = contactId,
            });
            context.InsurancePolicyBeneficiaries.Add(new InsurancePolicyBeneficiary
            {
                Id = id, InsurancePolicyId = id, ContactId = contactId, CreatedAtUtc = now,
            });
            context.PolicyRenewals.Add(new PolicyRenewal
            {
                PolicyRenewalId = id, InsurancePolicyId = id, FromDate = now, ToDate = now.AddYears(1),
                Premium = 10m, CoverageAmount = 100m, CreatedAtUtc = now,
            });
            context.PolicyRenewalFiles.Add(new PolicyRenewalFile
            {
                Id = id, PolicyRenewalId = id, FileMetadataId = fileMetadataId, AttachedAtUtc = now,
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

            context.Subscriptions.Add(new Subscription
            {
                SubscriptionId = id, Name = $"Sub {sequence}",
                StartDate = DateOnly.FromDateTime(now), Amount = 1m,
                FirstBillingDate = DateOnly.FromDateTime(now), CreatedAtUtc = now,
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

        context.AccountTerms.Add(new AccountTerm
        {
            AccountTermId = Guid.NewGuid(),
            AccountId = accountId,
            TermKind = TermKind.InterestRate,
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
            Name = "Food",
            PlannedAmount = 300m,
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

        // A policy wired to all four party collections — the link rows are what issue #33 was
        // really about, since a (policy, contact) pair discloses a relationship between people.
        var policyId = Guid.NewGuid();
        context.InsurancePolicies.Add(new InsurancePolicy
        {
            InsurancePolicyId = policyId,
            Name = "Life cover",
            PolicyNumber = "POL-123",
            Type = InsurancePolicyType.Life,
            CreatedAtUtc = DateTime.UtcNow,
        });
        context.InsurancePolicyInsurers.Add(new InsurancePolicyInsurer
        {
            Id = Guid.NewGuid(), InsurancePolicyId = policyId, ContactId = contactId,
        });
        context.InsurancePolicyInsuredAccounts.Add(new InsurancePolicyInsuredAccount
        {
            Id = Guid.NewGuid(), InsurancePolicyId = policyId, AccountId = accountId,
        });
        context.InsurancePolicyInsuredContacts.Add(new InsurancePolicyInsuredContact
        {
            Id = Guid.NewGuid(), InsurancePolicyId = policyId, ContactId = contactId,
        });
        context.InsurancePolicyBeneficiaries.Add(new InsurancePolicyBeneficiary
        {
            Id = Guid.NewGuid(),
            InsurancePolicyId = policyId,
            ContactId = contactId,
            CreatedByUserId = "designator",
            CreatedAtUtc = DateTime.UtcNow,
        });

        var renewalId = Guid.NewGuid();
        context.PolicyRenewals.Add(new PolicyRenewal
        {
            PolicyRenewalId = renewalId,
            InsurancePolicyId = policyId,
            FromDate = DateTime.UtcNow,
            ToDate = DateTime.UtcNow.AddYears(1),
            Premium = 420m,
            CoverageAmount = 500_000m,
            CreatedAtUtc = DateTime.UtcNow,
        });
        context.PolicyRenewalFiles.Add(new PolicyRenewalFile
        {
            Id = Guid.NewGuid(),
            PolicyRenewalId = renewalId,
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
        context.ContractFiles.Add(new ContractFile
        {
            ContractFileId = Guid.NewGuid(),
            ContractId = contractId,
            FileMetadataId = fileMetadataId,
            AttachedByUserId = "uploader",
            AttachedAtUtc = DateTime.UtcNow,
        });

        context.Subscriptions.Add(new Subscription
        {
            SubscriptionId = Guid.NewGuid(),
            Name = "Streaming",
            ContactId = contactId,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Amount = 12.99m,
            CurrencyCode = "USD",
            Interval = BillingInterval.Monthly,
            IntervalCount = 1,
            FirstBillingDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAtUtc = DateTime.UtcNow,
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
