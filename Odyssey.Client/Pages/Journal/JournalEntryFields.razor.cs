using Odyssey.Client.Services;

namespace Odyssey.Client.Pages.Journal;

public partial class JournalEntryFields
{
    // Journal tag names are unique among ACTIVE tags only (JournalTagService.EnsureNameAvailable), so only
    // the listed tags block a name; an archived tag's name stays creatable.
    private string? BlockedTagName(string text) =>
        TagNameRule.Blocked(TagOptions.Select(o => new TagNameEntry(o.Label, Archived: false)), text, TagNameRule.Pages.Journal);
}
