namespace Odyssey.Dtos.Books;

/// <summary>
/// ISBN normalisation and checksum (issue #527 §8.3), shared by the server and the WASM client so both validate one
/// way. Spaces and hyphens are removed and a trailing <c>x</c> is upper-cased; what remains must be a valid ISBN-10
/// (mod-11) or a <c>978</c>/<c>979</c> ISBN-13 (mod-10). The normalised form is stored and returned unconverted.
/// </summary>
public static class IsbnRules
{
    /// <summary>
    /// The normalised value, <c>null</c> for an empty or whitespace input, or <c>false</c> when the input is not a valid
    /// ISBN. Never throws.
    /// </summary>
    public static bool TryNormalize(string? input, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        var chars = new List<char>(input.Length);
        foreach (var c in input)
        {
            if (c is ' ' or '-' or '‐' or '‑' or '‒' or '–' or ' ')
            {
                continue;
            }

            chars.Add(c);
        }

        if (chars.Count > 0 && chars[^1] == 'x')
        {
            chars[^1] = 'X';
        }

        var candidate = new string(chars.ToArray());
        if (candidate.Length == 10 && IsValidIsbn10(candidate))
        {
            normalized = candidate;
            return true;
        }

        if (candidate.Length == 13 && IsValidIsbn13(candidate))
        {
            normalized = candidate;
            return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="input"/> is empty or a valid ISBN — the client's field rule.</summary>
    public static bool IsValidOrEmpty(string? input) => TryNormalize(input, out _);

    /// <summary>The other form of a normalised ISBN for search: ISBN-10 ↔ <c>978</c> ISBN-13; <c>null</c> when none.</summary>
    public static string? Counterpart(string normalized)
    {
        if (normalized.Length == 10)
        {
            var body = "978" + normalized[..9];
            return body + Isbn13CheckDigit(body);
        }

        if (normalized.Length == 13 && normalized.StartsWith("978", StringComparison.Ordinal))
        {
            var body = normalized[3..12];
            return body + Isbn10CheckDigit(body);
        }

        return null;
    }

    private static bool IsValidIsbn10(string value)
    {
        for (var i = 0; i < 9; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }
        }

        var last = value[9];
        if (!char.IsAsciiDigit(last) && last != 'X')
        {
            return false;
        }

        return Isbn10CheckDigit(value[..9]) == last;
    }

    private static bool IsValidIsbn13(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return (value.StartsWith("978", StringComparison.Ordinal) || value.StartsWith("979", StringComparison.Ordinal))
            && Isbn13CheckDigit(value[..12]) == value[12];
    }

    private static char Isbn10CheckDigit(string nineDigits)
    {
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (10 - i) * (nineDigits[i] - '0');
        }

        var check = (11 - (sum % 11)) % 11;
        return check == 10 ? 'X' : (char)('0' + check);
    }

    private static char Isbn13CheckDigit(string twelveDigits)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            sum += (i % 2 == 0 ? 1 : 3) * (twelveDigits[i] - '0');
        }

        return (char)('0' + ((10 - (sum % 10)) % 10));
    }
}
