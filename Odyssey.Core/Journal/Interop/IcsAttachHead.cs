using System.Text;

namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// The ONE reading of an <c>ATTACH</c> property's head — its name and parameters, up to the value (PR #529 review S1).
/// Pass 1 of the calendar import decides which claims a file needs from it, and the spooler decides what it stores from
/// it, so the two can never disagree about whether an attachment is inline or a photo: a quoted, padded or
/// case-varied <c>X-ODYSSEY-KIND</c> or <c>ENCODING</c> reads the same everywhere, and a parameter smuggled inside
/// another's quoted value (<c>FILENAME="a;X-ODYSSEY-KIND=PHOTO"</c>) is never a parameter.
/// </summary>
internal sealed class IcsAttachHead
{
    private IcsAttachHead(string name, Dictionary<string, string> parameters)
    {
        Name = name;
        Parameters = parameters;
    }

    /// <summary>The property name, trimmed.</summary>
    public string Name { get; }

    /// <summary>Parameters by case-insensitive name; a quoted value is unquoted and every value trimmed. First wins.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    public bool IsAttach => string.Equals(Name, "ATTACH", StringComparison.OrdinalIgnoreCase);

    /// <summary>Inline content (<c>ENCODING=BASE64</c>), whatever <c>VALUE</c> says.</summary>
    public bool IsInline => IsAttach && Is("ENCODING", "BASE64");

    /// <summary>A library photo (<c>X-ODYSSEY-KIND=PHOTO</c>); any other kind, or none, is a file.</summary>
    public bool IsPhoto => IsAttach && Is("X-ODYSSEY-KIND", "PHOTO");

    public string? this[string name] => Parameters.GetValueOrDefault(name);

    private bool Is(string name, string expected) =>
        Parameters.TryGetValue(name, out var value) && string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a head: everything before the value's <c>:</c>, which the caller has already found unquoted.</summary>
    public static IcsAttachHead Parse(string head)
    {
        var parts = Split(head);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var value = part[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1].Trim();
            }

            parameters.TryAdd(part[..equals].Trim(), value);
        }

        return new IcsAttachHead(parts.Count == 0 ? string.Empty : parts[0].Trim(), parameters);
    }

    // Splits on ';' outside a quoted value.
    private static List<string> Split(string head)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in head)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }

            if (c == ';' && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        return parts;
    }
}
