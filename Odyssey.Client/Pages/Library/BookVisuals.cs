using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Theme;
using Odyssey.Dtos;
using Odyssey.Dtos.Books;

namespace Odyssey.Client.Pages.Library;

/// <summary>One book status: its chip tone, its glyph, and the finance tone the overview row and filter take.</summary>
public sealed record BookStatusInfo(string Key, string Label, string Icon, OdsChipTone ChipTone, string Tone);

/// <summary>
/// The Books page's display vocabulary (issue #571; design system · <c>books-data.js</c>, <c>docs/components-books.md</c>).
/// The format registry is shared (<see cref="OdsTypeRegistries.BookFormats"/>); the status set carries a chip tone
/// rather than a colour of its own, so it lives beside the page, as the property statuses do.
/// </summary>
public static class BookVisuals
{
    /// <summary>BookStatus in ordinal order — the DS <c>BOOK_STATUSES</c>.</summary>
    public static readonly IReadOnlyList<BookStatusInfo> Statuses =
    [
        new(nameof(BookStatus.Owned), "Owned", "check_circle", OdsChipTone.Income, "income"),
        new(nameof(BookStatus.LentOut), "Lent out", "outbound", OdsChipTone.Pending, "pending"),
        new(nameof(BookStatus.Wishlist), "Wishlist", "bookmark_border", OdsChipTone.Info, "info"),
        new(nameof(BookStatus.SoldOrGivenAway), "Sold or given away", "do_not_disturb_on", OdsChipTone.Outline, "outline"),
    ];

    private static readonly BookStatusInfo UnknownStatus = new("Unknown", "Unknown status", "help_outline", OdsChipTone.Outline, "outline");

    /// <summary>The status descriptor. An ordinal this build does not know reads <i>Unknown status</i>, never a real value.</summary>
    public static BookStatusInfo StatusOf(BookStatus status) =>
        Statuses.FirstOrDefault(s => s.Key == status.ToString()) ?? UnknownStatus;

    /// <summary>The By status tile's definitions — every status, zero counts included.</summary>
    public static readonly OdsBreakdownDef<string>[] StatusDefs =
        [.. Statuses.Select(s => new OdsBreakdownDef<string>(s.Key, s.Label, s.Tone, s.Icon))];

    /// <summary>The Status filter's options, in the overview's glyph and tone.</summary>
    public static readonly IReadOnlyList<OdsOption> StatusOptions = OdsBreakdown.Options(StatusDefs);

    /// <summary>The Status picker in the dialog (glyph + label).</summary>
    public static readonly IReadOnlyList<OdsOption> StatusPickerOptions =
        [.. Statuses.Select(s => new OdsOption(s.Key, s.Label) { Icon = s.Icon, IconColor = OdsBreakdown.Tone(s.Tone) })];

    /// <summary>On the shelf = Owned + Lent out (the header sub-line).</summary>
    public static bool OnTheShelf(BookStatus status) => status is BookStatus.Owned or BookStatus.LentOut;

    /// <summary>The file picker's extensions — a surface tightening; the server still decides (DS <c>BOOK_FILE_EXTENSIONS</c>).</summary>
    public static readonly IReadOnlyList<string> FileExtensions = [".epub", ".pdf", ".txt", ".doc", ".docx", ".zip"];

    /// <summary>A year as shown: positive as itself, negative as <i>{abs} BC</i>.</summary>
    public static string Year(int year) =>
        year < 0
            ? $"{(-year).ToString(CultureInfo.InvariantCulture)} BC"
            : year.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses the Year field. Accepts an optional leading minus (<c>-</c> or U+2212) and 1–4 digits; refuses 0, a lone
    /// minus, decimals and letters. Empty is <c>null</c> with no error. Returns the error text, or <c>null</c> when valid.
    /// </summary>
    public static string? ParseYear(string? text, out int? year)
    {
        year = null;
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return null;
        }

        var negative = t[0] is '-' or '−';
        var digits = negative ? t[1..] : t;
        if (digits.Length is 0 or > 4 || !digits.All(char.IsAsciiDigit))
        {
            return "Enter a year as up to four digits, with a minus sign for BC.";
        }

        var value = int.Parse(digits, CultureInfo.InvariantCulture);
        if (value == 0)
        {
            return "There is no year 0 — use 1 or −1.";
        }

