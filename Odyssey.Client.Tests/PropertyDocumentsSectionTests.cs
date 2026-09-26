using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// A property's Documents section (issue #210; design system · Properties.jsx PropertyDocuments): the
/// shared <c>OdsFilesTable</c> in its contract configuration over <c>GET /api/properties/{id}/files</c>.
/// </summary>
/// <remarks>
/// The rules pinned here are the ones that fail silently: an issuer the caller may not name must say
/// so rather than print a dash that reads as "no issuer"; the danger item only DETACHES, so it must
/// never read "Delete"; and the row menu's "Attach documents" token must open the dialog only for a
/// caller who can attach — a stale or empty token opening it on expand would be a dialog nobody asked
/// for.
/// </remarks>
public class PropertyDocumentsSectionTests
{
    static PropertyDocumentsSectionTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid PropertyId = Guid.Parse("21021021-0000-0000-0000-000000000001");
    private static readonly Guid IssuerId = Guid.Parse("21021021-0000-0000-0000-0000000000aa");

    private static ExistingProperty House() => new()
    {
        PropertyId = PropertyId,
        Name = "Storgata 14",
        Description = "Primary residence",
        Type = PropertyType.RealEstate,
        CurrencyCode = "NOK",
    };

    private static ExistingProperty Car() => new()
    {
        PropertyId = PropertyId,
        Name = "Family Tesla",
        Description = "Daily driver",
        Type = PropertyType.Vehicle,
        CurrencyCode = "NOK",
    };

    private static ExistingPropertyFile Document(
        string name, PropertyFileType type = PropertyFileType.Deed, Guid? issuedBy = null) => new()
    {
        PropertyFileId = Guid.NewGuid(),
        PropertyId = PropertyId,
        FileType = type,
        AttachedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        IssuedBy = issuedBy,
        FileMetadata = new ExistingFileMetadata
        {
            Id = Guid.NewGuid(),
            FileName = name,
            ContentType = "application/pdf",
            SizeBytes = 25_800,
            FileBlobId = Guid.NewGuid(),
            UploadedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        },
    };

    private static ExistingContact Contact(Guid id, string name) => new()
    {
        ContactId = id,
        DisplayName = name,
        ResolvedDisplayName = name,
        NormalizedName = name.ToUpperInvariant(),
        ExternalUid = id.ToString(),
    };

    private sealed record Harness(
        BunitContext Context,
        IRenderedComponent<SectionHost> Host,
        Mock<IPropertiesApiClient> Properties,
        List<int> Counts)
    {
        public IRenderedComponent<PropertyDocumentsSection> Section => Host.FindComponent<PropertyDocumentsSection>();

        public string Markup => Host.Markup;
    }

