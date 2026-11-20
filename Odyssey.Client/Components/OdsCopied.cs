namespace Odyssey.Client.Components;

/// <summary>
/// The confirmation toast after a copy to the clipboard (Odyssey Design System · ActionMenu,
/// "Copy ID"). One wording everywhere: the record's noun in sentence case, then "ID copied." —
/// never "to clipboard", which is what copying already means.
/// </summary>
public static class OdsCopied
{
    /// <summary>"Account ID copied." — the toast for a Copy ID item.</summary>
    public static string Id(string noun) => $"{noun} ID copied.";

    /// <summary>"Currency code copied." — the toast for copying any other value.</summary>
    public static string Value(string what) => $"{what} copied.";
}
