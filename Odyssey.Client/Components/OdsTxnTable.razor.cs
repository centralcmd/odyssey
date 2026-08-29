using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Components;

public partial class OdsTxnTable
{
    /// <summary>Rows to render (already filtered by the host).</summary>
    [Parameter, EditorRequired] public IEnumerable<ExistingTransaction> Rows { get; set; } = [];

    /// <summary>Drop the Account column — redundant inside one account.</summary>
    [Parameter] public bool HideAccount { get; set; }

    /// <summary>Initial sort (uncontrolled seed). Defaults to date, newest first.</summary>
    [Parameter] public OdsTableSort? DefaultSort { get; set; }

    /// <summary>Controlled sort — bind <c>@bind-Sort</c> to share one state with a toolbar <c>OdsSortSelect</c>.</summary>
    [Parameter] public OdsTableSort? Sort { get; set; }

    /// <summary>Raised with the complete next sort (enables <c>@bind-Sort</c>).</summary>
    [Parameter] public EventCallback<OdsTableSort> SortChanged { get; set; }

    /// <summary>Server-sort pass-through (issue #277): render <see cref="Rows"/> verbatim, no client re-sort.</summary>
    [Parameter] public bool ServerSort { get; set; }

    /// <summary>Keep several rows expanded at once instead of accordion behaviour.</summary>
    [Parameter] public bool MultiOpen { get; set; }

    /// <summary>Row overflow menu (View / Edit / Copy ID / Delete) — built by the host.</summary>
    [Parameter] public Func<ExistingTransaction, OdsRecordActionContext, IReadOnlyList<OdsMenuItem>>? Actions { get; set; }

    /// <summary>Read-only panel shown when a row is expanded. Omit for non-expanding rows.</summary>
    [Parameter] public RenderFragment<ExistingTransaction>? RenderDetail { get; set; }

    /// <summary>Edit panel shown when a row is in edit mode. Omit for read-only tables.</summary>
    [Parameter] public Func<ExistingTransaction, OdsRecordEditContext, RenderFragment>? RenderEdit { get; set; }

    /// <summary>Persist a row edit.</summary>
    [Parameter] public EventCallback<OdsRecordSaveEventArgs> OnSave { get; set; }

    /// <summary>Remove a row.</summary>
    [Parameter] public EventCallback<object> OnDelete { get; set; }

    /// <summary>Replaces the whole first-run empty state, for onboarding copy that needs markup.</summary>
    [Parameter] public RenderFragment? Empty { get; set; }

    // ── List states (forwarded verbatim to OdsRecordTable → OdsListStatus) ────
    /// <summary>The first fetch is in flight — renders a spinner instead of rows.</summary>
    [Parameter] public bool Loading { get; set; }

    /// <summary>A background refetch is in flight — renders the indeterminate bar above the table.</summary>
    [Parameter] public bool Refetching { get; set; }

    /// <summary>The fetch failed — renders the error state with a Retry, never the empty state.</summary>
    [Parameter] public bool Error { get; set; }

    /// <summary>Retry the failed fetch.</summary>
    [Parameter] public EventCallback OnRetry { get; set; }

    /// <summary>Optional detail line under the error title.</summary>
    [Parameter] public string? ErrorDescription { get; set; }

    /// <summary>A search or filter is narrowing the rows, so empty means "no matches", not "first run".</summary>
    [Parameter] public bool HasFilters { get; set; }

    /// <summary>Clear every filter and search — offered from the filtered-empty state.</summary>
    [Parameter] public EventCallback OnClearFilters { get; set; }

    /// <summary>Lower-case plural noun for the state copy.</summary>
    [Parameter] public string Noun { get; set; } = "transactions";

    /// <summary>Material Icons ligature for the first-run empty state.</summary>
    [Parameter] public string EmptyIcon { get; set; } = "receipt_long";

    /// <summary>First-run empty title. Defaults to "No {Noun} yet".</summary>
    [Parameter] public string? EmptyTitle { get; set; }

    /// <summary>First-run empty supporting line.</summary>
    [Parameter] public string? EmptyDescription { get; set; }

    /// <summary>The first-run CTA — pass an OdsButton.</summary>
    [Parameter] public RenderFragment? CreateAction { get; set; }

    /// <summary>Utility classes for the refetch bar.</summary>
    [Parameter] public string? BarClass { get; set; }

    [Parameter] public string? Class { get; set; }

    /// <summary>Accessible name for the table (no visible caption). Defaults to "Transactions".</summary>
    [Parameter] public string? AriaLabel { get; set; }

    // How many tag chips show before collapsing into "+N" in the dense column.
    private const int TagCap = 2;

    // Sort key for the multi-tag column: the joined tag names (empty sorts last via "~").
    private static string TagSortValue(ExistingTransaction t) =>
        t.TransactionTags.Count == 0
            ? "~"
            : string.Join(", ", t.TransactionTags.Select(tag => tag.Name)).ToLowerInvariant();

    // Status → chip tone — the single mapping every transaction surface shares.
    private static OdsChipTone StatusTone(TransactionStatus status) => status switch
    {
        TransactionStatus.Approved => OdsChipTone.Income,
        TransactionStatus.Flagged => OdsChipTone.Expense,
        _ => OdsChipTone.Info,
    };

    // Signed income/expense encoding: +N for money in, -N for money out, plus the ISO code.
    private static string SignedAmount(ExistingTransaction t) =>
        $"{(t.Amount >= 0 ? "+" : "-")}{Math.Abs(t.Amount).ToString("N2")} {t.CurrencyCode}";
}