    private static Harness Render(
        List<ExistingPropertyFile>? files,
        ExistingProperty? property = null,
        bool canUpdate = true,
        bool canAttach = true,
        bool canUpload = true,
        Guid? token = null,
        IReadOnlyList<ExistingContact>? contacts = null,
        bool canReadContacts = true,
        bool load = true)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var properties = new Mock<IPropertiesApiClient>();
        properties.Setup(p => p.ListFilesAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => files is null
                ? ApiResult<List<ExistingPropertyFile>>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = "boom" })
                : ApiResult<List<ExistingPropertyFile>>.Success([.. files], HttpStatusCode.OK));
        properties.Setup(p => p.DetachFileAsync(PropertyId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        ctx.Services.AddSingleton(properties.Object);

        var library = new Mock<IFilesApiClient>();
        library.Setup(f => f.ListAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<FileListItem>>.Success([], HttpStatusCode.OK));
        ctx.Services.AddSingleton(library.Object);
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton(Mock.Of<IContactQuickCreate>());
        ctx.Services.AddSingleton(Mock.Of<IUploadLimitsCache>());
        ctx.Services.AddSingleton(TimeProvider.System);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedOut());

        var counts = new List<int>();
        var cut = ctx.Render<SectionHost>(p => p
            .Add(h => h.Property, property ?? House())
            .Add(h => h.CanUpdate, canUpdate)
            .Add(h => h.CanAttach, canAttach)
            .Add(h => h.CanUpload, canUpload)
            .Add(h => h.Token, token)
            .Add(h => h.OnCountChanged, (int n) => counts.Add(n)));

        var harness = new Harness(ctx, cut, properties, counts);
        var section = harness.Section;

        // OnInitializedAsync early-returns outside the browser: the two seams are the way in.
        cut.InvokeAsync(() => section.Instance.UseContacts(contacts ?? [], canReadContacts)).GetAwaiter().GetResult();
        if (load)
            cut.InvokeAsync(() => section.Instance.LoadAsync()).GetAwaiter().GetResult();

        return harness;
    }

    // ── Empty states ────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_real_estate_property_invites_the_deed_and_draws_no_table()
    {
        var h = Render([]);

        Assert.Contains("No documents yet — attach the deed, the purchase agreement, a valuation or a warranty.",
            h.Markup, StringComparison.Ordinal);
        Assert.Empty(h.Host.FindAll("div.con-files"));
        Assert.Empty(h.Host.FindAll("table"));
        Assert.Contains("0 files", h.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_vehicle_invites_the_registration()
    {
        var h = Render([], property: Car());

        Assert.Contains("No documents yet — attach the registration, an inspection, the insurance certificate or a warranty.",
            h.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("the deed", h.Markup, StringComparison.Ordinal);
    }

    /// <summary>A reader who cannot attach is not told to attach — the copy only states the fact.</summary>
    [Theory]
    [InlineData(PropertyType.RealEstate)]
    [InlineData(PropertyType.Vehicle)]
    public void A_reader_who_cannot_attach_gets_the_plain_empty_line(PropertyType type)
    {
        var h = Render([], property: type == PropertyType.Vehicle ? Car() : House(), canAttach: false);

        Assert.Contains("No documents are attached to this property.", h.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("attach the", h.Markup, StringComparison.Ordinal);
    }

    // ── Loading, counting and failure ───────────────────────────────────────────

    [Fact]
    public void Documents_render_in_the_table_and_the_count_is_reported_up()
    {
        var h = Render([Document("skjøte-storgata.pdf"), Document("takst-2025.pdf", PropertyFileType.Valuation)]);

        Assert.NotEmpty(h.Host.FindAll("div.con-files table"));
        Assert.Contains("skjøte-storgata.pdf", h.Markup, StringComparison.Ordinal);
        Assert.Contains("takst-2025.pdf", h.Markup, StringComparison.Ordinal);
        Assert.Contains("2 files", h.Markup, StringComparison.Ordinal);
        Assert.Equal([2], h.Counts);
        Assert.Equal("Documents for Storgata 14", h.Section.FindComponent<OdsFilesTable>().Instance.AriaLabel);
    }

    [Fact]
    public void One_document_reads_in_the_singular()
    {
        var h = Render([Document("deed.pdf")]);

        Assert.Contains("1 file", h.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("1 files", h.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The table is in the contract configuration: validity columns, no rename (the PUT carries no
    /// name), a required type (the PUT is a full replacement), and the property vocabulary as its kinds.
    /// </summary>
    [Fact]
    public void The_table_runs_in_the_link_configuration()
    {
        var table = Render([Document("deed.pdf")]).Section.FindComponent<OdsFilesTable>().Instance;

        Assert.True(table.ValidityColumns);
        Assert.False(table.Renameable);
        Assert.True(table.RequireType);
        Assert.Same(OdsTypeRegistries.PropertyFileOptions, table.Kinds);
        Assert.Equal("Detach", table.DeleteLabel);
        Assert.Equal("link_off", table.DeleteIcon);
    }

    /// <summary>A failed read says so and offers a retry — it never reports a count, so the delete
    /// dialog is not told "no documents" on the strength of an error.</summary>
    [Fact]
    public void A_failed_load_offers_a_retry_and_reports_no_count()
    {
        var h = Render(files: null);

        Assert.Contains("Couldn’t load the documents.", h.Markup, StringComparison.Ordinal);
        Assert.Contains(h.Host.FindAll("button"), b => b.TextContent.Contains("Try again", StringComparison.Ordinal));
        Assert.Empty(h.Counts);
    }

    // ── Issuer resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// The response carries the issuer's id alone (§7.3). Without contacts.read the cell says the
    /// name is withheld — never a blank, which would read as "no issuer".
    /// </summary>
    [Fact]
    public void Without_contacts_read_the_issuer_reads_no_access()
    {
        var h = Render(
            [Document("deed.pdf", issuedBy: IssuerId)],
            contacts: [Contact(IssuerId, "Kartverket")],
            canReadContacts: false);

        Assert.Contains("Contact (no access)", h.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Kartverket", h.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_issuer_the_contacts_read_does_not_know_reads_unknown_contact()
    {
        var h = Render(
            [Document("deed.pdf", issuedBy: IssuerId)],
            contacts: [Contact(Guid.NewGuid(), "Someone else")]);

        Assert.Contains("Unknown contact", h.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resolved_issuer_reads_by_name()
    {
        var h = Render([Document("deed.pdf", issuedBy: IssuerId)], contacts: [Contact(IssuerId, "Kartverket")]);

        Assert.Contains("Kartverket", h.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown contact", h.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Contact (no access)", h.Markup, StringComparison.Ordinal);
    }

    /// <summary>No issuer at all is not a withheld one — the no-access label is for an id only.</summary>
    [Fact]
    public void A_document_with_no_issuer_is_not_labelled_no_access()
    {
        var h = Render([Document("deed.pdf")], canReadContacts: false);

        Assert.DoesNotContain("Contact (no access)", h.Markup, StringComparison.Ordinal);
    }

    // ── The row menu ────────────────────────────────────────────────────────────

    // An item's TextContent also carries its icon ligature, so read the label span.
    private static IReadOnlyList<string> OpenRowMenu(Harness h)
    {
        h.Host.Find("button[aria-label='Row actions']").Click();
        h.Host.WaitForElement("div.mud-menu-item");
        return [.. h.Host.FindAll(".odc-menu-item-body > span:first-child").Select(e => e.TextContent.Trim())];
    }

    /// <summary>
    /// With properties.update the danger item is Detach · link_off — DELETE removes the link only, the
    /// file stays in Files — and it is never labelled Delete.
    /// </summary>
    [Fact]
    public void An_updater_gets_edit_and_a_detach_item_never_a_delete()
    {
        var h = Render([Document("deed.pdf")]);

        var labels = OpenRowMenu(h);

        Assert.Equal(["Edit", "Download", "Copy ID", "Detach"], labels);
        Assert.DoesNotContain("Delete", labels);
        Assert.Contains("link_off", h.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reader_without_update_gets_only_the_read_items()
    {
        var h = Render([Document("deed.pdf")], canUpdate: false);

        Assert.Equal(["Download", "Copy ID"], OpenRowMenu(h));
    }

    /// <summary>Detach asks first, then removes the link and re-reads the list.</summary>
    [Fact]
    public void Detach_confirms_then_calls_the_link_endpoint_and_reloads()
    {
        var doc = Document("deed.pdf");
        var h = Render([doc]);

        OpenRowMenu(h);
        h.Host.FindAll("div.mud-menu-item").Last().Click();

        h.Host.WaitForAssertion(() =>
            Assert.Contains("The file itself is kept in Files.", h.Markup, StringComparison.Ordinal));
        h.Host.FindAll(".mud-message-box button, .mud-dialog-actions button")
            .First(b => b.TextContent.Trim() == "Detach")
            .Click();

        h.Host.WaitForAssertion(() => h.Properties.Verify(
            p => p.DetachFileAsync(PropertyId, doc.FileMetadata.Id, It.IsAny<CancellationToken>()), Times.Once));
        // Once by the harness, once after the detach.
        h.Host.WaitForAssertion(() => h.Properties.Verify(
            p => p.ListFilesAsync(PropertyId, It.IsAny<CancellationToken>()), Times.Exactly(2)));
    }

    // ── The attach request token ───────────────────────────────────────────────

    /// <summary>
    /// A fresh token opens the attach dialog — but only for a caller who can attach, and an empty
    /// token (a host's dictionary miss) is ignored like no token at all.
    /// </summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Only_a_real_token_for_an_attacher_opens_the_attach_dialog(bool real, bool canAttach, bool opens)
    {
        var h = Render([], canAttach: canAttach, token: real ? Guid.NewGuid() : Guid.Empty);

        Assert.Equal(opens, h.Host.FindComponents<PropertyAttachDialog>().Count == 1);
    }

    [Fact]
    public void No_token_opens_nothing()
    {
        var h = Render([Document("deed.pdf")]);

        Assert.Empty(h.Host.FindComponents<PropertyAttachDialog>());
    }

    /// <summary>The same token re-sent on a re-render does not re-open a dialog the user closed.</summary>
    [Fact]
    public async Task A_handled_token_does_not_reopen_the_dialog()
    {
        var token = Guid.NewGuid();
        var h = Render([], token: token);

        var dialog = h.Host.FindComponent<PropertyAttachDialog>();
        await h.Host.InvokeAsync(() => dialog.Instance.OpenChanged.InvokeAsync(false));
        h.Host.Render(p => p.Add(x => x.Token, token));

        Assert.False(h.Host.FindComponent<PropertyAttachDialog>().Instance.Open);
    }

    /// <summary>The dialog receives the section's list, so a linked file reads "Already attached".</summary>
    [Fact]
    public void The_attach_dialog_is_handed_the_current_documents_and_the_upload_claim()
    {
        var doc = Document("deed.pdf");
        var h = Render([doc], canUpload: false, token: Guid.NewGuid());

        var dialog = h.Host.FindComponent<PropertyAttachDialog>().Instance;
        Assert.Contains(dialog.Attached, a => a.FileMetadata.Id == doc.FileMetadata.Id);
        Assert.False(dialog.CanUpload);
    }

    private sealed class SignedOut : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    /// <summary>
    /// The section beside both providers: the row menu portals into the popover provider, and the
    /// attach dialog and the detach confirmation into the dialog provider — one render tree, so their
    /// markup is the host's.
    /// </summary>
    public sealed class SectionHost : ComponentBase
    {
        [Parameter] public ExistingProperty Property { get; set; } = default!;
        [Parameter] public bool CanUpdate { get; set; }
        [Parameter] public bool CanAttach { get; set; }
        [Parameter] public bool CanUpload { get; set; }
        [Parameter] public Guid? Token { get; set; }
        [Parameter] public Action<int>? OnCountChanged { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<PropertyDocumentsSection>(2);
            builder.AddComponentParameter(3, nameof(PropertyDocumentsSection.Property), Property);
            builder.AddComponentParameter(4, nameof(PropertyDocumentsSection.CanUpdate), CanUpdate);
            builder.AddComponentParameter(5, nameof(PropertyDocumentsSection.CanAttach), CanAttach);
            builder.AddComponentParameter(6, nameof(PropertyDocumentsSection.CanUpload), CanUpload);
            builder.AddComponentParameter(7, nameof(PropertyDocumentsSection.AttachRequestToken), Token);
            builder.AddComponentParameter(8, nameof(PropertyDocumentsSection.OnCountChanged),
                EventCallback.Factory.Create<int>(this, n => OnCountChanged?.Invoke(n)));
            builder.CloseComponent();
        }
    }
}
