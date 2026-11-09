using System.Net;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance.Instruments;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>Serialises the classes that move <c>InstrumentsPage.InteractiveCheck</c>, a process-wide static.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InstrumentsPageCollection
{
    public const string Name = "Instruments page";
}

/// <summary>
/// The <c>/instruments</c> page (issue #452 §3): claim gating (absent, never greyed), the list states, the row menu,
/// the saving row, deep links with their pin and focus, and the source rules for the new files.
/// </summary>
[Collection(InstrumentsPageCollection.Name)]
public sealed class InstrumentsPageTests : IDisposable
{
    public void Dispose() => InstrumentsPage.InteractiveCheck = static () => OperatingSystem.IsBrowser();

    private static readonly string[] Guest = [PermissionClaims.InstrumentsRead];

    private static readonly string[] Owner =
    [
        PermissionClaims.InstrumentsRead, PermissionClaims.InstrumentsCreate, PermissionClaims.InstrumentsUpdate,
        PermissionClaims.InstrumentsDelete, PermissionClaims.InstrumentPricesRead, PermissionClaims.InstrumentPricesCreate,
        PermissionClaims.InstrumentPricesUpdate, PermissionClaims.InstrumentPricesDelete,
    ];

    private static readonly string[] Admin =
        [.. Owner, PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    internal static ExistingInstrument Instrument(
        string symbol, int? priceCount = 0, bool archived = false, bool locked = false, Guid? id = null,
        InstrumentType type = InstrumentType.Stock) => new()
    {
        InstrumentId = id ?? Guid.NewGuid(),
        Symbol = symbol,
        MicCode = type == InstrumentType.Fund ? null : "XOSL",
        Isin = "NO0010096985",
        Name = symbol + " name",
        Type = type,
        CurrencyCode = "NOK",
        ShowOnDashboard = true,
        Archived = archived ? new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) : null,
        PriceCount = priceCount,
        LatestClose = priceCount > 0 ? 284.6m : null,
        LatestTradingDate = priceCount > 0 ? new DateOnly(2026, 11, 6) : null,
        IdentityLocked = locked || priceCount > 0,
    };

    private static ApiResult<T> Ok<T>(T value) => ApiResult<T>.Success(value, HttpStatusCode.OK);

    private sealed class Harness
    {
        public required BunitContext Ctx { get; init; }
        public required Mock<IInstrumentsApiClient> Api { get; init; }
        public required Mock<IInstrumentPriceSourcesApiClient> Sources { get; init; }
        public required RecordingSnackbar Snackbar { get; init; }
    }

    private static Harness Context(string[] claims, params ExistingInstrument[] rows) =>
        Context(claims, Ok(new PagedResult<ExistingInstrument> { Items = [.. rows], TotalCount = rows.Length }));

