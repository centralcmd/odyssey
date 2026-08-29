namespace Odyssey.Dtos.Journal;

/// <summary>
/// Shared validation rule for a caller- or import-supplied journal-entry <c>ExternalUid</c> (issue #339
/// §5 step 3.2a). The value becomes the exported <c>VJOURNAL</c> <c>UID</c> verbatim and is matched
/// case-sensitively on import, so its character set is bounded like every other free-text field: no C0
/// control characters (CR, LF, or other non-printables) and no leading/trailing whitespace. The
/// whitespace rule matters because the column's <c>utf8mb4_bin</c> collation is PAD SPACE, so
/// <c>"uid"</c> and <c>"uid "</c> would collide at the unique index despite being ordinally distinct.
/// </summary>
public static class JournalEntryExternalUidRules
{
    /// <summary>Regex accepting a value with no control characters (C0 range plus DEL) and no
    /// leading/trailing whitespace. Used as a <c>[RegularExpression]</c> on the create/update DTOs; the
    /// ICS import path enforces the same rule in code against values parsed from a file.</summary>
    public const string Pattern = "^(?!\\s)[^\\u0000-\\u001F\\u007F]*(?<!\\s)$";

    public const string ErrorMessage =
        "External ID must not contain control characters or leading/trailing whitespace.";
}
