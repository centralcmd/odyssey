using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
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
/// section stays fully writable on an <b>archived</b> contract (§8.6), as every section does; that both ends of
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
        string? createdBy = "Jane Doe",
        ContractEventSource source = ContractEventSource.User) => new()
    {
        ContractEventId = Guid.NewGuid(),
        ContractId = ContractId,
        Type = type,
        Source = source,
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

    // ── Accessibility (from the #143 accessibility review) ───────────────────

    /// <summary>
    /// WCAG 1.3.1 — the rail is a LIST, and says so. Without the roles, AT announces a run of
    /// anonymous divs with no count and no per-entry boundary.
    /// </summary>
    /// <remarks>
    /// Markers carry <c>listitem</c> too, and that is the load-bearing half: <c>role="list"</c> admits
    /// only <c>listitem</c> children, so giving a year marker <c>role="separator"</c> instead would
    /// make the list malformed — which some AT answers by dropping the list semantics altogether,
    /// losing the very thing the roles were added for.
    /// </remarks>
    [Fact]
    public void The_rail_carries_list_semantics_and_every_direct_child_is_a_listitem()
    {
        var cut = RenderSection(Lease(),
        [
            Event(title: "Newer", occurredAt: new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc)),
            Event(title: "Older", occurredAt: new DateTime(2025, 9, 1, 10, 0, 0, DateTimeKind.Utc)),
        ]);

        var rail = cut.Find(".odc-er");
        Assert.Equal("list", rail.GetAttribute("role"));

        var children = rail.Children;
        Assert.NotEmpty(children);
        Assert.All(children, child => Assert.Equal("listitem", child.GetAttribute("role")));

        // Not vacuous: the children under test are both kinds, entries AND markers.
        Assert.NotEmpty(cut.FindAll(".odc-er-item"));
        Assert.NotEmpty(cut.FindAll(".odc-er-marker"));
    }

    /// <summary>
    /// WCAG 2.1.1 — the provenance line reveals on hover or focus-within, and a read-only row contains
    /// nothing focusable, so focus-within can never fire there. A caller holding <c>contracts.read</c>
    /// without <c>contracts.update</c> — the seeded <b>User</b> role — sees every row that way, so a
    /// keyboard-only User would otherwise never be able to see who recorded an event.
    /// </summary>
    [Fact]
    public void A_row_with_no_actions_shows_its_provenance_without_needing_a_reveal()
    {
        var readOnly = RenderSection(Lease(), [Event()], canUpdate: false);

        var row = readOnly.Find(".odc-er-item");
        Assert.Contains("no-actions", row.ClassName, StringComparison.Ordinal);
        // Nothing focusable in the row is exactly the premise — assert it rather than assume it.
        Assert.Empty(readOnly.FindAll(".odc-er-item button"));

        // ...and a writable row keeps the quiet, hover-revealed treatment, because there the reveal
        // has a target: tabbing to the actions cluster fires :focus-within.
        var writable = RenderSection(Lease(), [Event()], canUpdate: true);
        Assert.DoesNotContain("no-actions", writable.Find(".odc-er-item").ClassName, StringComparison.Ordinal);
        Assert.NotEmpty(writable.FindAll(".odc-er-item button"));
    }

    /// <summary>
    /// The collapsed card's counts strip is the record body's table of contents, and the design
    /// system lists four entries in it: Parties · Terms · Documents · Events, in the order the
    /// sections run. A section present in the body and absent from the strip reads as a section that
    /// is empty, which is exactly wrong for a log someone has been keeping.
    /// </summary>
    /// <remarks>
    /// A source-lint for the reason <see cref="ContractsCardRowActionTests"/> records: the rows this
    /// strip renders on arrive through <c>OdsInfiniteList</c>, which materialises nothing in bUnit.
    /// The count is asserted end to end over HTTP in <c>ContractEventsApiTests.List_CarriesTheEventCount</c>
    /// — what is checked here is that the card actually spends it.
    /// </remarks>
    [Fact]
    public void The_collapsed_card_counts_the_event_log()
    {
        var markup = File.ReadAllText(
            Path.Combine(ClientSource.Root, "Pages", "Finance", "ContractsCard.razor"));

        var strip = Regex.Match(markup, @"var counts = new\[\]\s*\{[\s\S]*?\};");
        Assert.True(strip.Success, "the counts strip still exists");

        // It reads the SERVER's count, never the loaded detail's collection: the strip renders on the
        // collapsed row, where no detail has been fetched.
        Assert.Matches(
            new Regex(@"new OdsRecordCount\(""history"", c\.EventCount\.ToString\([^)]*\), ""Events""\)"),
            strip.Value);

        // …and it comes last, matching the order the body's sections run in.
        var order = Regex.Matches(strip.Value, @"""(Parties|Terms|Documents|Events)""\)")
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(["Parties", "Terms", "Documents", "Events"], order);
    }

    /// <summary>
    /// WCAG 1.4.3 — the marker label must not be dimmed a second time. <c>text-secondary</c> is
    /// already the muted token (~7:1 against the surface); the design system's own
    /// <c>opacity: 0.75</c> on top of it drops an 11px label to ~3.95:1 light / ~4.45:1 dark, under
    /// the 4.5:1 AA minimum. A source-lint because a computed style is not observable in bUnit — the
    /// stylesheet is global, not scoped, so it is never attached to the rendered component.
    /// </summary>
    [Fact]
    public void The_marker_label_is_not_dimmed_below_the_contrast_minimum()
    {
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));

        var rule = Regex.Match(css, @"\.odc-er-marker > span \{[^}]*\}", RegexOptions.Singleline);
        Assert.True(rule.Success, "the marker label rule still exists");
        Assert.DoesNotContain("opacity", rule.Value, StringComparison.Ordinal);
        Assert.Contains("--mud-palette-text-secondary", rule.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// WCAG 1.3.1 — the "When" pair's helper and error text must be associated with a control.
    /// <c>OdsFieldShell</c> derives those element ids from <c>HtmlFor</c>, which a two-control group
    /// has nothing to point at, so shell-level <c>Help</c>/<c>Error</c> there would render text with
    /// no id and nothing referencing it. They ride on the date picker instead, where MudBlazor wires
    /// <c>aria-describedby</c> itself.
    /// </summary>
    [Fact]
    public void The_when_fields_helper_and_error_are_associated_with_a_control()
    {
        var cut = RenderDialog(Lease());

        // The group is named, and the shell renders no orphaned (id-less) help node of its own.
        var group = cut.Find(".cev-when");
        Assert.Equal("cev-when-label", group.GetAttribute("aria-labelledby"));
        Assert.NotEmpty(cut.FindAll("#cev-when-label"));

        // The helper text sits inside the picker's own field, which carries the describedby wiring.
        var helper = cut.Find(".cev-when .mud-input-helper-text");
        Assert.Contains("A log, not a plan", helper.TextContent, StringComparison.Ordinal);
        Assert.All(
            cut.FindAll(".odc-field-help"),
            node => Assert.False(
                string.IsNullOrEmpty(node.Id),
                "a help node with no id is referenced by nothing"));
    }

    // ── Writes (§8.6, AC 7) ──────────────────────────────────────────────────

    /// <summary>
    /// The section takes no Archived parameter at all, and this is why: the server accepts every event
    /// write on an archived contract, so withdrawing the affordance would refuse something the API
    /// allows. <c>ContractTermsSection</c> used to be the contrast — it went read-only — and no longer
    /// is; every section of a contract record now behaves like this one.
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


    // ── Provenance: the recorded/hand-written distinction (issue #154 / #155) ──

    /// <summary>
    /// A recorded row says so, in TEXT, in the attribution line — and a hand-written one says nothing.
    /// </summary>
    /// <remarks>
    /// <b>This is the design system's product decision, and it overrides issue #155's §4 and its AC
    /// 1/2/17</b>, which asked for a persistent <c>OdsChip</c> in a new rail-item <c>tag</c> slot. One
    /// sentence carrying who and how was preferred to a pill per row competing with the record head's
    /// status vocabulary; the accepted cost is that the note inherits <c>.odc-er-meta</c>'s
    /// hover-reveal. Asserted as markup rather than by eye because "the marker is text, not a hue" is
    /// the WCAG 1.4.1 claim and a colour-only marker would still render.
    /// </remarks>
    [Fact]
    public void A_recorded_row_names_its_origin_in_the_attribution_line_and_a_hand_written_one_does_not()
    {
        var recorded = RenderSection(Lease(), [Event(source: ContractEventSource.System)]);

        Assert.Contains("(automatically generated)", recorded.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(recorded.FindAll(".cev-auto-note"));
        // In the author's own line, so who and how are read together.
        Assert.Contains("Recorded by", recorded.Find(".odc-er-meta").TextContent, StringComparison.Ordinal);
        Assert.Contains(
            "automatically generated", recorded.Find(".odc-er-meta").TextContent, StringComparison.Ordinal);

        var handWritten = RenderSection(Lease(), [Event()]);
        Assert.DoesNotContain("automatically generated", handWritten.Markup, StringComparison.Ordinal);
        Assert.Empty(handWritten.FindAll(".cev-auto-note"));
    }

    /// <summary>
    /// The marker is not carried by colour or glyph: <c>.cev-auto-note</c> declares a font style and
    /// nothing else, so the row reads the same in greyscale (WCAG 1.4.1).
    /// </summary>
    [Fact]
    public void The_origin_note_is_styled_without_relying_on_colour()
    {
        var rule = ComponentRule(".cev-auto-note");

        Assert.Contains("font-style: italic", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("color:", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("background", rule, StringComparison.Ordinal);
    }

    /// <summary>
    /// Non-Goal 1 — a recorded row carries the same two actions as any other. It is a log line, not an
    /// audit record, and the backend's §7.7 makes the same claim from the other side.
    /// </summary>
    [Fact]
    public void A_recorded_row_carries_the_same_row_actions_as_a_hand_written_one()
    {
        var recorded = RenderSection(Lease(), [Event(source: ContractEventSource.System)]);
        var handWritten = RenderSection(Lease(), [Event()]);

        Assert.Equal(
            handWritten.FindAll(".odc-er-item .odc-rowactions").Count,
            recorded.FindAll(".odc-er-item .odc-rowactions").Count);
        Assert.NotEmpty(recorded.FindAll(".odc-er-item .odc-rowactions"));
    }

    /// <summary>
    /// AC 9 — a caller without <c>contracts.update</c> sees every row, recorded ones included, with no
    /// row actions. <b>Presentation only</b>: the server's <c>403</c> on every write path is the
    /// enforcement, never the hidden control.
    /// </summary>
    [Fact]
    public void A_read_only_caller_sees_recorded_rows_with_no_actions()
    {
        var cut = RenderSection(
            Lease(), [Event(source: ContractEventSource.System)], canUpdate: false);

        Assert.Contains("(automatically generated)", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".odc-er-item .odc-rowactions"));
    }

    /// <summary>
    /// Non-Goal 3 / AC 8 — the section surfaces NO filter control, and calls the endpoint with no
    /// <c>source</c> argument. The endpoint serves the filter and the typed client exposes it; the
    /// per-row marker already answers "was this written or recorded?" where the question is asked.
    /// </summary>
    [Fact]
    public async Task The_section_renders_no_filter_control_and_asks_for_no_source()
    {
        var ctx = NewContext();
        var client = new Mock<IContractsApiClient>();
        client
            .Setup(c => c.ListEventsAsync(
                ContractId, It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ContractEventSource?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingContractEvent>>.Success(
                new PagedResult<ExistingContractEvent>
                {
                    Items = [Event()],
                    Offset = 0,
                    Limit = ContractEventsSection.PageSize,
                    TotalCount = 1,
                },
                HttpStatusCode.OK));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<ContractEventsSection>(p => p
            .Add(s => s.Contract, Lease())
            .Add(s => s.CanUpdate, true));
        await cut.InvokeAsync(() => cut.Instance.ReloadAsync());

        client.Verify(
            c => c.ListEventsAsync(
                ContractId, It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // No select, no segmented control, no search box — the section keeps zero filter controls.
        Assert.Empty(cut.FindAll(".odc-select"));
        Assert.Empty(cut.FindAll("input[type=search]"));
        Assert.Empty(cut.FindAll(".odc-segmented"));
    }

    /// <summary>
    /// AC 13 — the section heading is a real focus destination, which it is not by default:
    /// <c>OdsSectionDivider</c> renders a plain span until it is given a <c>HeadingId</c>. The
    /// <c>@@key</c> already on each rail item is a separate and also-necessary protection and does not
    /// substitute for this one.
    /// </summary>
    [Fact]
    public void The_section_heading_is_focusable_and_identified()
    {
        var label = RenderSection(Lease(), [Event()]).Find(".odc-sectiondivider-l");

        Assert.Equal("con-events-heading", label.GetAttribute("id"));
        Assert.Equal("-1", label.GetAttribute("tabindex"));
        Assert.Equal("heading", label.GetAttribute("role"));
    }

    /// <summary>
    /// The focus target has to survive the transition into the empty state, because deleting the last
    /// row is exactly when focus is moved to it.
    /// </summary>
    [Fact]
    public void The_heading_stays_focusable_in_the_empty_state()
    {
        var cut = RenderSection(Lease(), []);

        var label = cut.Find(".odc-sectiondivider-l");
        Assert.Equal("con-events-heading", label.GetAttribute("id"));
        Assert.Equal("-1", label.GetAttribute("tabindex"));
    }

    /// <summary>
    /// AC 14 / Non-Goal 8 — the section mounts NO live region of its own. Two regions on one page race
    /// each other, so the sentence goes up to the page's single announcer through <c>OnAnnounce</c>.
    /// </summary>
    [Fact]
    public void The_section_mounts_no_live_region_of_its_own()
    {
        var cut = RenderSection(Lease(), [Event()]);

        // The loading wrapper is the one role="status" the section owns, and it is gone once loaded.
        Assert.Empty(cut.FindAll("[aria-live=assertive]"));
        Assert.Empty(cut.FindAll(".odc-liveannouncer"));
    }

    /// <summary>
    /// The revised empty-state copy names BOTH halves of the log, in that order — what Odyssey
    /// records, then what the user can add. The old copy named only the second, which is now half the
    /// story.
    /// </summary>
    [Fact]
    public void The_empty_state_names_both_halves_of_the_log()
    {
        var text = RenderSection(Lease(), []).Find(".odc-empty").TextContent;

        Assert.Contains("Odyssey records what it does to this agreement", text, StringComparison.Ordinal);
        Assert.Contains("you can add anything else", text, StringComparison.Ordinal);
    }

    // ── The dialog's copy (AC 10, AC 11) ─────────────────────────────────────

    /// <summary>
    /// AC 10 — the "recording an event does not change the contract" sentence rides the TYPE field's
    /// help, so it lands in that field's <c>aria-describedby</c>. A caption a screen reader never
    /// reaches would leave the misconception uncorrected for the users most likely to form it — and a
    /// type list now offering <em>Paused</em>, <em>Archived</em> and <em>Signed</em> is exactly what
    /// invites it.
    /// </summary>
    [Fact]
    public void The_dialog_says_recording_an_event_does_not_change_the_contract_in_the_type_fields_description()
    {
        var cut = RenderDialog(Lease());

        AssertDescribedBy(cut, "cev-type", "Recording an event does not change the contract.");
    }

    /// <summary>
    /// AC 11 — on a recorded row the "recorded automatically" wording is in the TITLE field's help,
    /// which is what makes it programmatic. <c>OdsModal</c>'s subtitle is a visual slot with no
    /// <c>aria-describedby</c> wiring and Title is the field that takes focus, so a screen-reader user
    /// would otherwise land on Title and never hear it. Verified by inspecting the attribute, not by
    /// the subtitle's presence.
    /// </summary>
    [Fact]
    public void Editing_a_recorded_row_puts_the_origin_in_the_title_fields_description()
    {
        var cut = RenderDialogWithClient(
            Lease(), Event(type: ContractEventType.Paused, source: ContractEventSource.System)).Cut;

        AssertDescribedBy(cut, "cev-title", "Recorded automatically when the contract was paused.");
        AssertDescribedBy(cut, "cev-title", "it does not change the contract");

        // The subtitle repeats it for sighted users; it must never be the only place it appears.
        Assert.Contains("Recorded automatically.", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>Editing a hand-written row keeps the ordinary title help — nothing claims an origin it does not have.</summary>
    [Fact]
    public void Editing_a_hand_written_row_keeps_the_ordinary_title_help()
    {
        var cut = RenderDialogWithClient(Lease(), Event()).Cut;

        AssertDescribedBy(cut, "cev-title", "A short label.");
        Assert.DoesNotContain("Recorded automatically", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The full-replacement notice gains one sentence on a RECORDED row: nothing regenerates a
    /// description cleared there.
    /// </summary>
    /// <remarks>
    /// The PUT replaces the whole event either way, but the consequence differs by origin. A
    /// hand-written description was typed and can be typed again; a generated one was written once, at
    /// the moment of the change it records, and no later write re-derives it. Clearing it is therefore
    /// permanent in a way the ordinary notice does not convey.
    /// </remarks>
    [Fact]
    public void Editing_a_recorded_row_warns_that_a_cleared_description_never_comes_back()
    {
        var cut = RenderDialogWithClient(
            Lease(), Event(type: ContractEventType.Paused, source: ContractEventSource.System)).Cut;

        Assert.Contains(
            "Nothing regenerates a description you clear on a recorded event.",
            cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// …and a hand-written row does not carry it, because there the claim would be false: its
    /// description was typed, so retyping it restores exactly what was lost.
    /// </summary>
    [Fact]
    public void Editing_a_hand_written_row_carries_no_regeneration_warning()
    {
        var cut = RenderDialogWithClient(Lease(), Event()).Cut;

        // The ordinary full-replacement notice is still there — only the origin-specific sentence is not.
        Assert.Contains("Saving replaces the whole event", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing regenerates", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Creating an event shows neither notice: there is nothing to replace, and a new row is always
    /// hand-written — this dialog is the hand.
    /// </summary>
    [Fact]
    public void Creating_an_event_carries_no_replacement_notice()
    {
        var cut = RenderDialogWithClient(Lease(), editing: null).Cut;

        Assert.DoesNotContain("Saving replaces the whole event", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing regenerates", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>source</c> appears nowhere in the dialog — not as a control, not as a disabled one. A
    /// disabled input would imply it is ordinarily settable, when in fact it is absent from both write
    /// DTOs and cannot be set at all (#154 §7.4).
    /// </summary>
    [Fact]
    public void The_dialog_offers_no_source_control_at_all()
    {
        var cut = RenderDialogWithClient(
            Lease(), Event(source: ContractEventSource.System)).Cut;

        Assert.Empty(cut.FindAll("#cev-source"));
        Assert.Empty(cut.FindAll("[name=source]"));
        Assert.Empty(cut.FindAll("input[disabled]"));
    }

    /// <summary>
    /// The nine automation members are pickable by hand — "the contract was paused last March" is a
    /// legitimate entry — and <c>Other</c> is offered LAST despite keeping ordinal 8 (AC 3, AC 4).
    /// </summary>
    [Fact]
    public void Every_automation_type_is_offered_and_Other_is_offered_last()
    {
        var options = OdsTypeRegistries.ContractEventTypes.Select(t => t.Key).ToList();

        foreach (var member in new[]
                 {
                     "Paused", "Unpaused", "Ready", "Unready", "Unsigned",
                     "Archived", "Unarchived", "PartyAdded", "PartyRemoved",
                 })
        {
            Assert.Contains(member, options);
        }

        Assert.Equal("Other", options[^1]);
        Assert.Equal(8, (int)ContractEventType.Other);
    }

    /// <summary>
    /// AC 20 — every rail string renders through escaped binding. A system row's title and description
    /// are server-authored but travel through the same component as user-authored text, so a
    /// <c>MarkupString</c> introduced "just for the generated half" would open the other one too.
    /// </summary>
    [Fact]
    public void No_markup_string_appears_anywhere_in_the_section_or_its_dialog()
    {
        foreach (var file in new[]
                 {
                     "Pages/Finance/ContractEventsSection.razor",
                     "Pages/Finance/ContractEventsSection.razor.cs",
                     "Pages/Finance/AddContractEventDialog.razor",
                     "Pages/Finance/AddContractEventDialog.razor.cs",
                 })
        {
            var source = File.ReadAllText(Path.Combine(ClientSource.Root, file));
            Assert.DoesNotContain("MarkupString", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// An event carrying an ordinal this build does not know renders as <c>Other</c> — the trailing
    /// registry entry, which is why <c>Other</c> must stay last even though it no longer ends the enum.
    /// </summary>
    [Fact]
    public void An_unknown_ordinal_renders_as_Other()
    {
        var cut = RenderSection(Lease(), [Event(type: (ContractEventType)99)]);

        Assert.Equal("Other", cut.Find(".odc-er-node").GetAttribute("aria-label"));
    }

    private static void AssertDescribedBy(IRenderedComponent<DialogHost> cut, string fieldId, string expected)
    {
        var control = cut.Find($"#{fieldId}");
        var describedBy = control.GetAttribute("aria-describedby");
        Assert.False(string.IsNullOrWhiteSpace(describedBy),
            $"#{fieldId} carries no aria-describedby, so its help text reaches nobody.");

        var described = string.Join(
            " ",
            describedBy!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => cut.FindAll($"#{id}").FirstOrDefault()?.TextContent ?? string.Empty));

        Assert.Contains(expected, described, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 13 and AC 14, behaviourally — a delete really does move focus to the section heading and
    /// really does raise the resulting count. Both are driven through the row's own Delete button and
    /// MudBlazor's confirm, so what is exercised is <c>DeleteAsync</c> rather than the two helpers it
    /// calls.
    /// </summary>
    /// <remarks>
    /// The structural assertions elsewhere in this file — that the heading carries an <c>id</c> and
    /// <c>tabindex="-1"</c>, and that no second live region is mounted — say the destination and the
    /// channel exist. Neither says either is used, and under <c>JSRuntimeMode.Loose</c> an omitted JS
    /// call would pass silently. This is the test that fails if the wiring is dropped.
    /// </remarks>
    [Fact]
    public async Task Deleting_a_row_moves_focus_to_the_heading_and_announces_what_is_left()
    {
        var ctx = NewContext();
        var announced = new List<string>();
        var rows = new List<ExistingContractEvent> { Event(title: "Kept"), Event(title: "Doomed") };

        var client = new Mock<IContractsApiClient>();
        client
            .Setup(c => c.ListEventsAsync(
                ContractId, null, null, null, null, null, null,
                It.IsAny<int>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ApiResult<PagedResult<ExistingContractEvent>>.Success(
                new PagedResult<ExistingContractEvent>
                {
                    Items = [.. rows],
                    Offset = 0,
                    Limit = ContractEventsSection.PageSize,
                    TotalCount = rows.Count,
                },
                HttpStatusCode.OK));
        client
            .Setup(c => c.DeleteEventAsync(ContractId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent))
            .Callback(() => rows.RemoveAll(e => e.Title == "Doomed"));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<SectionHost>(p => p
            .Add(h => h.Contract, Lease())
            .Add(h => h.OnAnnounce, EventCallback.Factory.Create<string>(this, announced.Add)));
        var section = cut.FindComponent<ContractEventsSection>();
        await cut.InvokeAsync(() => section.Instance.ReloadAsync());

        cut.Find("button[aria-label='Delete Doomed']").Click();

        // MudBlazor's message box renders into the provider in the same tree; confirming it lets
        // DeleteAsync run on past its await.
        var confirm = cut.FindAll("button").First(b => b.TextContent.Trim() == "Delete");
        confirm.Click();
        cut.WaitForAssertion(() => Assert.Single(announced));

        Assert.Equal("Event deleted. 1 entry in the log.", announced[0]);

        var focusCall = Assert.Single(
            ctx.JSInterop.Invocations, i => i.Identifier == "focusHeading");
        Assert.Equal("con-events-heading", Assert.Single(focusCall.Arguments));
    }

    /// <summary>
    /// AC 14 — the sentence a delete raises to the page's announcer, in both numbers. An emptied log
    /// says "0 entries" rather than falling silent, which is the case a naive guard drops.
    /// </summary>
    /// <remarks>
    /// The sentence is asserted directly because the delete that raises it goes through MudBlazor's
    /// message box, which needs the dialog provider this section's own test context does not mount.
    /// <c>DeleteAsync</c> has exactly one call site for it, beside the focus move, and both are
    /// reached only after the write succeeds.
    /// </remarks>
    [Theory]
    [InlineData(0, "Event deleted. 0 entries in the log.")]
    [InlineData(1, "Event deleted. 1 entry in the log.")]
    [InlineData(4, "Event deleted. 4 entries in the log.")]
    public void The_delete_announcement_agrees_in_number_with_what_is_left(int left, string expected)
    {
        Assert.Equal(expected, ContractEventsSection.DeletionAnnouncement(left));
    }

    /// <summary>
    /// The section raises its announcement rather than mounting a region for it, so the callback has
    /// to exist as a parameter and the host has to bind it. Both halves, because either alone leaves
    /// the sentence going nowhere.
    /// </summary>
    [Fact]
    public void The_announcement_callback_is_a_parameter_and_the_host_binds_it()
    {
        var parameter = typeof(ContractEventsSection).GetProperty(nameof(ContractEventsSection.OnAnnounce));
        Assert.NotNull(parameter);
        Assert.Equal(typeof(EventCallback<string>), parameter!.PropertyType);

        var host = File.ReadAllText(
            Path.Combine(ClientSource.Root, "Pages", "Finance", "ContractDetailView.razor"));
        Assert.Matches(@"<ContractEventsSection[^>]*OnAnnounce=""OnAnnounce""", host.Replace("\n", " "));
    }

    /// <summary>
    /// The imported focus module is released on teardown. A section is torn down every time a contract
    /// row collapses, so without this each collapse and re-expand leaks a module registration — the
    /// house pattern every other component importing a module follows.
    /// </summary>
    [Fact]
    public void The_section_releases_its_focus_module_on_teardown()
    {
        Assert.True(
            typeof(IAsyncDisposable).IsAssignableFrom(typeof(ContractEventsSection)),
            "ContractEventsSection imports a JS module, so it must release it on teardown.");
    }

    /// <summary>
    /// One declaration block out of <c>odyssey-components.css</c>, comments stripped so a rationale
    /// note cannot satisfy an assertion. Same shape <c>ContractOrphanRowContrastTests</c> uses.
    /// </summary>
    private static string ComponentRule(string selector)
    {
        var css = Regex.Replace(
            File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css")),
            @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        if (start < 0)
            start = css.IndexOf(selector, StringComparison.Ordinal);

        Assert.True(start >= 0, $"No rule for '{selector}' in odyssey-components.css.");

        var end = css.IndexOf('}', start);
        Assert.True(end > start, $"Unterminated rule for '{selector}'.");
        return css[start..end];
    }

    private static IRenderedComponent<ContractEventsSection> RenderSection(
        ExistingContract contract, IReadOnlyList<ExistingContractEvent> events, bool canUpdate = true)
    {
        var ctx = NewContext();
        var client = new Mock<IContractsApiClient>();
        client
            .Setup(c => c.ListEventsAsync(
                contract.ContractId, null, null, null, null, null, null,
                It.IsAny<int>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
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

    /// <summary>
    /// The section beside MudBlazor's dialog provider. The confirm is portaled into the provider, so
    /// both have to sit in ONE render tree for a test to reach it — the same arrangement
    /// <see cref="DialogHost"/> uses for the modal.
    /// </summary>
    public sealed class SectionHost : ComponentBase
    {
        [Parameter] public ExistingContract Contract { get; set; } = default!;

        [Parameter] public EventCallback<string> OnAnnounce { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<ContractEventsSection>(2);
            builder.AddComponentParameter(3, nameof(ContractEventsSection.Contract), Contract);
            builder.AddComponentParameter(4, nameof(ContractEventsSection.CanUpdate), true);
            builder.AddComponentParameter(5, nameof(ContractEventsSection.OnAnnounce), OnAnnounce);
            builder.CloseComponent();
        }
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