    private static Harness Context(string[] claims, ApiResult<PagedResult<ExistingInstrument>> list)
    {
        var ctx = PageTestContext.Create(claims);
        var api = new Mock<IInstrumentsApiClient>();
        api.Setup(a => a.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InstrumentsFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => list.IsSuccess
                ? Ok(new PagedResult<ExistingInstrument> { Items = [.. list.Value!.Items.Select(i => i with { })], TotalCount = list.Value.TotalCount })
                : list);
        api.Setup(a => a.GetSummaryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new InstrumentSummary { TotalCount = 1, ByType = [new() { Type = InstrumentType.Stock, Count = 1 }] }));
        var sources = new Mock<IInstrumentPriceSourcesApiClient>();
        sources.Setup(s => s.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new InstrumentPriceSourcesStatus()));
        sources.Setup(s => s.GetBindingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingInstrumentPriceBinding>()));
        var reference = new Mock<IReferenceDataCache>();
        reference.Setup(r => r.CurrenciesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ExistingCurrency { CurrencyCode = "NOK", Name = "Norwegian krone", Symbol = "kr", MinorUnits = 2 }]);
        reference.Setup(r => r.CurrencyOptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new OdsOption("NOK", "Norwegian krone")]);
        var snackbar = new RecordingSnackbar();
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton(sources.Object);
        ctx.Services.AddSingleton(reference.Object);
        ctx.Services.AddSingleton<ISnackbar>(snackbar);
        InstrumentsPage.InteractiveCheck = static () => true;
        return new Harness { Ctx = ctx, Api = api, Sources = sources, Snackbar = snackbar };
    }

    private static IRenderedComponent<ProvidersHost> Render(Harness h, string waitFor)
    {
        var cut = h.Ctx.Render<ProvidersHost>(p => p.AddChildContent<InstrumentsPage>());
        cut.WaitForAssertion(() => Assert.Contains(waitFor, cut.Markup));
        return cut;
    }

    private static AngleSharp.Dom.IElement Card(IRenderedComponent<ProvidersHost> cut, Guid id) =>
        cut.Find($"#{InstrumentsPage.RecordIdFor(id)}");

    private static void OpenCard(IRenderedComponent<ProvidersHost> cut, Guid id)
    {
        Card(cut, id).QuerySelector(".odc-record-trigger")!.Click();
        cut.WaitForAssertion(() => Assert.Equal("true", Card(cut, id).QuerySelector(".odc-record-trigger")!.GetAttribute("aria-expanded")));
    }

    private static void ClickMenu(IRenderedComponent<ProvidersHost> cut, string symbol, string label)
    {
        cut.Find($"button[aria-label='Actions for {symbol}']").Click();
        cut.WaitForAssertion(() => Assert.Contains(label, cut.Markup));
        cut.FindAll(".mud-menu-item, [role=menuitem]").First(i => i.TextContent.Contains(label, StringComparison.Ordinal)).Click();
    }

    private static string[] LastFocusChain(BunitContext ctx) =>
        ctx.JSInterop.Invocations.LastOrDefault(i => i.Identifier == "odsFocusChainOrHeading").Arguments?[0] as string[] ?? ["(no focus call)"];

    // ── Claim gates (§3.1, AC 10, 11) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_guest_sees_the_list_with_no_write_price_or_source_surface()
    {
        var eqnr = Instrument("EQNR", priceCount: null);
        var h = Context(Guest, eqnr);
        await using var _ = h.Ctx;
        var cut = Render(h, "EQNR name");

        Assert.DoesNotContain("New instrument", cut.Markup);
        Assert.Empty(cut.FindAll("button[aria-label='Actions for EQNR']"));
        Assert.DoesNotContain(InstrumentsPage.NoPriceYet, cut.Markup);
        OpenCard(cut, eqnr.InstrumentId);
        Assert.DoesNotContain("Latest close", cut.Markup);
        Assert.DoesNotContain("Price sources", cut.Markup);
        h.Sources.Verify(s => s.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void The_latest_price_date_sort_is_offered_only_to_price_readers()
    {
        Assert.DoesNotContain(InstrumentsPage.SortFieldsFor(canReadPrices: false), f => f.Label == "Latest price date");
        Assert.Contains(InstrumentsPage.SortFieldsFor(canReadPrices: true), f => f.Label == "Latest price date");
    }

    private static IReadOnlyList<string?> Labels(ExistingInstrument i, InstrumentsPage.RowClaims claims) =>
        [.. InstrumentsPage.BuildActions(i, claims, (_, _) => default).Where(m => !m.Divider).Select(m => m.Label)];

    private static readonly InstrumentsPage.RowClaims OwnerRow = new(true, true, true, true, true);

    [Fact]
    public void The_menu_offers_delete_only_without_prices_and_delete_all_prices_only_with_them()
    {
        Assert.Equal(["Edit", "Archive", "Hide from dashboard", "View prices", "Add price", "Delete all prices…"],
            Labels(Instrument("EQNR", priceCount: 251), OwnerRow));
        Assert.Equal(["Edit", "Archive", "Hide from dashboard", "View prices", "Add price", "Delete"],
            Labels(Instrument("NHY", priceCount: 0), OwnerRow));
    }

    [Fact]
    public void An_archived_instrument_has_no_dashboard_toggle_and_no_add_price()
    {
        Assert.Equal(["Edit", "Unarchive", "View prices", "Delete"], Labels(Instrument("TEL", priceCount: 0, archived: true), OwnerRow));
    }

    [Fact]
    public void A_caller_with_no_usable_action_gets_no_menu_at_all()
    {
        Assert.Empty(InstrumentsPage.BuildActions(Instrument("EQNR"), new(false, false, false, false, false), (_, _) => default));
        Assert.Equal(["View prices"], Labels(Instrument("EQNR", priceCount: 3), new(false, false, true, false, false)));
    }

    [Fact]
    public async Task With_prices_the_open_record_says_why_delete_is_missing_and_shows_the_identity_lock()
    {
        var eqnr = Instrument("EQNR", priceCount: 251);
        var h = Context(Owner, eqnr);
        await using var _ = h.Ctx;
        var cut = Render(h, "EQNR name");

        Assert.Contains("284.60 NOK", cut.Markup);
        OpenCard(cut, eqnr.InstrumentId);
        Assert.Contains(InstrumentsPage.DeletePricesFirstLine, cut.Markup);
        Assert.Contains(InstrumentsPage.IdentityLockedLine, cut.Markup);
        Assert.Contains("251", Card(cut, eqnr.InstrumentId).TextContent);
    }

    [Fact]
    public async Task The_price_sources_section_needs_system_settings_read()
    {
        var eqnr = Instrument("EQNR");
        var h = Context(Admin, eqnr);
        await using var _ = h.Ctx;
        var cut = Render(h, "EQNR name");
        OpenCard(cut, eqnr.InstrumentId);

        cut.WaitForAssertion(() => Assert.Contains(InstrumentPriceSourcesPanel.NoSourcesInstalled, cut.Markup));
        h.Sources.Verify(s => s.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
        h.Sources.Verify(s => s.GetBindingsAsync(eqnr.InstrumentId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── States (§3.2, AC 14) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_load_is_an_error_with_retry_never_the_empty_state()
    {
        var h = Context(Owner, ApiResult<PagedResult<ExistingInstrument>>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = "boom" }));
        await using var _ = h.Ctx;
        var cut = h.Ctx.Render<ProvidersHost>(p => p.AddChildContent<InstrumentsPage>());

        cut.WaitForAssertion(() => Assert.Contains("Retry", cut.Markup));
        Assert.DoesNotContain("No instruments yet", cut.Markup);
    }

    [Fact]
    public async Task An_empty_list_invites_a_writer_to_add_one()
    {
        var h = Context(Owner);
        await using var _ = h.Ctx;
        var cut = Render(h, "No instruments yet. Add the stocks, ETFs and funds you follow.");

        Assert.Contains("New instrument", cut.Markup);
    }

    // ── Saving rows (§3.2): a labelled spinner instead of the menu, rolled back on failure ──────────

    [Fact]
    public async Task Archiving_shows_the_saving_spinner_instead_of_the_menu_and_rolls_back_on_failure()
    {
        var eqnr = Instrument("EQNR");
        var h = Context(Owner, eqnr);
        await using var _ = h.Ctx;
        var pending = new TaskCompletionSource<ApiResult>();
        h.Api.Setup(a => a.ArchiveAsync(eqnr.InstrumentId, It.IsAny<CancellationToken>())).Returns(pending.Task);
        var cut = Render(h, "EQNR name");

        ClickMenu(cut, "EQNR", "Archive");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[aria-label='Saving EQNR']")));
        Assert.Contains("Saving…", cut.Markup);
        Assert.Empty(cut.FindAll("button[aria-label='Actions for EQNR']"));
        Assert.Contains("Archived", Card(cut, eqnr.InstrumentId).TextContent);

        await cut.InvokeAsync(() => pending.SetResult(ApiResult.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = "nope" })));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Actions for EQNR']")));
        Assert.DoesNotContain("Archived", Card(cut, eqnr.InstrumentId).TextContent);
        Assert.Contains(h.Snackbar.Toasts, t => t.Message.Contains("Couldn't archive EQNR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task View_prices_and_add_price_link_to_the_prices_page_for_that_instrument()
    {
        var eqnr = Instrument("EQNR", priceCount: 3);
        var h = Context(Owner, eqnr);
        await using var _ = h.Ctx;
        var cut = Render(h, "EQNR name");
        var nav = h.Ctx.Services.GetRequiredService<NavigationManager>();

        ClickMenu(cut, "EQNR", "Add price");
        cut.WaitForAssertion(() => Assert.EndsWith($"/instrument-prices?instrument={eqnr.InstrumentId}&add=1", nav.Uri));
    }

    // ── Deep links (§3.3, AC 9, 19) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_link_to_an_archived_instrument_pins_it_expands_it_and_focuses_its_header()
    {
        var listed = Instrument("EQNR");
        var archived = Instrument("TEL", archived: true);
        var h = Context(Owner, listed);
        await using var _ = h.Ctx;
        h.Api.Setup(a => a.GetAsync(archived.InstrumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(archived));
        h.Ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"instruments?instrument={archived.InstrumentId}");

        var cut = Render(h, "Shown from a link");

        cut.WaitForAssertion(() => Assert.Equal("true", Card(cut, archived.InstrumentId).QuerySelector(".odc-record-trigger")!.GetAttribute("aria-expanded")));
        cut.WaitForAssertion(() => Assert.Equal([InstrumentsPage.RecordIdFor(archived.InstrumentId)], LastFocusChain(h.Ctx)));

        cut.FindAll(".ins-pin-head button").Single().Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("Shown from a link", cut.Markup));
        cut.WaitForAssertion(() => Assert.Empty(LastFocusChain(h.Ctx)));
        Assert.DoesNotContain("instrument=", h.Ctx.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task A_link_to_a_listed_instrument_opens_it_in_place_without_a_pin()
    {
        var listed = Instrument("EQNR");
        var h = Context(Owner, listed);
        await using var _ = h.Ctx;
        h.Api.Setup(a => a.GetAsync(listed.InstrumentId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(listed));
        h.Ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"instruments?instrument={listed.InstrumentId}");

        var cut = Render(h, "EQNR name");

        cut.WaitForAssertion(() => Assert.Equal("true", Card(cut, listed.InstrumentId).QuerySelector(".odc-record-trigger")!.GetAttribute("aria-expanded")));
        Assert.DoesNotContain("Shown from a link", cut.Markup);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public async Task An_unknown_or_malformed_link_says_the_instrument_no_longer_exists(string value)
    {
        var h = Context(Owner, Instrument("EQNR"));
        await using var _ = h.Ctx;
        h.Api.Setup(a => a.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingInstrument>.Failure(HttpStatusCode.NotFound, new ApiProblem { Status = 404 }));
        h.Ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"instruments?instrument={value}");

        var cut = Render(h, "EQNR name");

        cut.WaitForAssertion(() => Assert.Contains(h.Snackbar.Toasts, t => t.Message == InstrumentsPage.UnknownInstrument));
        Assert.DoesNotContain("Shown from a link", cut.Markup);
        Assert.DoesNotContain(h.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusChainOrHeading");
        if (!Guid.TryParse(value, out var __))
            h.Api.Verify(a => a.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Row removal focus (§3.3) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_removed_record_hands_focus_to_the_next_else_the_previous_else_the_heading()
    {
        var a = Instrument("A");
        var b = Instrument("B");
        var c = Instrument("C");

        Assert.Equal($"{InstrumentsPage.RecordIdFor(c.InstrumentId)} {InstrumentsPage.RecordIdFor(a.InstrumentId)}",
            InstrumentsPage.RemovalFocusChain([a, b, c], b.InstrumentId));
        Assert.Equal(InstrumentsPage.RecordIdFor(b.InstrumentId), InstrumentsPage.RemovalFocusChain([a, b, c], c.InstrumentId));
        Assert.Equal(string.Empty, InstrumentsPage.RemovalFocusChain([a], a.InstrumentId));
    }

    [Fact]
    public void By_type_lists_the_three_kit_types_and_other_only_when_present()
    {
        var rows = InstrumentsPage.TypeRowsFor(new InstrumentSummary { ByType = [new() { Type = InstrumentType.Fund, Count = 2 }] });
        Assert.Equal(["Stock", "ETF", "Fund"], rows.Select(r => r.Label));
        Assert.Equal(2, rows[2].Count);

        Assert.Contains(InstrumentsPage.TypeRowsFor(new InstrumentSummary { ByType = [new() { Type = InstrumentType.Other, Count = 1 }] }),
            r => r.Label == "Other");
    }

    [Fact]
    public void The_attribution_is_plain_text_from_the_summary()
    {
        Assert.Equal("Prices from EODHD", InstrumentsPage.AttributionText(new InstrumentSummary { Attributions = ["EODHD"] }));
        Assert.Null(InstrumentsPage.AttributionText(new InstrumentSummary { Attributions = null }));
    }

    // ── Source rules (§5.2, §9; AC 25) ───────────────────────────────────────────────────────────

    private static IEnumerable<string> InstrumentFiles() =>
        Directory.EnumerateFiles(Path.Combine(ClientSource.Root, "Pages", "Finance", "Instruments"))
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal));

    [Fact]
    public void The_lint_scope_reaches_the_page_its_dialogs_and_the_panel()
    {
        var names = InstrumentFiles().Select(Path.GetFileName).ToHashSet();

        Assert.Contains("InstrumentsPage.razor", names);
        Assert.Contains("InstrumentDialog.razor.cs", names);
        Assert.Contains("PurgeInstrumentPricesDialog.razor.cs", names);
        Assert.Contains("InstrumentPriceSourcesPanel.razor.cs", names);
    }

    [Theory]
    [InlineData("MarkupString")]
    [InlineData("innerHTML")]
    [InlineData("AddMarkupContent")]
    [InlineData("Href=\"@source")]
    [InlineData("href=\"@source")]
    public void No_server_string_becomes_markup_or_a_link(string term)
    {
        var offenders = InstrumentFiles().Where(f => File.ReadAllText(f).Contains(term, StringComparison.Ordinal)).Select(ClientSource.Relative).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_server_cap_is_restated_as_a_literal()
    {
        // The bindings cap comes from the sources response, the decimals from InstrumentPriceRules.
        var offenders = InstrumentFiles()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"maximum of \d|MaxBindingsPerInstrument\s*=\s*\d|MaxDecimals\s*=\s*\d"))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_page_persists_its_toolbar_under_its_own_state_key_and_titles_itself()
    {
        Assert.Equal("instruments-page", InstrumentsPage.PageStateKey);
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "Instruments", "InstrumentsPage.razor"));
        Assert.Contains("<PageTitle>Instruments · Odyssey</PageTitle>", markup, StringComparison.Ordinal);
        Assert.Contains("@page \"/instruments\"", markup, StringComparison.Ordinal);
    }
}
