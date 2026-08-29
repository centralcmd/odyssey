using Odyssey.Client.Components;

namespace Odyssey.Client.Services;

/// <summary>
/// Helpers that collapse the per-page persistence preamble every list page repeated:
/// the "load saved state, seed defaults when there's none" dance, and the coercion of
/// a saved multi-select filter back to the values still on offer.
/// </summary>
public static class PageStateExtensions
{
    /// <summary>
    /// Loads the saved state for <paramref name="pageKey"/> and hands it to
    /// <paramref name="apply"/>; when no preference exists yet, queues a save of the
    /// page's current defaults (via <paramref name="capture"/>) so one always exists
    /// going forward. Mirrors the hand-rolled
    /// <c>if (state is null) { PersistPageState(); return; }</c> idiom.
    /// </summary>
    public static async Task RestoreOrSeedAsync<TState>(
        this IPageStateService service,
        string pageKey,
        Action<TState> apply,
        Func<TState> capture)
        where TState : class
    {
        var state = await service.LoadAsync<TState>(pageKey);
        if (state is null)
        {
            service.QueueSave(pageKey, capture());
            return;
        }

        apply(state);
    }

    /// <summary>
    /// Keeps only the saved filter values that still correspond to an offered option,
    /// dropping anything an older layout persisted that the page no longer presents.
    /// </summary>
    public static List<string> KnownValues(this IReadOnlyList<OdsOption> options, IEnumerable<string>? saved) =>
        saved is null ? [] : [.. saved.Where(value => options.Any(option => option.Value == value))];
}
