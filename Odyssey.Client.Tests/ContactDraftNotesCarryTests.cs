using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>ContactDraft.Notes</c> still round-trips after <c>RelationshipType</c> was removed from the
/// draft beside it (issue #52 AC #8, Non-Goal 3).
/// </summary>
/// <remarks>
/// <para>
/// The removal took the only genuinely unbound carrier out of <c>ContactDraft</c> and, with it, the
/// comment that had described <c>Notes</c> as a second one. It is not: <c>ContactFields.razor</c>
/// binds it to a rendered <c>OdsNoteField</c> outside the Person branch, so it is a visible, editable
/// field for both contact types. These tests pin that, at both places the draft carries it.
/// </para>
/// <para>
/// Deliberately NOT in <c>Odyssey.Api.Tests</c>: <c>ContactDraft.ToNew</c> is what BUILDS the request
/// body, so an API-tier test that hand-constructed a <see cref="NewContact"/> with notes set would
/// still pass after someone deleted the draft property — the API would behave correctly while the
/// client sent <c>notes: null</c>. That tier structurally cannot see the defect.
/// </para>
/// </remarks>
public class ContactDraftNotesCarryTests : IAsyncLifetime
{
    private const string Notes = "Pays by invoice, 30 days.";

    // MudBlazor registers an async-only service, so the context has to be torn down asynchronously —
    // a synchronous Dispose throws before any assertion is reached.
    private readonly BunitContext ctx = new();

    public ContactDraftNotesCarryTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IContactsApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    /// <summary>
    /// Carry site one — the request builder. <c>ToNew</c> takes the archival flag, so the bare
    /// <c>ToNew()</c> the obvious spelling reaches for does not compile.
    /// </summary>
    [Fact]
    public void ToNew_PreservesNotesLoadedFromAnExistingContact()
    {
        var draft = ContactDraft.From(new ExistingContact
        {
            ContactId = Guid.NewGuid(),
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "ADA LOVELACE",
            ResolvedDisplayName = "Ada Lovelace",
            Type = ContactType.Person,
            Notes = Notes,
            PersonDetails = new PersonDetailsDto { FirstName = "Ada", LastName = "Lovelace" },
        });

        var body = draft.ToNew(archived: false);

        Assert.Equal(Notes, body.Notes);
        Assert.Equal("Ada", body.PersonDetails!.FirstName);
        Assert.Equal("Lovelace", body.PersonDetails.LastName);
    }

    /// <summary>
    /// Carry site two, and the fragile one — the type-switch initializer in
    /// <c>ContactDialog.OnTypeChanged</c>, which dropped from four fields to three. It survives a
    /// full Person → Organization → Person round trip.
    /// </summary>
    /// <remarks>
    /// Rendered in CREATE mode on purpose: the type selector only renders there. Edit mode locks the
    /// type behind a read-only chip, which leaves <c>OnTypeChanged</c> unreachable and would make this
    /// assert nothing.
    /// </remarks>
    [Fact]
    public void The_type_switch_preserves_the_notes_across_Person_Organization_Person()
    {
        var cut = ctx.Render<DialogHost>();

        TypeNotes(cut, Notes);
        Assert.Equal(Notes, NotesValue(cut));

        SwitchType(cut, nameof(ContactType.Organization));
        Assert.Equal(Notes, NotesValue(cut));
        Assert.Contains("Legal name", cut.Markup, StringComparison.Ordinal);

        SwitchType(cut, nameof(ContactType.Person));
        Assert.Equal(Notes, NotesValue(cut));
        Assert.Contains("First name", cut.Markup, StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>The rendered, bound Notes control — the real <c>OdsNoteField</c> textarea.</summary>
    private static AngleSharp.Dom.IElement NotesTextarea(IRenderedComponent<DialogHost> cut) =>
        cut.FindAll("textarea").Single();

    private static string? NotesValue(IRenderedComponent<DialogHost> cut) =>
        NotesTextarea(cut).GetAttribute("value");

    private static void TypeNotes(IRenderedComponent<DialogHost> cut, string value) =>
        NotesTextarea(cut).Input(value);

    /// <summary>
    /// Drives the type change through the selector's own <c>ValueChanged</c>, which is what the
    /// <c>OdsContactTypeSelect</c> row raises. The rows themselves live in a portaled MudMenu popover;
    /// going through the callback keeps the assertion on <c>OnTypeChanged</c> rather than on
    /// MudBlazor's popover plumbing, while still requiring the selector to be rendered at all.
    /// </summary>
    private static void SwitchType(IRenderedComponent<DialogHost> cut, string key)
    {
        var select = cut.FindComponent<Odyssey.Client.Components.OdsContactTypeSelect>();
        cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(key)).GetAwaiter().GetResult();
    }

    /// <summary>The dialog beside MudBlazor's providers, which portal its popovers.</summary>
    public sealed class DialogHost : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<ContactDialog>(2);
            builder.AddComponentParameter(3, nameof(ContactDialog.Open), true);
            builder.AddComponentParameter(4, nameof(ContactDialog.Contact), null);
            builder.CloseComponent();
        }
    }
}
