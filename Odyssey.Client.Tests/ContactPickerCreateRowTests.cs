using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The design system's rule that every contact and tag picker can CREATE, and that its create rows
/// name what they make (Odyssey Design System · <c>ContactSelect</c> / <c>TagMultiSelect</c>).
/// </summary>
/// <remarks>
/// <para>
/// The point of the typed rows is that nothing guesses a type after the fact: a contact is a person
/// <b>or</b> a company, and the row the user picks is what the payload carries. A regression that
/// dropped the kind would not fail visibly — it would quietly file every inline-created contact as an
/// organization, which is exactly what this replaced.
/// </para>
/// <para>
/// The rendering half is asserted on <c>OdsTagMultiSelect</c>, whose popover is reachable in bUnit;
/// <c>OdsCombobox</c>'s rows are MudAutocomplete's, so its half is covered through the shared
/// registries and the payload builder instead.
/// </para>
/// </remarks>
public class ContactPickerCreateRowTests : IAsyncLifetime
{
    // One context per test, torn down with it: several of these render MudBlazor's portaled popover,
    // and a leaked renderer makes the NEXT test's dispatched events land on a stale tree — which
    // showed up as an input event that silently did nothing. MudBlazor registers an async-only
    // service, so the teardown has to be the async one.
    private readonly BunitContext ctx = new();

    public ContactPickerCreateRowTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    private static readonly Guid ActiveId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ArchivedId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ── The create-row vocabularies ──────────────────────────────────────────

    /// <summary>
    /// Organization leads. A contact linked from a transaction, a file, a subscription or a statement
    /// merchant is a company far more often than a person, and the leading row is what the one-click
    /// "create from the extracted name" affordance uses.
    /// </summary>
    [Fact]
    public void The_contact_create_rows_are_organization_then_person()
    {
        var keys = OdsTypeRegistries.ContactCreateKinds.Select(k => k.Key).ToList();

        Assert.Equal([nameof(ContactType.Organization), nameof(ContactType.Person)], keys);
        Assert.All(OdsTypeRegistries.ContactCreateKinds, k => Assert.False(string.IsNullOrWhiteSpace(k.Icon)));
    }

    /// <summary>
    /// A tag field only offers the vocabulary it searches — a journal-tag field cannot mint a
    /// transaction tag — so each registry is exactly one row, and it says which store it writes to.
    /// </summary>
    [Theory]
    [InlineData("transaction", "Transaction tag")]
    [InlineData("journal", "Journal tag")]
    [InlineData("task", "Task tag")]
    [InlineData("photo", "Photo tag")]
    public void A_tag_field_offers_exactly_its_own_vocabulary(string key, string label)
    {
        var kinds = key switch
        {
            "transaction" => OdsTypeRegistries.TagCreateKinds.Transaction,
            "journal" => OdsTypeRegistries.TagCreateKinds.Journal,
            "task" => OdsTypeRegistries.TagCreateKinds.Task,
            _ => OdsTypeRegistries.TagCreateKinds.Photo,
        };

        var kind = Assert.Single(kinds);
        Assert.Equal(key, kind.Key);
        Assert.Equal(label, kind.Label);
    }

    // ── The staged payload ───────────────────────────────────────────────────

    /// <summary>An organization takes the typed text as its legal name, and nothing else.</summary>
    [Fact]
    public void An_organization_is_created_from_its_legal_name()
    {
        var payload = ContactQuickCreate.BuildPayload("Kiwi Minipris", ContactType.Organization);

        Assert.Equal(ContactType.Organization, payload.Type);
        Assert.Equal("Kiwi Minipris", payload.OrganizationDetails!.LegalName);
        Assert.Null(payload.PersonDetails);
        Assert.False(payload.Archived);
    }

    /// <summary>
    /// A person needs both a first and a last name, and a picker only ever collects one string — so
    /// the text splits on its LAST whitespace, which is what keeps a middle name with the first.
    /// </summary>
    [Fact]
    public void A_person_splits_on_the_last_whitespace()
    {
        var payload = ContactQuickCreate.BuildPayload("Ada Byron Lovelace", ContactType.Person);

        Assert.Equal(ContactType.Person, payload.Type);
        Assert.Equal("Ada Byron", payload.PersonDetails!.FirstName);
        Assert.Equal("Lovelace", payload.PersonDetails.LastName);
        Assert.Null(payload.OrganizationDetails);

        // A two-part name already reads correctly, so no override is imposed on it.
        Assert.Null(payload.DisplayName);
    }

    /// <summary>
    /// A single-token person still creates — refusing would be the dead end the typed rows exist to
    /// close — and carries a display-name override so the record READS as exactly what was typed
    /// rather than as the duplicated token the required fields force.
    /// </summary>
    [Fact]
    public void A_single_token_person_carries_a_display_name_override()
    {
        var payload = ContactQuickCreate.BuildPayload("Nopa", ContactType.Person);

        Assert.Equal("Nopa", payload.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(payload.PersonDetails!.FirstName));
        Assert.False(string.IsNullOrWhiteSpace(payload.PersonDetails.LastName));
    }

