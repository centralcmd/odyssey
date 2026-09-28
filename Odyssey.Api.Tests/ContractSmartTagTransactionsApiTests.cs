using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;
using ContractPartyRole = Odyssey.Context.ContractPartyRole;

namespace Odyssey.Api.Tests;

/// <summary>
/// <c>GET /api/contracts/{id}/smart-tag-transactions</c> over HTTP (issue #226): the two-claim gate, the
/// status-code contract, the wire shape, and parity with <c>GET /api/transactions</c> — the endpoint's
/// items come from the same pipeline, so they must search, sort, page and project identically.
/// </summary>
public partial class ContractSmartTagTransactionsApiTests
{
    private const string UploaderUserId = "smart-tag-txn-uploader";

    private static readonly string[] BothClaims =
        [PermissionClaims.ContractsRead, PermissionClaims.TransactionsRead];

    private static string Path(Guid contractId, string query = "") =>
        $"/api/contracts/{contractId}/smart-tag-transactions{query}";

    // ── AC 17: authorization ──────────────────────────────────────────────────

    [Fact]
    public async Task Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new OdysseyApiFactory(permissions: null);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path(Guid.NewGuid()))).StatusCode);
    }

    [Theory]
    [InlineData(PermissionClaims.ContractsRead)]
    [InlineData(PermissionClaims.TransactionsRead)]
    public async Task EitherClaimAlone_ReturnsForbidden(string claim)
    {
        await using var factory = new OdysseyApiFactory([claim]);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path(seeded.ContractId))).StatusCode);
    }

    [Fact]
    public async Task BothClaims_ReturnsOk()
    {
        await using var factory = new OdysseyApiFactory(BothClaims);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Path(seeded.ContractId))).StatusCode);
    }

    /// <summary>
    /// Every seeded role, with its outcome PINNED rather than derived from the claims it is fed — a
    /// role-map change that granted Guest contracts.read, or took transactions.read from User, must
    /// fail here rather than move the expectation along with it.
    /// </summary>
    [Theory]
    [InlineData(nameof(RolePermissions.AdminClaims), HttpStatusCode.OK)]
    [InlineData(nameof(RolePermissions.OwnerClaims), HttpStatusCode.OK)]
    [InlineData(nameof(RolePermissions.UserClaims), HttpStatusCode.OK)]
    [InlineData(nameof(RolePermissions.GuestClaims), HttpStatusCode.Forbidden)]
    public async Task EverySeededRole_GetsItsPinnedOutcome(string role, HttpStatusCode expected)
    {
        var claims = (string[])typeof(RolePermissions).GetField(role)!.GetValue(null)!;

        await using var factory = new OdysseyApiFactory(claims);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(expected, (await client.GetAsync(Path(seeded.ContractId))).StatusCode);
    }

    // ── AC 18-19: status codes ────────────────────────────────────────────────

    [Fact]
    public async Task UnknownContract_Returns404_EchoingOnlyTheRouteId()
    {
        await using var factory = new OdysseyApiFactory(BothClaims);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        var response = await client.GetAsync(Path(unknown));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(unknown.ToString(), body);
        Assert.Equal([unknown.ToString()], GuidPattern().Matches(body).Select(m => m.Value).Distinct());
        Assert.DoesNotContain(seeded.ContractId.ToString(), body);
    }

    [Theory]
    [InlineData("?offset=-1")]
    [InlineData("?sortBy=Nonsense")]
    public async Task InvalidQuery_Returns400(string query)
    {
        await using var factory = new OdysseyApiFactory(BothClaims);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path(seeded.ContractId, query))).StatusCode);
    }

    [Fact]
    public async Task LimitAboveMax_Returns400()
    {
        await using var factory = new OdysseyApiFactory(BothClaims);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync(Path(seeded.ContractId, $"?limit={ListDefaults.MaxLimit + 1}"))).StatusCode);
    }

    [Fact]
    public async Task OverLongSearch_Returns400()
    {
        await using var factory = new OdysseyApiFactory(BothClaims);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var search = new string('a', ListDefaults.MaxSearchLength + 1);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync(Path(seeded.ContractId, $"?search={search}"))).StatusCode);
    }

    // ── AC 23, 31: wire shape ─────────────────────────────────────────────────

    [Fact]
    public async Task Envelope_IsCamelCase_WithAnOrdinalReasonAndNumericAmounts()
    {
        await using var factory = new OdysseyApiFactory(BothClaims);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(Path(seeded.ContractId)));
        var root = document.RootElement;

        Assert.Equal(["page", "scope", "summary"], Names(root));
        var scope = root.GetProperty("scope");
        Assert.Equal(["emptyReason", "from", "partyContactCount", "smartTagCount", "toExclusive"], Names(scope));
        Assert.Equal(JsonValueKind.Number, scope.GetProperty("emptyReason").ValueKind);
        Assert.Equal(0, scope.GetProperty("emptyReason").GetInt32());

        var summary = root.GetProperty("summary");
        Assert.Equal(["byCurrency", "transactionCount"], Names(summary));
        var row = summary.GetProperty("byCurrency")[0];
        Assert.Equal(["currencyCode", "net", "totalIn", "totalOut", "transactionCount"], Names(row));
        foreach (var amount in new[] { "net", "totalIn", "totalOut" })
            Assert.Equal(JsonValueKind.Number, row.GetProperty(amount).ValueKind);
        Assert.Equal(["items", "limit", "offset", "totalCount"], Names(root.GetProperty("page")));
    }

    [Fact]
    public async Task EmptyReason_SerialisesAsOrdinal_AndTheEmptySummaryHasAnArray()
    {
        await using var factory = new OdysseyApiFactory(BothClaims);
        var seeded = await SeedAsync(factory, withSmartTag: false);
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(Path(seeded.ContractId)));
        var root = document.RootElement;

        Assert.Equal((int)ContractSmartTagEmptyReason.NoSmartTags, root.GetProperty("scope").GetProperty("emptyReason").GetInt32());
        var byCurrency = root.GetProperty("summary").GetProperty("byCurrency");
        Assert.Equal(JsonValueKind.Array, byCurrency.ValueKind);
        Assert.Equal(0, byCurrency.GetArrayLength());
        Assert.Equal(0, root.GetProperty("summary").GetProperty("transactionCount").GetInt32());
        Assert.Equal(0, root.GetProperty("page").GetProperty("items").GetArrayLength());
    }

    // ── AC 15-16: parity with GET /api/transactions ───────────────────────────

    /// <summary>
    /// Every transaction in the fixture matches the contract, so the transaction list over the same
    /// rows is the oracle: identical order, filtering and paging, for each sort key and each searched
    /// field (description, account name, merchant name, tag name).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("?sortBy=Amount&sortDir=Asc")]
    [InlineData("?sortBy=Desc")]
    [InlineData("?sortBy=Contact&sortDir=Desc")]
    [InlineData("?sortBy=Account")]
    [InlineData("?sortBy=Status&offset=1&limit=2")]
    [InlineData("?search=invoice")]
    [InlineData("?search=household")]
    [InlineData("?search=hafslund")]
    [InlineData("?search=electric")]
    [InlineData("?search=nothing-matches-this")]
    public async Task Items_MatchTheTransactionList(string query)
    {
        await using var factory = new OdysseyApiFactory(BothClaims);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var scoped = JsonDocument.Parse(await client.GetStringAsync(Path(seeded.ContractId, query)));
        using var list = JsonDocument.Parse(await client.GetStringAsync($"/api/transactions{query}"));

        var page = scoped.RootElement.GetProperty("page");
        Assert.Equal(list.RootElement.GetProperty("totalCount").GetInt32(), page.GetProperty("totalCount").GetInt32());
        Assert.Equal(list.RootElement.GetProperty("items").GetRawText(), page.GetProperty("items").GetRawText());
    }

    /// <summary>
    /// AC 16 — each item is field-for-field the single-transaction read, including the resolved file
    /// attribution names and the two-member merchant embed, also for an archived merchant.
    /// </summary>
    [Fact]
    public async Task EachItem_IsFieldForFieldTheSingleRead()
    {
        await using var factory = new OdysseyApiFactory(BothClaims);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var scoped = JsonDocument.Parse(await client.GetStringAsync(Path(seeded.ContractId)));
        var items = scoped.RootElement.GetProperty("page").GetProperty("items");
        Assert.Equal(seeded.MatchingCount, items.GetArrayLength());

        foreach (var item in items.EnumerateArray())
        {
            var id = item.GetProperty("transactionId").GetGuid();
            using var single = JsonDocument.Parse(await client.GetStringAsync($"/api/transactions/{id}"));
            Assert.Equal(single.RootElement.GetRawText(), item.GetRawText());

            var contact = item.GetProperty("contact");
            Assert.Equal(["contactId", "resolvedDisplayName"], Names(contact));
        }

        var withFile = items.EnumerateArray().Single(i => i.GetProperty("transactionFiles").GetArrayLength() == 1);
        var file = withFile.GetProperty("transactionFiles")[0];
        Assert.Equal("Ada L.", file.GetProperty("attachedByName").GetString());
        Assert.Equal("Ada L.", file.GetProperty("fileMetadata").GetProperty("uploadedByName").GetString());

        Assert.Contains(items.EnumerateArray(), i =>
            i.GetProperty("contact").GetProperty("contactId").GetGuid() == seeded.ArchivedMerchantId);
    }

    /// <summary>
    /// A caller holding both claims and no <c>contacts.*</c> claim sees the merchant's id and display name
    /// and nothing more — the ContactEmbed boundary holds on this surface too.
    /// </summary>
    [Fact]
    public async Task WithoutAnyContactsClaim_TheMerchantEmbedIsIdAndNameOnly()
    {
        await using var factory = new OdysseyApiFactory(BothClaims);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync(Path(seeded.ContractId));

        Assert.Contains("Hafslund", body);
        foreach (var leaked in new[] { "organizationNumber", "legalName", "aliases", "notes", "personDetails" })
            Assert.DoesNotContain(leaked, body, StringComparison.OrdinalIgnoreCase);
    }

    // ── AC 21: GET /api/transactions pinned across the extraction ─────────────

    /// <summary>
    /// Pins search + sort + paging output of the transaction list against a fixed fixture, so the
    /// pipeline extraction (issue #226 §3.1) is proven not to have changed it.
    /// </summary>
    [Fact]
    public async Task TransactionList_SearchSortAndPaging_ArePinned()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TransactionsRead]);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        async Task<string[]> Descriptions(string query)
        {
            using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/transactions{query}"));
            return [.. doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("description").GetString()!)];
        }

        Assert.Equal(
            ["Invoice March", "Invoice February", "Invoice January", "Grid fee"],
            await Descriptions(""));
        Assert.Equal(
            ["Invoice March", "Invoice January", "Grid fee", "Invoice February"],
            await Descriptions("?sortBy=Amount&sortDir=Asc"));
        Assert.Equal(["Invoice February", "Invoice January"], await Descriptions("?offset=1&limit=2&search=invoice"));
        Assert.Equal(["Grid fee"], await Descriptions("?search=grid"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    private static string[] Names(JsonElement element) =>
        [.. element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)];

    private sealed record Seeded(Guid ContractId, Guid ArchivedMerchantId, int MatchingCount);

    /// <summary>
    /// One contract (term 2026) with the "Electricity" smart tag and two contact parties — an
    /// organization and an archived person. Every transaction seeded here matches it, so the plain
    /// transaction list is an exact oracle for the scoped one.
    /// </summary>
    private static async Task<Seeded> SeedAsync(OdysseyApiFactory factory, bool withSmartTag = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new ApplicationUser { Id = UploaderUserId, UserName = "ada@example.com", Email = "ada@example.com" });
        db.UserProfiles.Add(new UserProfile { UserId = UploaderUserId, DisplayName = "Ada L." });

        var account = new Account
        {
            Name = "Household",
            Description = "Bills",
            Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "NOK",
        };
        var supplier = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            DisplayName = "Hafslund",
            NormalizedName = "hafslund",
            Type = ContactType.Organization,
            OrganizationDetails = new OrganizationDetails { LegalName = "Hafslund Strøm AS", OrganizationNumber = "987654321" },
        };
        var landlord = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            DisplayName = "Kari Nordmann",
            NormalizedName = "kari nordmann",
            Type = ContactType.Person,
            Archived = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var tag = new TransactionTag { Name = "Electricity" };
        var contract = new Contract
        {
            Name = "Power",
            Type = Odyssey.Context.ContractType.Subscription,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.AddRange(account, supplier, landlord, tag, contract);
        await db.SaveChangesAsync();

        if (withSmartTag)
            db.ContractSmartTags.Add(new ContractSmartTag
            {
                ContractId = contract.ContractId, TransactionTagId = tag.TransactionTagId, AddedAt = DateTime.UtcNow,
            });
        db.ContractParties.AddRange(
            new ContractParty { ContractId = contract.ContractId, ContactId = supplier.ContactId, Role = ContractPartyRole.Other },
            new ContractParty { ContractId = contract.ContractId, ContactId = landlord.ContactId, Role = ContractPartyRole.Guarantor });

        Transaction Txn(string description, decimal amount, int month, Guid contactId, TransactionStatus status, int day = 15) => new()
        {
            Description = description,
            Amount = amount,
            TimeStamp = new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Utc),
            AccountId = account.AccountId,
            ContactId = contactId,
            CurrencyCode = "NOK",
            Status = status,
            TransactionTags = [tag],
        };
        var withFile = Txn("Invoice January", -900m, 1, supplier.ContactId, TransactionStatus.Approved);
        db.Transactions.AddRange(
            withFile,
            Txn("Invoice February", -800m, 2, supplier.ContactId, TransactionStatus.New),
            Txn("Invoice March", -1100m, 3, landlord.ContactId, TransactionStatus.Flagged),
            Txn("Grid fee", -850m, 1, landlord.ContactId, TransactionStatus.New, day: 10));
        await db.SaveChangesAsync();

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = UploaderUserId,
            FileName = "invoice.pdf",
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = "hash-invoice",
            FileBlobId = blob.Id,
            UploadedAtUtc = new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc),
        };
        db.FileBlob.Add(blob);
        db.FileMetadata.Add(metadata);
        await db.SaveChangesAsync();
        db.TransactionFiles.Add(new TransactionFile
        {
            Id = Guid.NewGuid(),
            TransactionId = withFile.TransactionId,
            FileMetadataId = metadata.Id,
            AttachedByUserId = UploaderUserId,
            AttachedAtUtc = new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();

        return new Seeded(contract.ContractId, landlord.ContactId, 4);
    }
}
