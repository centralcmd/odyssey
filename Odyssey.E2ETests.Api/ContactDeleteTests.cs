using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// Exercises <c>DELETE /api/contacts/{id}</c> end to end over real HTTP → <c>ContactController</c> →
/// the DI-resolved <c>ContactService</c>/<c>IContactReferenceGuard</c> → real MariaDB. The guard runs in
/// front of the database's own cross-module foreign keys (RESTRICT an in-use insurer, SET NULL the
/// nullable links) so the caller gets an explained 409 rather than a constraint violation — behaviour
/// that EF InMemory cannot run (the guard uses ExecuteUpdate/ExecuteDelete) and that the direct-service
/// integration tests exercise without the controller/DI wiring. These fill that gap.
///
/// Each test creates its own contact (and any referencing entity) with fresh ids and cleans up after
/// itself, so it stays safe against the shared seeded database.
/// </summary>
[Collection(ApiStackCollection.Name)]
public class ContactDeleteTests(ApiStackFixture fixture)
{
    private static readonly DemoUser Admin = DemoUsers.All.First(user => user.Role == "Admin");

    [SkippableFact]
    public async Task Delete_unreferenced_contact_succeeds_and_the_contact_is_gone()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var client = await fixture.CreateAuthenticatedClientAsync(Admin.Email, Admin.Password);
        var contactId = await CreateOrganizationContactAsync(client, "E2E Delete Co");

        // Proves the whole wired path — controller → DI-resolved ContactService.Delete → the guard's
        // ExecuteUpdate/ExecuteDelete against real MariaDB (a no-op here, no references) → row removed.
        var delete = await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contacts/{contactId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var get = await client.GetAsync($"/api/contacts/{contactId}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [SkippableFact]
    public async Task Deleting_an_in_use_insurer_contact_is_blocked_with_409()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var client = await fixture.CreateAuthenticatedClientAsync(Admin.Email, Admin.Password);
        var insurerId = await CreateOrganizationContactAsync(client, "E2E Insurer");
        var policyId = await CreateInsurancePolicyAsync(client, insurerId, "E2E Policy");

        // Former required + ON DELETE RESTRICT FK, now enforced by IContactReferenceGuard: the guard
        // sees the policy and ContactService.Delete throws a DomainConflictException → 409.
        var delete = await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contacts/{insurerId}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        // The contact is untouched — the block happened before any deletion.
        var stillThere = await client.GetAsync($"/api/contacts/{insurerId}");
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);

        // Cleanup: drop the policy, then the now-unreferenced contact.
        await fixture.DeleteWithAntiforgeryAsync(client, $"/api/insurance-policies/{policyId}");
        await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contacts/{insurerId}");
    }

    [SkippableFact]
    public async Task Deleting_a_custodian_contact_nulls_the_account_link_and_keeps_the_account()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var client = await fixture.CreateAuthenticatedClientAsync(Admin.Email, Admin.Password);
        var custodianId = await CreateOrganizationContactAsync(client, "E2E Custodian");
        var accountId = await CreateAccountAsync(client, custodianId, "E2E Brokerage");

        // Former ON DELETE SET NULL FK, now reconstructed by the guard: the account survives with its
        // custodian link cleared.
        var delete = await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contacts/{custodianId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var account = await client.GetFromJsonAsync<JsonDocument>($"/api/accounts/{accountId}");
        Assert.Equal(JsonValueKind.Null, account!.RootElement.GetProperty("custodianId").ValueKind);

        // Cleanup: the account we created (the contact is already gone).
        await fixture.DeleteWithAntiforgeryAsync(client, $"/api/accounts/{accountId}");
    }

    private async Task<Guid> CreateOrganizationContactAsync(HttpClient client, string legalName)
    {
        var response = await fixture.PostWithAntiforgeryAsync(client, "/api/contacts", new
        {
            type = ContactType.Organization,
            archived = false,
            organizationDetails = new { legalName },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return IdFromLocation(response);
    }

    private async Task<Guid> CreateInsurancePolicyAsync(HttpClient client, Guid insurerId, string name)
    {
        var response = await fixture.PostWithAntiforgeryAsync(client, "/api/insurance-policies", new
        {
            name,
            insurerId,
            type = InsurancePolicyType.Other,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return doc!.RootElement.GetProperty("insurancePolicyId").GetGuid();
    }

    private async Task<Guid> CreateAccountAsync(HttpClient client, Guid custodianId, string name)
    {
        var response = await fixture.PostWithAntiforgeryAsync(client, "/api/accounts", new
        {
            name,
            description = "e2e",
            accountType = AccountType.InvestmentAccount,
            currencyCode = "USD",
            archived = false,
            custodianId,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return IdFromLocation(response);
    }

    private static Guid IdFromLocation(HttpResponseMessage response)
    {
        var location = response.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("Create response had no Location header.");
        return Guid.Parse(location[(location.LastIndexOf('/') + 1)..]);
    }
}