    // ── OdsContactSelect ─────────────────────────────────────────────────────

    /// <summary>
    /// Archived contacts are not selectable, but a value already pointing at one stays resolvable, so
    /// the trigger keeps showing its name instead of collapsing to a blank or a GUID.
    /// </summary>
    [Fact]
    public void An_archived_contact_is_not_offered_but_a_linked_one_still_resolves()
    {
        var cut = RenderContactSelect(value: ArchivedId.ToString());

        var picker = cut.FindComponent<OdsCombobox>();
        var options = picker.Instance.Options;

        Assert.Contains(options, o => o.Value == ActiveId.ToString());
        Assert.Contains(options, o => o.Value == ArchivedId.ToString());

        // …and only because it is the current value: the same list without it offers the active one.
        var unlinked = RenderContactSelect(value: null).FindComponent<OdsCombobox>().Instance.Options;
        Assert.DoesNotContain(unlinked, o => o.Value == ArchivedId.ToString());
    }

    /// <summary>Each row carries its own ContactType glyph, read off the registry — the list must not
    /// read as merchants only.</summary>
    [Fact]
    public void Every_option_carries_its_own_type_glyph()
    {
        var options = RenderContactSelect(value: null).FindComponent<OdsCombobox>().Instance.Options;

        var person = Assert.Single(options, o => o.Value == ActiveId.ToString());
        Assert.Equal(OdsTypeRegistries.ContactTypeOf(nameof(ContactType.Person)).Icon, person.Icon);
        Assert.Equal(OdsTypeRegistries.ContactTypeOf(nameof(ContactType.Person)).Color, person.IconColor);
    }

    /// <summary>
    /// AllowCreate forwards the typed rows to the combobox. Without it the field is pick-only — the
    /// gate a caller applies from its <c>contacts.create</c> claim, so a user who would meet a 403
    /// never sees the control.
    /// </summary>
    [Fact]
    public void The_typed_create_rows_are_forwarded_only_when_create_is_allowed()
    {
        var withCreate = RenderContactSelect(value: null, allowCreate: true).FindComponent<OdsCombobox>();
        Assert.Equal(OdsTypeRegistries.ContactCreateKinds, withCreate.Instance.CreateKinds);
        Assert.NotNull(withCreate.Instance.OnCreate);

        var pickOnly = RenderContactSelect(value: null).FindComponent<OdsCombobox>();
        Assert.Null(pickOnly.Instance.CreateKinds);
        Assert.Null(pickOnly.Instance.OnCreate);
    }

    /// <summary>
    /// An empty address book stays OPERABLE when a contact can be created from it — disabling the
    /// field would put the create row out of reach, which is the dead end it exists to close.
    /// </summary>
    [Fact]
    public void An_empty_list_stays_operable_when_it_can_create()
    {
        var pickOnly = RenderContactSelect(value: null, contacts: []).FindComponent<OdsCombobox>();
        Assert.True(pickOnly.Instance.Disabled);

        var creatable = RenderContactSelect(value: null, contacts: [], allowCreate: true).FindComponent<OdsCombobox>();
        Assert.False(creatable.Instance.Disabled);
    }

    // ── OdsTagMultiSelect's rendered create rows ─────────────────────────────

