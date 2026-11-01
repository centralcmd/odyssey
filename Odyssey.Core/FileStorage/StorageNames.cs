using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Odyssey.Core.FileStorage;

/// <summary>
/// The shape rules for everything that becomes part of a path on a volume (issue #346 §3.2–§3.3, §7.5). A
/// path is composed only from a configured root, a catalogue root folder, code-declared layout segments and a
/// sanitized file name, so user text can only ever reach the last segment — and these rules are re-checked
/// before every filesystem operation, because the row a path comes from may have been edited by hand.
/// </summary>
public static partial class StorageNames
{
    public const string NamePattern = @"^[A-Za-z][A-Za-z0-9]{0,63}\z";
    public const string RootFolderPattern = @"^[a-z0-9][a-z0-9-]{0,31}\z";
    public const string SegmentPattern = @"^[a-z0-9][a-z0-9_-]{0,31}\z";
    public const string SuffixPattern = @"^[A-Za-z0-9_-]{1,32}\z";
    public const string ExtensionPattern = @"^\.[a-z0-9]{1,10}\z";

    /// <summary>Layout depth below the feature root.</summary>
    public const int MinSegments = 1;

    public const int MaxSegments = 5;

    /// <summary>The file-name budget, in UTF-8 bytes (§3.3).</summary>
    public const int MaxFileNameBytes = 150;

    /// <summary>The marker file, the temp folder, the quarantine and ext4's lost+found — never reachable by a structural path.</summary>
    public const string MarkerFileName = ".odyssey-volume.json";
    public const string TempFolder = ".tmp";
    public const string QuarantineFolder = ".quarantine";
    public const string LostAndFound = "lost+found";

