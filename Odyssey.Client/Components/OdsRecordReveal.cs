using MudBlazor;

namespace Odyssey.Client.Components;

/// <summary>
/// The two halves of the header-rollup → record jump the record-list pages share: scroll the record
/// into view, then end its attention flash once <see cref="OdsTiming.RowFlashMs"/> has run. Each page
/// still owns its expanded and flashed ids — the state differs per page — and calls these in between.
/// Before this, Accounts, Contracts, Properties and Tax statements each carried a copy of both.
/// </summary>
public static class OdsRecordReveal
{
    /// <summary>
    /// Smooth-scrolls <paramref name="selector"/> into view. Best-effort: a failure is swallowed,
    /// because the record is already expanded and a missed scroll leaves nothing to recover.
    /// </summary>
    public static async Task ScrollIntoViewAsync(IScrollManager scrollManager, string selector)
    {
        try
        {
            await scrollManager.ScrollIntoViewAsync(selector, ScrollBehavior.Smooth);
        }
        catch (Exception)
        {
            // Best-effort scroll; the record is already expanded.
        }
    }

    /// <summary>
    /// Waits out the flash, then calls <paramref name="endFlash"/> — but only while
    /// <paramref name="stillFlashing"/> holds, so a second jump started in the meantime keeps its own
    /// ring instead of having it cleared by the first jump's timer.
    /// </summary>
    public static async Task EndFlashAsync(Func<bool> stillFlashing, Action endFlash)
    {
        await Task.Delay(OdsTiming.RowFlashMs);
        if (stillFlashing())
        {
            endFlash();
        }
    }
}