    /// <summary>
    /// One row per kind, each naming what it makes. The rule + gap belong to the FIRST row only, so a
    /// stack of typed rows reads as one group under the options rather than as separate sections.
    /// </summary>
    [Fact]
    public void A_tag_picker_renders_one_create_row_per_kind()
    {
        var cut = RenderTagPicker(
            [new OdsCreateKind("Organization", "Organization") { Icon = "corporate_fare" },
             new OdsCreateKind("Person", "Person") { Icon = "person" }]);

        cut.Find("#tms").Click();
        Search(cut, "Nopa");

        var rows = CreateRows(cut, 2);
        Assert.Contains("first", rows[0].ClassName!.Split(' '));
        Assert.DoesNotContain("first", rows[1].ClassName!.Split(' '));

        Assert.Contains("Organization", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Person", rows[1].TextContent, StringComparison.Ordinal);
        Assert.All(rows, r => Assert.Contains("Nopa", r.TextContent, StringComparison.Ordinal));
    }

    /// <summary>The picked row's key reaches OnCreate — the whole reason the rows are typed.</summary>
    [Fact]
    public void Picking_a_create_row_passes_its_kind_to_the_caller()
    {
        string? picked = null;
        var cut = RenderTagPicker(
            [new OdsCreateKind("Organization", "Organization") { Icon = "corporate_fare" },
             new OdsCreateKind("Person", "Person") { Icon = "person" }],
            onCreate: (text, kind) => { picked = kind; return new OdsOption("new", text); });

        cut.Find("#tms").Click();
        Search(cut, "Nopa");
        CreateRows(cut, 2)[1].Click();

        Assert.Equal("Person", picked);
    }

    /// <summary>With no kinds named, the picker keeps its single unqualified row.</summary>
    [Fact]
    public void A_picker_with_no_kinds_keeps_one_unqualified_create_row()
    {
        var cut = RenderTagPicker(kinds: null);

        cut.Find("#tms").Click();
        Search(cut, "Nopa");

        var row = CreateRows(cut, 1)[0];
        Assert.DoesNotContain("odc-tagms-create-kind", row.InnerHtml, StringComparison.Ordinal);
    }

    // Type into the popover's search field, then wait for the rows it produces.
    //
    // The input lives in MudBlazor's portaled popover, which is rendered through a section outlet a
    // beat after the trigger is clicked — so the element has to be WAITED for rather than found, and
    // the event dispatched explicitly (Input() does not reach it). Finding it too early yields a node
    // from the outlet's previous render, and the event then lands on a tree nothing is showing: the
    // search box stays empty and no create row ever appears.
    private static void Search<T>(IRenderedComponent<T> cut, string text) where T : IComponent =>
        cut.WaitForElement(".odc-tagms-search input")
            .TriggerEvent("oninput", new ChangeEventArgs { Value = text });

    private static IReadOnlyList<AngleSharp.Dom.IElement> CreateRows<T>(
        IRenderedComponent<T> cut, int expected) where T : IComponent
    {
        cut.WaitForAssertion(() => Assert.Equal(expected, cut.FindAll(".odc-tagms-create").Count));
        return [.. cut.FindAll(".odc-tagms-create")];
    }

    // ── Harnesses ────────────────────────────────────────────────────────────

    private IRenderedComponent<ContactSelectHost> RenderContactSelect(
        string? value, IReadOnlyList<ExistingContact>? contacts = null, bool allowCreate = false) =>
        ctx.Render<ContactSelectHost>(p => p
            .Add(h => h.Value, value)
            .Add(h => h.Contacts, contacts ?? Contacts())
            .Add(h => h.AllowCreate, allowCreate));

    private IRenderedComponent<TagPickerHost> RenderTagPicker(
        IReadOnlyList<OdsCreateKind>? kinds,
        Func<string, string?, OdsOption?>? onCreate = null) =>
        ctx.Render<TagPickerHost>(p => p
            .Add(h => h.Kinds, kinds)
            .Add(h => h.OnCreate, onCreate ?? ((text, _) => new OdsOption("new", text))));

    private static IReadOnlyList<ExistingContact> Contacts() =>
    [
        new()
        {
            ContactId = ActiveId, ResolvedDisplayName = "Ada Lovelace",
            NormalizedName = "ADA LOVELACE", ExternalUid = "urn:uuid:1", Type = ContactType.Person,
        },
        new()
        {
            ContactId = ArchivedId, ResolvedDisplayName = "Old Bank",
            NormalizedName = "OLD BANK", ExternalUid = "urn:uuid:2", Type = ContactType.Organization,
            Archived = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        },
    ];

    public sealed class ContactSelectHost : ComponentBase
    {
        [Parameter] public string? Value { get; set; }
        [Parameter] public IReadOnlyList<ExistingContact> Contacts { get; set; } = [];
        [Parameter] public bool AllowCreate { get; set; }

        // No MudPopoverProvider: these tests read the picker's resolved option list rather than
        // opening it, and a second provider in one context collides on MudBlazor's overlay section.
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<OdsContactSelect>(1);
            builder.AddComponentParameter(2, nameof(OdsContactSelect.Value), Value);
            builder.AddComponentParameter(3, nameof(OdsContactSelect.Contacts), Contacts);
            builder.AddComponentParameter(4, nameof(OdsContactSelect.AllowCreate), AllowCreate);
            builder.AddComponentParameter(5, nameof(OdsContactSelect.OnCreate),
                (Func<string, string, OdsOption?>)((text, _) => new OdsOption("new", text)));
            builder.CloseComponent();
        }
    }

    public sealed class TagPickerHost : ComponentBase
    {
        [Parameter] public IReadOnlyList<OdsCreateKind>? Kinds { get; set; }
        [Parameter] public Func<string, string?, OdsOption?>? OnCreate { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsTagMultiSelect>(1);
            builder.AddComponentParameter(2, nameof(OdsTagMultiSelect.Id), "tms");
            builder.AddComponentParameter(3, nameof(OdsTagMultiSelect.Label), "Tags");
            builder.AddComponentParameter(4, nameof(OdsTagMultiSelect.Options),
                (IReadOnlyList<OdsOption>)[new OdsOption("t1", "Groceries")]);
            builder.AddComponentParameter(5, nameof(OdsTagMultiSelect.OnCreate), OnCreate);
            builder.AddComponentParameter(6, nameof(OdsTagMultiSelect.CreateKinds), Kinds);
            builder.CloseComponent();
        }
    }
}