    [GeneratedRegex(NamePattern, RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex(RootFolderPattern, RegexOptions.CultureInvariant)]
    private static partial Regex RootFolderRegex();

    [GeneratedRegex(SegmentPattern, RegexOptions.CultureInvariant)]
    private static partial Regex SegmentRegex();

    [GeneratedRegex(SuffixPattern, RegexOptions.CultureInvariant)]
    private static partial Regex SuffixRegex();

    [GeneratedRegex(ExtensionPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionRegex();

    /// <summary>
    /// A file name the store can have produced: it starts with a letter, mark, digit, <c>_</c> or <c>(</c> —
    /// never <c>.</c> or <c>-</c> — holds only the sanitizer's allow-list, and ends in a mapped extension.
    /// </summary>
    [GeneratedRegex(@"^[\p{L}\p{M}\p{Nd}_(][\p{L}\p{M}\p{Nd} ._()\-]*\.[a-z0-9]{1,10}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();

    /// <summary>A volume name or a feature key.</summary>
    public static bool IsValidName(string? value) => value is not null && NameRegex().IsMatch(value);

    public static bool IsValidRootFolder(string? value) => value is not null && RootFolderRegex().IsMatch(value);

    public static bool IsValidSegment(string? value) => value is not null && SegmentRegex().IsMatch(value);

    public static bool IsValidSuffix(string? value) => value is not null && SuffixRegex().IsMatch(value);

    public static bool IsValidExtension(string? value) => value is not null && ExtensionRegex().IsMatch(value);

    public static bool IsConformingFileName(string? value) =>
        value is not null
        && FileNameRegex().IsMatch(value)
        && Encoding.UTF8.GetByteCount(value) <= MaxFileNameBytes;

    /// <summary>
    /// The structural check (§7.5): <c>^&lt;featureRoot&gt;/(&lt;segment&gt;/){1,5}&lt;file name&gt;$</c>. This — not a
    /// prefix test — is what keeps the marker, <c>.tmp</c>, <c>.quarantine</c> and <c>lost+found</c> unreachable.
    /// </summary>
    public static bool IsConformingPath(string? relativePath, string featureRoot) =>
        TrySplit(relativePath, out var root, out var segments, out var fileName)
        && string.Equals(root, featureRoot, StringComparison.Ordinal)
        && segments.All(IsValidSegment)
        && IsConformingFileName(fileName);

    /// <summary>
    /// The catalogue-free variant (§3.7, R5-1) for a row whose feature this build does not know: the first
    /// segment must look like a root folder and must not be any current catalogue root.
    /// </summary>
    public static bool IsConformingPathWithoutCatalogue(string? relativePath, StorageFeatureCatalogue catalogue) =>
        TrySplit(relativePath, out var root, out var segments, out var fileName)
        && IsValidRootFolder(root)
        && !catalogue.IsRootFolder(root)
        && segments.All(IsValidSegment)
        && IsConformingFileName(fileName);

    /// <summary>
    /// Compares two names as the database's default collation does — case- and accent-insensitively — so a name
    /// that collides in the unique index also collides here.
    /// </summary>
    public static readonly StringComparer CollationComparer =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);

    /// <summary>Escapes <c>\</c>, <c>%</c> and <c>_</c> for a <c>LIKE … ESCAPE '\'</c> pattern.</summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>The first segment of a relative path.</summary>
    public static string RootOf(string relativePath)
    {
        var slash = relativePath.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? relativePath : relativePath[..slash];
    }

    private static bool TrySplit(string? relativePath, out string root, out string[] segments, out string fileName)
    {
        root = string.Empty;
        segments = [];
        fileName = string.Empty;
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        var parts = relativePath.Split('/');
        if (parts.Length < MinSegments + 2 || parts.Length > MaxSegments + 2)
        {
            return false;
        }

        root = parts[0];
        segments = parts[1..^1];
        fileName = parts[^1];
        return true;
    }
}

/// <summary>
/// The on-disk file-name rules (issue #346 §3.3). The stem comes from the user's original name, the extension
/// from the validated content type — never from the user's name — and a code-supplied suffix is validated, not
/// sanitized.
/// </summary>
/// <remarks>
/// A third sanitizer beside the two display-name ones (an upload's <c>FileName</c>, a zip entry): those produce
/// display text with a deny-list; this produces a path segment with an allow-list, and is the strictest of the
/// three. Consolidating them is a follow-up (Non-Goal 11).
/// </remarks>
public static class StoredFileNames
{
    /// <summary>The stem used when nothing survives sanitizing.</summary>
    public const string FallbackStem = "file";

    /// <summary>Room left in the budget for the longest collision marker, <c> (xxxxxxxx)</c>.</summary>
    public const int CollisionMarkerReserveBytes = 11;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>The ten steps of §3.3 rule 3, in order, leaving room for <paramref name="suffix"/> and <paramref name="extension"/>.</summary>
    public static string SanitizeStem(string? originalFileName, string? suffix, string extension)
    {
        var name = originalFileName ?? string.Empty;

        // 1. The last path component, splitting on both separators.
        var lastSeparator = name.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0)
        {
            name = name[(lastSeparator + 1)..];
        }

        // 2. NFC.
        name = name.IsNormalized(NormalizationForm.FormC) ? name : name.Normalize(NormalizationForm.FormC);

        // 3. Leading dots, spaces and hyphens, so `.hidden` is `hidden` before the extension is dropped.
        name = name.TrimStart('.', ' ', '-');

        // 4. The last `.ext`, if one remains.
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[..dot];
        }

        // 5. The allow-list.
        var builder = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            builder.Append(IsAllowed(rune) ? rune.ToString() : "_");
        }

        // 6. Collapse runs of whitespace and of `_`.
        name = Collapse(builder.ToString());

        // 7. Trim the edges.
        name = TrimEdges(name);

        // 8. A Windows device name, so a backup restores onto any OS.
        var beforeDot = name.Split('.', 2)[0].TrimEnd(' ');
        if (ReservedDeviceNames.Contains(beforeDot))
        {
            name = "_" + name;
        }

        // 9. The byte budget, cut on a grapheme boundary.
        var budget = StorageNames.MaxFileNameBytes
            - CollisionMarkerReserveBytes
            - Encoding.UTF8.GetByteCount(suffix ?? string.Empty)
            - Encoding.UTF8.GetByteCount(extension);
        name = Truncate(name, budget);

        // 10. Something must survive.
        return name.Length == 0 ? FallbackStem : name;
    }

    /// <summary><c>stem + marker + suffix + extension</c>; the marker sits between the stem and the suffix.</summary>
    public static string Compose(string stem, string? marker, string? suffix, string extension) =>
        stem + (marker is null ? string.Empty : $" ({marker})") + (suffix ?? string.Empty) + extension;

    private static bool IsAllowed(Rune rune)
    {
        if (rune.Value is ' ' or '-' or '_' or '(' or ')' or '.')
        {
            return true;
        }

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber;
    }

    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (builder.Length > 0 && (c is ' ' or '_') && builder[^1] == c)
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string TrimEdges(string value) => value.Trim(' ', '.', '_', '-');

    private static string Truncate(string value, int budgetBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= budgetBytes)
        {
            return value;
        }

        var builder = new StringBuilder();
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var bytes = Encoding.UTF8.GetByteCount(element);
            if (used + bytes > budgetBytes)
            {
                break;
            }

            builder.Append(element);
            used += bytes;
        }

        return TrimEdges(builder.ToString());
    }
}

/// <summary>
/// The content-type → extension table (issue #346 §3.3), shared by the store and the admin file export.
/// <c>null</c> for an unmapped type: the export keeps its empty-extension behaviour, and the store refuses it
/// as a code defect.
/// </summary>
public static class ContentTypeExtensions
{
    public static string? For(string? contentType) => contentType?.Trim().ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf",
        "text/csv" => ".csv",
        "text/plain" => ".txt",
        "application/json" => ".json",
        "application/xml" or "text/xml" => ".xml",
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "application/zip" => ".zip",
        "application/msword" => ".doc",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.ms-excel" => ".xls",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        _ => null,
    };
}
