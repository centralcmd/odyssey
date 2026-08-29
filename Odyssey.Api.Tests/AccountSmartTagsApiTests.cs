using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
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

public class AccountSmartTagsApiTests
{
    private const string ActorUserId = "account-smart-tags-actor-id";

    private static string SmartTagsPath(Guid accountId) => $"/api/accounts/{accountId}/smart-tags";
    private static string SmartTagPath(Guid accountId, Guid tagId) => $"/api/accounts/{accountId}/smart-tags/{tagId}";

    private static readonly string[] WriteAndRead =
        [PermissionClaims.AccountsRead, PermissionClaims.AccountsUpdate];

    // ── Authorization matrix (spec §10) ───────────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(SmartTagsPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutAccountsReadPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(SmartTagsPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Write_WithReadOnlyPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.AccountsRead]);
        var accountId = await SeedAccountAsync(factory);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsync(SmartTagPath(accountId, tagId), null);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var delete = await client.DeleteAsync(SmartTagPath(accountId, tagId));
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    // ── CRUD behaviour (spec §16) ─────────────────────────────────────────────

    [Fact]
    public async Task List_NewAccount_ReturnsEmpty()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var tags = await client.GetFromJsonAsync<List<ExistingTransactionTag>>(SmartTagsPath(accountId));

        Assert.Empty(tags!);
    }

    [Fact]
    public async Task List_MissingAccount_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(SmartTagsPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_ValidTag_ReturnsCreatedAndIsListed()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory);
        var tagId = await SeedTagAsync(factory, "Groceries");
        using var client = factory.CreateClient();

        var post = await client.PostAsync(SmartTagPath(accountId, tagId), null);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var tags = await client.GetFromJsonAsync<List<ExistingTransactionTag>>(SmartTagsPath(accountId));
        var tag = Assert.Single(tags!);
        Assert.Equal(tagId, tag.TransactionTagId);
        Assert.Equal("Groceries", tag.Name);
    }

    [Fact]
    public async Task Post_Duplicate_ReturnsConflict()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();

        await client.PostAsync(SmartTagPath(accountId, tagId), null);
        var duplicate = await client.PostAsync(SmartTagPath(accountId, tagId), null);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Post_ArchivedTag_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory);
        var tagId = await SeedTagAsync(factory, archived: true);
        using var client = factory.CreateClient();

        var post = await client.PostAsync(SmartTagPath(accountId, tagId), null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task Post_MissingAccount_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsync(SmartTagPath(Guid.NewGuid(), tagId), null);

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact]
    public async Task Post_MissingTag_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsync(SmartTagPath(accountId, Guid.NewGuid()), null);

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact]
    public async Task Delete_ExistingAssociation_ReturnsNoContentAndRemoves()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();

        await client.PostAsync(SmartTagPath(accountId, tagId), null);

        var delete = await client.DeleteAsync(SmartTagPath(accountId, tagId));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var tags = await client.GetFromJsonAsync<List<ExistingTransactionTag>>(SmartTagsPath(accountId));
        Assert.Empty(tags!);
    }

    [Fact]
    public async Task Delete_NonexistentAssociation_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();

        var delete = await client.DeleteAsync(SmartTagPath(accountId, tagId));

        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task GetAccount_ExposesSmartTagCount()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        var accountId = await SeedAccountAsync(factory);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();

        await client.PostAsync(SmartTagPath(accountId, tagId), null);

        var account = await client.GetFromJsonAsync<ExistingAccount>($"/api/accounts/{accountId}");
        Assert.Equal(1, account!.SmartTagCount);

        var list = await client.GetPagedItemsAsync<ExistingAccount>("/api/accounts");
        Assert.Equal(1, list!.Single(a => a.AccountId == accountId).SmartTagCount);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedAccountAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        context.Accounts.Add(new Account
        {
            AccountId = accountId,
            Name = "Test",
            Description = "Test account",
            Opened = DateTime.UtcNow,
            AccountType = Odyssey.Context.AccountType.CheckingAccount,
            CurrencyCode = "USD",
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    /// <summary>
    /// The cap is admin-editable since issue #434 (key 15), and the SERVER is the control: a client-side
    /// pre-check is a convenience, so an over-cap add is rejected here whatever the browser believed.
    /// The cap under test is deliberately not the shipped 20 — a test that only passed at the default
    /// would pass equally well against a service that had gone back to reading a constant.
    /// </summary>
    [Fact]
    public async Task Add_OverTheConfiguredCap_IsRejected_AndNamesTheEffectiveCap()
    {
        await using var factory = new ApiFactory(WriteAndRead);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.AccountMaxSmartTagsPerAccount, "3");
        using var client = factory.CreateClient();
        var accountId = await SeedAccountAsync(factory);

        for (var i = 0; i < 3; i++)
        {
            var tagId = await SeedTagAsync(factory, $"cap-{i}");
            var allowed = await client.PostAsync(SmartTagPath(accountId, tagId), content: null);
            Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        }

        var overCapTag = await SeedTagAsync(factory, "over-cap");
        var response = await client.PostAsync(SmartTagPath(accountId, overCapTag), content: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("3", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<Guid> SeedTagAsync(
        WebApplicationFactory<Program> factory,
        string name = "Tag",
        bool archived = false)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var tagId = Guid.NewGuid();
        context.TransactionTags.Add(new TransactionTag
        {
            TransactionTagId = tagId,
            Name = name,
            Archived = archived ? DateTime.UtcNow : null,
        });
        await context.SaveChangesAsync();
        return tagId;
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
