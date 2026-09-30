using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Edit-transaction dialog (design system · AddTransactionModal): it edits the transaction's details
/// and tags only — attaching moved to the row menu's "Attach documents" (TransactionAttachDialog) and
/// detaching to the Documents section — and it pins the required date, the one Save-blocking rule the
/// form adds on top of the DTO.
/// </summary>
[Collection(TransactionDialogCollection.Name)]
public sealed class TransactionDialogTests : IAsyncLifetime
{
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid SavingsId = Guid.NewGuid();
    private static readonly Guid TransactionId = Guid.NewGuid();
    private static readonly Guid FileId = Guid.NewGuid();

    private readonly BunitContext ctx = new();
    private readonly List<string> calls = [];
    private readonly Mock<ITransactionsApiClient> transactions = new();
    private readonly Mock<IFilesApiClient> files = new();

    static TransactionDialogTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    public TransactionDialogTests()
    {
        // A bUnit host is not a browser, so the dialog's reference-data load (which resolves the
        // transaction's account — without it Save stops at validation) would be skipped. Restored on
        // teardown.
        CreateTransactionDialog.InteractiveCheck = static () => true;

        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        // Any file call from this dialog is a regression: it no longer uploads, attaches or detaches. The
        // transaction is seeded WITH an attached file so the dialog has one it could wrongly touch.
        files.Setup(f => f.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("upload"))
            .ThrowsAsync(new InvalidOperationException("The dialog must not upload."));

        var accounts = new Mock<IAccountsApiClient>();
        accounts.Setup(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<List<ExistingAccount>>
            {
                Status = HttpStatusCode.OK,
                Value =
                [
                    new ExistingAccount { AccountId = AccountId, Name = "Everyday Checking", Description = "", Opened = DateTime.UtcNow },
                    new ExistingAccount { AccountId = SavingsId, Name = "Holiday Savings", Description = "", Opened = DateTime.UtcNow, CurrencyCode = "EUR" },
                ],
            });

        var uploadLimits = new Mock<IUploadLimitsCache>();
        uploadLimits.Setup(u => u.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UploadLimitsCache.Fallback);

        ctx.Services.AddSingleton(transactions.Object);
        ctx.Services.AddSingleton(files.Object);
        ctx.Services.AddSingleton(accounts.Object);
        ctx.Services.AddSingleton(uploadLimits.Object);
        var referenceData = new Mock<IReferenceDataCache>();
        referenceData.Setup(r => r.TransactionTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        referenceData.Setup(r => r.ContactsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        referenceData.Setup(r => r.ActiveCurrenciesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        referenceData.Setup(r => r.CurrencyOptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        ctx.Services.AddSingleton(referenceData.Object);
        ctx.Services.AddSingleton(Mock.Of<IUserPreferenceService>());
        ctx.Services.AddSingleton(Mock.Of<IContactQuickCreate>());
        ctx.Services.AddSingleton(Mock.Of<ITagQuickCreate<ExistingTransactionTag>>());
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedOut());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        CreateTransactionDialog.InteractiveCheck = static () => OperatingSystem.IsBrowser();
        await ctx.DisposeAsync();
    }

    private static ExistingTransactionFile AttachedFile() => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = TransactionId,
        Type = TransactionFileType.Receipt,
        AttachedAtUtc = new DateTime(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc),
        FileMetadata = new ExistingFileMetadata
        {
            Id = FileId,
            FileName = "groceries-receipt.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1_024,
            FileBlobId = Guid.NewGuid(),
            UploadedAtUtc = new DateTime(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc),
        },
    };

    private static ExistingTransaction Transaction() => new()
    {
        TransactionId = TransactionId,
        AccountId = AccountId,
        Description = "Weekly groceries",
        Amount = -211.04m,
        TimeStamp = new DateTime(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc),
        CurrencyCode = "USD",
        Status = TransactionStatus.New,
        TransactionFiles = [AttachedFile()],
    };

    private void UpdateReturns(ApiResult result) =>
        transactions.Setup(t => t.UpdateAsync(TransactionId, It.IsAny<NewTransaction>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("update"))
            .ReturnsAsync(result);

    private IRenderedComponent<DialogHost> RenderEdit()
    {
        var cut = ctx.Render<DialogHost>(p => p.Add(h => h.Transaction, Transaction()));
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<OdsDateField>(), f => f.Instance.Label == "Date"));
        return cut;
    }

    private static void ClickFooter(IRenderedComponent<DialogHost> cut, string label) =>
        cut.FindAll(".atm-dialog button").Single(b => b.TextContent.Contains(label, StringComparison.OrdinalIgnoreCase)).Click();

    // ─────────────────────────────────────────────────────────────────────────
    //  No attachments (design system · AddTransactionModal, attachments moved to the row)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_edit_dialog_has_no_attachments_field_and_no_file_list()
    {
        var cut = RenderEdit();

        Assert.DoesNotContain(cut.FindComponents<OdsFieldShell>(), f => f.Instance.Label == "Attachments");
        Assert.Empty(cut.FindComponents<OdsFileUpload>());
        Assert.Empty(cut.FindComponents<TransactionFilesSection>());
        Assert.Contains("Update this transaction's details or tags.", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>Save writes the transaction and nothing else — the attached files are not the dialog's to touch.</summary>
    [Fact]
    public void Save_writes_only_the_transaction()
    {
        UpdateReturns(ApiResult.Success(HttpStatusCode.NoContent));
        var cut = RenderEdit();

        ClickFooter(cut, "Save changes");

        cut.WaitForAssertion(() => Assert.Equal(["update"], calls));
        files.Verify(f => f.AttachToTransactionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<TransactionFileType>(), It.IsAny<CancellationToken>()), Times.Never);
        transactions.Verify(t => t.DetachFileAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        cut.WaitForAssertion(() => Assert.True(cut.Instance.Closed));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  The required date (design system · "forms mark required only")
    // ─────────────────────────────────────────────────────────────────────────

    // NewTransaction.TimeStamp is nullable, so the server would accept the omission; the form refuses
    // it, because the field is marked required and a marker that validation ignores is a lie.

    private static IRenderedComponent<OdsDateField> DateField(IRenderedComponent<DialogHost> cut) =>
        cut.FindComponents<OdsDateField>().Single(f => f.Instance.Label == "Date");

    private static void SetDate(IRenderedComponent<DialogHost> cut, DateTime? value)
    {
        var field = DateField(cut);
        cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value)).GetAwaiter().GetResult();
    }

    [Fact]
    public void The_date_is_marked_required()
    {
        var cut = RenderEdit();

        Assert.True(DateField(cut).Instance.Required);
    }

    [Fact]
    public void Saving_with_the_date_cleared_is_refused_on_the_field_and_sends_nothing()
    {
        UpdateReturns(ApiResult.Success(HttpStatusCode.NoContent));
        var cut = RenderEdit();

        SetDate(cut, null);
        ClickFooter(cut, "Save changes");

        cut.WaitForAssertion(() => Assert.Equal("Pick the transaction date.", DateField(cut).Instance.Error));
        Assert.Empty(calls);
        Assert.False(cut.Instance.Closed);
    }

    [Fact]
    public void Picking_a_date_again_clears_the_error_and_lets_the_save_through()
    {
        UpdateReturns(ApiResult.Success(HttpStatusCode.NoContent));
        var cut = RenderEdit();
        SetDate(cut, null);
        ClickFooter(cut, "Save changes");
        cut.WaitForAssertion(() => Assert.NotNull(DateField(cut).Instance.Error));

        SetDate(cut, new DateTime(2026, 6, 18));
        cut.WaitForAssertion(() => Assert.Null(DateField(cut).Instance.Error));

        ClickFooter(cut, "Save changes");

        cut.WaitForAssertion(() => Assert.Equal(["update"], calls));
        transactions.Verify(t => t.UpdateAsync(TransactionId,
            It.Is<NewTransaction>(n => n.TimeStamp != null && n.TimeStamp.Value.Date == new DateTime(2026, 6, 18)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  The account picker (issue #255 — one control, no nested interactive element)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The account picker's trigger is one real button carrying the popup state itself, not a button
    /// inside MudMenu's <c>role="button"</c> activator wrapper, and neither the closed dialog nor the
    /// open picker nests one interactive element in another.
    /// </summary>
    [Fact]
    public void The_account_picker_is_one_trigger_with_no_nested_controls()
    {
        var cut = RenderEdit();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.aam-type-trigger")));

        var trigger = cut.Find("button.aam-type-trigger");
        Assert.Equal("menu", trigger.GetAttribute("aria-haspopup"));
        Assert.Equal("false", trigger.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(".mud-menu-activator"));
        Assert.Empty(NestedInteractiveControlTests.NestedInteractive(cut.FindAll(".aam-field")));

        trigger.Click();
        cut.Render();

        Assert.Equal("true", cut.Find("button.aam-type-trigger").GetAttribute("aria-expanded"));
        Assert.Equal(2, cut.FindAll("[role='menuitemradio']").Count);
        var row = Assert.Single(cut.FindAll("[role='menuitemradio'][aria-checked='true']"));
        Assert.Contains("Everyday Checking", row.TextContent, StringComparison.Ordinal);
        Assert.Equal("menu", cut.Find($"#{cut.Find("button.aam-type-trigger").GetAttribute("aria-controls")}").GetAttribute("role"));
        Assert.Empty(NestedInteractiveControlTests.NestedInteractive(
            cut.FindAll(".aam-field").Concat(cut.FindAll(".mud-popover-open"))));
    }

    /// <summary>
    /// Choosing an account runs the dialog's selection (the trigger now names it and the amount takes
    /// its currency), closes the picker — the rows are not MudMenuItems any more — and hands focus
    /// back to the trigger.
    /// </summary>
    [Fact]
    public void Choosing_an_account_selects_it_closes_the_picker_and_returns_focus()
    {
        var cut = RenderEdit();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.aam-type-trigger")));
        var triggerId = cut.Find("button.aam-type-trigger").Id;

        cut.Find("button.aam-type-trigger").Click();
        cut.Render();
        cut.FindAll("[role='menuitemradio']").Single(r => r.TextContent.Contains("Holiday Savings", StringComparison.Ordinal)).Click();
        cut.Render();

        Assert.Empty(cut.FindAll("[role='menuitemradio']"));
        var trigger = cut.Find("button.aam-type-trigger");
        Assert.Equal("false", trigger.GetAttribute("aria-expanded"));
        Assert.Contains("Holiday Savings", trigger.TextContent, StringComparison.Ordinal);
        Assert.Contains(cut.FindComponents<OdsMoneyField>(), f => f.Instance.Currency == "EUR");
        Assert.Equal(triggerId, ctx.JSInterop.Invocations["odsFocusById"].Last().Arguments[0]);
    }

    /// <summary>The keyboard opens the picker onto the chosen account, and Esc gives focus back.</summary>
    [Fact]
    public void The_account_picker_opens_on_the_chosen_row_and_escape_returns_focus()
    {
        var cut = RenderEdit();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.aam-type-trigger")));
        var trigger = cut.Find("button.aam-type-trigger");
        var triggerId = trigger.Id;

        trigger.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown" });
        cut.Render();

        var chosen = cut.Find("[role='menuitemradio'][aria-checked='true']");
        Assert.Equal(chosen.Id, ctx.JSInterop.Invocations["odsFocusById"].Last().Arguments[0]);

        chosen.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.Render();

        Assert.Empty(cut.FindAll("[role='menuitemradio']"));
        Assert.Equal(triggerId, ctx.JSInterop.Invocations["odsFocusById"].Last().Arguments[0]);
    }

    /// <summary>A signed-out principal — the dialog's inline-create claims are not under test.</summary>
    private sealed class SignedOut : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    /// <summary>The dialog beside MudBlazor's providers, which host its modal and its pickers.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingTransaction Transaction { get; set; } = default!;

        public bool Closed { get; private set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<CreateTransactionDialog>(2);
            builder.AddComponentParameter(3, nameof(CreateTransactionDialog.Transaction), Transaction);
            builder.AddComponentParameter(4, nameof(CreateTransactionDialog.Open), !Closed);
            builder.AddComponentParameter(5, nameof(CreateTransactionDialog.OpenChanged),
                EventCallback.Factory.Create<bool>(this, open => Closed = !open));
            builder.CloseComponent();
        }
    }
}