        year = negative ? -value : value;
        return null;
    }

    /// <summary>The form's text for a stored year: U+2212 for a negative one, so it reads as the help says.</summary>
    public static string YearText(int? year) =>
        year is { } y ? (y < 0 ? $"−{(-y).ToString(CultureInfo.InvariantCulture)}" : y.ToString(CultureInfo.InvariantCulture)) : string.Empty;

    /// <summary>
    /// The Saved-as preview and the field error for an ISBN, through the server's own rule (<see cref="IsbnRules"/>,
    /// shared from <c>Odyssey.Dtos</c>) — never a client copy of it. Empty input is neither.
    /// </summary>
    public static (string? Normalized, string? Error) Isbn(string? text) =>
        IsbnRules.TryNormalize(text, out var normalized)
            ? (normalized, null)
            : (null, "This isn’t a valid ISBN-10 or ISBN-13 — check the digits.");

    /// <summary>A contact reference's display name: <i>Name not available</i> without the claim, <i>Unavailable</i> when gone.</summary>
    public static string ContactName(BookContactReference? contact, bool canReadContacts) =>
        !canReadContacts ? "Name not available"
        : contact?.Name is { Length: > 0 } name ? name
        : "Unavailable";

    /// <summary>The tag ink fallback when a tag has no colour — the Journal / Tasks rule.</summary>
    public const string TagInk = JournalOverviews.TagRowColor;

    /// <summary>A tag option in its own glyph and colour; an archived one says so in the text.</summary>
    public static OdsOption TagOption(ExistingBookTag tag) =>
        new(tag.BookTagId.ToString(), tag.Archived is null ? tag.Name : $"{tag.Name} (archived)")
        {
            Icon = OdsTagChip.GlyphFor(tag.Icon),
            IconColor = UserColor.Glyph(tag.Color) ?? TagInk,
        };

    /// <summary>Active tags first by name, then archived ones (the filter and the dialog picker).</summary>
    public static IReadOnlyList<OdsOption> TagOptions(IEnumerable<ExistingBookTag> tags, Func<ExistingBookTag, bool>? keepArchived = null) =>
        [
            .. tags.Where(t => t.Archived is null).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(TagOption),
            .. tags.Where(t => t.Archived is not null && (keepArchived?.Invoke(t) ?? true))
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(TagOption),
        ];

    /// <summary>The By tag rows: active tags with at least one book, in the tag's glyph and colour.</summary>
    public static IReadOnlyList<OdsBreakdownRow> TagRows(IEnumerable<ExistingBookTag> tags, IEnumerable<BookSummary> books)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var id in books.SelectMany(b => b.TagIds.Distinct()))
        {
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }

        return
        [
            .. tags.Where(t => t.Archived is null && counts.GetValueOrDefault(t.BookTagId) > 0)
                .OrderByDescending(t => counts[t.BookTagId]).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => new OdsBreakdownRow
                {
                    Key = t.BookTagId,
                    Icon = OdsTagChip.GlyphFor(t.Icon),
                    IconColor = UserColor.Glyph(t.Color) ?? TagInk,
                    Label = t.Name,
                    Count = counts[t.BookTagId],
                }),
        ];
    }

    /// <summary>The By format rows: formats present, in registry order.</summary>
    public static IReadOnlyList<OdsBreakdownRow> FormatRows(IEnumerable<BookSummary> books) =>
        OdsBreakdown.TypeRows(books, b => b.Format.ToString(), [.. OdsTypeRegistries.BookFormats.Select(f => f.Key)],
            key =>
            {
                var f = OdsTypeRegistries.BookFormats.First(x => x.Key == key);
                return (f.Icon, f.Color, f.Label);
            });

    /// <summary>The By status rows: all four statuses, zero counts included.</summary>
    public static IReadOnlyList<OdsBreakdownRow> StatusRows(IEnumerable<BookSummary> books) =>
        OdsBreakdown.StatusRows(books, b => b.Status.ToString(), StatusDefs);

    /// <summary>"1 book" / "3 books".</summary>
    public static string Plural(int n, string one, string many) => $"{n.ToString(CultureInfo.CurrentCulture)} {(n == 1 ? one : many)}";
}
