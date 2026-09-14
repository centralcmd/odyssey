using Bunit;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="OdsTransactionTagPicker"/> — the control a budget item's identity is chosen with
/// (issue #75 §3, AC 30–31, 34, 36, 38, 40).
/// </summary>
/// <remarks>
/// <para>
/// It is not a new widget: an <c>OdsCombobox</c> inside an <c>OdsFieldShell</c>, both already audited.
/// What this pins is the wiring between them, which is where the accessibility of a REQUIRED,
/// identity-bearing field actually lives — the label association, the constant two-id
/// <c>aria-describedby</c>, the help line that never empties, and the two no-control states.
/// </para>
/// <para>
/// The combobox's own popover rows are MudAutocomplete's and are not reachable in bUnit, so the option
/// model is asserted through the rendered shell and the picker's own state rather than by opening the
/// list.
/// </para>
/// </remarks>
public class TransactionTagPickerTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    public TransactionTagPickerTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    private static readonly Guid GroceriesId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RetiredId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static List<ExistingTransactionTag> Tags() =>
    [
        new() { TransactionTagId = GroceriesId, Name = "Groceries", Description = "Weekly food", Archived = null },
        new() { TransactionTagId = RentId, Name = "Rent", Description = null, Archived = null },
        new()
        {
            TransactionTagId = RetiredId,
            Name = "Retired",
            Description = null,
            Archived = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        },
    ];

    /// <summary>
    /// AC 30. The visible label, the design system's required marker and the helper line belong to the
    /// shell: <c>OdsCombobox</c> has no label, no helper slot and only a boolean error.
    /// </summary>
    [Fact]
    public void The_shell_carries_the_label_the_required_marker_and_the_help()
    {
        var cut = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "bi-tag-pick")
            .Add(p => p.Tags, Tags()));

        var label = cut.Find("label.odc-field-label");
        Assert.Equal("bi-tag-pick", label.GetAttribute("for"));
        Assert.Contains("Transaction tag", label.TextContent);
        Assert.NotNull(cut.Find(".odc-field-req"));

        Assert.Equal(
            OdsTransactionTagPicker.DefaultHelp,
            cut.Find("#bi-tag-pick-help").TextContent.Trim());
    }

    /// <summary>
    /// The selected tag's description becomes the help line — and falls back to the standard helper
    /// when it has none, so the slot never renders empty. That is not tidiness: <c>OdsFieldShell</c>
    /// moves the error to a DIFFERENT id while help is present, and the combobox's
    /// <c>aria-describedby</c> is wired once on first render, so a help line that came and went would
    /// move the error out from under it.
    /// </summary>
    [Fact]
    public void The_help_line_shows_the_selected_tags_description_and_never_empties()
    {
        var withDescription = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "pick-a")
            .Add(p => p.Tags, Tags())
            .Add(p => p.Value, GroceriesId));

        Assert.Equal("Weekly food", withDescription.Find("#pick-a-help").TextContent.Trim());

        var withoutDescription = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "pick-b")
            .Add(p => p.Tags, Tags())
            .Add(p => p.Value, RentId));

        Assert.Equal(OdsTransactionTagPicker.DefaultHelp, withoutDescription.Find("#pick-b-help").TextContent.Trim());
    }

    /// <summary>
    /// AC 31, the shell half. The error renders at <c>{id}-help-error</c> — a different node from the
    /// help — with <c>role="alert"</c>, and the help is STILL shown beside it.
    /// </summary>
    [Fact]
    public void An_error_renders_at_its_own_id_alongside_the_help_that_remains()
    {
        var cut = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "pick-c")
            .Add(p => p.Tags, Tags())
            .Add(p => p.Value, GroceriesId)
            .Add(p => p.Error, "Choose a transaction tag."));

        var error = cut.Find("#pick-c-help-error");
        Assert.Equal("alert", error.GetAttribute("role"));
        Assert.Equal("Choose a transaction tag.", error.TextContent.Trim());

        // The description is not blanked while the error shows.
        Assert.Equal("Weekly food", cut.Find("#pick-c-help").TextContent.Trim());
    }

    /// <summary>
    /// AC 38, first half. With no live tag left to plan for and no way to create one, the picker shows
    /// the empty state and a route to the admin page — not a combobox with nothing in it.
    /// </summary>
    [Fact]
    public void With_nothing_selectable_and_no_create_it_shows_the_empty_state()
    {
        var cut = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "pick-d")
            .Add(p => p.Tags, [])
            .Add(p => p.UsedTagIds, []));

        Assert.Contains("No tags left to plan for.", cut.Find(".odc-tagpick-empty").TextContent);
        Assert.Equal("/transaction-tags", cut.Find(".odc-tagpick-empty a").GetAttribute("href"));
    }

    /// <summary>
    /// AC 34 and AC 38, second half. A caller that CAN create is never shown the empty state — the
    /// create row is the way forward — and a failed FETCH is a different message with a retry, never
    /// the "create a tag" copy, which would point the reader at the wrong problem.
    /// </summary>
    [Fact]
    public void A_failed_fetch_is_not_the_same_state_as_an_empty_list()
    {
        var withCreate = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "pick-e")
            .Add(p => p.Tags, [])
            .Add(p => p.OnCreateTag, _ => new OdsOption(Guid.NewGuid().ToString(), "New")));

        Assert.Empty(withCreate.FindAll(".odc-tagpick-empty"));

        var failed = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "pick-f")
            .Add(p => p.Tags, [])
            .Add(p => p.LoadFailed, true));

        Assert.Empty(failed.FindAll(".odc-tagpick-empty"));
        Assert.Contains("Unable to load the tag list.", failed.Find(".odc-tagpick-unavailable").TextContent);
    }

    /// <summary>
    /// AC 40's marker rule, at the option level: an archived tag reads "· Archived" as TEXT, so the
    /// marker survives into the collapsed input and the accessible name rather than living in a colour.
    /// A tag already planned for is offered but unselectable, and says "in use" in words.
    /// </summary>
    [Fact]
    public void Options_state_archived_and_in_use_in_words()
    {
        var cut = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "pick-g")
            .Add(p => p.Tags, Tags())
            .Add(p => p.Value, RetiredId)
            .Add(p => p.UsedTagIds, [RentId]));

        var options = cut.Instance.Options;

        var archived = Assert.Single(options, option => option.Value == RetiredId.ToString());
        Assert.Equal("Retired · Archived", archived.Label);

        var inUse = Assert.Single(options, option => option.Value == RentId.ToString());
        Assert.Equal("in use", inUse.Note);
        Assert.True(inUse.Disabled);

        var free = Assert.Single(options, option => option.Value == GroceriesId.ToString());
        Assert.Null(free.Note);
        Assert.False(free.Disabled);
    }

    /// <summary>
    /// The record's own tag stays selectable once archived — keeping a link that already exists removes
    /// no capability — but no OTHER archived tag is offered, because making a new one does.
    /// </summary>
    [Fact]
    public void An_archived_tag_is_offered_only_when_it_is_already_the_records_own()
    {
        var unselected = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "pick-h")
            .Add(p => p.Tags, Tags()));

        Assert.DoesNotContain(unselected.Instance.Options, o => o.Value == RetiredId.ToString());

        var selected = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "pick-i")
            .Add(p => p.Tags, Tags())
            .Add(p => p.Value, RetiredId));

        Assert.Contains(selected.Instance.Options, o => o.Value == RetiredId.ToString());
    }

    /// <summary>
    /// AC 36. A name an existing tag already holds — in any case, archived included — is refused by the
    /// picker's own pre-check, WITHOUT the create ever being staged, and the archived case says so and
    /// offers no inline remedy.
    /// </summary>
    [Fact]
    public async Task A_duplicate_name_is_refused_before_the_create_is_staged()
    {
        var staged = 0;
        var cut = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "pick-j")
            .Add(p => p.Tags, Tags())
            .Add(p => p.OnCreateTag, name =>
            {
                staged++;
                return new OdsOption(Guid.NewGuid().ToString(), name);
            }));

        // BeginCreate renders, so it has to run on the dispatcher — as it does in the real flow,
        // where OdsCombobox invokes it from inside its own selection event.
        // Case-insensitive against a LIVE tag.
        Assert.Null(await cut.InvokeAsync(() => cut.Instance.BeginCreate("  groceries ")));
        Assert.Equal(0, staged);
        Assert.Contains("already exists", cut.Find(".odc-field-help.error").TextContent);

        // And against an ARCHIVED one, which the unique index covers too.
        Assert.Null(await cut.InvokeAsync(() => cut.Instance.BeginCreate("RETIRED")));
        Assert.Equal(0, staged);
        var message = cut.Find(".odc-field-help.error").TextContent;
        Assert.Contains("archived", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Transaction tags", message);

        // A genuinely new name stages exactly once and clears the verdict.
        Assert.NotNull(await cut.InvokeAsync(() => cut.Instance.BeginCreate("Childcare")));
        Assert.Equal(1, staged);
        Assert.Empty(cut.FindAll(".odc-field-help.error"));
    }

    /// <summary>
    /// AC 33 / 34, the gate itself. <c>OdsCombobox</c> has no <c>AllowCreate</c> flag — it renders its
    /// create row exactly when <c>OnCreate</c> is non-null — so passing no handler IS the suppression,
    /// and that is what the grid and a caller without the claim do.
    /// </summary>
    [Fact]
    public void Without_a_create_handler_nothing_can_be_staged()
    {
        var cut = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "pick-k")
            .Add(p => p.Tags, Tags()));

        Assert.Null(cut.Instance.CreateHandler);
    }

    /// <summary>
    /// AC 32's id half. The shell's label points at the SAME id the combobox is told to use, and a
    /// caller rendering one per row gets a distinct one each time — a constant would make every row's
    /// aria wiring collide.
    /// </summary>
    [Fact]
    public void The_label_association_follows_the_per_instance_input_id()
    {
        var first = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "bi-tag-pick-row-1")
            .Add(p => p.Tags, Tags())
            .Add(p => p.HideLabel, true));

        var second = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "bi-tag-pick-row-2")
            .Add(p => p.Tags, Tags())
            .Add(p => p.HideLabel, true));

        Assert.Equal("bi-tag-pick-row-1", first.Find("label.odc-field-label").GetAttribute("for"));
        Assert.Equal("bi-tag-pick-row-2", second.Find("label.odc-field-label").GetAttribute("for"));

        // Hidden visually, NOT from assistive tech: the grid's column header is display:none below
        // 760px, so this label is the control's only accessible name there.
        Assert.Contains("hide-label", first.Find(".odc-tagpick").GetAttribute("class"));
        Assert.Contains("Transaction tag", first.Find("label.odc-field-label").TextContent);
    }

    /// <summary>
    /// AC 31's constant-value half. The wiring runs on first render only, so the value must name BOTH
    /// the help id and the error id from the start — the error lives at a different id while help is
    /// present, and one introduced on a failed submit would never be applied.
    /// </summary>
    [Fact]
    public void The_described_by_names_both_the_help_and_the_error_id()
    {
        var cut = ctx.Render<OdsTransactionTagPicker>(parameters => parameters
            .Add(p => p.InputId, "pick-l")
            .Add(p => p.Tags, Tags()));

        var before = cut.Instance.DescribedBy;
        Assert.Equal("pick-l-help pick-l-help-error", before);

        cut.Render(parameters => parameters
            .Add(p => p.InputId, "pick-l")
            .Add(p => p.Tags, Tags())
            .Add(p => p.Error, "Choose a transaction tag."));

        Assert.Equal(before, cut.Instance.DescribedBy);
        Assert.NotNull(cut.Find("#pick-l-help-error"));
    }
}
