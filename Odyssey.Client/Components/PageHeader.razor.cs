using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class PageHeader
{
    /// <summary>Page name shown as the title (rendered at Typo.h2).</summary>
    [Parameter, EditorRequired] public string Title { get; set; } = string.Empty;

    /// <summary>Optional muted sub-line under the title (e.g. "12 active · combined $48,260.00").</summary>
    [Parameter] public RenderFragment? SubLine { get; set; }

    /// <summary>Optional leading slot rendered before the title block (e.g. a record or user avatar).</summary>
    [Parameter] public RenderFragment? Leading { get; set; }

    /// <summary>Optional page-identity glyph. When set (and no <see cref="Leading"/> slot is supplied),
    /// the header draws the standard brand-tinted leading icon tile (Odyssey Design System · p.10).</summary>
    [Parameter] public string? Icon { get; set; }

    /// <summary>Optional status chips rendered under the title (e.g. role · email · 2FA · tenure).</summary>
    [Parameter] public RenderFragment? TitleChips { get; set; }

    /// <summary>Problem rollup rows. When non-empty, the severity-tinted toggle appears.</summary>
    [Parameter] public IReadOnlyCollection<PageHeaderProblem>? Problems { get; set; }

    /// <summary>Label on the problem toggle (the rollup count is appended as a badge).</summary>
    [Parameter] public string ProblemsLabel { get; set; } = "Problems";

    /// <summary>Whether the rollup region starts open the first time problems appear.</summary>
    [Parameter] public bool ProblemsOpenByDefault { get; set; } = true;

    /// <summary>Overview region content. When set, an Overview toggle appears.</summary>
    [Parameter] public RenderFragment? OverviewContent { get; set; }

    /// <summary>Whether the Overview region starts open.</summary>
    [Parameter] public bool OverviewOpenByDefault { get; set; }

    /// <summary>Search region content (query + filters). When set, a Search toggle appears.</summary>
    [Parameter] public RenderFragment? SearchContent { get; set; }

    /// <summary>Whether the Search region starts open.</summary>
    [Parameter] public bool SearchOpenByDefault { get; set; }

    /// <summary>Passive reference/lookup region (e.g. a permission-claims catalog). When set, an Info toggle appears.</summary>
    [Parameter] public RenderFragment? InfoContent { get; set; }

    /// <summary>Label on the Info toggle (the design default is "Reference").</summary>
    [Parameter] public string InfoLabel { get; set; } = "Reference";

    /// <summary>Icon on the Info toggle.</summary>
    [Parameter] public string InfoIcon { get; set; } = Icons.Material.Filled.MenuBook;

    /// <summary>Whether the Info region starts open.</summary>
    [Parameter] public bool InfoOpenByDefault { get; set; }

    /// <summary>Label of the primary (create) action. When set, a filled button appears last.</summary>
    [Parameter] public string? PrimaryActionText { get; set; }

    /// <summary>Icon of the primary action button.</summary>
    [Parameter] public string PrimaryActionIcon { get; set; } = Icons.Material.Filled.Add;

    /// <summary>Invoked when the primary action button is clicked.</summary>
    [Parameter] public EventCallback OnPrimaryAction { get; set; }

    /// <summary>Gate for the primary action (e.g. permissions). Hidden when false.</summary>
    [Parameter] public bool ShowPrimaryAction { get; set; } = true;

    /// <summary>Custom primary action markup — overrides the convenience button (e.g. a Save button).</summary>
    [Parameter] public RenderFragment? PrimaryAction { get; set; }

    /// <summary>Optional overflow menu items (MudMenuItems) supplied by the page. When set, the
    /// header renders a kebab (⋮) menu as the right-most control, after the primary verb.</summary>
    [Parameter] public RenderFragment? ActionMenu { get; set; }

    /// <summary>Accessible name for the kebab trigger (WCAG 4.1.2 — an icon-only MudMenu has no name
    /// otherwise). Override when a page's overflow actions need a more specific label.</summary>
    [Parameter] public string ActionMenuLabel { get; set; } = "More actions";

    /// <summary>Extra one-off regions. Wrap each in &lt;div class="ph-region"&gt;.</summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>Optional extra CSS classes applied to the outer card.</summary>
    [Parameter] public string? Class { get; set; }

    // Optional two-way control over each region's open state. A page that wants to
    // persist/restore the header layout binds these (e.g. @bind-OverviewOpen); when
    // bound, the bound value is the source of truth and every toggle raises the
    // matching *Changed so the page can save it. Pages that don't bind keep the old
    // behaviour — internal state seeded from the *OpenByDefault flags.
    [Parameter] public bool ProblemsOpen { get; set; }
    [Parameter] public EventCallback<bool> ProblemsOpenChanged { get; set; }
    [Parameter] public bool OverviewOpen { get; set; }
    [Parameter] public EventCallback<bool> OverviewOpenChanged { get; set; }
    [Parameter] public bool SearchOpen { get; set; }
    [Parameter] public EventCallback<bool> SearchOpenChanged { get; set; }
    [Parameter] public bool InfoOpen { get; set; }
    [Parameter] public EventCallback<bool> InfoOpenChanged { get; set; }

    private bool _showProblems;
    private bool _showOverview;
    private bool _showSearch;
    private bool _showInfo;

    protected override void OnInitialized()
    {
        _showProblems = ProblemsOpenByDefault;
        _showInfo = InfoOpenByDefault;
        _showOverview = OverviewOpenByDefault;
        _showSearch = SearchOpenByDefault;
    }

    protected override void OnParametersSet()
    {
        // When a page binds a region's open state, mirror it (the page — restoring a
        // saved layout — is authoritative). Unbound regions keep their internal state.
        if (ProblemsOpenChanged.HasDelegate)
            _showProblems = ProblemsOpen;
        if (OverviewOpenChanged.HasDelegate)
            _showOverview = OverviewOpen;
        if (SearchOpenChanged.HasDelegate)
            _showSearch = SearchOpen;
        if (InfoOpenChanged.HasDelegate)
            _showInfo = InfoOpen;
    }

    private Task ToggleProblems() => ToggleAsync(_showProblems = !_showProblems, ProblemsOpenChanged);
    private Task ToggleOverview() => ToggleAsync(_showOverview = !_showOverview, OverviewOpenChanged);
    private Task ToggleSearch() => ToggleAsync(_showSearch = !_showSearch, SearchOpenChanged);
    private Task ToggleInfo() => ToggleAsync(_showInfo = !_showInfo, InfoOpenChanged);

    private static Task ToggleAsync(bool open, EventCallback<bool> changed) =>
        changed.HasDelegate ? changed.InvokeAsync(open) : Task.CompletedTask;

    private bool HasProblems => Problems is { Count: > 0 };

    private PageHeaderSeverity HighestSeverity =>
        Problems is null || Problems.Count == 0
            ? PageHeaderSeverity.Information
            : Problems.Max(p => p.Severity);

    private Color SignalColor => HighestSeverity switch
    {
        PageHeaderSeverity.Error => Color.Error,
        PageHeaderSeverity.Warning => Color.Warning,
        _ => Color.Info,
    };

    private string SignalIcon => HighestSeverity switch
    {
        PageHeaderSeverity.Error => Icons.Material.Filled.ErrorOutline,
        PageHeaderSeverity.Warning => Icons.Material.Filled.WarningAmber,
        _ => Icons.Material.Filled.Info,
    };

    private static string SeverityClass(PageHeaderSeverity severity) => severity switch
    {
        PageHeaderSeverity.Error => "error",
        PageHeaderSeverity.Warning => "warning",
        _ => "info",
    };

    private static string SeverityGlyph(PageHeaderSeverity severity) => severity switch
    {
        PageHeaderSeverity.Error => "error_outline",
        PageHeaderSeverity.Warning => "warning_amber",
        _ => "info",
    };

    private Task InvokeView(PageHeaderProblem problem) =>
        problem.HasView ? problem.OnView.InvokeAsync() : Task.CompletedTask;
}
