using Odyssey.Client.Pages.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Journal page's "Any attachment" picker → <c>hasPhotos</c>/<c>hasFiles</c> translation.
///
/// <para>
/// The API and ApiClient tiers both pin that <c>false</c> is a real filter ("entries with NO photos")
/// and only <c>null</c> means "don't filter". This is the one site that actually decides which of the
/// three a user's checkbox produces, so it is where that distinction can be lost for real: emitting
/// <c>false</c> for an unticked box would turn the unfiltered case into one that hides every entry
/// carrying a photo, and every tier below would faithfully carry out exactly that.
/// </para>
/// </summary>
public class JournalMediaFilterTests
{
    private const string Photos = "photos";
    private const string Files = "files";

    [Fact]
    public void A_selected_option_asks_for_presence()
    {
        Assert.True(JournalMediaFilter.Want([Photos], Photos));
    }

    // The point of the whole helper: absent is null, NOT false.
    [Fact]
    public void An_unselected_option_does_not_filter_rather_than_asking_for_absence()
    {
        Assert.Null(JournalMediaFilter.Want([], Photos));
        Assert.Null(JournalMediaFilter.Want([Files], Photos));
    }

    [Fact]
    public void Each_option_is_read_independently_so_the_two_can_be_anded()
    {
        Assert.True(JournalMediaFilter.Want([Photos, Files], Photos));
        Assert.True(JournalMediaFilter.Want([Photos, Files], Files));
    }

    [Fact]
    public void Selecting_one_leaves_the_other_unfiltered()
    {
        Assert.True(JournalMediaFilter.Want([Files], Files));
        Assert.Null(JournalMediaFilter.Want([Files], Photos));
    }

    // Values are the option keys as stored in page state, which is restored from the browser — an
    // unrecognised or differently-cased leftover must not read as a selection.
    [Fact]
    public void An_unrecognised_value_does_not_filter()
    {
        Assert.Null(JournalMediaFilter.Want(["Photos"], Photos));
        Assert.Null(JournalMediaFilter.Want(["attachments"], Files));
    }
}
