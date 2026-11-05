using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The one dropdown contract every select-style control shares (Odyssey Design System ·
/// docs/components-data-table-menu-form-controls.md · Dropdowns): one popover surface, a popover never narrower than its trigger, a filter-bar
/// trigger sized to a short option list, and Status filters that wear the overview's glyphs.
/// </summary>
public class DropdownConsistencyTests
{
    private static BunitContext NewContext() => NestedInteractiveControlTests.NewContext();

    [Fact]
    public void A_popup_menu_is_never_narrower_than_its_trigger_by_default() =>
        Assert.Equal(DropdownWidth.Adaptive, new OdsPopupMenu().RelativeWidth);

    [Theory]
    [InlineData(null)]
    [InlineData("odc-ms-pop-paper")]
    public async Task Every_popover_wears_the_shared_surface(string? consumerClass)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsPopupMenu>(1);
            builder.AddAttribute(2, nameof(OdsPopupMenu.PopoverClass), consumerClass);
            builder.AddAttribute(3, nameof(OdsPopupMenu.TriggerContent), (RenderFragment)(b => b.AddContent(0, "Open")));
            builder.AddAttribute(4, nameof(OdsPopupMenu.ChildContent), (RenderFragment)(b => b.AddContent(0, "Body")));
            builder.CloseComponent();
        });

        cut.Find("button.odc-popmenu-trigger").Click();

        var paper = cut.Find(".mud-popover.odc-popmenu-paper");
        Assert.Contains("odc-popmenu-paper", paper.ClassList);
        if (consumerClass is not null)
        {
            Assert.Contains(consumerClass, paper.ClassList);
        }
        // The rendered popover carries the Adaptive width rule, not just the parameter default.
        Assert.Contains("mud-popover-adaptive-width", paper.ClassList);
    }

    private static IReadOnlyList<OdsOption> OptionsOf(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new OdsOption($"v{i}", $"Option {i}") { Icon = "label" })];

    private static async Task<int> GhostCount(int optionCount, bool quiet = false)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Status")
            .Add(c => c.Options, OptionsOf(optionCount))
            .Add(c => c.Quiet, quiet));
        return cut.FindAll(".odc-ms-fit-ghost").Count;
    }

    [Fact]
    public async Task A_short_option_list_sizes_the_trigger_to_its_widest_row() =>
        Assert.Equal(OdsMultiSelect.FitLimit, await GhostCount(OdsMultiSelect.FitLimit));

    [Fact]
    public async Task An_open_ended_list_keeps_the_trigger_floor_and_lets_the_popover_grow() =>
        Assert.Equal(0, await GhostCount(OdsMultiSelect.FitLimit + 1));

    [Fact]
    public async Task A_quiet_trigger_is_not_widened() => Assert.Equal(0, await GhostCount(3, quiet: true));

    [Fact]
    public async Task An_empty_list_renders_no_ghosts() => Assert.Equal(0, await GhostCount(0));

    [Fact]
    public async Task Fit_ghosts_mirror_the_option_labels()
    {
        await using var ctx = NewContext();
        var options = OptionsOf(3);
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Status")
            .Add(c => c.Options, options));

        Assert.Equal(options.Select(o => o.Label),
            cut.FindAll(".odc-ms-fit-ghost .odc-check-label").Select(e => e.TextContent));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(OdsMultiSelect.FitLimit + 1)]
    public async Task The_trigger_face_is_the_same_in_fit_and_non_fit_modes(int optionCount)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Status")
            .Add(c => c.Icon, "check_circle")
            .Add(c => c.Options, OptionsOf(optionCount))
            .Add(c => c.Values, ["v1", "v2"]));

        var trigger = cut.Find("button.odc-ms-trigger");
        Assert.Equal("Status", trigger.QuerySelector(".odc-ms-label")!.TextContent);
        Assert.Equal("2", trigger.QuerySelector(".odc-ms-count")!.TextContent);
        Assert.NotNull(trigger.QuerySelector(".odc-ms-chevron"));
        Assert.Equal("check_circle", trigger.QuerySelector(".material-icons")!.TextContent);
        Assert.Equal(optionCount <= OdsMultiSelect.FitLimit, trigger.QuerySelector(".odc-ms-fit-ghost") is not null);
    }

    /// <summary>Only a fitted panel drops the CSS width floor (<c>.odc-ms-pop:not(.fit)</c>), so the
    /// <c>fit</c> class must appear exactly when the trigger is sized to its rows.</summary>
    [Theory]
    [InlineData(OdsMultiSelect.FitLimit, false, true)]
    [InlineData(OdsMultiSelect.FitLimit + 1, false, false)]
    [InlineData(3, true, false)]
    [InlineData(0, false, false)]
    public async Task The_panel_is_marked_fit_exactly_when_the_trigger_is_fitted(int optionCount, bool quiet, bool expectFit)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsMultiSelect>(1);
            builder.AddAttribute(2, nameof(OdsMultiSelect.Label), "Status");
            builder.AddAttribute(3, nameof(OdsMultiSelect.Options), OptionsOf(optionCount));
            builder.AddAttribute(4, nameof(OdsMultiSelect.Quiet), quiet);
            builder.CloseComponent();
        });

        cut.Find("button.odc-ms-trigger").Click();

        Assert.Equal(expectFit, cut.Find(".odc-ms-pop").ClassList.Contains("fit"));
    }

    [Fact]
    public async Task Fit_ghosts_are_hidden_from_the_accessible_name()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Status")
            .Add(c => c.Options, OptionsOf(2)));

        Assert.All(cut.FindAll(".odc-ms-fit-ghost"), g => Assert.Equal("true", g.GetAttribute("aria-hidden")));
    }

    [Theory]
    [InlineData(OdsMultiSelect.FitLimit + 1, true, true)]
    [InlineData(3, false, false)]
    public async Task An_explicit_fit_overrides_the_count(int optionCount, bool fit, bool expectGhosts)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Account type")
            .Add(c => c.Options, OptionsOf(optionCount))
            .Add(c => c.Fit, fit));

        Assert.Equal(expectGhosts, cut.FindAll(".odc-ms-fit-ghost").Count > 0);
    }

    /// <summary>An open-ended list keeps its own trigger width and its popover opens at exactly that
    /// width (Relative caps it; the panel's CSS asks for more). A long row wraps rather than truncating,
    /// so no row leans on a hover-only tooltip (WCAG 1.4.13).</summary>
    [Fact]
    public async Task An_open_ended_list_opens_at_the_triggers_width_without_tooltips()
    {
        await using var ctx = NewContext();
        var options = OptionsOf(3);
        var cut = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsMultiSelect>(1);
            builder.AddAttribute(2, nameof(OdsMultiSelect.Label), "Merchant");
            builder.AddAttribute(3, nameof(OdsMultiSelect.Options), options);
            builder.AddAttribute(4, nameof(OdsMultiSelect.Fit), (bool?)false);
            builder.CloseComponent();
        });

        Assert.Contains("open-ended", cut.Find(".odc-popmenu.odc-ms").ClassList);
        cut.Find("button.odc-ms-trigger").Click();

        Assert.Contains("mud-popover-relative-width", cut.Find(".mud-popover.odc-popmenu-paper").ClassList);
        Assert.Contains("open-ended", cut.Find(".odc-ms-pop").ClassList);
        Assert.Equal(options.Select(o => o.Label), cut.FindAll(".odc-ms-opt .odc-check-label").Select(e => e.TextContent.Trim()));
        Assert.All(cut.FindAll(".odc-ms-opt .odc-check-label"), e => Assert.Null(e.GetAttribute("title")));
    }

    /// <summary>Open-ended is opt-in (Fit="false") and does not wait for rows: an account or tag list that
    /// loads after first render must not widen its trigger when it arrives.</summary>
    [Theory]
    [InlineData(false, 0, false, true)]                         // opted in, still loading: already open-ended
    [InlineData(false, 3, true, false)]                         // opted in but quiet: never
    [InlineData(null, OdsMultiSelect.FitLimit + 1, false, false)] // a long list nobody opted in: content-sized
    [InlineData(null, 3, false, false)]                         // a short fitted list
    [InlineData(true, OdsMultiSelect.FitLimit + 1, false, false)] // a fixed vocabulary past the limit
    public async Task Open_ended_mode_is_opt_in_and_independent_of_the_row_count(bool? fit, int optionCount, bool quiet, bool expectOpenEnded)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Merchant")
            .Add(c => c.Options, OptionsOf(optionCount))
            .Add(c => c.Fit, fit)
            .Add(c => c.Quiet, quiet));

        Assert.Equal(expectOpenEnded, cut.Find(".odc-popmenu.odc-ms").ClassList.Contains("open-ended"));
    }

    [Theory]
    [InlineData(true, 3, true, false)]   // Quiet outranks an explicit fit: a heading-like trigger is never widened
    [InlineData(true, 0, false, false)]  // nothing to fit to
    public async Task An_explicit_fit_still_needs_rows_and_a_non_quiet_trigger(bool fit, int optionCount, bool quiet, bool expectGhosts)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Account type")
            .Add(c => c.Options, OptionsOf(optionCount))
            .Add(c => c.Fit, fit)
            .Add(c => c.Quiet, quiet));

        Assert.Equal(expectGhosts, cut.FindAll(".odc-ms-fit-ghost").Count > 0);
    }

    [Fact]
    public async Task The_contact_type_filter_trigger_carries_no_glyph()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsContactTypeMultiSelect>();

        var trigger = cut.Find("button.odc-ms-trigger");
        Assert.Equal("Contact type", trigger.QuerySelector(".odc-ms-label")!.TextContent);
        Assert.Null(trigger.QuerySelector(".odc-ms-fit-cur > .material-icons:not(.odc-ms-chevron)"));
    }

    private const string MultiSelectTag = @"<Ods\w*MultiSelect\b(?:[^>""]|""[^""]*"")*>";

    /// <summary>Every filter-bar multi-select (<c>Class="ph-select"</c>) on any page, as (file, tag).</summary>
    private static IEnumerable<(string File, string Tag)> FilterBarMultiSelects() =>
        Directory.GetFiles(Path.Combine(ClientSource.Root, "Pages"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(path => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), MultiSelectTag)
                .Select(m => m.Value)
                .Where(tag => tag.Contains("ph-select", StringComparison.Ordinal))
                .Select(tag => (Path.GetRelativePath(ClientSource.Root, path), tag)));

    [Fact]
    public void The_scan_finds_the_filter_bars_of_finance_journal_photos_and_admin_pages()
    {
        var files = FilterBarMultiSelects().Select(x => x.File).Distinct().ToList();
        Assert.True(files.Count >= 15, $"Found filter-bar multi-selects in only {files.Count} files.");
        Assert.Contains(files, f => f.EndsWith("PhotosCard.razor", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("TransactionsCard.razor", StringComparison.Ordinal));
    }

    /// <summary>Filter-bar triggers carry their name only; the glyphs are on the rows. A trigger
    /// <c>Icon=</c> or an "Any …" label is the drift these changes removed, on every page. The tag
    /// match steps over quoted attribute values, so a <c>&gt;</c> inside a lambda cannot end it early.</summary>
    [Fact]
    public void Every_filter_bar_trigger_carries_its_name_only()
    {
        var offenders = FilterBarMultiSelects()
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Tag, @"\sIcon=")
                || x.Tag.Contains("Label=\"Any", StringComparison.Ordinal))
            .Select(x => $"{x.File}: {x.Tag}")
            .ToList();

        Assert.True(offenders.Count == 0, "A filter-bar trigger carries a glyph or an \"Any …\" label:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void The_trigger_match_steps_over_a_lambda_in_an_attribute()
    {
        const string markup = """<OdsMultiSelect ValuesChanged="@(v => Set(v))" Icon="flag" />""";
        var match = System.Text.RegularExpressions.Regex.Match(markup, MultiSelectTag);

        Assert.Contains("Icon=", match.Value);
    }

    [Fact]
    public void A_transactions_account_row_wears_its_account_type()
    {
        var account = new ExistingAccount
        {
            AccountId = Guid.NewGuid(), Name = "Everyday Checking", Description = "", Opened = DateTime.UtcNow,
            AccountType = AccountType.CheckingAccount,
        };

        var option = TransactionsCard.AccountOption(account);

        Assert.Equal(AccountTypeVisuals.MaterialIcon(AccountType.CheckingAccount), option.Icon);
        Assert.Equal(AccountTypeVisuals.FgColor(AccountType.CheckingAccount), option.IconColor);
    }

    [Theory]
    [InlineData(false, false, "Acme AS")]
    [InlineData(true, false, "Kari Nordmann · Deceased")]
    [InlineData(false, true, "Acme AS · Dissolved")]
    public void A_transactions_merchant_row_wears_its_contact_type_and_state(bool deceasedPerson, bool dissolved, string expectedLabel)
    {
        var contact = new Odyssey.Dtos.Journal.ExistingContact
        {
            ContactId = Guid.NewGuid(),
            ResolvedDisplayName = deceasedPerson ? "Kari Nordmann" : "Acme AS",
            NormalizedName = "x",
            ExternalUid = "x",
            Type = deceasedPerson ? Odyssey.Dtos.ContactType.Person : Odyssey.Dtos.ContactType.Organization,
        };
        if (deceasedPerson)
        {
            contact.PersonDetails = new() { FirstName = "Kari", LastName = "Nordmann", DateOfDeath = new DateTime(2020, 1, 1) };
        }
        if (dissolved)
        {
            contact.OrganizationDetails = new() { LegalName = "Acme AS", DissolvedDate = new DateTime(2020, 1, 1) };
        }

        var option = TransactionsCard.MerchantOption(contact);
        var type = OdsTypeRegistries.ContactTypeOf(contact.Type.ToString());

        Assert.Equal(expectedLabel, option.Label);
        Assert.Equal(type.Icon, option.Icon);
        Assert.Equal(type.Color, option.IconColor);
    }

    [Fact]
    public void A_task_status_row_wears_its_status_chips_glyph_and_tone() =>
        Assert.All(Odyssey.Client.Pages.Journal.JournalTaskUi.StatusOptions, option =>
        {
            var meta = OdsTodoStatusChip.Meta(Enum.Parse<Odyssey.Dtos.Journal.JournalTaskStatus>(option.Value));
            Assert.Equal(meta.Label, option.Label);
            Assert.Equal(meta.Icon, option.Icon);
            Assert.Equal(OdsBreakdown.Tone(meta.Tone), option.IconColor);
        });

    [Fact]
    public void The_archival_status_filter_keeps_the_values_the_api_binds_and_wears_glyphs()
    {
        Assert.Equal(Enum.GetNames<Odyssey.Dtos.ArchivalStatus>().Order(), ArchivalStatusOptions.Options.Select(o => o.Value).Order());
        Assert.All(ArchivalStatusOptions.Options, o => Assert.False(string.IsNullOrEmpty(o.Icon)));
        Assert.Same(ArchivalStatusOptions.Options, Odyssey.Client.Pages.Journal.LocationVisuals.StatusOptions);
    }

    [Theory]
    [InlineData("#B23B3B", "beach_access")]
    [InlineData(null, null)]
    [InlineData("not-a-colour", "unknown_glyph")]
    public void A_journal_tag_row_wears_the_same_glyph_and_colour_as_its_by_tag_row(string? color, string? icon)
    {
        var option = Odyssey.Client.Pages.Journal.JournalOverviews.TagOption(Guid.NewGuid(), "Trip", icon, color, "var(--tag-text)");

        Assert.Equal(JournalTagChips.GlyphFor(icon), option.Icon);
        Assert.Equal(Odyssey.Client.Theme.UserColor.Glyph(color) ?? "var(--tag-text)", option.IconColor);
    }

    /// <summary>Each tag family's filter row against its own By tag row, built by the production code on
    /// both sides — so the per-family fallback ink (journal and task tags --tag-text, photo tags the
    /// secondary text colour) is what is compared, not a value the test passes in.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("#B23B3B")]
    public void Each_tag_familys_filter_row_matches_its_by_tag_row(string? color)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var journalTag = new Odyssey.Dtos.Journal.ExistingJournalTag { JournalTagId = id, Name = "Trip", Icon = "beach_access", Color = color };
        var journalEntry = new Odyssey.Dtos.Journal.JournalEntrySummary
        {
            JournalEntryId = Guid.NewGuid(), Title = "t", Content = "c", EntryDate = now, CreatedAt = now, UpdatedAt = now,
            AttachmentCount = 0, TagIds = [id],
        };
        AssertSame(Odyssey.Client.Pages.Journal.JournalOverviews.JournalTagRows([journalTag], [journalEntry]).Single(),
            Odyssey.Client.Pages.Journal.JournalOverviews.JournalTagOption(journalTag));

        var taskTag = new Odyssey.Dtos.Journal.ExistingJournalTaskTag { JournalTaskTagId = id, Name = "Trip", Icon = "beach_access", Color = color };
        var task = new Odyssey.Dtos.Journal.JournalTaskSummary
        {
            JournalTaskId = Guid.NewGuid(), Title = "t", Status = Odyssey.Dtos.Journal.JournalTaskStatus.Backlog, Position = 0,
            AttachmentCount = 0, TagIds = [id],
        };
        AssertSame(Odyssey.Client.Pages.Journal.JournalOverviews.TaskTagRows([taskTag], [task]).Single(),
            Odyssey.Client.Pages.Journal.JournalOverviews.TaskTagOption(taskTag));

        var photoTag = new Odyssey.Dtos.Journal.ExistingPhotoTag { PhotoTagId = id, Name = "Trip", Icon = "beach_access", Color = color };
        AssertSame(Odyssey.Client.Pages.Journal.JournalOverviews.PhotoTagRows([photoTag], [new() { Key = id, Count = 1 }]).Single(),
            Odyssey.Client.Pages.Journal.JournalOverviews.PhotoTagOption(photoTag));

        static void AssertSame(OdsBreakdownRow row, OdsOption option)
        {
            Assert.Equal(row.Icon, option.Icon);
            Assert.Equal(row.IconColor, option.IconColor);
            Assert.Equal(row.Label, option.Label);
        }
    }

    public static TheoryData<string, IReadOnlyList<OdsOption>> HandWrittenGlyphOptions => new()
    {
        { "Journal attachment", Odyssey.Client.Pages.Journal.JournalCard._mediaOptions },
        { "Location coordinates", Odyssey.Client.Pages.Journal.LocationVisuals.CoordinatesOptions },
        { "Location use", Odyssey.Client.Pages.Journal.LocationVisuals.UseOptions },
    };

    /// <summary>The fixed two-row lists whose glyphs are written by hand: every row carries one, and no two
    /// rows in a list share it (a filter whose rows look alike reads as one option).</summary>
    [Theory]
    [MemberData(nameof(HandWrittenGlyphOptions))]
    public void Every_hand_written_filter_row_carries_its_own_glyph(string filter, IReadOnlyList<OdsOption> options)
    {
        Assert.All(options, o =>
        {
            Assert.False(string.IsNullOrEmpty(o.Icon), $"{filter}: {o.Label} has no icon");
            Assert.False(string.IsNullOrEmpty(o.IconColor), $"{filter}: {o.Label} has no colour");
        });
        Assert.Equal(options.Count, options.Select(o => o.Icon).Distinct().Count());
    }

    [Fact]
    public void The_no_coordinates_row_wears_the_location_rows_no_coordinates_glyph() =>
        Assert.Contains(Odyssey.Client.Pages.Journal.LocationVisuals.CoordinatesOptions,
            o => o.Icon == Odyssey.Client.Pages.Journal.LocationVisuals.NoCoordinatesIcon);

    [Theory]
    [InlineData("Owner", "verified_user")]
    [InlineData("Admin", "shield")]
    [InlineData("User", "how_to_reg")]
    [InlineData("Guest", "visibility")]
    [InlineData("Auditor", "person")]
    public void A_role_row_wears_its_pill_glyph_in_its_avatar_colour(string role, string glyph)
    {
        var option = Odyssey.Client.Pages.UserDisplay.RoleOption(role);

        Assert.Equal(role, option.Value);
        Assert.Equal(glyph, option.Icon);
        Assert.Equal(Odyssey.Client.Pages.UserDisplay.AvatarFg(role), option.IconColor);
    }

    /// <summary>The role pill draws an SVG (<c>RoleIcon</c>), the filter row a ligature (<c>RoleGlyph</c>):
    /// two spellings of one glyph, so they are checked against each other here, not just each on its own.</summary>
    [Theory]
    [InlineData("Owner", nameof(MudBlazor.Icons.Material.Filled.VerifiedUser))]
    [InlineData("Admin", nameof(MudBlazor.Icons.Material.Filled.Shield))]
    [InlineData("User", nameof(MudBlazor.Icons.Material.Filled.HowToReg))]
    [InlineData("Guest", nameof(MudBlazor.Icons.Material.Filled.Visibility))]
    [InlineData("Auditor", nameof(MudBlazor.Icons.Material.Filled.Person))]
    public void The_role_ligature_names_the_same_glyph_as_the_role_pill(string role, string svgName)
    {
        var svg = typeof(MudBlazor.Icons.Material.Filled).GetField(svgName)!.GetValue(null);
        Assert.Equal(svg, Odyssey.Client.Pages.UserDisplay.RoleIcon(role));

        var ligature = Odyssey.Client.Pages.UserDisplay.RoleGlyph(role);
        Assert.Equal(svgName, string.Concat(ligature.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..])));
    }

    [Theory]
    [InlineData("PDF", "picture_as_pdf")]
    [InlineData("Image", "image")]
    [InlineData("File", "insert_drive_file")]
    public void A_file_type_row_wears_its_by_type_glyph(string kind, string glyph)
    {
        var option = Odyssey.Client.Pages.Files.KindOption(kind);

        Assert.Equal(kind, option.Value);
        Assert.Equal(glyph, option.Icon);
        Assert.False(string.IsNullOrEmpty(option.IconColor));
    }

    [Fact]
    public void An_album_row_wears_the_by_album_glyph_and_colour()
    {
        var album = new Odyssey.Dtos.Journal.PhotoAlbumSummary { PhotoAlbumId = Guid.NewGuid(), Name = "Summer", PhotoCount = 0 };

        var option = Odyssey.Client.Pages.Photos.AlbumVisuals.FilterOption(album);

        Assert.Equal(album.PhotoAlbumId.ToString(), option.Value);
        Assert.Equal("Summer", option.Label);
        Assert.Equal(Odyssey.Dtos.PhotoAlbumIcons.Default, option.Icon);
        Assert.Equal(Odyssey.Client.Pages.Photos.AlbumVisuals.OverviewFallback, option.IconColor);
    }

    [Fact]
    public void A_calendar_row_wears_its_calendars_colour_through_the_validated_door()
    {
        var calendar = new Odyssey.Dtos.Journal.ExistingCalendar { CalendarId = Guid.NewGuid(), Name = "Work", Color = "#1E88E5" };

        var option = Odyssey.Client.Pages.Calendar.CalendarPage.CalendarOption(calendar);

        Assert.Equal("calendar_month", option.Icon);
        Assert.Equal(Odyssey.Client.Theme.UserColor.Glyph("#1E88E5"), option.IconColor);
    }

    [Theory]
    [InlineData("#1E88E5", true)]
    [InlineData(null, false)]
    [InlineData("red;background:url(x)", false)]
    public void A_tag_row_wears_its_own_colour_through_the_validated_door(string? color, bool expectColour)
    {
        var tag = new ExistingTransactionTag { TransactionTagId = Guid.NewGuid(), Name = "Groceries", Archived = null, Color = color };

        var option = OdsTransactionTagOptions.From(tag);

        Assert.Equal(Odyssey.Client.Theme.UserColor.Glyph(color), option.IconColor);
        Assert.Equal(expectColour, option.IconColor is not null);
    }

    [Fact]
    public void A_breakdown_definition_becomes_a_filter_option_with_its_glyph_and_tone()
    {
        var option = OdsBreakdown.Option(new OdsBreakdownDef<bool>(true, "Archived", "outline", "inventory_2"),
            archived => archived ? "Archived" : "Active");

        Assert.Equal("Archived", option.Value);
        Assert.Equal("Archived", option.Label);
        Assert.Equal("inventory_2", option.Icon);
        Assert.Equal(OdsBreakdown.Tone("outline"), option.IconColor);
    }

    public static TheoryData<string, IReadOnlyList<OdsOption>> FinanceStatusFilters => new()
    {
        { "Accounts", AccountsCard._statusOptions },
        { "Budgets", BudgetsCard._statusOptions },
        { "Contacts", ContactsCard._statusOptions },
        { "Contracts", ContractsCard._statusOptions },
        { "Currencies", CurrenciesCard._statusOptions },
        { "Exchange rates", ExchangeRatesCard._statusOptions },
        { "Properties", PropertyVisuals.StatusOptions },
        { "Tax statements", TaxStatementsCard._statusOptions },
        { "Transactions status", TransactionsCard._statusOptions },
        { "Transactions direction", TransactionsCard._directionOptions },
        { "Transaction tags", OdsTagAdmin<ExistingTransactionTag>._statusOptions },
        { "Journal tags", OdsTagAdmin<Odyssey.Dtos.Journal.ExistingJournalTag>._statusOptions },
        { "Task tags", OdsTagAdmin<Odyssey.Dtos.Journal.ExistingJournalTaskTag>._statusOptions },
        { "Photo tags", OdsTagAdmin<Odyssey.Dtos.Journal.ExistingPhotoTag>._statusOptions },
        { "Users status", Odyssey.Client.Pages.Users._statusOptions },
        { "File analysis outcome", Odyssey.Client.Pages.AnalysisLog._statusOptions },
    };

    /// <summary>The wire values each filter sends — what the list endpoint binds, unchanged by the move
    /// to shared definitions.</summary>
    public static TheoryData<string, IReadOnlyList<OdsOption>, string[]> FinanceFilterWireValues => new()
    {
        { "Accounts", AccountsCard._statusOptions, ["Open", "Closed", "Archived"] },
        { "Budgets", BudgetsCard._statusOptions, ["Active", "Archived"] },
        { "Contacts", ContactsCard._statusOptions, ["active", "archived"] },
        { "Contracts", ContractsCard._statusOptions, [.. OdsContractStatus.Order.Select(s => s.ToString())] },
        { "Currencies", CurrenciesCard._statusOptions, [.. OdsTypeRegistries.CurrencyStatuses.Keys.Select(k => k.ToString())] },
        { "Exchange rates", ExchangeRatesCard._statusOptions, ["Current", "Upcoming", "Historical"] },
        { "Tax statements", TaxStatementsCard._statusOptions, Enum.GetNames<TaxStatementStatusFilter>() },
        // Every TransactionStatus member: the filter was once derived from the enum, so a new member must
        // be added to StatusDefs or this fails rather than silently vanishing from the filter.
        { "Transactions status", TransactionsCard._statusOptions, Enum.GetNames<TransactionStatus>() },
        { "Transactions direction", TransactionsCard._directionOptions, ["income", "expense"] },
        // The tag pages keep their lower-case values: they are the persisted page-state keys.
        { "Tag pages", OdsTagAdmin<ExistingTransactionTag>._statusOptions, ["active", "archived"] },
        { "Users status", Odyssey.Client.Pages.Users._statusOptions, ["enabled", "disabled"] },
        // FileAnalysisJobStatus names the API binds; Cancelled is listed under Failed, as the rows show it.
        { "File analysis outcome", Odyssey.Client.Pages.AnalysisLog._statusOptions, ["Completed", "Running", "Failed"] },
    };

    [Theory]
    [MemberData(nameof(FinanceFilterWireValues))]
    public void Every_filter_keeps_the_wire_values_the_list_binds(string page, IReadOnlyList<OdsOption> options, string[] expected) =>
        Assert.True(expected.SequenceEqual(options.Select(o => o.Value)),
            $"{page}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", options.Select(o => o.Value))}]");

    [Fact]
    public void The_contracts_filter_wears_the_overview_tiles_glyph_and_dot_colour() =>
        Assert.All(OdsContractStatus.Order.Zip(ContractsCard._statusOptions), pair =>
        {
            var meta = OdsContractStatus.Meta(pair.First);
            Assert.Equal(meta.Icon, pair.Second.Icon);
            Assert.Equal(meta.DotColor, pair.Second.IconColor);
        });

    [Theory]
    [MemberData(nameof(FinanceStatusFilters))]
    public void Every_status_filter_option_carries_an_icon(string page, IReadOnlyList<OdsOption> options)
    {
        Assert.NotEmpty(options);
        Assert.All(options, o =>
        {
            Assert.False(string.IsNullOrEmpty(o.Icon), $"{page}: {o.Label} has no icon");
            Assert.False(string.IsNullOrEmpty(o.IconColor), $"{page}: {o.Label} has no colour");
        });
    }

}
