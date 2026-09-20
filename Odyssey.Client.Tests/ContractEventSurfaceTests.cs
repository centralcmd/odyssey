using System.Globalization;
using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The contract event log surface (issue #138), rendered rather than derived.
/// </summary>
/// <remarks>
/// <para>
/// The properties under test are the ones a reviewer cannot confirm by reading the markup, and every
/// one of them is a spec rule rather than a styling choice: that the rail shows the title and the
/// description but <b>not</b> the notes (§4.1, a presentation rule and never an access one); that the
/// section stays fully writable on an <b>archived</b> contract (§8.6), unlike Terms; that both ends of
/// the rail are anchored to real dates and to the right ones; and that a <c>PUT</c> is a full
/// replacement whose cleared fields go as <c>null</c> (§5.3).
/// </para>
/// </remarks>
public class ContractEventSurfaceTests
{
    private static readonly Guid ContractId = Guid.NewGuid();

    private static readonly DateTime ContractAdded = new(2025, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private static ExistingContract Lease(DateTime? archived = null) => new()
    {
        ContractId = ContractId,
        Name = "Maple St lease",
        Type = ContractType.Rental,
        StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedAtUtc = ContractAdded,
        Archived = archived,
    };

    private static ExistingContractEvent Event(
        string title = "Emailed landlord about the rent increase",
        ContractEventType type = ContractEventType.EmailSent,
        string? description = "Asked for the CPI basis in writing.",
        string? notes = "Chase on the 21st if no reply.",
        DateTime? occurredAt = null,
        string? createdBy = "Jane Doe") => new()
    {
        ContractEventId = Guid.NewGuid(),
        ContractId = ContractId,
        Type = type,
        Title = title,
        Description = description,
        Notes = notes,
        OccurredAt = occurredAt ?? new DateTime(2026, 6, 14, 9, 31, 0, DateTimeKind.Utc),
        CreatedBy = createdBy,
        CreatedAtUtc = new DateTime(2026, 6, 14, 9, 35, 0, DateTimeKind.Utc),
    };

    // ── The rail's content (§4.1) ────────────────────────────────────────────

    /// <summary>
    /// The one rule most likely to be "tidied" into a leak or into a gate. Notes must be absent from
    /// the RAIL — and that is all: the field is not private, and the dialog reads and writes it.
    /// </summary>
    [Fact]
    public void The_rail_shows_the_title_and_description_but_never_the_notes()
    {
        var cut = RenderSection(Lease(), [Event()]);

        var markup = cut.Markup;
        Assert.Contains("Emailed landlord about the rent increase", markup, StringComparison.Ordinal);
        Assert.Contains("Asked for the CPI basis in writing.", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Chase on the 21st", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Attribution is the resolved LABEL. The response carries no user id at all, so there is nothing
    /// for the surface to leak — this pins that it renders the label rather than reaching past it.
    /// </summary>
    [Fact]
    public void Each_entry_names_who_recorded_it()
    {
        var cut = RenderSection(Lease(), [Event()]);

        Assert.Contains("Recorded by", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Jane Doe", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unresolved author is drawn quieter, so "Unknown user" reads as a description of an absence
    /// rather than as somebody's name.
    /// </summary>
    [Fact]
    public void An_unresolved_author_is_marked_as_unknown_rather_than_rendered_as_a_name()
    {
        var cut = RenderSection(Lease(), [Event(createdBy: "Unknown user")]);

        Assert.NotEmpty(cut.FindAll(".cev-by-unknown"));
    }

    /// <summary>The kind is on the node's glyph, which therefore needs a name of its own (WCAG 1.1.1).</summary>
    [Fact]
    public void The_type_glyph_carries_an_accessible_name()
    {
        var cut = RenderSection(Lease(), [Event(type: ContractEventType.NoticeGiven)]);

        var node = cut.Find(".odc-er-node");
        Assert.Equal("img", node.GetAttribute("role"));
        Assert.Equal("Notice given", node.GetAttribute("aria-label"));
    }

    // ── The rail's two anchors ───────────────────────────────────────────────

    /// <summary>
    /// The foot is anchored to when the RECORD was added, not to the contract's start date. The two
    /// are different facts and the start date is the wrong one: this lease starts in 2026 and carries
    /// a 2025 event, so pinning to the start would put an event below its own origin.
    /// </summary>
    [Fact]
    public void Both_ends_of_the_rail_are_anchored_and_the_foot_names_when_the_record_was_added()
    {
        var cut = RenderSection(Lease(), [Event(occurredAt: new DateTime(2025, 8, 27, 14, 5, 0, DateTimeKind.Utc))]);

        var markup = cut.Markup;
        Assert.Contains("Today", markup, StringComparison.Ordinal);
        Assert.Contains("Contract added", markup, StringComparison.Ordinal);
        Assert.Contains("1 Jun 2025", markup, StringComparison.Ordinal);

        // One page holds the whole log, so the line stops hard at both ends rather than fading.
        var rail = cut.Find(".odc-er");
        Assert.Contains("capped-top", rail.ClassName, StringComparison.Ordinal);
        Assert.Contains("capped-end", rail.ClassName, StringComparison.Ordinal);
    }

    /// <summary>
    /// A year is a marker ON the line between rows, not a heading over a sub-list — which is the whole
    /// reason this is the event rail and not <c>OdsTimeline</c>, whose rail restarts per item.
    /// </summary>
    [Fact]
    public void A_year_boundary_becomes_a_marker_between_the_two_rows_it_separates()
    {
        var cut = RenderSection(Lease(),
        [
            Event(title: "Newer", occurredAt: new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc)),
            Event(title: "Older", occurredAt: new DateTime(2025, 9, 1, 10, 0, 0, DateTimeKind.Utc)),
        ]);

        var markers = cut.FindAll(".odc-er-marker").Select(m => m.TextContent.Trim()).ToList();

        // The 2025 row gets its own year marker, sitting on the line between the two entries...
        Assert.Contains("2025", markers);

        // ...and 2026 does NOT, because the Today cap one row above already carries it. A marker
        // repeating the year the cap just named would say nothing.
        Assert.DoesNotContain("2026", markers);
        Assert.Contains(markers, m => m.StartsWith("Today", StringComparison.Ordinal));
    }

    /// <summary>A contract with no log is a stated state, not a blank.</summary>
    [Fact]
    public void An_empty_log_says_what_an_event_is_for()
    {
        var cut = RenderSection(Lease(), []);

        Assert.Contains("No events yet", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".odc-er-item"));
    }

    // ── Writes (§8.6, AC 7) ──────────────────────────────────────────────────

    /// <summary>
    /// The section takes no Archived parameter at all, and this is why: the server accepts every event
    /// write on an archived contract, so withdrawing the affordance would refuse something the API
    /// allows. The contrast with <c>ContractTermsSection</c>, which DOES go read-only, is deliberate.
    /// </summary>
    [Fact]
    public void An_archived_contract_keeps_its_per_entry_actions()
    {
        var cut = RenderSection(Lease(archived: DateTime.UtcNow), [Event()], canUpdate: true);

        Assert.NotEmpty(cut.FindAll(".odc-rowactions"));
        // And it says nothing about restoring — there is no refusal to explain.
        Assert.DoesNotContain("Restore the contract", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>A caller without <c>contracts.update</c> reads the log and is offered nothing to do to it.</summary>
    [Fact]
    public void A_reader_without_update_sees_the_log_and_no_write_affordance()
    {
        var cut = RenderSection(Lease(), [Event()], canUpdate: false);

        Assert.Contains("Emailed landlord about the rent increase", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".odc-rowactions"));
    }

    // ── The dialog (§5.3, §8.1, §8.3) ────────────────────────────────────────

    /// <summary>
    /// §4.1's audience rule, which binds the WORDING and not only the projection: a user who mistakes
    /// the field for private storage will put things in it that the label promised to protect.
    /// </summary>
    [Fact]
    public void The_notes_field_states_its_real_audience_and_never_claims_privacy()
    {
        var cut = RenderDialog(Lease());

        var markup = cut.Markup;
        Assert.Contains("anyone who can see this contract can read them", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Private note", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Only you", markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AC 3 — a <c>PUT</c> is a FULL replacement, so a cleared description or note goes as
    /// <c>null</c> and clears. That is the opposite of <c>UpdateContract</c>, and the edit dialog says
    /// so before the save rather than after it.
    /// </summary>
    [Fact]
    public void Clearing_a_field_on_an_edit_sends_null_and_the_dialog_says_so_first()
    {
        var existing = Event();
        var (cut, client) = RenderDialogWithClient(Lease(), existing);

        Assert.Contains("Saving replaces the whole event", cut.Markup, StringComparison.Ordinal);

        cut.Find("#cev-description").Input(string.Empty);
        cut.Find("#cev-notes").Input(string.Empty);
        Save(cut);

        client.Verify(c => c.UpdateEventAsync(
            ContractId,
            existing.ContractEventId,
            It.Is<UpdateContractEvent>(u => u.Description == null && u.Notes == null && u.Title == existing.Title),
            It.IsAny<CancellationToken>()));
    }

    /// <summary>
    /// §8.1 — whitespace-only is rejected as empty, which is what it is. A
    /// <c>[StringLength(MinimumLength = 1)]</c> would accept a string of spaces, so this is the client
    /// mirroring the server's own service-layer check rather than its annotation.
    /// </summary>
    [Fact]
    public void A_whitespace_only_title_is_refused_without_a_round_trip()
    {
        var (cut, client) = RenderDialogWithClient(Lease());

        cut.Find("#cev-title").Input("   ");
        Create(cut);

        Assert.Contains("Give this event a title", cut.Markup, StringComparison.Ordinal);
        client.Verify(
            c => c.AddEventAsync(It.IsAny<Guid>(), It.IsAny<NewContractEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// §8.3 — the future bound. The client mirrors it so the refusal lands on the control instead of
    /// arriving as a toast after the round trip; the server still decides.
    /// </summary>
    [Fact]
    public void A_future_date_is_refused_on_the_field_rather_than_by_the_server()
    {
        var (cut, client) = RenderDialogWithClient(Lease());

        cut.Find("#cev-title").Input("Something that has not happened");
        // Through the picker's own editable input, so the path under test is the one a user takes.
        // Change, not Input: the picker's editable field commits on change, while the plain text
        // fields above bind on input.
        cut.Find(".mud-picker input")
            .Change(DateTime.UtcNow.Date.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Create(cut);

        Assert.Contains("can’t be in the future", cut.Markup, StringComparison.Ordinal);
        client.Verify(
            c => c.AddEventAsync(It.IsAny<Guid>(), It.IsAny<NewContractEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The new-entry default. An omitted type binds to <c>Other</c> server-side, and the dialog starts
    /// there rather than presenting a member the user did not choose.
    /// </summary>
    [Fact]
    public void A_new_entry_defaults_to_the_catch_all_type_and_to_now()
    {
        var (cut, client) = RenderDialogWithClient(Lease());

        cut.Find("#cev-title").Input("Something happened");
        Create(cut);

        client.Verify(c => c.AddEventAsync(
            ContractId,
            It.Is<NewContractEvent>(n =>
                n.Type == ContractEventType.Other
                && n.Title == "Something happened"
                && n.OccurredAt <= DateTime.UtcNow.AddSeconds(60)),
            It.IsAny<CancellationToken>()));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void Create(IRenderedComponent<DialogHost> cut) => ClickFooter(cut, "Create event");

    private static void Save(IRenderedComponent<DialogHost> cut) => ClickFooter(cut, "Save changes");

    /// <summary>Submits through the modal's own footer button — the path a user takes.</summary>
    private static void ClickFooter(IRenderedComponent<DialogHost> cut, string label) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(label, StringComparison.Ordinal)).Click();

    private static IRenderedComponent<ContractEventsSection> RenderSection(
        ExistingContract contract, IReadOnlyList<ExistingContractEvent> events, bool canUpdate = true)
    {
        var ctx = NewContext();
        var client = new Mock<IContractsApiClient>();
        client
            .Setup(c => c.ListEventsAsync(
                contract.ContractId, null, null, null, null, null, null,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingContractEvent>>.Success(
                new PagedResult<ExistingContractEvent>
                {
                    Items = [.. events],
                    Offset = 0,
                    Limit = ContractEventsSection.PageSize,
                    TotalCount = events.Count,
                },
                HttpStatusCode.OK));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<ContractEventsSection>(p => p
            .Add(s => s.Contract, contract)
            .Add(s => s.CanUpdate, canUpdate));

        // The section loads on OnInitializedAsync, which early-returns outside the browser (matching
        // the sibling term section, which has no interactive-check seam either), so the load is driven
        // through the section's own public reload — the same entry point the host uses.
        cut.InvokeAsync(() => cut.Instance.ReloadAsync()).GetAwaiter().GetResult();

        return cut;
    }

    private static IRenderedComponent<DialogHost> RenderDialog(ExistingContract contract) =>
        RenderDialogWithClient(contract).Cut;

    private static (IRenderedComponent<DialogHost> Cut, Mock<IContractsApiClient> Client) RenderDialogWithClient(
        ExistingContract contract, ExistingContractEvent? editing = null)
    {
        var ctx = NewContext();
        var client = new Mock<IContractsApiClient>();
        client
            .Setup(c => c.AddEventAsync(It.IsAny<Guid>(), It.IsAny<NewContractEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
        client
            .Setup(c => c.UpdateEventAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UpdateContractEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.Contract, contract)
            .Add(h => h.Event, editing));
        return (cut, client);
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // MudBlazor's own registrations supply ISnackbar and IDialogService; substituting a mock for
        // either breaks the providers the modal renders through.
        ctx.Services.AddMudServices();
        return ctx;
    }

    /// <summary>The dialog beside MudBlazor's providers, which portal the modal it renders into.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingContract Contract { get; set; } = default!;

        [Parameter] public ExistingContractEvent? Event { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<AddContractEventDialog>(2);
            builder.AddComponentParameter(3, nameof(AddContractEventDialog.Contract), Contract);
            builder.AddComponentParameter(4, nameof(AddContractEventDialog.Event), Event);
            builder.AddComponentParameter(5, nameof(AddContractEventDialog.Open), true);
            builder.CloseComponent();
        }
    }
}
