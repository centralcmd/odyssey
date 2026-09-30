namespace Odyssey.Client.Components;

/// <summary>
/// Small pure helpers shared by the controls built on <c>OdsPopupMenu</c> (issue #255): the selected
/// row's index, and a trigger name that starts with the trigger's visible text.
/// </summary>
internal static class OdsPopupMenuText
{
    /// <summary>The index of the first item matching <paramref name="match"/>, or -1 — without allocating.</summary>
    public static int IndexOf<T>(IReadOnlyList<T> items, Func<T, bool> match)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (match(items[i]))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// An accessible name that STARTS with the trigger's visible text (WCAG 2.5.3 Label in Name), so a
    /// speech-input user can say what they see: the visible parts joined by spaces, then the extra
    /// <paramref name="label"/> after a comma unless the visible text already contains it.
    /// </summary>
    public static string AccessibleName(string? label, params string?[] visibleParts)
    {
        var visible = string.Join(' ', visibleParts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return string.IsNullOrWhiteSpace(label) || visible.Contains(label.Trim(), StringComparison.OrdinalIgnoreCase)
            ? visible
            : $"{visible}, {label.Trim()}";
    }
}
