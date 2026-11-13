using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance.Instruments;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Instruments page's dialogs and its price-sources panel (issue #452 §3.3, §3.4): the identity lock, the
/// type-driven required markers, the shared validation, the duplicate with <i>Go to it</i>, the purge count and its
/// typed confirmation, and the sources list's reorder, limit, disclosure and read-only states.
/// </summary>
public sealed class InstrumentDialogsTests
{
    private static ApiResult<T> Ok<T>(T value) => ApiResult<T>.Success(value, HttpStatusCode.OK);

    private static BunitContext Context(out RecordingSnackbar snackbar)
    {
        var ctx = PageTestContext.Create();
        var reference = new Mock<IReferenceDataCache>();
        reference.Setup(r => r.CurrencyOptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new OdsOption("NOK", "Norwegian krone")]);
        ctx.Services.AddSingleton(reference.Object);
        snackbar = new RecordingSnackbar();
        ctx.Services.AddSingleton<ISnackbar>(snackbar);
        return ctx;
    }

    // ── New / Edit instrument ────────────────────────────────────────────────────────────────────

    private static IRenderedComponent<ProvidersHost> RenderDialog(
        BunitContext ctx, ExistingInstrument? instrument = null, Action<Guid>? onGoTo = null) =>
        ctx.Render<ProvidersHost>(p => p.AddChildContent<InstrumentDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Instrument, instrument)
            .Add(x => x.CanGoTo, true)
            .Add(x => x.OnGoTo, id => onGoTo?.Invoke(id))));

    private static AngleSharp.Dom.IElement Input(IRenderedComponent<ProvidersHost> cut, string label) =>
        cut.FindAll(".odc-field").First(f => f.QuerySelector("label")?.TextContent.Trim().StartsWith(label, StringComparison.Ordinal) == true)
            .QuerySelector("input")!;

    [Fact]
    public async Task A_locked_identity_is_read_only_not_disabled_and_the_name_stays_editable()
    {
        await using var ctx = Context(out _);
        var cut = RenderDialog(ctx, InstrumentsPageTests.Instrument("EQNR", priceCount: 251));
        cut.WaitForAssertion(() => Assert.Contains(InstrumentDialog.LockedLine, cut.Markup));

        foreach (var label in new[] { "Type", "Symbol", "Exchange code", "ISIN", "Currency" })
        {
            var input = Input(cut, label);
            Assert.True(input.HasAttribute("readonly"), label);
            Assert.Equal("true", input.GetAttribute("aria-readonly"));
            Assert.False(input.HasAttribute("disabled"), label);
        }

        Assert.False(Input(cut, "Name").HasAttribute("readonly"));
        Assert.Empty(cut.FindAll("[role=radiogroup]"));
    }

    [Fact]
    public async Task The_type_moves_the_required_marker_between_exchange_and_isin()
    {
        await using var ctx = Context(out _);
        var cut = RenderDialog(ctx);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=radio]")));

        Assert.Equal("true", Input(cut, "Exchange code").GetAttribute("aria-required"));
        Assert.Null(Input(cut, "ISIN").GetAttribute("aria-required"));

        cut.FindAll("[role=radio]").First(r => r.TextContent.Contains("Fund", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal("true", Input(cut, "ISIN").GetAttribute("aria-required")));
        Assert.Null(Input(cut, "Exchange code").GetAttribute("aria-required"));
    }

    [Fact]
    public void Validation_runs_the_shared_identifier_rules()
    {
        Assert.Equal(InstrumentDialog.MicFormat,
            InstrumentDialog.Validate(InstrumentType.Stock, "EQNR", null, null, "Equinor", "NOK", locked: false)["micCode"]);
        Assert.Equal(InstrumentDialog.IsinRequired,
            InstrumentDialog.Validate(InstrumentType.Fund, "GLOB", null, null, "Global", "NOK", locked: false)["isin"]);
        Assert.Equal(InstrumentDialog.IsinCheckDigit,
            InstrumentDialog.Validate(InstrumentType.Fund, "GLOB", null, "NO0010096984", "Global", "NOK", locked: false)["isin"]);
        Assert.Equal(InstrumentDialog.IsinFormat,
            InstrumentDialog.Validate(InstrumentType.Fund, "GLOB", null, "NO00100969", "Global", "NOK", locked: false)["isin"]);
        Assert.Equal(InstrumentDialog.SymbolFormat,
            InstrumentDialog.Validate(InstrumentType.Stock, ".EQNR", "XOSL", null, "Equinor", "NOK", locked: false)["symbol"]);
        Assert.Equal(InstrumentDialog.IdentifierRequired,
            InstrumentDialog.Validate(InstrumentType.Other, "X", null, null, "X", "NOK", locked: false)["micCode"]);

        // Lower case is fine: it is saved in capital letters.
        Assert.Empty(InstrumentDialog.Validate(InstrumentType.Stock, "eqnr", "xosl", "no0010096985", "Equinor", "NOK", locked: false));
        // A locked identity is the stored one; only the name is checked.
        Assert.Equal(["name"], InstrumentDialog.Validate(InstrumentType.Stock, "", "", "", " ", "", locked: true).Keys);
    }

    [Fact]
    public async Task A_failed_submit_lists_every_problem_in_the_summary_and_sends_nothing()
    {
        await using var ctx = Context(out _);
        var api = new Mock<IInstrumentsApiClient>();
        ctx.Services.AddSingleton(api.Object);
        var cut = RenderDialog(ctx);
        cut.WaitForAssertion(() => Assert.Contains("Create instrument", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Contains("Create instrument", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("Fix these 3 things before saving", cut.Markup));
        Assert.Contains("Symbol: " + InstrumentDialog.SymbolRequired, cut.Markup);
        Assert.Contains("Name: " + InstrumentDialog.NameRequired, cut.Markup);
        Assert.Equal("true", Input(cut, "Symbol").GetAttribute("aria-invalid"));
        api.Verify(a => a.CreateAsync(It.IsAny<NewInstrument>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_duplicate_says_so_and_offers_go_to_it_with_the_existing_id()
    {
        await using var ctx = Context(out _);
        var existing = Guid.NewGuid();
        var api = new Mock<IInstrumentsApiClient>();
        api.Setup(a => a.CreateAsync(It.IsAny<NewInstrument>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingInstrument>.Failure(HttpStatusCode.Conflict, new ApiProblem
            {
                Status = 409,
                Code = InstrumentDialog.DuplicateCode,
                Detail = "This instrument already exists.",
                Errors = new Dictionary<string, string[]> { ["Symbol"] = ["This instrument already exists."] },
                Extensions = new Dictionary<string, JsonElement> { ["existingInstrumentId"] = JsonSerializer.SerializeToElement(existing) },
            }));
        ctx.Services.AddSingleton(api.Object);
        Guid? goneTo = null;
        var cut = RenderDialog(ctx, onGoTo: id => goneTo = id);
        cut.WaitForAssertion(() => Assert.Contains("Create instrument", cut.Markup));

        Input(cut, "Symbol").Input("eqnr");
        Input(cut, "Exchange code").Input("xosl");
        Input(cut, "Name").Input("Equinor");
        cut.FindAll("button").First(b => b.TextContent.Contains("Create instrument", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains(InstrumentDialog.DuplicateMessage, cut.Markup));
        api.Verify(a => a.CreateAsync(It.Is<NewInstrument>(n => n.Symbol == "EQNR" && n.MicCode == "XOSL"), It.IsAny<CancellationToken>()));
        cut.FindAll("button").First(b => b.TextContent.Contains("Go to it", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Equal(existing, goneTo));
    }

    // ── Delete all prices ────────────────────────────────────────────────────────────────────────

    private static Mock<IInstrumentPricesApiClient> PricesCounting(params int[] counts)
    {
        var prices = new Mock<IInstrumentPricesApiClient>();
        var sequence = prices.SetupSequence(p => p.ListAsync(1, 1, It.IsAny<InstrumentPricesFilter?>(), It.IsAny<CancellationToken>()));
        foreach (var count in counts)
            sequence = sequence.ReturnsAsync(Ok(new PagedResult<ExistingInstrumentPrice> { Items = [], TotalCount = count }));
        return prices;
    }

    private static IRenderedComponent<ProvidersHost> RenderPurge(
        BunitContext ctx, ExistingInstrument instrument, bool canPurgeFetched, Action<int>? onPurged = null) =>
        ctx.Render<ProvidersHost>(p => p.AddChildContent<PurgeInstrumentPricesDialog>(d => d
            .Add(x => x.Open, true)
            .Add(x => x.Instrument, instrument)
            .Add(x => x.CanPurgeFetched, canPurgeFetched)
            .Add(x => x.OnPurged, n => onPurged?.Invoke(n))));

    private static AngleSharp.Dom.IElement DeleteButton(IRenderedComponent<ProvidersHost> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Contains("Delete prices", StringComparison.Ordinal));

    [Fact]
    public async Task The_count_is_shown_and_delete_waits_for_the_exact_number_with_spaces_allowed()
    {
        await using var ctx = Context(out _);
        var instrument = InstrumentsPageTests.Instrument("EQNR", priceCount: 251);
        var prices = PricesCounting(251);
        var api = new Mock<IInstrumentsApiClient>();
        api.Setup(a => a.PurgePricesAsync(instrument.InstrumentId, InstrumentPricePurgeScope.Manual, 251, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new InstrumentPricePurgeResult { Deleted = 251 }));
        ctx.Services.AddSingleton(prices.Object);
        ctx.Services.AddSingleton(api.Object);
        int? purged = null;
        var cut = RenderPurge(ctx, instrument, canPurgeFetched: false, n => purged = n);

        cut.WaitForAssertion(() => Assert.Contains("This deletes 251 prices.", cut.Markup));
        Assert.Empty(cut.FindAll("input[type=radio][value=Fetched]"));
        Assert.Equal("true", DeleteButton(cut).GetAttribute("aria-disabled"));
        prices.Verify(p => p.ListAsync(1, 1, It.Is<InstrumentPricesFilter?>(f =>
            f!.InstrumentIds!.Single() == instrument.InstrumentId && f.Sources!.Single() == InstrumentPriceSource.Manual), It.IsAny<CancellationToken>()));

        var field = cut.Find("input[inputmode=numeric]");
        field.Input("  251 ");
        cut.WaitForAssertion(() => Assert.Null(DeleteButton(cut).GetAttribute("aria-disabled")));
        DeleteButton(cut).Click();

        cut.WaitForAssertion(() => Assert.Equal(251, purged));
    }

    [Fact]
    public void The_typed_confirmation_is_an_invariant_integer_equal_to_the_count()
    {
        Assert.True(PurgeInstrumentPricesDialog.Confirms(" 251 ", 251));
        Assert.False(PurgeInstrumentPricesDialog.Confirms("250", 251));
        Assert.False(PurgeInstrumentPricesDialog.Confirms("2,51", 251));
        Assert.False(PurgeInstrumentPricesDialog.Confirms("0", 0));
        Assert.False(PurgeInstrumentPricesDialog.Confirms(null, 251));
    }

    [Fact]
    public async Task A_scope_change_recounts_and_zero_leaves_only_close()
    {
        await using var ctx = Context(out _);
        var instrument = InstrumentsPageTests.Instrument("EQNR", priceCount: 12);
        var prices = PricesCounting(12, 0);
        ctx.Services.AddSingleton(prices.Object);
        var cut = RenderPurge(ctx, instrument, canPurgeFetched: true);
        cut.WaitForAssertion(() => Assert.Contains("This deletes 12 prices.", cut.Markup));

        cut.Find("input[type=radio][value=Fetched]").Change(true);

        cut.WaitForAssertion(() => Assert.Contains(PurgeInstrumentPricesDialog.NothingToDelete, cut.Markup));
        Assert.Empty(cut.FindAll("input[inputmode=numeric]"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Delete prices", StringComparison.Ordinal));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Close");
        prices.Verify(p => p.ListAsync(1, 1, It.Is<InstrumentPricesFilter?>(f => f!.Sources!.Single() == InstrumentPriceSource.Fetched), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_count_mismatch_shows_the_new_number_and_clears_the_field()
    {
        await using var ctx = Context(out _);
        var instrument = InstrumentsPageTests.Instrument("EQNR", priceCount: 251);
        ctx.Services.AddSingleton(PricesCounting(251).Object);
        var api = new Mock<IInstrumentsApiClient>();
        api.Setup(a => a.PurgePricesAsync(It.IsAny<Guid>(), It.IsAny<InstrumentPricePurgeScope>(), 251, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<InstrumentPricePurgeResult>.Failure(HttpStatusCode.Conflict, new ApiProblem
            {
                Status = 409,
                Code = PurgeInstrumentPricesDialog.CountMismatchCode,
                Extensions = new Dictionary<string, JsonElement> { ["actualCount"] = JsonSerializer.SerializeToElement(248) },
            }));
        ctx.Services.AddSingleton(api.Object);
        var cut = RenderPurge(ctx, instrument, canPurgeFetched: false);
        cut.WaitForAssertion(() => Assert.Contains("This deletes 251 prices.", cut.Markup));

        cut.Find("input[inputmode=numeric]").Input("251");
        DeleteButton(cut).Click();

        cut.WaitForAssertion(() => Assert.Contains(PurgeInstrumentPricesDialog.ChangedMessage(248), cut.Markup));
        Assert.Contains("Type 248 to confirm", cut.Markup);
        Assert.Equal(string.Empty, cut.Find("input[inputmode=numeric]").GetAttribute("value") ?? string.Empty);
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById" && (string?)i.Arguments[0] == cut.Find("input[inputmode=numeric]").Id);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "job-run.already-running", PurgeInstrumentPricesDialog.AlreadyRunningMessage)]
    [InlineData(HttpStatusCode.Forbidden, null, PurgeInstrumentPricesDialog.ForbiddenMessage)]
    public async Task A_refused_purge_says_why(HttpStatusCode status, string? code, string message)
    {
        await using var ctx = Context(out _);
        var instrument = InstrumentsPageTests.Instrument("EQNR", priceCount: 3);
        ctx.Services.AddSingleton(PricesCounting(3).Object);
        var api = new Mock<IInstrumentsApiClient>();
        api.Setup(a => a.PurgePricesAsync(It.IsAny<Guid>(), It.IsAny<InstrumentPricePurgeScope>(), 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<InstrumentPricePurgeResult>.Failure(status, new ApiProblem { Status = (int)status, Code = code }));
        ctx.Services.AddSingleton(api.Object);
        var cut = RenderPurge(ctx, instrument, canPurgeFetched: false);
        cut.WaitForAssertion(() => Assert.Contains("This deletes 3 prices.", cut.Markup));

        cut.Find("input[inputmode=numeric]").Input("3");
        DeleteButton(cut).Click();

        cut.WaitForAssertion(() => Assert.Contains(message, cut.Markup));
    }

    // ── Price sources panel ──────────────────────────────────────────────────────────────────────

    private static InstrumentPriceSourceInfo Source(string key, string name, int max = 0, int today = 0) => new()
    {
        Key = key, DisplayName = name, Attribution = name, SupportedTypes = [InstrumentType.Stock, InstrumentType.Etf, InstrumentType.Fund],
        Configured = true, MaxRequestsPerDay = max, RequestsToday = today,
    };

    private static ExistingInstrumentPriceBinding Binding(string key, int priority, bool enabled = false) => new()
    {
        SourceKey = key, SourceSymbol = "EQNR.OL", Priority = priority, Enabled = enabled, SourceRegistered = true,
    };

    private static IRenderedComponent<ProvidersHost> RenderPanel(
        BunitContext ctx, Mock<IInstrumentPriceSourcesApiClient> api, InstrumentPriceSourcesStatus status,
        bool canEdit = true, ExistingInstrument? instrument = null)
    {
        ctx.Services.AddSingleton(api.Object);
        return ctx.Render<ProvidersHost>(p => p.AddChildContent<InstrumentPriceSourcesPanel>(d => d
            .Add(x => x.Instrument, instrument ?? InstrumentsPageTests.Instrument("EQNR"))
            .Add(x => x.Status, status)
            .Add(x => x.CanEdit, canEdit)));
    }

    private static Mock<IInstrumentPriceSourcesApiClient> Bindings(params ExistingInstrumentPriceBinding[] bindings)
    {
        var api = new Mock<IInstrumentPriceSourcesApiClient>();
        api.Setup(a => a.GetBindingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(bindings.ToList()));
        return api;
    }

    private static readonly InstrumentPriceSourcesStatus TwoSources = new()
    {
        Sources = [Source("eodhd", "EODHD"), Source("funds", "Fund NAV feed")],
        MaxBindingsPerInstrument = 3,
    };

    private static string[] Order(IRenderedComponent<ProvidersHost> cut) =>
        [.. cut.FindAll(".ins-src-name").Select(n => n.TextContent.Trim())];

    [Fact]
    public async Task Moving_a_source_reorders_the_list_announces_it_and_saves_the_whole_list_once()
    {
        await using var ctx = Context(out _);
        var api = Bindings(Binding("eodhd", 1), Binding("funds", 2));
        api.Setup(a => a.ReplaceBindingsAsync(It.IsAny<Guid>(), It.IsAny<InstrumentPriceBindingsUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new List<ExistingInstrumentPriceBinding>()));
        var cut = RenderPanel(ctx, api, TwoSources);
        cut.WaitForAssertion(() => Assert.Equal(["EODHD", "Fund NAV feed"], Order(cut)));

        // Move up is absent on the first item and Move down on the last — never disabled.
        Assert.Empty(cut.FindAll("button[aria-label='Move EODHD up']"));
        Assert.Empty(cut.FindAll("button[aria-label='Move Fund NAV feed down']"));

        cut.Find("button[aria-label='Move Fund NAV feed up']").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Fund NAV feed", "EODHD"], Order(cut)));
        cut.WaitForAssertion(() => Assert.Contains("Fund NAV feed moved to priority 1", cut.Markup));
        Assert.Contains("Unsaved changes to price sources", cut.Markup);

        cut.FindAll(".ins-src-bar button").Last().Click();

        cut.WaitForAssertion(() => api.Verify(a => a.ReplaceBindingsAsync(It.IsAny<Guid>(),
            It.Is<InstrumentPriceBindingsUpdate>(u => u.Bindings.Select(b => b.SourceKey).SequenceEqual(new[] { "funds", "eodhd" })),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task At_the_limit_add_source_is_replaced_by_the_servers_maximum()
    {
        await using var ctx = Context(out _);
        var status = TwoSources with { MaxBindingsPerInstrument = 2 };
        var cut = RenderPanel(ctx, Bindings(Binding("eodhd", 1), Binding("funds", 2)), status);

        cut.WaitForAssertion(() => Assert.Contains(InstrumentPriceSourcesPanel.MaximumText(2), cut.Markup));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Add source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Turning_a_source_on_asks_first_and_cancel_leaves_it_off()
    {
        await using var ctx = Context(out _);
        var cut = RenderPanel(ctx, Bindings(Binding("eodhd", 1)), TwoSources);
        cut.WaitForAssertion(() => Assert.Equal(["EODHD"], Order(cut)));

        cut.Find(".ins-src-ctl input[type=checkbox]").Change(true);

        var disclosure = EnablePriceSourceDialog.Disclosure("EODHD", "EQNR.OL");
        cut.WaitForAssertion(() => Assert.Contains(disclosure, cut.Markup));
        Assert.DoesNotContain("Unsaved changes to price sources", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain(disclosure, cut.Markup));
        Assert.DoesNotContain("Unsaved changes to price sources", cut.Markup);
    }

    [Fact]
    public async Task Turn_on_confirms_the_switch()
    {
        await using var ctx = Context(out _);
        var cut = RenderPanel(ctx, Bindings(Binding("eodhd", 1)), TwoSources);
        cut.WaitForAssertion(() => Assert.Equal(["EODHD"], Order(cut)));

        cut.Find(".ins-src-ctl input[type=checkbox]").Change(true);
        cut.WaitForAssertion(() => Assert.Contains("Turn on EODHD?", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Contains("Turn on", StringComparison.Ordinal) && !b.TextContent.Contains('?')).Click();

        cut.WaitForAssertion(() => Assert.Contains("Unsaved changes to price sources", cut.Markup));
        Assert.Contains(">Active<", cut.Markup);
    }

    [Fact]
    public async Task With_no_source_installed_the_panel_says_so_and_offers_nothing_to_add()
    {
        await using var ctx = Context(out _);
        var cut = RenderPanel(ctx, Bindings(), new InstrumentPriceSourcesStatus());

        cut.WaitForAssertion(() => Assert.Contains(InstrumentPriceSourcesPanel.NoSourcesInstalled, cut.Markup));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Add source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_settings_reader_without_update_sees_the_list_as_text()
    {
        await using var ctx = Context(out _);
        var cut = RenderPanel(ctx, Bindings(Binding("eodhd", 1), Binding("funds", 2)), TwoSources, canEdit: false);
        cut.WaitForAssertion(() => Assert.Equal(["EODHD", "Fund NAV feed"], Order(cut)));

        Assert.Empty(cut.FindAll(".ins-src-ctl"));
        Assert.Empty(cut.FindAll("input"));
        Assert.DoesNotContain("Add source", cut.Markup);
    }

    [Fact]
    public async Task An_archived_instrument_is_read_only_and_says_it_is_not_synced()
    {
        await using var ctx = Context(out _);
        var cut = RenderPanel(ctx, Bindings(Binding("eodhd", 1)), TwoSources,
            instrument: InstrumentsPageTests.Instrument("TEL", archived: true));

        cut.WaitForAssertion(() => Assert.Contains("Archived instruments are not synced.", cut.Markup));
        Assert.Empty(cut.FindAll(".ins-src-ctl"));
    }

    [Fact]
    public void A_sources_state_is_words_including_the_daily_limit()
    {
        var now = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Off", InstrumentPriceSourcesPanel.StateText(Source("e", "E"), enabled: false, registered: true, now));
        Assert.Equal(InstrumentPriceSourcesPanel.DailyLimitReached,
            InstrumentPriceSourcesPanel.StateText(Source("e", "E", max: 20, today: 20), enabled: true, registered: true, now));
        Assert.Equal("Active", InstrumentPriceSourcesPanel.StateText(Source("e", "E", max: 20, today: 19), enabled: true, registered: true, now));
        Assert.Equal("Not configured",
            InstrumentPriceSourcesPanel.StateText(Source("e", "E") with { Configured = false }, enabled: true, registered: true, now));
        Assert.Equal("Not installed", InstrumentPriceSourcesPanel.StateText(null, enabled: true, registered: false, now));
        Assert.StartsWith("Paused until",
            InstrumentPriceSourcesPanel.StateText(Source("e", "E") with { BackOffUntilUtc = now.AddHours(1), BackOffReason = JobFailureCategory.ProviderRateLimited },
                enabled: true, registered: true, now));
    }

    // ── Issue #447: the provider's symbol help, data-processing note, hourly limit and next try ─────────────────

    private static InstrumentPriceSourcesStatus Tiingo => new()
    {
        Sources =
        [
            Source("tiingo", "Tiingo") with
            {
                SymbolExample = "BRK-B",
                DataProcessingNote = "Tiingo is a US company. It handles these requests under its own terms.",
            },
        ],
        MaxBindingsPerInstrument = 3,
    };

    [Fact]
    public void The_symbol_help_uses_the_providers_own_example_and_falls_back_to_the_generic_one()
    {
        Assert.Equal("The symbol as Tiingo writes it, e.g. BRK-B", InstrumentPriceSourcesPanel.SymbolHelp(Tiingo.Sources[0]));
        Assert.Equal(InstrumentPriceSourcesPanel.GenericSymbolHelp, InstrumentPriceSourcesPanel.SymbolHelp(Source("e", "E")));
        Assert.Equal(InstrumentPriceSourcesPanel.GenericSymbolHelp, InstrumentPriceSourcesPanel.SymbolHelp(null));
    }

    [Fact]
    public void The_hourly_limit_is_a_state_with_the_hour_it_resumes_and_goes_once_it_has_passed()
    {
        var now = new DateTime(2026, 11, 9, 10, 20, 0, DateTimeKind.Utc);
        var capped = Source("tiingo", "Tiingo") with { MaxRequestsPerHour = 25, RequestsThisHour = 25, HourlyLimitResetsAtUtc = now.AddMinutes(40) };

        Assert.Equal($"Hourly request limit reached · resumes at {RunTime.Clock(now.AddMinutes(40))}",
            InstrumentPriceSourcesPanel.StateText(capped, enabled: true, registered: true, now));
        Assert.Equal("Active", InstrumentPriceSourcesPanel.StateText(capped, enabled: true, registered: true, now.AddHours(1)));
    }

    [Fact]
    public async Task The_symbol_field_carries_the_providers_example()
    {
        await using var ctx = Context(out _);
        var cut = RenderPanel(ctx, Bindings(Binding("tiingo", 1)), Tiingo);

        cut.WaitForAssertion(() => Assert.Contains("The symbol as Tiingo writes it, e.g. BRK-B", cut.Markup));
        Assert.DoesNotContain("e.g. EQNR.OL", cut.Markup);
    }

    [Fact]
    public async Task Turning_tiingo_on_adds_its_data_processing_note_as_plain_text()
    {
        await using var ctx = Context(out _);
        var cut = RenderPanel(ctx, Bindings(Binding("tiingo", 1)), Tiingo);
        cut.WaitForAssertion(() => Assert.Equal(["Tiingo"], Order(cut)));

        cut.Find(".ins-src-ctl input[type=checkbox]").Change(true);

        cut.WaitForAssertion(() => Assert.Contains(EnablePriceSourceDialog.Disclosure("Tiingo", "EQNR.OL"), cut.Markup));
        var paragraphs = cut.FindAll("p.ins-dialog-body").Select(p => p.TextContent.Trim()).ToList();
        Assert.Equal(2, paragraphs.Count);
        Assert.Equal(Tiingo.Sources[0].DataProcessingNote, paragraphs[1]);
    }

    [Fact]
    public async Task A_provider_without_a_note_shows_the_disclosure_alone()
    {
        await using var ctx = Context(out _);
        var cut = RenderPanel(ctx, Bindings(Binding("eodhd", 1)), TwoSources);
        cut.WaitForAssertion(() => Assert.Equal(["EODHD"], Order(cut)));

        cut.Find(".ins-src-ctl input[type=checkbox]").Change(true);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("p.ins-dialog-body")));
    }

    [Fact]
    public async Task A_source_skipped_until_tomorrow_says_when_it_is_asked_again()
    {
        await using var ctx = Context(out _);
        var skipped = Binding("tiingo", 1, enabled: true) with
        {
            LastFailure = JobFailureCategory.UnsupportedInput,
            LastFailureUtc = new DateTime(2026, 11, 9, 7, 0, 0, DateTimeKind.Utc),
            RetryOn = new DateOnly(2026, 11, 10),
        };
        var cut = RenderPanel(ctx, Bindings(skipped), Tiingo);

        cut.WaitForAssertion(() => Assert.Contains(InstrumentPriceSourcesPanel.RetryText(new DateOnly(2026, 11, 10)), cut.Markup));
        Assert.Contains("Next try", cut.Markup);
    }

    [Fact]
    public async Task A_data_processing_note_carrying_markup_renders_as_literal_text()
    {
        await using var ctx = Context(out _);
        const string hostile = "<b>bold</b> <img src=x onerror=alert(1)> & co";
        var status = Tiingo;
        status.Sources[0] = status.Sources[0] with { DataProcessingNote = hostile };
        var cut = RenderPanel(ctx, Bindings(Binding("tiingo", 1)), status);
        cut.WaitForAssertion(() => Assert.Equal(["Tiingo"], Order(cut)));

        cut.Find(".ins-src-ctl input[type=checkbox]").Change(true);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("p.ins-dialog-body").Count));
        var note = cut.FindAll("p.ins-dialog-body")[1];
        Assert.Empty(note.Children);
        Assert.Equal(hostile, note.TextContent.Trim());
        Assert.Empty(cut.FindAll("p.ins-dialog-body img"));
    }

    [Fact]
    public async Task Adding_a_source_turned_on_uses_its_symbol_help_and_its_note()
    {
        await using var ctx = Context(out _);
        var cut = RenderPanel(ctx, Bindings(), Tiingo);
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Add source", StringComparison.Ordinal)));

        cut.FindAll("button").First(b => b.TextContent.Contains("Add source", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("The symbol as Tiingo writes it, e.g. BRK-B", cut.Markup));
        cut.Find("input[id$='-add-symbol']").Input("BRK-B");
        cut.Find("span[id$='-add-on'] input[type=checkbox]").Change(true);
        cut.FindAll(".ins-src-add-actions button").Last().Click();

        cut.WaitForAssertion(() => Assert.Contains("Turn on Tiingo?", cut.Markup));
        var paragraphs = cut.FindAll("p.ins-dialog-body").Select(p => p.TextContent.Trim()).ToList();
        Assert.Equal(EnablePriceSourceDialog.Disclosure("Tiingo", "BRK-B"), paragraphs[0]);
        Assert.Equal(Tiingo.Sources[0].DataProcessingNote, paragraphs[1]);
    }

    [Fact]
    public async Task The_next_try_line_reads_exactly_and_hides_once_the_symbol_is_edited()
    {
        await using var ctx = Context(out _);
        var skipped = Binding("tiingo", 1, enabled: true) with
        {
            LastFailure = JobFailureCategory.UnsupportedInput,
            LastFailureUtc = new DateTime(2026, 11, 9, 7, 0, 0, DateTimeKind.Utc),
            RetryOn = new DateOnly(2026, 11, 10),
        };
        var cut = RenderPanel(ctx, Bindings(skipped), Tiingo);

        var expected = $"Not asked again until {new DateOnly(2026, 11, 10).ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture)}. "
            + "Change the symbol, or turn the source off and on, to try now.";
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".ins-src-facts dd"), dd => dd.TextContent.Trim() == expected));

        cut.Find(".ins-src-symfield input").Input("BRK-B");

        cut.WaitForAssertion(() => Assert.DoesNotContain("Next try", cut.Markup));
    }

    [Fact]
    public void The_symbol_help_falls_back_to_the_key_and_the_hourly_state_ends_at_its_reset()
    {
        Assert.Equal("The symbol as tiingo writes it, e.g. BRK-B",
            InstrumentPriceSourcesPanel.SymbolHelp(Source("tiingo", " ") with { SymbolExample = "BRK-B" }));

        var resets = new DateTime(2026, 11, 9, 11, 0, 0, DateTimeKind.Utc);
        var capped = Source("tiingo", "Tiingo") with { MaxRequestsPerHour = 25, RequestsThisHour = 25, HourlyLimitResetsAtUtc = resets };
        Assert.Equal("Active", InstrumentPriceSourcesPanel.StateText(capped, enabled: true, registered: true, resets));
        Assert.StartsWith("Hourly request limit reached",
            InstrumentPriceSourcesPanel.StateText(capped, enabled: true, registered: true, resets.AddTicks(-1)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_symbol_follows_the_shared_rule()
    {
        Assert.Null(InstrumentPriceSourcesPanel.ValidateSymbol("EQNR.OL"));
        Assert.Equal(InstrumentPriceSourcesPanel.SymbolFormat, InstrumentPriceSourcesPanel.ValidateSymbol("EQNR..OL"));
        Assert.Equal(InstrumentPriceSourcesPanel.SymbolFormat, InstrumentPriceSourcesPanel.ValidateSymbol("-EQNR"));
        Assert.Equal(InstrumentPriceSourcesPanel.SymbolRequired, InstrumentPriceSourcesPanel.ValidateSymbol("  "));
    }
}
