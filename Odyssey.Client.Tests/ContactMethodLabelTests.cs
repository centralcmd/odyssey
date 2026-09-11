using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
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
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The contact-method Label picker (issue #47 §3, §16.11, §16.20–26) — the one registry select whose
/// options depend on a sibling field, the parent contact's type.
/// </summary>
public class ContactMethodLabelTests
{
    private static readonly Guid ContactId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PhoneId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // ── Registry parity and the per-type projection (§16.11) ─────────────────

    /// <summary>
    /// Each registry holds exactly one entry per defined member of its own enum, keyed by member name.
    /// Scoped <b>per kind</b>: <c>Billing</c> is now a key in two registries, so a label key is no
    /// longer unique across the three.
    /// </summary>
    [Fact]
    public void Each_label_registry_holds_exactly_one_entry_per_defined_member()
    {
        AssertRegistryCovers<AddressLabel>(OdsTypeRegistries.AddressLabels);
        AssertRegistryCovers<EmailLabel>(OdsTypeRegistries.EmailLabels);
        AssertRegistryCovers<PhoneLabel>(OdsTypeRegistries.PhoneLabels);
    }

    private static void AssertRegistryCovers<TLabel>(IReadOnlyList<OdsTypeOption> registry)
        where TLabel : struct, Enum
    {
        var members = Enum.GetNames<TLabel>();

        Assert.Equal(members.Length, registry.Count);
        Assert.Equal([.. members.Order()], [.. registry.Select(t => t.Key).Order()]);
        Assert.All(registry, t => Assert.False(string.IsNullOrWhiteSpace(t.Icon)));
    }

    /// <summary>
    /// The projection is exactly <c>ContactLabelScope</c>'s list, <b>in the same order</b> — the picker
    /// must never offer a label the server would reject, and the first entry is what a new method opens
    /// on, so a reordering here changes the default.
    /// </summary>
    [Theory]
    [InlineData(ContactType.Person)]
    [InlineData(ContactType.Organization)]
    public void The_per_type_projection_matches_the_scope_map_in_order(ContactType type)
    {
        Assert.Equal(
            [.. ContactLabelScope.AddressLabelsFor(type).Select(l => l.ToString())],
            [.. OdsTypeRegistries.AddressLabelsFor(type).Select(t => t.Key)]);
        Assert.Equal(
            [.. ContactLabelScope.EmailLabelsFor(type).Select(l => l.ToString())],
            [.. OdsTypeRegistries.EmailLabelsFor(type).Select(t => t.Key)]);
        Assert.Equal(
            [.. ContactLabelScope.PhoneLabelsFor(type).Select(l => l.ToString())],
            [.. OdsTypeRegistries.PhoneLabelsFor(type).Select(t => t.Key)]);
    }

    /// <summary>
    /// <c>EmailLabel.Home</c> renders as "Personal" while its key — the value that reaches the API —
    /// stays <c>Home</c>, ordinal 1 (§16.26).
    /// </summary>
    [Fact]
    public void The_personal_email_label_renames_only_its_display_string()
    {
        var home = OdsTypeRegistries.EmailLabelOf("Home");

        Assert.Equal("Home", home.Key);
        Assert.Equal("Personal", home.Label);
        Assert.Equal(1, (int)EmailLabel.Home);
        Assert.Equal(EmailLabel.Home, new ContactMethodDraft { Label = "Home" }.ToNewEmail().Label);
    }

    // ── Opening a new method through the host (§16.20) ───────────────────────

    /// <summary>
    /// Driven through <c>ContactDetailPanel</c>'s own open-new action rather than by constructing the
    /// dialog directly — that is what makes this catch a missing <c>Default…Label</c> call site, which
    /// is the whole reason the draft no longer carries a default of its own.
    /// </summary>
    [Fact]
    public void Adding_a_phone_on_an_organization_offers_the_nine_organization_labels()
    {
        using var harness = new Harness();
        var cut = harness.RenderPanel(Contact(ContactType.Organization), addKind: "phone");

        Assert.Equal("Switchboard", SelectedLabel(cut));
        Assert.Equal(
            ["Switchboard", "Support", "Sales", "Billing", "Claims", "Emergency", "Direct", "Mobile", "Other"],
            OfferedLabels(cut));
    }

    [Fact]
    public void Adding_a_phone_on_a_person_offers_the_four_person_labels()
    {
        using var harness = new Harness();
        var cut = harness.RenderPanel(Contact(ContactType.Person), addKind: "phone");

        Assert.Equal("Home", SelectedLabel(cut));
        Assert.Equal(["Home", "Work", "Mobile", "Other"], OfferedLabels(cut));
    }

    [Fact]
    public void Adding_an_email_on_an_organization_opens_on_General_and_renders_Personal_nowhere()
    {
        using var harness = new Harness();
        var cut = harness.RenderPanel(Contact(ContactType.Organization), addKind: "email");

        Assert.Equal("General", SelectedLabel(cut));
        Assert.DoesNotContain("Personal", OfferedLabels(cut));
    }

    [Fact]
    public void Adding_an_email_on_a_person_renders_Home_as_Personal()
    {
        using var harness = new Harness();
        var cut = harness.RenderPanel(Contact(ContactType.Person), addKind: "email");

        Assert.Equal("Personal", SelectedLabel(cut));
        Assert.Equal(["Personal", "Work", "Other"], OfferedLabels(cut));
    }

    [Fact]
    public void Adding_an_address_on_an_organization_opens_on_Visiting()
    {
        using var harness = new Harness();
        var cut = harness.RenderPanel(Contact(ContactType.Organization), addKind: "address");

        Assert.Equal("Visiting", SelectedLabel(cut));
        Assert.Equal(["Visiting", "Registered", "Branch", "Billing", "Postal", "Other"], OfferedLabels(cut));
    }

    /// <summary>Submitting without touching the picker posts that type's default.</summary>
    [Fact]
    public void Submitting_without_touching_the_picker_posts_the_organization_default()
    {
        using var harness = new Harness();
        var cut = harness.RenderPanel(Contact(ContactType.Organization), addKind: "phone");

        Type(cut, "input", "+47 22 00 00 00");
        Submit(cut, "Create phone number");

        Assert.Equal(PhoneLabel.Switchboard, Assert.Single(harness.AddedPhones).Label);
    }

    // ── Editing an existing method (§16.21) ──────────────────────────────────

    /// <summary>
    /// The stored label is in the list, because the invariant holds for every row the application
    /// wrote — so an edit round-trips without the user having to re-pick. The draft is built the way
    /// the host's own Edit action builds it, with <c>ContactMethodDraft.FromPhone</c>.
    /// </summary>
    [Fact]
    public void Editing_an_organization_method_shows_the_stored_label_selected()
    {
        using var harness = new Harness();
        var stored = new ExistingPhoneNumber
        {
            Id = PhoneId, ContactId = ContactId, Label = PhoneLabel.Claims, Value = "+47 22 00 00 00",
        };

        var cut = harness.RenderDialog(
            ContactMethodDraft.FromPhone(stored), "phone", ContactType.Organization, isEdit: true);

        Assert.Equal("Claims", SelectedLabel(cut));
        Assert.Contains("Claims", OfferedLabels(cut));
    }

    // ── The label is text on the tile (§16.25) ───────────────────────────────

    /// <summary>
    /// Asserted against the text node, not a glyph: the tile's leading icon is the <i>kind</i> icon,
    /// not the label registry's, so meaning is never carried by an icon here.
    /// </summary>
    [Fact]
    public void The_label_is_rendered_as_text_on_the_tile()
    {
        using var harness = new Harness(
            phones: [new ExistingPhoneNumber { Id = PhoneId, ContactId = ContactId, Label = PhoneLabel.Emergency, Value = "+47 22 00 00 00" }],
            emails: [new ExistingEmailAddress { Id = Guid.NewGuid(), ContactId = ContactId, Label = EmailLabel.Home, Value = "a@b.example" }]);
        var cut = harness.RenderPanel(Contact(ContactType.Organization));

        var feet = cut.FindAll(".cp-tile-foot span").Select(s => s.TextContent).ToList();

        Assert.Contains("Emergency", feet);
        Assert.Contains("Personal", feet);   // EmailLabel.Home, displayed (§16.26)
    }

    /// <summary>
    /// A stored label that names no member renders as the honest <c>Other</c>, never as the registry's
    /// last entry — which, now that the organization band is appended after <c>Other</c>, would be
    /// <c>Direct</c>: plausible, specific and wrong (§16.19).
    /// </summary>
    [Fact]
    public void An_undefined_stored_ordinal_renders_as_Other_on_the_tile()
    {
        using var harness = new Harness(
            phones: [new ExistingPhoneNumber { Id = PhoneId, ContactId = ContactId, Label = (PhoneLabel)99, Value = "+47 22 00 00 00" }]);
        var cut = harness.RenderPanel(Contact(ContactType.Organization));

        var feet = cut.FindAll(".cp-tile-foot span").Select(s => s.TextContent).ToList();

        Assert.Contains("Other", feet);
        Assert.DoesNotContain("Direct", feet);
    }

    // ── The residual, client-side (§16.22) ───────────────────────────────────

    /// <summary>
    /// <b>This fails against a merely-non-empty validation rule.</b> Given a draft holding <c>"Home"</c>
    /// on an organization, <c>OdsTypeSelect</c> renders its placeholder — the value is not in the
    /// offered list — while <c>Draft.Label</c> still holds the old string. Under a non-empty rule that
    /// stale value passes, is parsed by <c>ToNewPhone()</c>, and comes back as a server 422 on a control
    /// that looked empty.
    ///
    /// <para>The draft is constructed directly here, unlike §16.20: this tests <c>Validate</c>, not the
    /// default's application site, and routing it through the host would test the wrong thing.</para>
    /// </summary>
    [Fact]
    public void A_stale_label_renders_the_placeholder_and_blocks_the_submit()
    {
        using var harness = new Harness();
        var draft = new ContactMethodDraft { Label = "Home", Value = "+47 22 00 00 00" };
        var cut = harness.RenderDialog(draft, "phone", ContactType.Organization);

        Assert.Equal("Select label…", SelectedLabel(cut));

        Submit(cut, "Create phone number");

        Assert.Empty(harness.AddedPhones);
        Assert.Equal("Label is required.", LabelError(cut));
    }

    /// <summary>
    /// Required-style wording, not "invalid": the stale value is invisible on the trigger, so a message
    /// naming <c>"Home"</c> would point at something nowhere on screen. (The server 422 is the opposite
    /// case — there the caller supplied the label, so naming it is right.)
    /// </summary>
    [Fact]
    public void The_blocked_submit_reports_the_label_as_required_not_as_invalid()
    {
        using var harness = new Harness();
        var cut = harness.RenderDialog(
            new ContactMethodDraft { Label = "Home", Value = "+47 22 00 00 00" }, "phone", ContactType.Organization);

        Submit(cut, "Create phone number");

        var error = LabelError(cut);
        Assert.DoesNotContain("Home", error);
        Assert.DoesNotContain("invalid", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Picking a valid label clears the block and the write goes through.</summary>
    [Fact]
    public void Picking_an_offered_label_unblocks_the_submit()
    {
        using var harness = new Harness();
        var cut = harness.RenderDialog(
            new ContactMethodDraft { Label = "Home", Value = "+47 22 00 00 00" }, "phone", ContactType.Organization);

        Submit(cut, "Create phone number");
        PickLabel(cut, "Support");
        Submit(cut, "Create phone number");

        Assert.Equal(PhoneLabel.Support, Assert.Single(harness.AddedPhones).Label);
    }

    /// <summary>A draft whose label was never set fails the same way — which is what makes the removal
    /// of <c>ContactMethodDraft</c>'s <c>"Home"</c> default fail loudly rather than silently.</summary>
    [Fact]
    public void An_unset_label_is_refused_client_side()
    {
        using var harness = new Harness();
        var cut = harness.RenderDialog(
            new ContactMethodDraft { Value = "+47 22 00 00 00" }, "phone", ContactType.Organization);

        Submit(cut, "Create phone number");

        Assert.Empty(harness.AddedPhones);
        Assert.Equal("Label is required.", LabelError(cut));
    }

    // ── The server-error channel (§16.23, §16.24) ────────────────────────────

    /// <summary>
    /// A 422 naming <c>label</c> lands on the Label control with <c>aria-invalid="true"</c> and an
    /// <c>aria-describedby</c> association, rather than only in a toast the user has to translate back
    /// into a field. (Focus movement is not bUnit-assertable and is verified in the manual pass.)
    /// </summary>
    [Fact]
    public void A_server_422_on_the_label_field_renders_on_the_control_and_is_announced()
    {
        const string Detail = "Label 'Home' is not valid for an Organization contact. Valid labels: Switchboard, …";
        using var harness = new Harness(addPhoneResult: Rejected(Detail, ("label", Detail)));
        var cut = harness.RenderDialog(
            new ContactMethodDraft { Label = "Switchboard", Value = "+47 22 00 00 00" }, "phone", ContactType.Organization);

        Submit(cut, "Create phone number");

        Assert.Equal(Detail, LabelError(cut));

        var trigger = cut.Find(".odc-select-trigger");
        Assert.Equal("true", trigger.GetAttribute("aria-invalid"));
        Assert.False(string.IsNullOrEmpty(trigger.GetAttribute("aria-describedby")));
    }

    /// <summary>
    /// A rejection the form cannot place must never vanish: it is toasted with the problem's own
    /// message, which is what makes the "Set as primary" failure explicable rather than generic.
    /// </summary>
    [Fact]
    public void A_server_rejection_the_form_cannot_place_is_toasted_with_the_problem_message()
    {
        const string Detail = "Label 'Home' is not valid for an Organization contact.";
        using var harness = new Harness(addPhoneResult: Rejected(Detail));
        var cut = harness.RenderDialog(
            new ContactMethodDraft { Label = "Switchboard", Value = "+47 22 00 00 00" }, "phone", ContactType.Organization);

        Submit(cut, "Create phone number");

        var toast = Assert.Single(harness.Snackbar.Toasts);
        Assert.Equal(Severity.Error, toast.Severity);
        Assert.Contains(Detail, toast.Message);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static ApiResult Rejected(string detail, params (string Field, string Message)[] fields) =>
        ApiResult.Failure(HttpStatusCode.UnprocessableEntity, new ApiProblem
        {
            Status = 422,
            Detail = detail,
            Errors = fields.ToDictionary(f => f.Field, f => new[] { f.Message }, StringComparer.OrdinalIgnoreCase),
        });

    private static ExistingContact Contact(ContactType type) => new()
    {
        ContactId = ContactId,
        Type = type,
        ExternalUid = $"urn:uuid:{ContactId}",
        ResolvedDisplayName = type == ContactType.Person ? "Ada Lovelace" : "Acme",
        NormalizedName = "ACME",
    };

    // ── DOM helpers ──────────────────────────────────────────────────────────

    private static string SelectedLabel(IRenderedComponent<IComponent> cut) => cut.Find(".odc-select-val").TextContent.Trim();

    private static string LabelError(IRenderedComponent<IComponent> cut) =>
        cut.FindAll(".odc-field-help.error").Select(e => e.TextContent.Trim()).FirstOrDefault() ?? "";

    private static List<string> OfferedLabels(IRenderedComponent<IComponent> cut)
    {
        cut.Find(".odc-select-trigger").Click();
        return [.. cut.FindAll(".odc-typesel-lab").Select(l => l.TextContent.Trim())];
    }

    // Driven through the picker's own ValueChanged rather than by clicking a popover row: MudMenu
    // closes asynchronously, so a click dispatched into a popover that is already tearing down lands
    // on nothing — which showed up as an intermittently empty write list, not as a failed click.
    private static void PickLabel(IRenderedComponent<IComponent> cut, string key)
    {
        var picker = cut.FindComponent<OdsTypeSelect>();
        cut.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(key)).GetAwaiter().GetResult();
    }

    private static void Type(IRenderedComponent<IComponent> cut, string selector, string value) =>
        cut.Find(selector).Input(value);

    private static void Submit(IRenderedComponent<IComponent> cut, string label) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(label, StringComparison.Ordinal)).Click();

    /// <summary>
    /// The panel (or the dialog alone) beside MudBlazor's providers, which portal the popovers, over a
    /// stubbed API client that records what each write was handed.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly BunitContext ctx = new();

        public List<NewPhoneNumber> AddedPhones { get; } = [];
        public RecordingSnackbar Snackbar { get; } = new();

        public Harness(
            IReadOnlyList<ExistingPhoneNumber>? phones = null,
            IReadOnlyList<ExistingEmailAddress>? emails = null,
            ApiResult? addPhoneResult = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.Services.AddMudServices();
            ctx.Services.AddSingleton<ISnackbar>(Snackbar);
            ctx.Services.AddSingleton(Mock.Of<IClipboardService>());

            var api = new Mock<IContactsApiClient>();
            api.Setup(a => a.AddPhoneAsync(It.IsAny<Guid>(), It.IsAny<NewPhoneNumber>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, NewPhoneNumber p, CancellationToken _) =>
                {
                    if (addPhoneResult is { } rejection)
                        return Task.FromResult(rejection);

                    AddedPhones.Add(p);
                    return Task.FromResult(ApiResult.Success(HttpStatusCode.Created));
                });
            api.Setup(a => a.AddEmailAsync(It.IsAny<Guid>(), It.IsAny<NewEmailAddress>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
            api.Setup(a => a.AddAddressAsync(It.IsAny<Guid>(), It.IsAny<NewAddress>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
            api.Setup(a => a.ListAddressesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiResult<List<ExistingAddress>>.Success([], HttpStatusCode.OK));
            api.Setup(a => a.ListEmailsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiResult<List<ExistingEmailAddress>>.Success([.. emails ?? []], HttpStatusCode.OK));
            api.Setup(a => a.ListPhonesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiResult<List<ExistingPhoneNumber>>.Success([.. phones ?? []], HttpStatusCode.OK));

            ctx.Services.AddSingleton(api.Object);

            Phones = phones ?? [];
            Emails = emails ?? [];
        }

        private IReadOnlyList<ExistingPhoneNumber> Phones { get; }

        private IReadOnlyList<ExistingEmailAddress> Emails { get; }

        public IRenderedComponent<PanelHost> RenderPanel(ExistingContact contact, string? addKind = null)
        {
            contact.PhoneNumbers = [.. Phones];
            contact.EmailAddresses = [.. Emails];

            return ctx.Render<PanelHost>(p => p
                .Add(h => h.Contact, contact)
                .Add(h => h.AddKind, addKind));
        }

        public IRenderedComponent<DialogHost> RenderDialog(
            ContactMethodDraft draft, string kind, ContactType type, bool isEdit = false) =>
            ctx.Render<DialogHost>(p => p
                .Add(h => h.Draft, draft)
                .Add(h => h.Kind, kind)
                .Add(h => h.ContactType, type)
                .Add(h => h.IsEdit, isEdit)
                .Add(h => h.ContactId, ContactId));

        public void Dispose() => ctx.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The panel beside MudBlazor's providers; <c>AddKind</c> drives its own open-new action.</summary>
    public sealed class PanelHost : ComponentBase
    {
        [Parameter] public ExistingContact Contact { get; set; } = default!;

        [Parameter] public string? AddKind { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<ContactDetailPanel>(2);
            builder.AddComponentParameter(3, nameof(ContactDetailPanel.Contact), Contact);
            builder.AddComponentParameter(4, nameof(ContactDetailPanel.CanEdit), true);
            if (AddKind is not null)
                builder.AddComponentParameter(5, nameof(ContactDetailPanel.AddRequest), ((string, Guid)?)(AddKind, Guid.NewGuid()));
            builder.CloseComponent();
        }
    }

    /// <summary>
    /// The dialog alone, wired to the same API client the panel uses — for the criteria that test
    /// <c>Validate</c> and the server-error channel rather than the default's application site.
    /// </summary>
    public sealed class DialogHost : ComponentBase
    {
        [Inject] private IContactsApiClient Contacts { get; set; } = default!;

        [Inject] private ISnackbar Snackbar { get; set; } = default!;

        [Parameter] public ContactMethodDraft Draft { get; set; } = new();

        [Parameter] public string Kind { get; set; } = "phone";

        [Parameter] public ContactType ContactType { get; set; } = ContactType.Organization;

        [Parameter] public bool IsEdit { get; set; }

        [Parameter] public Guid ContactId { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<ContactMethodDialog>(2);
            builder.AddComponentParameter(3, nameof(ContactMethodDialog.Open), true);
            builder.AddComponentParameter(4, nameof(ContactMethodDialog.Kind), Kind);
            builder.AddComponentParameter(5, nameof(ContactMethodDialog.Draft), Draft);
            builder.AddComponentParameter(6, nameof(ContactMethodDialog.ContactType), ContactType);
            builder.AddComponentParameter(7, nameof(ContactMethodDialog.IsEdit), IsEdit);
            builder.AddComponentParameter(
                8,
                nameof(ContactMethodDialog.OnCommit),
                new Func<ContactMethodDraft, Func<string, string, bool>, Task<bool>>(CommitAsync));
            builder.CloseComponent();
        }

        private async Task<bool> CommitAsync(ContactMethodDraft draft, Func<string, string, bool> assign) =>
            (await Contacts.AddPhoneAsync(ContactId, draft.ToNewPhone()))
                .ToastOrFields(Snackbar, "Unable to add phone", assign);
    }
}
