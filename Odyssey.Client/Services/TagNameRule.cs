namespace Odyssey.Client.Services;

/// <summary>One tag name a picker must not offer to create again, and whether it belongs to an archived tag.</summary>
public readonly record struct TagNameEntry(string Name, bool Archived);

/// <summary>
/// The one client copy of "this tag name is taken" for a tag field's inline create row (Odyssey Design
/// System · <c>TagMultiSelect</c> <c>createBlocked</c>, <c>data.js</c> <c>tagNameBlocked</c>). A picker
/// lists only active tags, so a name held by an archived tag would otherwise be offered as new and then
/// refused by the server; the sentence replaces the create rows instead.
/// </summary>
/// <remarks>
/// <para>
/// Names compare trimmed and case-insensitively, the same comparison every tag service applies on
/// create. Inner whitespace is not collapsed: the server keeps it, so "Foo  bar" is a different name there.
/// </para>
/// <para>
/// Which names a field passes is decided by its vocabulary's server rule, so the sentence is never
/// untrue. Every vocabulary refuses an archived tag's name: transaction and photo tags through a unique
/// index spanning both, journal and task tags through their services, which check a new or renamed
/// tag against archived tags too. So every field passes its archived names as well as its options.
/// </para>
/// </remarks>
public static class TagNameRule
{
    /// <summary>The refusal sentence for <paramref name="text"/>, or <c>null</c> when the name is free.</summary>
    /// <param name="tags">The names the server would refuse.</param>
    /// <param name="text">What the user typed.</param>
    /// <param name="page">The tag page that restores an archived tag, e.g. "Photo tags".</param>
    public static string? Blocked(IEnumerable<TagNameEntry> tags, string? text, string page)
    {
        var clean = (text ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            return null;
        }

        foreach (var tag in tags)
        {
            var name = (tag.Name ?? string.Empty).Trim();
            if (!string.Equals(name, clean, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return tag.Archived
                ? $"“{name}” is an archived tag. Restore it on the {page} page to use it."
                : $"A tag called “{name}” already exists.";
        }

        return null;
    }

    /// <summary>A <c>CreateBlocked</c> delegate over a snapshot of <paramref name="tags"/>.</summary>
    public static Func<string, string?> For(IEnumerable<TagNameEntry> tags, string page)
    {
        var snapshot = tags.ToList();
        return text => Blocked(snapshot, text, page);
    }

    /// <summary>The tag page names, so no field restates one.</summary>
    public static class Pages
    {
        public const string Transaction = "Transaction tags";
        public const string Journal = "Journal tags";
        public const string Task = "Task tags";
        public const string Photo = "Photo tags";
        public const string Recipe = "Recipe tags";
        public const string Book = "Book tags";
        public const string News = "News tags";
    }
}
