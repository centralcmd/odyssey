using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.Client.Components;
using Odyssey.Client.Models;

namespace Odyssey.Client.Pages.Finance;

public partial class FileAnalysisReviewGrid
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>The review's shared state — the grid edits it in place.</summary>
    [Parameter, EditorRequired] public FileAnalysisSession Session { get; set; } = default!;

    /// <summary>The statement under review (toolbar context line).</summary>
    [Parameter] public string FileName { get; set; } = "";

    /// <summary>The account it belongs to (toolbar context line).</summary>
    [Parameter] public string? AccountName { get; set; }

    /// <summary>Run the match step again — no re-extract, no second consent.</summary>
    [Parameter] public EventCallback OnReMatch { get; set; }

    /// <summary>Start over from the consent gate (offered when every candidate was removed).</summary>
    [Parameter] public EventCallback OnReanalyze { get; set; }

    /// <summary>
    /// A contact was optimistically staged in the session and now needs creating server-side. The
    /// dialog owns the POST and the reconcile/rollback that follows.
    /// </summary>
    [Parameter] public EventCallback<FileAnalysisPendingContact> OnCreateContact { get; set; }

    /// <summary>Speak a discrete outcome on the dialog's polite live region.</summary>
    [Parameter] public EventCallback<string> OnAnnounce { get; set; }

    // ── Inline merchant create ────────────────────────────────────────────────
    // Synchronous, because OdsCombobox's OnCreate must hand back the new option immediately. The
    // session stages it optimistically; the server round-trip is the dialog's job.
    private OdsOption? BeginCreateContact(FileAnalysisRow row, string text)
    {
        var option = Session.BeginCreateContact(row, text, out var tempId);
        if (option is not null)
            _ = OnCreateContact.InvokeAsync(new FileAnalysisPendingContact(tempId, option.Label));
        return option;
    }

    /// <summary>Create a contact straight from the extracted merchant string (no retyping) and link it.</summary>
    private async Task CreateMerchantFromExtractedAsync(FileAnalysisRow row)
    {
        var name = row.Merchant;
        BeginCreateContact(row, name);
        await AnnounceAndRefocusAsync($"Created and linked merchant {name}.", MerchantInputId(row));
    }

    // ── Suggestion chips ──────────────────────────────────────────────────────
    // Apply/Dismiss unmount the chip (including the button just pressed), so focus is restored to the
    // cell control and the outcome announced — never letting focus fall to <body>.
    private async Task ApplyMerchantSuggestionAsync(FileAnalysisRow row)
    {
        if (Session.ApplyMerchantSuggestion(row) is not { } name)
            return;
        await AnnounceAndRefocusAsync($"Linked merchant {name}.", MerchantInputId(row));
    }

    private async Task DismissMerchantSuggestionAsync(FileAnalysisRow row)
    {
        FileAnalysisSession.DismissMerchantSuggestion(row);
        await AnnounceAndRefocusAsync("Merchant suggestion dismissed.", MerchantInputId(row));
    }

    private async Task ApplyCategorySuggestionAsync(FileAnalysisRow row)
    {
        if (!FileAnalysisSession.ApplyCategorySuggestion(row))
            return;
        await AnnounceAndRefocusAsync("Applied category suggestion.", CategoryControlId(row));
    }

    private async Task DismissCategorySuggestionAsync(FileAnalysisRow row)
    {
        FileAnalysisSession.DismissCategorySuggestion(row);
        await AnnounceAndRefocusAsync("Category suggestion dismissed.", CategoryControlId(row));
    }

    // Per-cell control ids (focus restoration + the merchant combobox's <label for> association).
    private static string MerchantInputId(FileAnalysisRow row) => $"fan-merch-{row.CandidateId:N}";
    private static string CategoryControlId(FileAnalysisRow row) => $"fan-cat-{row.CandidateId:N}";

    /// <summary>
    /// Announce the outcome on the dialog's polite live region and move focus back into the cell so it
    /// is never lost to &lt;body&gt; when the suggestion chip unmounts (WCAG 3.2.2 / 4.1.3).
    /// </summary>
    private async Task AnnounceAndRefocusAsync(string message, string focusId)
    {
        await OnAnnounce.InvokeAsync(message);
        try { await JS.InvokeVoidAsync("odsFocusById", focusId); }
        catch { /* element not present (e.g. dialog closing) — non-fatal */ }
    }
}
