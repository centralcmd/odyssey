using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using System.Security.Claims;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The embedded ledger — the account, budget and smart-tag sections' shared transaction table.
/// </summary>
/// <remarks>
/// <para>
/// It resolves its own permission claims rather than taking them as parameters, precisely because a
/// host that forgot to pass one would silently render a lesser menu — which is the defect it was
/// written to fix, when three embedded copies each offered only View details and Copy ID. These tests
/// pin that the claims actually reach the menu, so the gating cannot regress into "always read-only"
/// or "always everything" without failing.
/// </para>
/// </remarks>
[Collection(TransactionLedgerCollection.Name)]
public class TransactionListViewTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    public TransactionListViewTests()
    {
        // A bUnit host is not a browser, so the component's off-browser early return would skip the
        // claim load these tests are about. Restored on teardown — the seam is process-wide.
        TransactionListView.InteractiveCheck = static () => true;
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<ITransactionsApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        TransactionListView.InteractiveCheck = static () => OperatingSystem.IsBrowser();
        await ctx.DisposeAsync();
    }

    private static ExistingTransaction Row() => new()
    {
        TransactionId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        Description = "Kiwi Minipris",
        Amount = -249.50m,
        TimeStamp = new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc),
        CurrencyCode = "NOK",
        Status = TransactionStatus.New,
    };

    private IRenderedComponent<TransactionListView> Render(params string[] permissions)
    {
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new StubAuth(permissions));
        return ctx.Render<TransactionListView>(p => p
            .Add(v => v.Rows, (IReadOnlyList<ExistingTransaction>)[Row()]));
    }

    private static IReadOnlyList<string> MenuLabels(IRenderedComponent<TransactionListView> cut) =>
        [.. cut.FindAll(".mud-menu button, .odc-menu-trigger").Count > 0
            ? cut.FindComponents<Odyssey.Client.Components.OdsMenu>()
                .SelectMany(m => m.Instance.Items ?? [])
                .Where(i => !i.Divider)
                .Select(i => i.Label ?? string.Empty)
            : []];

    /// <summary>
    /// The point of the component: an embedded row is NOT read-only. A permitted user gets the same
    /// menu here as on the Transactions page.
    /// </summary>
    [Fact]
    public void A_permitted_user_gets_the_full_menu_on_an_embedded_row()
    {
        var cut = Render(PermissionClaims.TransactionsUpdate, PermissionClaims.TransactionsDelete);

        Assert.Equal(
            ["View details", "Edit", "Approve", "Flag", "Copy ID", "Delete"],
            MenuLabels(cut));
    }

    /// <summary>Claims are what remove items — and they do reach the menu from inside the component.</summary>
    [Fact]
    public void Without_the_write_claims_only_the_reading_actions_remain()
    {
        var cut = Render();

        Assert.Equal(["View details", "Copy ID"], MenuLabels(cut));
    }

    [Fact]
    public void The_update_claim_alone_withholds_delete()
    {
        var cut = Render(PermissionClaims.TransactionsUpdate);

        var labels = MenuLabels(cut);
        Assert.Contains("Edit", labels);
        Assert.Contains("Approve", labels);
        Assert.DoesNotContain("Delete", labels);
    }

    [Fact]
    public void The_delete_claim_alone_withholds_edit_and_the_status_transitions()
    {
        var cut = Render(PermissionClaims.TransactionsDelete);

        Assert.Equal(["View details", "Copy ID", "Delete"], MenuLabels(cut));
    }

    private sealed class StubAuth(IReadOnlyList<string> permissions) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [.. permissions.Select(p => new Claim(PermissionClaims.Type, p))], "test"))));
    }
}
