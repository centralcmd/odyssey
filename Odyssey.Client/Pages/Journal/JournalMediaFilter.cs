namespace Odyssey.Client.Pages.Journal;

/// <summary>
/// Translates the Journal page's "Any attachment" multi-select into the list query's
/// <c>hasPhotos</c> / <c>hasFiles</c> flags.
///
/// <para>
/// Three-valued, and the middle value is the one worth stating: an <b>unselected</b> option means
/// "don't filter", never "must be absent". The picker offers presence only, so an unselected box sends
/// <c>null</c> rather than <c>false</c> — <c>false</c> is a real and different filter ("entries with NO
/// photos"), which this control never asks for. Sending <c>false</c> here would silently invert the
/// unfiltered case into one that hides every entry carrying a photo.
/// </para>
///
/// <para>
/// It is a static helper rather than two expression-bodied properties on the page because this is the
/// single site that produces the value the API and ApiClient tiers go to some length to pin below it,
/// and a page-private property cannot be tested. Same shape as <c>TermVisuals</c>, which was
/// extracted off its page for the same reason.
/// </para>
/// </summary>
internal static class JournalMediaFilter
{
    /// <summary><c>true</c> when <paramref name="option"/> is selected, otherwise <c>null</c> — never
    /// <c>false</c>.</summary>
    internal static bool? Want(IReadOnlyCollection<string> selected, string option) =>
        selected.Contains(option) ? true : null;
}
