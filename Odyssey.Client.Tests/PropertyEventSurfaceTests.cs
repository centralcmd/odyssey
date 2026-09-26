using System.Net;
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
/// The property event log surface (issue #209; design system · PropertyEvents.jsx and
/// AddPropertyEventModal.jsx), rendered rather than derived: the rail, the matrix-driven picker, the
/// system-only rules, and the full-replacement edit.
/// </summary>
public class PropertyEventSurfaceTests
{
    private static readonly Guid PropertyId = Guid.NewGuid();

    private static readonly DateTime PropertyAdded = new(2025, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private static ExistingProperty Car() => new()
    {
        PropertyId = PropertyId,
        Name = "Family car",
        Description = "Daily driver",
        Type = PropertyType.Vehicle,
        CurrencyCode = "NOK",
        CreatedAt = PropertyAdded,
        UpdatedAt = PropertyAdded,
        VehicleDetails = new VehicleDetailsDto { Kind = VehicleKind.Car },
    };

    private static ExistingProperty House() => Car() with
    {
        Name = "Maple St house",
        Type = PropertyType.RealEstate,
        VehicleDetails = null,
        RealEstateDetails = new RealEstateDetailsDto { Kind = RealEstateKind.House },
    };

    private static ExistingPropertyEvent Event(
        string title = "Winter tyres on",
        PropertyEventType type = PropertyEventType.TyreChange,
        string? description = "Studded, front left worn to 5 mm.",
        string? notes = "Next change mid-April.",
        DateTime? occurredAt = null,
        string? createdBy = "Kari Nordmann",
        ContractEventSource source = ContractEventSource.User) => new()
    {
        PropertyEventId = Guid.NewGuid(),
        PropertyId = PropertyId,
        Type = type,
        Source = source,
        Title = title,
        Description = description,
        Notes = notes,
        OccurredAt = occurredAt ?? new DateTime(2026, 6, 14, 9, 31, 0, DateTimeKind.Utc),
        CreatedBy = createdBy,
        CreatedAtUtc = new DateTime(2026, 6, 14, 9, 35, 0, DateTimeKind.Utc),
    };

    // ── The rail ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_rail_shows_the_title_and_description_but_never_the_notes()
    {
        var cut = RenderSection(Car(), [Event()]);

        Assert.Contains("Winter tyres on", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Studded, front left worn to 5 mm.", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Next change mid-April", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_type_glyph_is_named_from_the_property_registry()
    {
        var cut = RenderSection(Car(), [Event()]);

        var node = cut.Find(".odc-er-node");
        Assert.Equal("img", node.GetAttribute("role"));
        Assert.Equal("Tyre change", node.GetAttribute("aria-label"));
    }

    [Fact]
    public void A_recorded_row_says_so_in_its_attribution_line_and_a_hand_written_one_does_not()
    {
        var cut = RenderSection(Car(), [
            Event(title: "Property acquired", type: PropertyEventType.Acquired, source: ContractEventSource.System),
            Event(title: "Hand-written"),
        ]);

        Assert.Single(cut.FindAll(".cev-auto-note"));
        Assert.Contains("(automatically generated)", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unresolved_author_is_marked_as_unknown()
    {
        var cut = RenderSection(Car(), [Event(createdBy: "Unknown user")]);

        Assert.NotEmpty(cut.FindAll(".cev-by-unknown"));
    }

    [Fact]
    public void Both_ends_are_anchored_and_the_foot_names_when_the_property_was_added()
    {
        var cut = RenderSection(Car(), [Event(occurredAt: new DateTime(2025, 8, 27, 14, 5, 0, DateTimeKind.Utc))]);

        Assert.Contains("Today", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Property added", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("1 Jun 2025", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The design system's placement rule: an Acquired event from before the record existed sits
    /// BELOW the "Property added" marker, and the marker never lands under an older year's tick.
    /// </summary>
    [Fact]
    public void The_origin_marker_sits_above_an_older_years_tick()
    {
        var cut = RenderSection(Car(), [
            Event(title: "Recent", occurredAt: new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
            Event(title: "Bought", type: PropertyEventType.Acquired, source: ContractEventSource.System,
                occurredAt: new DateTime(2023, 4, 20, 0, 0, 0, DateTimeKind.Utc)),
        ]);

        var text = cut.Find(".cev-rail").TextContent;
        var origin = text.IndexOf("Property added", StringComparison.Ordinal);
        var tick = text.IndexOf("2023", StringComparison.Ordinal);
        var bought = text.IndexOf("Bought", StringComparison.Ordinal);
        Assert.True(origin < tick && tick < bought, text);
    }

    [Fact]
    public void An_empty_vehicle_log_names_what_the_server_records_and_vehicle_examples()
    {
        var cut = RenderSection(Car(), []);

        Assert.Contains("acquired, disposed of, archived or", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("a tyre change", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("0 entries", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_real_estate_log_offers_real_estate_examples()
    {
        var cut = RenderSection(House(), []);

        Assert.Contains("a renovation, a tenancy", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reader_without_update_sees_the_log_and_no_row_actions()
    {
        var cut = RenderSection(Car(), [Event()], canUpdate: false);

        Assert.Contains("Winter tyres on", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("button[aria-label^='Edit ']"));
        Assert.Empty(cut.FindAll("button[aria-label^='Delete ']"));
    }

    [Fact]
    public void The_heading_is_identified_per_property_and_focusable()
    {
        var cut = RenderSection(Car(), []);

        var heading = cut.Find($"#prop-events-heading-{PropertyId}");
        Assert.Equal("-1", heading.GetAttribute("tabindex"));
    }

    [Theory]
    [InlineData(0, "Event deleted. 0 entries in the log.")]
    [InlineData(1, "Event deleted. 1 entry in the log.")]
    [InlineData(3, "Event deleted. 3 entries in the log.")]
    public void The_delete_announcement_agrees_in_number(int left, string expected)
    {
        Assert.Equal(expected, PropertyEventsSection.DeletionAnnouncement(left));
    }

    [Fact]
    public void An_unknown_ordinal_renders_as_Other()
    {
        var cut = RenderSection(Car(), [Event(type: (PropertyEventType)199)]);

        Assert.Equal("Other", cut.Find(".odc-er-node").GetAttribute("aria-label"));
    }

    // ── The picker is the matrix ─────────────────────────────────────────────

    [Fact]
    public void A_vehicle_is_offered_its_specific_types_first_then_the_universal_ones_with_Other_last()
    {
        var groups = OdsTypeRegistries.PropertyEventTypesFor(PropertyType.Vehicle);

        Assert.Equal(["For vehicles", "Any property"], groups.Select(g => g.Label));
        Assert.Equal(["Serviced", "TyreChange", "PeriodicInspection", "Registered"], groups[0].Items.Select(i => i.Key));
        Assert.Equal("Other", groups[1].Items[^1].Key);
        Assert.Equal(9, groups[1].Items.Count);
    }

    [Fact]
    public void No_system_only_type_is_offered_on_create_and_no_other_types_members_either()
    {
        var offered = OdsTypeRegistries.PropertyEventTypesFor(PropertyType.RealEstate)
            .SelectMany(g => g.Items).Select(i => i.Key).ToList();

        Assert.Equal(13, offered.Count);
        foreach (var hidden in new[] { "Archived", "Unarchived", "AcquisitionDateCleared", "DisposalReversed", "TyreChange", "Serviced" })
        {
            Assert.DoesNotContain(hidden, offered);
        }
    }

    [Fact]
    public void Editing_a_system_row_offers_its_own_type_alone_under_recorded_automatically()
    {
        var groups = OdsTypeRegistries.PropertyEventTypesFor(PropertyType.Vehicle, PropertyEventType.Archived);

        var system = Assert.Single(groups, g => g.Label == "Recorded automatically");
        Assert.Equal("Archived", Assert.Single(system.Items).Key);
    }

    [Fact]
    public void The_registry_mirrors_every_enum_member_once_and_keeps_Other_last()
    {
        var keys = OdsTypeRegistries.PropertyEventTypes.Select(t => t.Key).ToList();

        Assert.Equal(Enum.GetNames<PropertyEventType>().Order(), keys.Order());
        Assert.Equal("Other", keys[^1]);
    }

    // ── The dialog ───────────────────────────────────────────────────────────

    [Fact]
    public void A_new_entry_defaults_to_Other_and_now_and_sends_no_owner_or_source()
    {
        var (cut, client) = RenderDialogWithClient(Car());

        cut.Find("#pev-title").Input("Something happened");
        Click(cut, "Create event");

        client.Verify(c => c.CreateEventAsync(
            PropertyId,
            It.Is<NewPropertyEvent>(n =>
                n.Type == PropertyEventType.Other
                && n.Title == "Something happened"
                && n.OccurredAt <= DateTime.UtcNow.AddSeconds(60)),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void The_type_help_says_recording_an_event_does_not_change_the_property()
    {
        var (cut, _) = RenderDialogWithClient(Car());

        Assert.Contains("Recording an event does not change the property", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whitespace_only_title_is_refused_without_a_round_trip()
    {
        var (cut, client) = RenderDialogWithClient(Car());

        cut.Find("#pev-title").Input("   ");
        Click(cut, "Create event");

        Assert.Contains("Give this event a title", cut.Markup, StringComparison.Ordinal);
        client.Verify(
            c => c.CreateEventAsync(It.IsAny<Guid>(), It.IsAny<NewPropertyEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Editing_a_recorded_row_keeps_its_system_type_and_explains_where_it_came_from()
    {
        var existing = Event(title: "Property archived", type: PropertyEventType.Archived,
            description: "Archived on 1 June 2026.", notes: null, source: ContractEventSource.System);
        var (cut, client) = RenderDialogWithClient(Car(), existing);

        Assert.Contains("Recorded automatically when the property was archived", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Nothing regenerates a description you clear", cut.Markup, StringComparison.Ordinal);

        cut.Find("#pev-title").Input("Archived after the sale");
        Click(cut, "Save changes");

        client.Verify(c => c.UpdateEventAsync(
            PropertyId,
            existing.PropertyEventId,
            It.Is<UpdatePropertyEvent>(u => u.Type == PropertyEventType.Archived && u.Title == "Archived after the sale"),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void Clearing_a_field_on_an_edit_sends_null_and_the_dialog_says_so_first()
    {
        var existing = Event();
        var (cut, client) = RenderDialogWithClient(Car(), existing);

        Assert.Contains("Saving replaces the whole event", cut.Markup, StringComparison.Ordinal);

        cut.Find("#pev-description").Input(string.Empty);
        cut.Find("#pev-notes").Input(string.Empty);
        Click(cut, "Save changes");

        client.Verify(c => c.UpdateEventAsync(
            PropertyId,
            existing.PropertyEventId,
            It.Is<UpdatePropertyEvent>(u => u.Description == null && u.Notes == null && u.Type == PropertyEventType.TyreChange),
            It.IsAny<CancellationToken>()));
    }

    /// <summary>A server 422 keyed <c>type</c> lands on the Type field, not in a toast.</summary>
    [Fact]
    public void A_server_refusal_keyed_to_type_is_shown_on_the_type_field()
    {
        var (cut, client) = RenderDialogWithClient(Car());
        client
            .Setup(c => c.CreateEventAsync(It.IsAny<Guid>(), It.IsAny<NewPropertyEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingPropertyEvent>.Failure(HttpStatusCode.UnprocessableEntity, new ApiProblem
            {
                Status = 422,
                Errors = new Dictionary<string, string[]> { ["type"] = ["Event type 'Other' is not legal here."] },
            }));

        cut.Find("#pev-title").Input("Something");
        Click(cut, "Create event");

        Assert.Contains("is not legal here", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void No_markup_string_appears_in_the_section_or_its_dialog()
    {
        foreach (var file in new[]
                 {
                     "Pages/Finance/PropertyEventsSection.razor",
                     "Pages/Finance/PropertyEventsSection.razor.cs",
                     "Pages/Finance/AddPropertyEventDialog.razor",
                     "Pages/Finance/AddPropertyEventDialog.razor.cs",
                 })
        {
            Assert.DoesNotContain("MarkupString", File.ReadAllText(Path.Combine(ClientSource.Root, file)), StringComparison.Ordinal);
        }
    }

    /// <summary>The delete confirmation states that the whole log goes with the property.</summary>
    [Theory]
    [InlineData(0, "No events")]
    [InlineData(1, "1 event — the whole log, including recorded ones")]
    [InlineData(4, "4 events — the whole log, including recorded ones")]
    public void The_delete_dialog_says_the_event_log_goes_too(int count, string expected)
    {
        var ctx = NewContext();
        var cut = ctx.Render<DeleteHost>(p => p.Add(h => h.Property, Car() with { EventCount = count }));

        Assert.Contains(expected, cut.Markup, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void Click(IRenderedComponent<DialogHost> cut, string label) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(label, StringComparison.Ordinal)).Click();

    private static IRenderedComponent<PropertyEventsSection> RenderSection(
        ExistingProperty property, IReadOnlyList<ExistingPropertyEvent> events, bool canUpdate = true)
    {
        var ctx = NewContext();
        var client = new Mock<IPropertiesApiClient>();
        client
            .Setup(c => c.ListEventsAsync(
                property.PropertyId, null, null, null, null, null, null,
                It.IsAny<int>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingPropertyEvent>>.Success(
                new PagedResult<ExistingPropertyEvent>
                {
                    Items = [.. events],
                    Offset = 0,
                    Limit = PropertyEventsSection.PageSize,
                    TotalCount = events.Count,
                },
                HttpStatusCode.OK));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<PropertyEventsSection>(p => p
            .Add(s => s.Property, property)
            .Add(s => s.CanUpdate, canUpdate));

        // OnParametersSetAsync loads only in the browser, so the load is driven through the section's
        // own public reload.
        cut.InvokeAsync(() => cut.Instance.ReloadAsync()).GetAwaiter().GetResult();
        return cut;
    }

    private static (IRenderedComponent<DialogHost> Cut, Mock<IPropertiesApiClient> Client) RenderDialogWithClient(
        ExistingProperty property, ExistingPropertyEvent? editing = null)
    {
        var ctx = NewContext();
        var client = new Mock<IPropertiesApiClient>();
        client
            .Setup(c => c.CreateEventAsync(It.IsAny<Guid>(), It.IsAny<NewPropertyEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingPropertyEvent>.Success(Event(), HttpStatusCode.Created));
        client
            .Setup(c => c.UpdateEventAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UpdatePropertyEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingPropertyEvent>.Success(Event(), HttpStatusCode.OK));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.Property, property)
            .Add(h => h.Event, editing));
        return (cut, client);
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    /// <summary>The dialog beside MudBlazor's providers, which portal the modal it renders into.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingProperty Property { get; set; } = default!;

        [Parameter] public ExistingPropertyEvent? Event { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<AddPropertyEventDialog>(2);
            builder.AddComponentParameter(3, nameof(AddPropertyEventDialog.Property), Property);
            builder.AddComponentParameter(4, nameof(AddPropertyEventDialog.Event), Event);
            builder.AddComponentParameter(5, nameof(AddPropertyEventDialog.Open), true);
            builder.CloseComponent();
        }
    }

    /// <summary>The delete dialog beside MudBlazor's providers.</summary>
    public sealed class DeleteHost : ComponentBase
    {
        [Parameter] public ExistingProperty Property { get; set; } = default!;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<DeletePropertyDialog>(2);
            builder.AddComponentParameter(3, nameof(DeletePropertyDialog.Property), Property);
            builder.AddComponentParameter(4, nameof(DeletePropertyDialog.Open), true);
            builder.CloseComponent();
        }
    }
}
