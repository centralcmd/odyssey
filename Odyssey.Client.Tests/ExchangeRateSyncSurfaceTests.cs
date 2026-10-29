using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Pages.Finance.ExchangeRateSync;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The exchange-rate sync frontend (issue #351): the eight vocabularies, the status panel's polling,
/// the currency rows' save behaviour, and the source rules the spec's acceptance criteria name.
/// </summary>
[Collection(ExchangeRateSyncCollection.Name)]
public partial class ExchangeRateSyncSurfaceTests : IDisposable
{
    public void Dispose()
    {
        CurrenciesCard.InteractiveCheck = static () => OperatingSystem.IsBrowser();
        ExchangeRatesCard.InteractiveCheck = static () => OperatingSystem.IsBrowser();
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 40, 0, TimeSpan.Zero);

    // ── Vocabularies (AC 9, 15) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_vocabulary_covers_its_backend_enum()
    {
        Assert.Equal(Enum.GetValues<ExchangeRateStatus>().ToHashSet(), OdsTypeRegistries.ExchangeRateStatuses.Keys.ToHashSet());
        Assert.Equal(Enum.GetValues<ExchangeRateSyncState>().ToHashSet(), OdsTypeRegistries.ExchangeRateSyncStates.Keys.ToHashSet());
        Assert.Equal(Enum.GetValues<ExchangeRateSyncRunStatus>().ToHashSet(), OdsTypeRegistries.ExchangeRateSyncRunStatuses.Keys.ToHashSet());
        Assert.Equal(Enum.GetValues<ExchangeRateSyncTrigger>().ToHashSet(), OdsTypeRegistries.ExchangeRateSyncTriggers.Keys.ToHashSet());
        Assert.Equal(Enum.GetValues<OdsTypeRegistries.ExchangeRateBackfillState>().ToHashSet(), OdsTypeRegistries.ExchangeRateBackfillStates.Keys.ToHashSet());

        // Every error kind is labelled except Internal, which reads as the fallback by design.
        Assert.Equal(
            Enum.GetValues<ExchangeRateSyncErrorKind>().Where(k => k != ExchangeRateSyncErrorKind.Internal).ToHashSet(),
            OdsTypeRegistries.ExchangeRateSyncErrors.Keys.ToHashSet());

        // A source is Manual or one of the providers.
        Assert.Equal(
            Enum.GetNames<ExchangeRateProvider>().Append("Manual").ToHashSet(),
            OdsTypeRegistries.ExchangeRateSources.Keys.ToHashSet());
    }

    [Fact]
    public void An_unknown_value_never_renders_its_raw_key()
    {
        Assert.Equal("Unknown", OdsTypeRegistries.ExchangeRateSyncStateOf((ExchangeRateSyncState)99).Label);
        Assert.Equal("Unknown", OdsTypeRegistries.ExchangeRateSyncRunStatusOf((ExchangeRateSyncRunStatus)99).Label);
        Assert.Equal("Unknown", OdsTypeRegistries.ExchangeRateStatusOf((ExchangeRateStatus)99).Label);
        Assert.Equal("Sync error", OdsTypeRegistries.ExchangeRateSyncErrorOf((ExchangeRateSyncErrorKind)99));
        Assert.Equal("Sync error", OdsTypeRegistries.ExchangeRateSyncErrorOf(ExchangeRateSyncErrorKind.Internal));
        Assert.Equal("Unknown", OdsTypeRegistries.ExchangeRateSourceOf(new ExistingExchangeRate
        {
            ExchangeRateId = Guid.NewGuid(), FromCurrencyCode = "USD", ToCurrencyCode = "EUR", Rate = 1m,
            AsOf = Now.UtcDateTime, CreatedAt = Now.UtcDateTime, Source = ExchangeRateSource.Fetched, Provider = (ExchangeRateProvider)99,
        }).Label);
    }

    // ── Source rules (AC 8, 13) ──────────────────────────────────────────────────────────────────

    // The sync folder plus the existing surfaces it changed — exchange rates, and Currencies, which hosts
    // the sync — markup and code-behind.
    // Settings.razor.cs is deliberately outside: its catalogue descriptions are static copy authored
    // with the design system, for every setting, and carry no runtime value. So is OdsTypeRegistries:
    // its source chip and run-error labels name the provider on purpose, because they are design-system
    // vocabulary keyed per ExchangeRateProvider member (a second provider is a second key, not a changed
    // string), mirrored from the artifact and parity-checked by OdsTypeRegistriesTests.
    private static readonly string[] ChangedSurfaces =
    [
        "ExchangeRatesCard.razor", "ExchangeRatesCard.razor.cs", "CreateExchangeRateDialog.razor", "CreateExchangeRateDialog.razor.cs",
        "CurrenciesCard.razor", "CurrenciesCard.Sync.cs",
    ];

    private static IEnumerable<string> NewSurfaceFiles()
    {
        var finance = Path.Combine(ClientSource.Root, "Pages", "Finance");
        return Directory.EnumerateFiles(Path.Combine(finance, "ExchangeRateSync"))
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Concat(ChangedSurfaces.Select(name => Path.Combine(finance, name)).Where(File.Exists));
    }

    [Fact]
    public void The_lint_scope_reaches_both_changed_surfaces_and_their_code_behind()
    {
        var names = NewSurfaceFiles().Select(Path.GetFileName).ToHashSet();

        Assert.Contains("ExchangeRatesCard.razor.cs", names);
        Assert.Contains("CurrenciesCard.Sync.cs", names);
        Assert.Contains("ExchangeRateSyncRunHistory.razor", names);
        Assert.Contains("ExchangeRateSyncStatusPanel.razor", names);
    }

    [Fact]
    public void No_provider_fact_is_hard_coded_in_the_new_surfaces()
    {
        var forbidden = new[] { "\"NOK\"", "Norges Bank", "16:00", "Europe/Oslo" };
        var offenders = NewSurfaceFiles()
            .SelectMany(file => forbidden
                .Where(term => File.ReadAllText(file).Contains(term, StringComparison.Ordinal))
                .Select(term => $"{ClientSource.Relative(file)}: {term}"))
            .ToList();

        Assert.True(NewSurfaceFiles().Any(), "The lint found no files to scan.");
        Assert.True(offenders.Count == 0, "Provider facts belong to the server's responses:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void No_server_string_is_rendered_as_markup()
    {
        var offenders = NewSurfaceFiles().Where(file => File.ReadAllText(file).Contains("MarkupString", StringComparison.Ordinal)).ToList();

        Assert.Empty(offenders);
    }

    // ── Status panel (AC 7, 8, 9) ────────────────────────────────────────────────────────────────

    private static ExchangeRateSyncStatus Running(DateTime startedAt) => ExchangeRateSyncFakes.IdleStatus() with
    {
        State = ExchangeRateSyncState.Running,
        EnabledCount = 2,
        CurrentRun = new ExchangeRateSyncCurrentRun { Id = Guid.NewGuid(), Trigger = ExchangeRateSyncTrigger.Manual, StartedAtUtc = startedAt },
    };

    private static ExchangeRateSyncStatus Scheduled(int rowsInserted = 2500) => ExchangeRateSyncFakes.IdleStatus() with
    {
        State = ExchangeRateSyncState.Scheduled,
        EnabledCount = 2,
        NextRunAtUtc = Now.UtcDateTime.AddHours(8),
        LastRun = new ExistingExchangeRateSyncRun
        {
            Id = Guid.NewGuid(), Status = ExchangeRateSyncRunStatus.Succeeded, Trigger = ExchangeRateSyncTrigger.Manual,
            StartedAtUtc = Now.UtcDateTime, FinishedAtUtc = Now.UtcDateTime, RowsInserted = rowsInserted,
        },
    };

    private IRenderedComponent<MudDialogProvider>? _dialogs;
    private FakeTimeProvider? _time;

    /// <summary>Where an <c>OdsModal</c> renders — an inline MudDialog draws through the provider.</summary>
    private IRenderedComponent<MudDialogProvider> Dialogs => _dialogs!;

    private (BunitContext Ctx, FakeTimeProvider Time) NewContext(IExchangeRateSyncApiClient sync)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var time = new FakeTimeProvider(Now);
        ctx.Services.AddSingleton<TimeProvider>(time);
        ctx.Services.AddSingleton(sync);
        _time = time;
        return (ctx, time);
    }

    [Fact]
    public async Task Polling_reads_every_interval_while_running_and_stops_when_the_run_ends()
    {
        var sync = new Mock<IExchangeRateSyncApiClient>();
        var reads = new Queue<ExchangeRateSyncStatus>([Running(Now.UtcDateTime), Running(Now.UtcDateTime), Scheduled()]);
        sync.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ApiResult<ExchangeRateSyncStatus>.Success(reads.Count > 1 ? reads.Dequeue() : reads.Peek(), HttpStatusCode.OK));
        var (ctx, time) = NewContext(sync.Object);
        await using var _ = ctx;
        var announced = new List<string>();
        var finished = 0;

        var cut = ctx.Render<ExchangeRateSyncStatusPanel>(p => p
            .Add(x => x.OnAnnounce, (string m) => announced.Add(m))
            .Add(x => x.OnRunFinished, (ExchangeRateSyncStatus _) => finished++));

        Assert.True(cut.Instance.IsPolling);
        sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);

        time.Advance(ExchangeRateSyncStatusPanel.PollInterval);
        cut.WaitForAssertion(() => sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(2)));
        Assert.Empty(announced);

        time.Advance(ExchangeRateSyncStatusPanel.PollInterval);
        cut.WaitForAssertion(() => Assert.False(cut.Instance.IsPolling));
        Assert.Equal(1, finished);
        Assert.Equal(["Run finished: 2,500 rates added"], announced);

        // Stopped: further time reads nothing.
        time.Advance(ExchangeRateSyncStatusPanel.PollInterval * 3);
        sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Polling_stops_as_stale_at_the_run_timeout_counted_from_the_runs_start()
    {
        var sync = new Mock<IExchangeRateSyncApiClient>();
        // Started long enough ago that the next poll is past startedAt + runTimeoutMinutes.
        var run = Running(Now.UtcDateTime.AddMinutes(-60).AddSeconds(2));
        sync.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExchangeRateSyncStatus>.Success(run, HttpStatusCode.OK));
        var (ctx, time) = NewContext(sync.Object);
        await using var _ = ctx;
        var announced = new List<string>();

        var cut = ctx.Render<ExchangeRateSyncStatusPanel>(p => p.Add(x => x.OnAnnounce, (string m) => announced.Add(m)));
        time.Advance(ExchangeRateSyncStatusPanel.PollInterval);

        cut.WaitForAssertion(() => Assert.False(cut.Instance.IsPolling));
        Assert.Equal(ExchangeRateSyncStatusPanel.StaleReason.Cap, cut.Instance.Stale);
        Assert.Contains("Status not current", announced);
        Assert.Contains("Status not current — the run is taking longer than expected", cut.Markup);
    }

    [Fact]
    public async Task Two_failed_reads_in_a_row_stop_polling_as_stale()
    {
        var sync = new Mock<IExchangeRateSyncApiClient>();
        var first = true;
        sync.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (!first)
                    return ApiResult<ExchangeRateSyncStatus>.Failure(HttpStatusCode.ServiceUnavailable, new ApiProblem { Title = "down" });
                first = false;
                return ApiResult<ExchangeRateSyncStatus>.Success(Running(Now.UtcDateTime), HttpStatusCode.OK);
            });
        var (ctx, time) = NewContext(sync.Object);
        await using var _ = ctx;

        var cut = ctx.Render<ExchangeRateSyncStatusPanel>();
        time.Advance(ExchangeRateSyncStatusPanel.PollInterval);
        cut.WaitForAssertion(() => sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(2)));
        Assert.True(cut.Instance.IsPolling);

        time.Advance(ExchangeRateSyncStatusPanel.PollInterval);
        cut.WaitForAssertion(() => Assert.False(cut.Instance.IsPolling));
        Assert.Equal(ExchangeRateSyncStatusPanel.StaleReason.Errors, cut.Instance.Stale);
    }

    [Fact]
    public async Task The_panel_has_no_buttons_and_notes_when_run_now_can_start_again()
    {
        var sync = ExchangeRateSyncFakes.Client(Scheduled() with { ManualRunAvailableAtUtc = Now.UtcDateTime.AddMinutes(5) });
        var (ctx, _) = NewContext(sync.Object);
        await using var __ = ctx;

        var cut = ctx.Render<ExchangeRateSyncStatusPanel>();

        // Its actions are the page header's menu items; the note says why Run now is absent there.
        Assert.Empty(cut.FindAll("button"));
        Assert.StartsWith("You can start another run at", cut.Find(".xrs-status-note").TextContent.Replace("schedule", "").Trim());
        await cut.InvokeAsync(cut.Instance.RunNowAsync);
        sync.Verify(c => c.RunNowAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_run_in_progress_needs_no_note_beside_its_running_row()
    {
        var (ctx, _) = NewContext(ExchangeRateSyncFakes.Client(Running(Now.UtcDateTime)).Object);
        await using var __ = ctx;

        var cut = ctx.Render<ExchangeRateSyncStatusPanel>();

        Assert.Equal("A run is in progress", cut.Instance.RunNowReason);
        Assert.Empty(cut.FindAll(".xrs-status-note"));
    }

    [Fact]
    public async Task The_last_finished_run_shows_beside_any_state_with_its_error_label()
    {
        var status = Scheduled() with
        {
            LastRun = new ExistingExchangeRateSyncRun
            {
                Id = Guid.NewGuid(), Status = ExchangeRateSyncRunStatus.Failed, ErrorKind = ExchangeRateSyncErrorKind.ProviderUnavailable,
                StartedAtUtc = Now.UtcDateTime.AddHours(-2), FinishedAtUtc = Now.UtcDateTime.AddHours(-2),
            },
        };
        var (ctx, _) = NewContext(ExchangeRateSyncFakes.Client(status).Object);
        await using var __ = ctx;

        var cut = ctx.Render<ExchangeRateSyncStatusPanel>();

        Assert.Contains("Scheduled", cut.Markup);
        // The result in its tone as text, and the run's time with its distance in brackets.
        Assert.Contains("xrs-tone-error", cut.Find(".xrs-rows").InnerHtml);
        Assert.Contains("Failed", cut.Markup);
        Assert.Contains("(2 hours ago)", cut.Markup);
        // The state and the result are folded into their rows, and the source carries no label of its own.
        var rows = cut.FindAll(".xrs-row").ToDictionary(
            r => r.QuerySelector(".xrs-row-label")!.TextContent.Trim(),
            r => System.Text.RegularExpressions.Regex.Replace(r.QuerySelector(".xrs-row-value")!.TextContent, @"\s+", " ").Trim());
        Assert.Equal(["Next run", "Last run", "Source"], rows.Keys);
        Assert.StartsWith("Scheduled at ", rows["Next run"]);
        Assert.StartsWith("Failed at ", rows["Last run"]);
        Assert.Equal(status.Attribution, rows["Source"]);
        Assert.Empty(cut.FindAll(".odc-chip, .mud-chip"));
        Assert.Contains(OdsTypeRegistries.ExchangeRateSyncErrorOf(ExchangeRateSyncErrorKind.ProviderUnavailable), cut.Markup);
        Assert.Contains(status.Attribution, cut.Markup);
    }

    // ── The currency rows (AC 3, 4) ──────────────────────────────────────────────────────────────

    private static ExistingExchangeRateSyncCurrency Currency(string code, bool enabled = false, bool locked = false, bool? supported = true) => new()
    {
        CurrencyCode = code, Name = code + " name", Enabled = enabled, Locked = locked, Supported = supported,
        // A synced fixture is caught up unless a test says otherwise, so it reads Synced.
        BackfillComplete = enabled || locked,
        EffectiveHistoryStartDate = new DateOnly(2026, 1, 1), FirstAvailableDate = new DateOnly(1999, 1, 4),
    };

    private IRenderedComponent<MudPopoverProvider>? _popover;

    [Fact]
    public void The_poll_interval_is_the_specs_five_seconds() =>
        Assert.Equal(TimeSpan.FromSeconds(5), ExchangeRateSyncStatusPanel.PollInterval);

    // ── /exchange-rates (AC 10, 11, 12) ──────────────────────────────────────────────────────────

    private static ExistingExchangeRate Rate(ExchangeRateSource source) => new()
    {
        ExchangeRateId = Guid.NewGuid(), FromCurrencyCode = "USD", ToCurrencyCode = "BASE", Rate = 10m,
        AsOf = Now.UtcDateTime.AddDays(-1), CreatedAt = Now.UtcDateTime.AddDays(-1), Source = source,
        Status = ExchangeRateStatus.Current,
        Provider = source == ExchangeRateSource.Fetched ? ExchangeRateProvider.NorgesBank : null,
    };

    private IRenderedComponent<ExchangeRatesCard> RenderRatesCard(BunitContext ctx, IReadOnlyList<string> claims, params ExistingExchangeRate[] rows)
    {
        var rates = new Mock<IExchangeRatesApiClient>();
        rates.Setup(c => c.GetSummaryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExchangeRateSummary>.Success(new ExchangeRateSummary
            {
                TotalCount = 1234, LatestAsOfUtc = Now.UtcDateTime, Attribution = "Provider (licence)",
                ByStatus = new ExchangeRateStatusCounts { Current = 4, Upcoming = 1, Historical = 1229 },
                ToCurrencyCodes = ["BASE"], ProviderDisplayName = "Provider",
            }, HttpStatusCode.OK));
        rates.Setup(c => c.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingExchangeRate>>.Success(
                new PagedResult<ExistingExchangeRate> { Items = rows, TotalCount = rows.Length }, HttpStatusCode.OK));
        ctx.Services.AddSingleton(rates.Object);
        ctx.Services.AddSingleton(new Mock<IClipboardService>().Object);
        ctx.Services.AddSingleton(new Mock<IPageStateService>().Object);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedInWith(claims));
        ExchangeRatesCard.InteractiveCheck = static () => true;
        ctx.Render<MudPopoverProvider>();
        _dialogs = ctx.Render<MudDialogProvider>();
        var cut = ctx.Render<ExchangeRatesCard>();
        cut.WaitForAssertion(() => Assert.Contains("1,234 rates", cut.Markup));
        return cut;
    }

    private static OdsRecordActionContext NoopActions(string menuTriggerId = "trigger") => new() { Toggle = () => { }, StartEdit = () => { }, Remove = () => { }, MenuTriggerId = menuTriggerId };

    [Theory]
    [InlineData(ExchangeRateStatus.Current, OdsChipTone.Income, "background:var(--finance-income-soft);color:var(--finance-income);")]
    [InlineData(ExchangeRateStatus.Upcoming, OdsChipTone.Info, "background:var(--info-soft);color:var(--info-text);")]
    [InlineData(ExchangeRateStatus.Historical, OdsChipTone.Outline,
        "background:color-mix(insrgb,var(--mud-palette-text-secondary)var(--tint-soft),transparent);color:var(--mud-palette-text-secondary);")]
    public async Task A_rates_status_colours_its_chip_and_its_rows_mark_alike(ExchangeRateStatus status, OdsChipTone tone, string markColours)
    {
        var (ctx, _) = NewContext(ExchangeRateSyncFakes.Client().Object);
        await using var __ = ctx;

        var cut = RenderRatesCard(ctx, [PermissionClaims.ExchangeRatesRead], Rate(ExchangeRateSource.Manual) with { Status = status });

        Assert.Equal(tone, OdsTypeRegistries.ExchangeRateStatusOf(status).Tone);
        var style = cut.FindComponents<OdsAvatar>().Single().Find(".odc-avatar, .mud-avatar").GetAttribute("style") ?? "";
        Assert.Contains(markColours, style.Replace(" ", ""));
    }

    [Fact]
    public async Task A_fetched_rate_has_neither_edit_nor_delete_whatever_the_claims()
    {
        var (ctx, _) = NewContext(ExchangeRateSyncFakes.Client().Object);
        await using var __ = ctx;
        var fetched = Rate(ExchangeRateSource.Fetched);
        var manual = Rate(ExchangeRateSource.Manual);

        var cut = RenderRatesCard(ctx,
            [PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesUpdate, PermissionClaims.ExchangeRatesDelete, PermissionClaims.SystemSettingsUpdate],
            fetched, manual);

        var fetchedItems = cut.Instance.BuildActions(fetched, NoopActions()).Select(i => i.Label).ToList();
        var manualItems = cut.Instance.BuildActions(manual, NoopActions()).Select(i => i.Label).ToList();
        Assert.DoesNotContain("Edit", fetchedItems);
        Assert.DoesNotContain(fetchedItems, label => label?.StartsWith("Delete", StringComparison.Ordinal) == true);
        Assert.Contains("Edit", manualItems);
        Assert.Contains("Delete", manualItems);
    }

    [Fact]
    public async Task The_header_reads_the_summary_and_the_rows_show_their_source()
    {
        var (ctx, _) = NewContext(ExchangeRateSyncFakes.Client().Object);
        await using var __ = ctx;

        var cut = RenderRatesCard(ctx, [PermissionClaims.ExchangeRatesRead], Rate(ExchangeRateSource.Fetched), Rate(ExchangeRateSource.Manual));

        Assert.Contains("1,234 rates · latest", cut.Markup);
        Assert.Contains("· Source: Provider (licence)", cut.Markup);
        Assert.Contains(OdsTypeRegistries.ExchangeRateSources[nameof(ExchangeRateProvider.NorgesBank)].Label, cut.Markup);
        Assert.Contains(OdsTypeRegistries.ExchangeRateSources["Manual"].Label, cut.Markup);
    }

    // ── Settings date row (AC 1) ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2020-03-01")]
    [InlineData("1980-01-01")]
    public void A_stored_date_round_trips_as_invariant_text_under_any_culture(string stored)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal(stored, Odyssey.Client.Pages.Settings.FormatStoredDate(Odyssey.Client.Pages.Settings.ParseStoredDate(stored)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("01/03/2020")]
    public void Empty_or_malformed_stored_text_reads_as_no_date_and_no_date_writes_empty(string stored)
    {
        Assert.Null(Odyssey.Client.Pages.Settings.ParseStoredDate(stored));
        Assert.Equal(string.Empty, Odyssey.Client.Pages.Settings.FormatStoredDate(null));
    }
}
