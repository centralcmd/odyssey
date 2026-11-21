using Odyssey.Client.Services;

namespace Odyssey.Client.Pages.Journal;

public partial class JournalEntryFields
{
    // A journal tag name is unique archived tags included (JournalTagService.EnsureNameAvailable): an active
    // match reads "already exists", an archived one names the page that restores it.
    private string? BlockedTagName(string text) =>
        TagNameRule.Blocked(
            TagOptions.Select(o => new TagNameEntry(o.Label, Archived: false))
                .Concat(ArchivedTagNames.Select(n => new TagNameEntry(n, Archived: true))),
            text, TagNameRule.Pages.Journal);
}
