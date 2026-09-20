using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Documents section of <see cref="ContractDetailView"/> (Odyssey Design System ·
/// <c>ui_kits/web/Contracts.jsx</c>, the ContractDetail documents section).
/// </summary>
/// <remarks>
/// With no documents the design system renders the empty LINE alone — no table. The implementation
/// used to hand the line to the table as its <c>Empty</c> slot instead, which headed a single
/// "none yet" cell with the four sortable column headers and the pager frame: chrome describing
/// rows that do not exist, and controls a keyboard user tabs through before reaching the sentence
/// saying there is nothing to sort. Parties, Terms and Events all take the standalone form, so the
/// table-headed one also read as a different kind of emptiness than its three sibling sections.
/// </remarks>
public class ContractDocumentsSectionTests
{
    // See InsurancePolicyPartyTileTests for why this ceiling is raised: scheduling starvation on a
    // contended runner, not how long an assertion takes to settle.
    static ContractDocumentsSectionTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private const string EmptyLine = "No documents yet";

    private static readonly Guid ContractId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static ExistingContractFile Document() => new()
    {
        ContractFileId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
        ContractId = ContractId,
        FileType = ContractFileType.Signed,
        AttachedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        FileMetadata = new ExistingFileMetadata
        {
            Id = Guid.Parse("77777777-7777-7777-7777-777777777777"),
            FileName = "maple-st-lease-signed.pdf",
            ContentType = "application/pdf",
            SizeBytes = 25_800,
            FileBlobId = Guid.Parse("88888888-8888-8888-8888-888888888888"),
            UploadedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        },
    };

    /// <summary>A contract with no documents shows the empty line and NO table around it.</summary>
    [Fact]
    public void No_documents_renders_the_empty_line_without_the_table()
    {
        var cut = Render([]);

        Assert.Contains(EmptyLine, cut.Markup, StringComparison.Ordinal);
        // The frame and the table are the same decision: neither heads an empty section.
        Assert.Empty(cut.FindAll("div.con-files"));
        Assert.Empty(cut.FindAll("table"));
        // The column header a reader would otherwise see over the one "none yet" cell.
        Assert.DoesNotContain(">Uploaded<", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>One document brings the table back — the empty line is not a replacement for it.</summary>
    [Fact]
    public void One_document_renders_the_table_and_not_the_empty_line()
    {
        var cut = Render([Document()]);

        Assert.DoesNotContain(EmptyLine, cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("div.con-files table"));
        Assert.Contains("maple-st-lease-signed.pdf", cut.Markup, StringComparison.Ordinal);
    }

    private static IRenderedComponent<DetailHost> Render(List<ExistingContractFile> files)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(Mock.Of<Odyssey.ApiClient.Resources.IContractsApiClient>());
        ctx.Services.AddSingleton(TimeProvider.System);
        // The files table resolves the issuer options and the contacts.create claim for its Edit
        // dialog (issue #146), so both have to be registered even when the section renders empty.
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton(Mock.Of<IContactQuickCreate>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedOut());

        return ctx.Render<DetailHost>(p => p.Add(h => h.Files, files));
    }

    private sealed class SignedOut : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    /// <summary>
    /// The detail view beside a MudPopoverProvider: MudBlazor portals an open row menu into that
    /// provider, and the files table mounts one per row.
    /// </summary>
    public sealed class DetailHost : ComponentBase
    {
        [Parameter] public List<ExistingContractFile> Files { get; set; } = [];

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<ContractDetailView>(1);
            builder.AddComponentParameter(2, nameof(ContractDetailView.Contract), new ExistingContract
            {
                ContractId = ContractId,
                Name = "Maple Street lease",
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Files = Files,
            });
            builder.AddComponentParameter(3, nameof(ContractDetailView.CanWrite), true);
            builder.AddComponentParameter(4, nameof(ContractDetailView.CanDownload), true);
            builder.CloseComponent();
        }
    }
}
