using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Client.Auth;
using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class TaxStatementsCard
{
    // ── Data ────────────────────────────────────────────────────────────────
    private List<ExistingTaxStatement> _statements = [];
    // Server-computed rollup backing the header count + the year-over-year overview charts (issue
    // #372): a per-year projection of the declared figures, not the statements themselves.
    private TaxStatementSummary? _summary;
    private readonly Dictionary<Guid, TaxStatementReport> _reports = new();
    private List<ExistingCurrency> _currencies = [];
    private Dictionary<string, ExistingCurrency> _currenciesByCode = new(StringComparer.OrdinalIgnoreCase);
    private List<ExistingTransactionTag> _tags = [];
    private Dictionary<Guid, string> _tagNames = new();
    private IReadOnlyList<OdsOption> _tagOptions = [];

    // Per-statement problems (derived-net-worth pending / off-currency excluded).
    private Dictionary<Guid, List<TaxProblem>> _problems = new();
    private Guid? _flashId; // one-shot attention ring after a header-rollup jump

    // Card-list windowing (OdsInfiniteList): "Load N at a time" batch size.
    private int _batch = OdsPageSizes.Batch[0];

    // ── UI state ───────────────────────────────────────────────────────────
    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private string _announce = "";
    private Guid? _expandedId;
    private Guid _filesRefreshToken = Guid.NewGuid();

    // ── Persisted page state (header sections + filters) ─────────────────────
    private const string PageStateKey = "tax-statements-page";
    private bool _problemsOpen = true;
    private bool _overviewOpen = true;
    private bool _searchOpen = true;

    private string _searchString = string.Empty;
    private IReadOnlyCollection<string> _statusFilter = [];

    // The list is server-filtered, so an empty result only means "first run" when nothing is filtering it.
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_searchString) || _statusFilter.Count > 0;

    private static readonly IReadOnlyList<OdsOption> _statusOptions =
        [new("New", "New"), new("Approved", "Approved"), new("Flagged", "Flagged"), new("Archived", "Archived")];

    // ── Sort (§6.10) — toolbar OdsSortSelect is the sole sort surface (no headers).
    // Default: Fiscal year, most recent first. ──
    private static readonly OdsTableSort DefaultSort = new("fiscalYear", OdsSortDirection.Desc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<ExistingTaxStatement>> _sortFields =
    [
        new() { Key = "fiscalYear", Label = "Fiscal year", Type = OdsSortType.Number, SortValue = s => s.FiscalYear },
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = s => s.Name.ToLowerInvariant() },
        new() { Key = "status", Label = "Status", Type = OdsSortType.Status, SortValue = s => (int)s.Status },
    ];

    // ── Permissions ──────────────────────────────────────────────────────────
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canDownloadFiles;
    private bool _canUploadFiles;

    // ── Computed ──────────────────────────────────────────────────────────────
    // Header count/latest come from the server rollup (issue #372), so they span the whole set rather
    // than the server-filtered display list.
    private int ActiveCount => _summary?.ActiveCount ?? 0;


    // ── Lifecycle ──────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        await LoadPermissionsAsync();
        await Task.WhenAll(GetStatements(), LoadSummary(), LoadCurrencies(), LoadTags());
        await LoadReports();
    }

    // ── Page-state persistence ─────────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<TaxPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(TaxPageState state)
    {
        _problemsOpen = state.ProblemsOpen;
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _searchString = state.Search ?? string.Empty;
        _statusFilter = _statusOptions.KnownValues(state.StatusFilter);
        _sort = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private TaxPageState BuildPageState() => new()
    {
        ProblemsOpen = _problemsOpen,
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _searchString,
        StatusFilter = [.. _statusFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnProblemsToggled(bool open) { _problemsOpen = open; PersistPageState(); }
    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _searchString = value ?? string.Empty; PersistPageState(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await GetStatements(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await GetStatements(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private async Task ClearFilters()
    {
        _searchString = string.Empty;
        _statusFilter = [];
        PersistPageState();
        await GetStatements();
    }

    private sealed class TaxPageState
    {
        public bool ProblemsOpen { get; set; } = true;
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[0];
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.TaxesCreate);
        _canUpdate = user.HasPermission(PermissionClaims.TaxesUpdate);
        _canDelete = user.HasPermission(PermissionClaims.TaxesDelete);
        _canDownloadFiles = user.HasPermission(PermissionClaims.FilesRead);
        _canUploadFiles = user.HasPermission(PermissionClaims.FilesCreate)
                       && user.HasPermission(PermissionClaims.TaxesUpdate);
    }

    // Server-side fetch (issue #277): name search + status filter + sort applied by the API.
    private async Task GetStatements()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        var result = await TaxStatements.ListAsync(
            page: 1, pageSize: PagedQuery.SizeAll,
            search: _searchString,
            statuses: _statusFilter,
            sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

        var load = result.PagedOrToast(Snackbar, "tax statements");
        if (load.IsSuccess)
        {
            _statements = [.. load.Items];
            _loadError = false;
            _announce = _statements.Count == 0 ? "No tax statements match your filters."
                : $"Showing {_statements.Count} tax statement{(_statements.Count == 1 ? "" : "s")}.";
        }
        else
        {
            _loadError = true;
            _announce = "Couldn't load tax statements.";
        }

        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    // Unfiltered rollup for the overview charts + header count (issue #372).
    private async Task LoadSummary()
    {
        _summary = await TaxStatements.GetSummaryAsync();
        StateHasChanged();
    }

    // A create/edit/status/delete can change the overview → refresh both the rollup + display.
    private async Task OnStatementChanged()
    {
        await LoadSummary();
        await GetStatements();
    }

    private async Task LoadCurrencies()
    {
        _currencies = [.. await ReferenceData.ActiveCurrenciesAsync()];
        _currenciesByCode = _currencies.ToDictionary(c => c.CurrencyCode, StringComparer.OrdinalIgnoreCase);
    }

    private async Task LoadTags()
    {
        var tags = await ReferenceData.TransactionTagsAsync();
        _tags = tags.Where(t => t.Archived is null).OrderBy(t => t.Name, StringComparer.CurrentCulture).ToList();
        _tagNames = tags.ToDictionary(t => t.TransactionTagId, t => t.Name);
        _tagOptions = [.. _tags.Select(t => new OdsOption(t.TransactionTagId.ToString(), t.Name))];
    }

    // Per-statement reconciliation reports (derived figures + variances). Fetched in
    // parallel — one statement per fiscal year, so the fan-out stays small — and used
    // for the header rollup, the per-row problem chip, and the expanded reconciliation.
    private async Task LoadReports()
    {
        _reports.Clear();
        var tasks = _statements.Select(async s =>
        {
            // Silent by design: a statement without a report just renders no reconciliation panel.
            var result = await TaxStatements.GetReportAsync(s.TaxStatementId);
            return (s.TaxStatementId, result.Value, result.Error);
        });

        foreach (var (id, report, _) in await Task.WhenAll(tasks))
        {
            if (report is not null)
                _reports[id] = report;
        }

        BuildProblems();
        StateHasChanged();
    }

    private void BuildProblems()
    {
        _problems = new();
        foreach (var s in _statements)
        {
            if (s.Archived is not null || !_reports.TryGetValue(s.TaxStatementId, out var report))
                continue;

            var list = new List<TaxProblem>();
            if (!report.Derived.Available)
            {
                list.Add(new TaxProblem(
                    OdsSeverity.Info, "Balances pending",
                    "Account balances not synced",
                    $"{s.Name}. Derived net worth is unavailable until account balances are computed for this period.",
                    "Odyssey derives net worth from your account balances, which haven't been computed for this period yet — so the net-worth reconciliation is pending. Advance tax and actual income still derive from tagged transactions.",
                    "Open accounts", "accounts"));
            }
            if (report.ExcludedTransactionCount > 0)
            {
                var n = report.ExcludedTransactionCount;
                var parts = string.Join(" · ", report.ExcludedCurrencies.Select(kv => $"{kv.Value} {kv.Key}"));
                list.Add(new TaxProblem(
                    OdsSeverity.Warning, "Off-currency",
                    "Off-currency transactions excluded",
                    $"{s.Name}. {n} off-currency transaction{(n == 1 ? "" : "s")} {(n == 1 ? "is" : "are")} left out of the derived sums.",
                    $"Derived advance tax and actual income only count {s.BaseCurrencyCode} transactions. {n} off-currency transaction{(n == 1 ? "" : "s")} ({parts}) {(n == 1 ? "is" : "are")} excluded — add today's exchange rates to fold them in.",
                    "Set exchange rates", "exchange-rates"));
            }
            if (list.Count > 0)
                _problems[s.TaxStatementId] = list;
        }
    }

    // Header rollup rows — one per affected statement, tinted by its highest severity.
    private List<PageHeaderProblem> HeaderProblems =>
        _statements
            .Where(s => _problems.ContainsKey(s.TaxStatementId))
            .Select(s =>
            {
                var top = _problems[s.TaxStatementId].MaxBy(p => (int)Sev(p.Severity))!;
                return new PageHeaderProblem
                {
                    Severity = ToHeaderSeverity(top.Severity),
                    Lead = s.Name,
                    Message = top.Summary[(top.Summary.IndexOf('.') + 2)..],
                    OnView = EventCallback.Factory.Create(this, () => JumpTo(s.TaxStatementId)),
                };
            })
            .ToList();

    private async Task JumpTo(Guid id)
    {
        _expandedId = id;
        _flashId = id;
        StateHasChanged();

        try
        {
            await ScrollManager.ScrollIntoViewAsync($"#tax-{id}", ScrollBehavior.Smooth);
        }
        catch (Exception)
        {
            // Best-effort scroll; the record is already expanded.
        }

        await Task.Delay(OdsTiming.RowFlashMs);
        if (_flashId == id)
        {
            _flashId = null;
            StateHasChanged();
        }
    }

    private void GoTo(string target) => NavigationManager.NavigateTo(target);

    private bool IsExpanded(Guid id) => _expandedId == id;

    private void ToggleExpand(Guid id)
    {
        _expandedId = _expandedId == id ? null : id;
    }

    // ── Create ──────────────────────────────────────────────────────────────
    private bool _createOpen;
    private Guid _createKey;

    private void AddClicked()
    {
        if (!_canCreate) return;
        _createKey = Guid.NewGuid();
        _createOpen = true;
    }

    private async Task OnStatementCreated()
    {
        await OnStatementChanged();
        await LoadReports();
    }

    // ── Upload ──────────────────────────────────────────────────────────────
    private ExistingTaxStatement? _uploadStatement;
    private Guid _uploadKey;
    private bool _uploadOpen;

    private void AddFile(ExistingTaxStatement s)
    {
        if (!_canUploadFiles) return;
        _uploadStatement = s;
        _uploadKey = Guid.NewGuid();
        _uploadOpen = true;
    }

    private async Task OnFilesChanged()
    {
        // Expand the affected statement and force its files section to reload by changing its @key.
        if (_uploadStatement is not null)
            _expandedId = _uploadStatement.TaxStatementId;
        _filesRefreshToken = Guid.NewGuid();
        await GetStatements();
    }

    // ── Inline edit ───────────────────────────────────────────────────────────
    private ExistingTaxStatement? _editStatement;
    private Guid _editStatementKey;
    private bool _editStatementOpen;

    private void EditClicked(ExistingTaxStatement s)
    {
        if (!_canUpdate)
            return;

        _editStatement = s;
        _editStatementKey = Guid.NewGuid();
        _editStatementOpen = true;
    }

    private async Task OnStatementEdited()
    {
        await OnStatementChanged();
        await LoadReports();
    }

    // ── Status + lifecycle actions ───────────────────────────────────────────
    private async Task SetStatus(ExistingTaxStatement s, TaxStatementStatus status, string? comment)
    {
        if (!_canUpdate) return;
        var body = new UpdateTaxStatementStatus { Status = status, StatusComment = comment };
        if ((await TaxStatements.UpdateStatusAsync(s.TaxStatementId, body)).Toast(Snackbar, "Update failed",
                $"Marked {status.ToString().ToLowerInvariant()}."))
        {
            s.Status = status;
            s.StatusComment = comment;
            s.StatusChangedAt = DateTime.UtcNow;
            StateHasChanged();
        }
    }

    private async Task ToggleArchive(ExistingTaxStatement s)
    {
        if (!_canUpdate) return;
        var archiving = s.Archived is null;
        var update = ToUpdate(s, archiving);
        if ((await TaxStatements.UpdateAsync(s.TaxStatementId, update)).Toast(Snackbar, "Update failed",
                archiving ? "Tax statement archived." : "Tax statement unarchived."))
        {
            s.Archived = archiving ? DateTime.UtcNow : null;
            BuildProblems();
            StateHasChanged();
            // Archiving moves a year in or out of the header count and the overview charts.
            await LoadSummary();
        }
    }

    private async Task ConfirmDelete(ExistingTaxStatement s)
    {
        if (!_canDelete) return;
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete tax statement",
            $"Delete '{s.Name}'? This cannot be undone.",
            yesText: "Delete", cancelText: "Cancel");
        if (confirmed == true && (await TaxStatements.DeleteAsync(s.TaxStatementId)).Toast(Snackbar, "Delete failed", "Tax statement deleted."))
        {
            _statements.Remove(s);
            _reports.Remove(s.TaxStatementId);
            await LoadSummary();
            if (_expandedId == s.TaxStatementId)
            {
                _expandedId = null;
            }
            BuildProblems();
            StateHasChanged();
        }
    }

    private Task CopyId(Guid id) => Clipboard.CopyAsync(id.ToString(), "Tax statement ID copied.");

    /// <summary>The row action menu. Every section-level action lives here — a section header inside a
    /// record body labels, it does not act — so "Upload file" is the documents section's only entry
    /// point.</summary>
    private IReadOnlyList<OdsMenuItem> RowActions(ExistingTaxStatement s, bool archived)
    {
        var items = new List<OdsMenuItem>();

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "edit",
                Label = "Edit statement",
                OnClick = EventCallback.Factory.Create(this, () => EditClicked(s)),
            });
        }

        if (_canUploadFiles)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "upload_file",
                Label = "Upload file",
                OnClick = EventCallback.Factory.Create(this, () => AddFile(s)),
            });
        }

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "check_circle",
                Label = "Mark approved",
                OnClick = EventCallback.Factory.Create(this, () => SetStatus(s, TaxStatementStatus.Approved, null)),
            });
            items.Add(new OdsMenuItem
            {
                Icon = "flag",
                Label = "Flag for review",
                OnClick = EventCallback.Factory.Create(this,
                    () => SetStatus(s, TaxStatementStatus.Flagged, s.StatusComment ?? "Flagged for review.")),
            });
            items.Add(new OdsMenuItem
            {
                Icon = "fiber_new",
                Label = "Mark as new",
                OnClick = EventCallback.Factory.Create(this, () => SetStatus(s, TaxStatementStatus.New, null)),
            });
        }

        items.Add(new OdsMenuItem
        {
            Icon = "fingerprint",
            Label = "Copy ID",
            TrailingIcon = "content_copy",
            OnClick = EventCallback.Factory.Create(this, () => CopyId(s.TaxStatementId)),
        });

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem
            {
                Icon = archived ? "unarchive" : "archive",
                Label = archived ? "Unarchive" : "Archive",
                OnClick = EventCallback.Factory.Create(this, () => ToggleArchive(s)),
            });
        }

        if (_canDelete)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "delete",
                Label = "Delete",
                Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => ConfirmDelete(s)),
            });
        }

        return items;
    }

    // Builds the full update payload from a statement, overriding only the archived flag.
    private static UpdateTaxStatement ToUpdate(ExistingTaxStatement s, bool archived) => new()
    {
        Name = s.Name,
        FiscalYear = s.FiscalYear,
        StartDate = s.StartDate,
        EndDate = s.EndDate,
        BaseCurrencyCode = s.BaseCurrencyCode,
        DeclaredTotalAssets = s.DeclaredTotalAssets,
        DeclaredTotalLiabilities = s.DeclaredTotalLiabilities,
        DeclaredNetWorth = s.DeclaredNetWorth,
        DeclaredTotalIncome = s.DeclaredTotalIncome,
        AssessedTax = s.AssessedTax,
        SettlementAmount = s.SettlementAmount,
        SettledAtUtc = s.SettledAtUtc,
        FiledAtUtc = s.FiledAtUtc,
        TaxOfficeApprovedAtUtc = s.TaxOfficeApprovedAtUtc,
        Notes = s.Notes,
        Archived = archived,
    };

    // ── Status display ────────────────────────────────────────────────────────
    private static string StatusLabel(ExistingTaxStatement s) => s.Archived is not null
        ? "Archived"
        : s.Status switch
        {
            TaxStatementStatus.Approved => "Approved",
            TaxStatementStatus.Flagged => "Flagged",
            _ => "New",
        };

    private static OdsChipTone StatusTone(ExistingTaxStatement s) => s.Archived is not null
        ? OdsChipTone.Outline
        : s.Status switch
        {
            TaxStatementStatus.Approved => OdsChipTone.Income,
            TaxStatementStatus.Flagged => OdsChipTone.Expense,
            _ => OdsChipTone.Info,
        };

    private static bool StatusDot(ExistingTaxStatement s) => s.Archived is null;

    /// <summary>The Status tile's glyph — the same lifecycle the chip shows, at tile scale.</summary>
    private static string StatusIcon(ExistingTaxStatement s) => s.Archived is not null
        ? "inventory_2"
        : s.Status switch
        {
            TaxStatementStatus.Approved => "check_circle",
            TaxStatementStatus.Flagged => "flag",
            _ => "fiber_new",
        };

    private static OdsInfoTileTone StatusTileTone(ExistingTaxStatement s) => s.Archived is not null
        ? OdsInfoTileTone.Muted
        : s.Status switch
        {
            TaxStatementStatus.Approved => OdsInfoTileTone.Income,
            TaxStatementStatus.Flagged => OdsInfoTileTone.Expense,
            _ => OdsInfoTileTone.Info,
        };

    /// <summary>When the state began, and a pointer to the note above when there is one — the derived
    /// tile carries its own date rather than borrowing one from the fields it summarises.</summary>
    private static string? StatusFoot(ExistingTaxStatement s)
    {
        var parts = new List<string>();
        if (s.Archived is { } archivedAt)
            parts.Add(LongDate(archivedAt));
        else if (s.StatusChangedAt != default)
            parts.Add(LongDate(s.StatusChangedAt));
        if (!string.IsNullOrWhiteSpace(s.StatusComment))
            parts.Add("see note above");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>An optional tile foot. Returns null for an absent caption so the tile renders no foot
    /// element at all — a foot has to earn its place, and an empty one is not the same as none.</summary>
    private static RenderFragment? Caption(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : builder => builder.AddContent(0, text);

    private static string LongDate(DateTime date) => date.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture);

    // ── Money ────────────────────────────────────────────────────────────────
    // Mirrors the design system's taxMoney: symbol-prefixed whole units with an
    // en-dash minus (e.g. "kr 1,600,000"). Tax figures carry no minor units.
    private string TaxMoney(decimal? n, string code)
    {
        if (n is null) return "—";
        var sign = n.Value < 0 ? "−" : string.Empty;
        return $"{sign}{SymbolFor(code)} {Math.Abs(n.Value).ToString("#,##0", CultureInfo.InvariantCulture)}";
    }

    private string SymbolFor(string code) =>
        _currenciesByCode.TryGetValue(code, out var c) && !string.IsNullOrWhiteSpace(c.Symbol) ? c.Symbol : code;

    // Compact y-axis tick: "kr 1.7M" / "kr 232k" / "kr 980".
    private string TaxAxis(decimal n, string code)
    {
        var sym = SymbolFor(code);
        var abs = Math.Abs(n);
        if (abs >= 1_000_000) return $"{sym} {(n / 1_000_000m):0.#}M";
        if (abs >= 1_000) return $"{sym} {(n / 1_000m):0}k";
        return $"{sym} {n:0}";
    }

    // The currency the overview charts read in — the latest active statement's base.
    private string OverviewCurrency =>
        _statements.FirstOrDefault(s => s.Archived is null)?.BaseCurrencyCode
        ?? _statements.FirstOrDefault()?.BaseCurrencyCode ?? "USD";

    // One declared figure plotted oldest → newest across active fiscal years. The rollup already
    // excludes archived years and orders them ascending.
    private IReadOnlyList<OdsLinePoint> DeclaredSeries(Func<TaxStatementYearFigures, decimal?> accessor) =>
        [.. (_summary?.Years ?? []).Select(y => new OdsLinePoint($"'{y.FiscalYear % 100:00}", accessor(y)))];

    /// <summary>The earliest live fiscal year — the baseline the charts' delta compares against.</summary>
    private int FirstYear => _summary?.FirstFiscalYear ?? 0;

    private int? LatestFiscalYear => _summary?.LatestFiscalYear;

    // Settlement figure shown in the collapsed head: the declared settlement, else the
    // estimated outstanding tax for a year not yet settled.
    private decimal? SettlementFigure(ExistingTaxStatement s) =>
        s.SettlementAmount ?? (_reports.TryGetValue(s.TaxStatementId, out var r) ? r.Reconciliation.OutstandingTax : null);

    // The headline figure takes the finance vocabulary, never the record's accent: tax still to pay
    // is an expense, a refund is income, and a settled (or unassessed) year is neutral.
    private static OdsRecordFigureTone SettlementTone(decimal? settle) => settle switch
    {
        > 0 => OdsRecordFigureTone.Expense,
        < 0 => OdsRecordFigureTone.Income,
        _ => OdsRecordFigureTone.Neutral,
    };

    private static string SettlementWord(ExistingTaxStatement s, decimal? settle)
    {
        if (s.SettlementAmount is { } declared)
            return declared > 0 ? "additional tax to pay" : declared < 0 ? "refund" : "settled";
        // Estimated from the reconciliation: an unassessed year says so, and the estimate reads with
        // the same three words its declared counterpart would, marked "(est.)".
        return settle switch
        {
            null => "awaiting assessment",
            > 0 => "outstanding (est.)",
            < 0 => "refund (est.)",
            _ => "settled (est.)",
        };
    }

    // Tag id lists → display names (dropping ids no longer in the catalog).
    private IEnumerable<string> TagNames(IEnumerable<Guid> ids) =>
        ids.Select(id => _tagNames.GetValueOrDefault(id)).Where(n => n is not null)!;

    private static OdsSeverity Sev(OdsSeverity s) => s;

    private static PageHeaderSeverity ToHeaderSeverity(OdsSeverity s) => s switch
    {
        OdsSeverity.Error => PageHeaderSeverity.Error,
        OdsSeverity.Warning => PageHeaderSeverity.Warning,
        _ => PageHeaderSeverity.Information,
    };

    private static string FlagClass(OdsSeverity s) => s switch
    {
        OdsSeverity.Error => "error",
        OdsSeverity.Warning => "warning",
        _ => "info",
    };

    private static string FlagGlyph(OdsSeverity s) => s switch
    {
        OdsSeverity.Error => "error_outline",
        OdsSeverity.Warning => "warning_amber",
        _ => "info",
    };

    // A data condition needing the user's attention: a severity, a short chip, a
    // record title + detail, and the page that resolves it (mirrors AccountsCard).
    private sealed record TaxProblem(
        OdsSeverity Severity,
        string Chip,
        string Title,
        string Summary,
        string Detail,
        string FixLabel,
        string FixTarget);
}
