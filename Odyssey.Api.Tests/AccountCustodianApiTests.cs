using Odyssey.Dtos;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using ContextContactType = Odyssey.Dtos.ContactType;

namespace Odyssey.Api.Tests;

/// <summary>
/// API-contract coverage for the account custodian link (issue #221): the scalar CustodianId flows
/// through the existing account contract, read responses carry the slim description-free Custodian
/// projection, invalid references map to 400 (not 500), and the write path cannot over-post
/// contact fields.
/// </summary>
public class AccountCustodianApiTests
{
    private const string ActorUserId = "account-custodian-actor-id";

    private static readonly string[] WriteAndRead =
        [PermissionClaims.AccountsRead, PermissionClaims.AccountsCreate, PermissionClaims.AccountsUpdate];

    private static NewAccount NewAccount(Guid? custodianId = null, string name = "Brokerage") => new()
    {
        Name = name,
        Description = "Acct description",
        AccountType = Odyssey.Dtos.Finance.AccountType.InvestmentAccount,
        CurrencyCode = "USD",
        Archived = false,
        CustodianId = custodianId,
    };

    [Fact]
    public async Task Post_WithValidCustodian_PersistsAndGetReturnsProjection()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var custodianId = await SeedContactAsync(factory, "Vanguard");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/api/accounts", NewAccount(custodianId));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var list = await client.GetPagedItemsAsync<ExistingAccount>("/api/accounts");
        var account = Assert.Single(list!);
        Assert.Equal(custodianId, account.CustodianId);
        Assert.NotNull(account.Custodian);
        Assert.Equal("Vanguard", account.Custodian!.Name);
        Assert.Equal(ContextContactType.Organization, account.Custodian.Type);
    }

    [Fact]
    public async Task GetSingle_SerializedCustodian_OmitsDescription()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var custodianId = await SeedContactAsync(factory, "DNB", description: "secret free-text notes");
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/accounts", NewAccount(custodianId));
        var accountId = (await client.GetPagedItemsAsync<ExistingAccount>("/api/accounts"))!.Single().AccountId;

        var json = await client.GetStringAsync($"/api/accounts/{accountId}");
        using var doc = JsonDocument.Parse(json);
        var custodian = doc.RootElement.GetProperty("custodian");

        // The slim projection must never carry the contact free-text Description (§6 / §16-3).
        var hasDescription = custodian.EnumerateObject()
            .Any(p => p.Name.Equals("description", StringComparison.OrdinalIgnoreCase));
        Assert.False(hasDescription);
        Assert.DoesNotContain("secret free-text notes", json);
        Assert.Equal("DNB", custodian.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Put_ChangesThenClearsCustodian()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var a = await SeedContactAsync(factory, "Bank A");
        var b = await SeedContactAsync(factory, "Bank B");
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/accounts", NewAccount(a));
        var accountId = (await client.GetPagedItemsAsync<ExistingAccount>("/api/accounts"))!.Single().AccountId;

        var change = await client.PutAsJsonAsync($"/api/accounts/{accountId}", NewAccount(b));
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        var afterChange = await client.GetFromJsonAsync<ExistingAccount>($"/api/accounts/{accountId}");
        Assert.Equal(b, afterChange!.CustodianId);

        var clear = await client.PutAsJsonAsync($"/api/accounts/{accountId}", NewAccount(custodianId: null));
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        var afterClear = await client.GetFromJsonAsync<ExistingAccount>($"/api/accounts/{accountId}");
        Assert.Null(afterClear!.CustodianId);
        Assert.Null(afterClear.Custodian);
    }

    [Fact]
    public async Task Post_NonExistentCustodian_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/api/accounts", NewAccount(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        // Nothing persisted on a rejected create.
        var list = await client.GetPagedItemsAsync<ExistingAccount>("/api/accounts");
        Assert.Empty(list!);
    }

    [Fact]
    public async Task Post_ArchivedCustodian_ReturnsBadRequestWithDistinctMessage()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var archivedId = await SeedContactAsync(factory, "Old Bank", archived: true);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/api/accounts", NewAccount(archivedId));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        var body = await post.Content.ReadAsStringAsync();
        Assert.Contains("archived", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PutToNonExistentId_WithValidCustodian_CreatesWithLink()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var custodianId = await SeedContactAsync(factory, "Broker");
        using var client = factory.CreateClient();

        var newId = Guid.NewGuid();
        var put = await client.PutAsJsonAsync($"/api/accounts/{newId}", NewAccount(custodianId));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        var account = (await client.GetPagedItemsAsync<ExistingAccount>("/api/accounts"))!.Single();
        Assert.Equal(custodianId, account.CustodianId);
    }

    [Fact]
    public async Task PutToNonExistentId_WithBadCustodian_ReturnsBadRequestAndCreatesNothing()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"/api/accounts/{Guid.NewGuid()}", NewAccount(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        var list = await client.GetPagedItemsAsync<ExistingAccount>("/api/accounts");
        Assert.Empty(list!);
    }

    [Fact]
    public async Task Post_WithNestedCustodianObject_DoesNotOverPostContact()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var custodianId = await SeedContactAsync(factory, "Real Bank", description: "original");
        using var client = factory.CreateClient();

        // A body that smuggles a populated nested custodian object alongside the scalar id. Only the
        // scalar CustodianId may be honoured; the nested object must never create/mutate a contact
        // (§16-12 over-posting guard).
        var body = new
        {
            name = "Acct",
            description = "Acct description",
            accountType = (int)Odyssey.Dtos.Finance.AccountType.InvestmentAccount,
            currencyCode = "USD",
            archived = false,
            custodianId,
            custodian = new
            {
                contactId = custodianId,
                name = "HIJACKED NAME",
                normalizedName = "HIJACKED NAME",
                type = (int)ContextContactType.Organization,
                description = "hijacked description",
                archived = (DateTime?)null,
            },
        };

        var post = await client.PostAsJsonAsync("/api/accounts", body);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        // The contact is untouched and no extra contact was created.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var contacts = context.Contacts.Include(c => c.OrganizationDetails).ToList();
        var only = Assert.Single(contacts);
        Assert.Equal("Real Bank", only.OrganizationDetails!.LegalName);
        Assert.Equal("original", only.Notes);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedContactAsync(
        WebApplicationFactory<Program> factory,
        string name,
        string? description = null,
        bool archived = false)
    {
        using var scope = factory.Services.CreateScope();
        // OdysseyContext holds the reference currencies (HasData) the account-create path validates
        // against; ensure it exists (Contact moved to OdysseyContext, so seeding the contact no longer
        // creates OdysseyContext as a side effect).
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = name.ToUpperInvariant(),
            Type = ContextContactType.Organization,
            Notes = description,
            Archived = archived ? DateTime.UtcNow : null,
            OrganizationDetails = new() { LegalName = name },
        });
        await context.SaveChangesAsync();
        return id;
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
