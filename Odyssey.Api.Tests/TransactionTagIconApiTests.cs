using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #279 over real HTTP: the icon on the tag write and read paths, model validation, the
/// authorization boundary, and the icon fields on embedding reads.
/// </summary>
public class TransactionTagIconApiTests
{
    private const string TagsPath = "/api/transaction-tags";

    private static readonly string[] TagAdmin =
    [
        PermissionClaims.TransactionTagsCreate,
        PermissionClaims.TransactionTagsRead,
        PermissionClaims.TransactionTagsUpdate,
    ];

    [Fact]
    public async Task Post_with_a_catalogue_icon_is_created_and_reads_back()
    {
        await using var factory = new OdysseyApiFactory(TagAdmin);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(TagsPath, new { name = "Groceries", archived = false, icon = "shopping_cart" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tag = await client.GetFromJsonAsync<ExistingTransactionTag>(response.Headers.Location!);
        Assert.Equal("shopping_cart", tag!.Icon);
    }

    [Fact]
    public async Task Post_without_an_icon_stores_null()
    {
        await using var factory = new OdysseyApiFactory(TagAdmin);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(TagsPath, new { name = "Groceries", archived = false });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await client.GetStringAsync(response.Headers.Location!));
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("icon").ValueKind);
    }

    [Fact]
    public async Task Put_changes_the_icon_and_an_omitted_icon_resets_it()
    {
        await using var factory = new OdysseyApiFactory(TagAdmin);
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync(TagsPath, new { name = "Groceries", archived = false, icon = "shopping_cart" });
        var location = created.Headers.Location!;

        var changed = await client.PutAsJsonAsync(location, new { name = "Groceries", archived = false, icon = "restaurant" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal("restaurant", (await client.GetFromJsonAsync<ExistingTransactionTag>(location))!.Icon);

        var reset = await client.PutAsJsonAsync(location, new { name = "Groceries", archived = false });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Null((await client.GetFromJsonAsync<ExistingTransactionTag>(location))!.Icon);
    }

    public static TheoryData<string> InvalidIcons => new()
    {
        "local_offer",
        "",
        "  ",
        "Shopping_Cart",
        "<svg>",
        "not_an_icon_zq",
        new string('q', TransactionTagIcons.MaxKeyLength + 1),
    };

    [Theory]
    [MemberData(nameof(InvalidIcons))]
    public async Task An_invalid_icon_is_a_400_keyed_on_Icon_that_echoes_nothing_and_persists_nothing(string icon)
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new OdysseyApiFactory(
            TagAdmin, configureServices: services => services.AddSingleton<ILoggerProvider>(logs));
        var existingId = await SeedTagAsync(factory, "Existing", "home");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(TagsPath, new { name = "New", archived = false, icon });
        var put = await client.PutAsJsonAsync($"{TagsPath}/{existingId}", new { name = "Existing", archived = false, icon });

        foreach (var response in new[] { post, put })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(raw);
            var errors = body.RootElement.GetProperty("errors").GetProperty("Icon");
            Assert.Equal([TransactionTagIcons.InvalidIconMessage], errors.EnumerateArray().Select(e => e.GetString()));
            if (!string.IsNullOrWhiteSpace(icon))
            {
                Assert.DoesNotContain(icon, raw, StringComparison.Ordinal);
                Assert.DoesNotContain(JsonEncodedText.Encode(icon).ToString(), raw, StringComparison.Ordinal);
            }
        }

        if (!string.IsNullOrWhiteSpace(icon))
        {
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(icon, StringComparison.Ordinal));
        }

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var stored = await context.TransactionTags.AsNoTracking().ToListAsync();
        Assert.Equal("home", Assert.Single(stored).Icon);
    }

    [Fact]
    public async Task A_stored_unknown_key_reads_as_null_and_never_500s()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TransactionTagsRead]);
        var tagId = await SeedTagAsync(factory, "Legacy", "<script>retired</script>");
        using var client = factory.CreateClient();

        var single = await client.GetAsync($"{TagsPath}/{tagId}");
        var list = await client.GetAsync(TagsPath);

        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain("retired", await single.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("retired", await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null((await single.Content.ReadFromJsonAsync<ExistingTransactionTag>())!.Icon);
    }

    [Fact]
    public async Task Put_without_the_update_claim_is_forbidden_whatever_the_body()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.TransactionTagsRead, PermissionClaims.TransactionTagsCreate]);
        var tagId = await SeedTagAsync(factory, "Groceries", null);
        using var client = factory.CreateClient();

        var valid = await client.PutAsJsonAsync($"{TagsPath}/{tagId}", new { name = "Groceries", archived = false, icon = "restaurant" });
        var invalid = await client.PutAsJsonAsync($"{TagsPath}/{tagId}", new { name = "Groceries", archived = false, icon = "<svg>" });

        Assert.Equal(HttpStatusCode.Forbidden, valid.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
    }

    [Fact]
    public async Task Transactions_read_alone_sees_the_display_icon_and_embedded_tag_icons_in_order()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TransactionsRead]);
        var accountId = await SeedAccountAsync(factory);
        var transactionId = await SeedTransactionAsync(factory, accountId,
            ("Zed", null), ("food", "restaurant"), ("Bills", "receipt_long"));
        using var client = factory.CreateClient();

        var detail = await client.GetFromJsonAsync<ExistingTransaction>($"/api/transactions/{transactionId}");
        var listed = Assert.Single(await client.GetPagedItemsAsync<ExistingTransaction>("/api/transactions"));

        foreach (var transaction in new[] { detail!, listed })
        {
            Assert.Equal("receipt_long", transaction.DisplayIcon);
            Assert.Equal(["Bills", "food", "Zed"], transaction.TransactionTags.Select(tag => tag.Name));
            Assert.Equal(["receipt_long", "restaurant", null], transaction.TransactionTags.Select(tag => tag.Icon));
        }
    }

    [Fact]
    public async Task An_untagged_transaction_has_the_default_display_icon_on_the_wire()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TransactionsRead]);
        var accountId = await SeedAccountAsync(factory);
        var transactionId = await SeedTransactionAsync(factory, accountId);
        using var client = factory.CreateClient();

        using var body = JsonDocument.Parse(await client.GetStringAsync($"/api/transactions/{transactionId}"));

        Assert.Equal(TransactionTagIcons.Default, body.RootElement.GetProperty("displayIcon").GetString());
    }

    [Fact]
    public async Task Account_transactions_carry_the_display_icon()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.AccountsRead, PermissionClaims.TransactionsRead]);
        var accountId = await SeedAccountAsync(factory);
        await SeedTransactionAsync(factory, accountId, ("Zed", null), ("Food", "restaurant"));
        using var client = factory.CreateClient();

        var transactions = await client.GetFromJsonAsync<List<ExistingTransaction>>($"/api/accounts/{accountId}/transactions");

        var transaction = Assert.Single(transactions!);
        Assert.Equal("restaurant", transaction.DisplayIcon);
        Assert.Equal(["Food", "Zed"], transaction.TransactionTags.Select(tag => tag.Name));
    }

    [Fact]
    public async Task Account_smart_tags_carry_the_tag_icon()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.AccountsRead, PermissionClaims.AccountsUpdate]);
        var accountId = await SeedAccountAsync(factory);
        var iconned = await SeedTagAsync(factory, "Food", "restaurant");
        var stale = await SeedTagAsync(factory, "Legacy", "retired_key");
        using var client = factory.CreateClient();
        await client.PostAsync($"/api/accounts/{accountId}/smart-tags/{iconned}", null);
        await client.PostAsync($"/api/accounts/{accountId}/smart-tags/{stale}", null);

        var tags = await client.GetFromJsonAsync<List<ExistingTransactionTag>>($"/api/accounts/{accountId}/smart-tags");

        Assert.Equal("restaurant", tags!.Single(tag => tag.TransactionTagId == iconned).Icon);
        Assert.Null(tags!.Single(tag => tag.TransactionTagId == stale).Icon);
    }

    private static async Task<Guid> SeedTagAsync(WebApplicationFactory<Program> factory, string name, string? icon)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var tag = new TransactionTag { TransactionTagId = Guid.NewGuid(), Name = name, Icon = icon };
        context.TransactionTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.TransactionTagId;
    }

    private static async Task<Guid> SeedAccountAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var account = new Account
        {
            AccountId = Guid.NewGuid(),
            Name = "Checking",
            Description = "Test account",
            Opened = DateTime.UtcNow,
            AccountType = Odyssey.Context.AccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }

    private static async Task<Guid> SeedTransactionAsync(
        WebApplicationFactory<Program> factory, Guid accountId, params (string Name, string? Icon)[] tags)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var transaction = new Transaction
        {
            TransactionId = Guid.NewGuid(),
            Description = "Lunch",
            Amount = -12,
            TimeStamp = DateTime.UtcNow,
            AccountId = accountId,
            CurrencyCode = "USD",
            Status = TransactionStatus.New,
            StatusChangedAt = DateTime.UtcNow,
        };
        foreach (var (name, icon) in tags)
        {
            transaction.TransactionTags.Add(new TransactionTag { Name = name, Icon = icon });
        }

        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();
        return transaction.TransactionId;
    }
}

/// <summary>Sets a tag's stored icon directly, for the per-site embed tests beside each surface's own suite.</summary>
internal static class TagIconSeed
{
    public static async Task SetAsync(WebApplicationFactory<Program> factory, Guid tagId, string? icon)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var tag = await context.TransactionTags.SingleAsync(t => t.TransactionTagId == tagId);
        tag.Icon = icon;
        await context.SaveChangesAsync();
    }
}
