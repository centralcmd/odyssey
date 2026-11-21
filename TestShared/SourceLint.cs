// Linked (not referenced) into every test project with a source-lint, so every lint strips comments by one
// definition instead of a hand-rolled regex per file — each with its own false negatives (a `//.*$` that eats
// the rest of a line after "https://" inside a string) or false positives (a line-start-only `//` that keeps
// a trailing comment).
// Every type here is internal: each test assembly compiles its own copy.

using System.Text;

namespace Odyssey.Testing;

/// <summary>
/// Comment and string stripping for source-lints over C# and Razor. Documentation legitimately DISCUSSES the very
/// things the lints look for, and a lint a doc comment can fail is a lint that gets the docs deleted.
/// </summary>
/// <remarks>
/// <para>
/// A small tokenizer, not a regex: it walks regular, verbatim (<c>@"…"</c>), interpolated (<c>$"…"</c>,
/// <c>$@"…"</c>, <c>@$"…"</c>) and raw (<c>"""…"""</c>, <c>$$"""…"""</c>) string literals and char literals, so a
/// <c>//</c> or <c>/*</c> inside one is never mistaken for a comment, and recurses into interpolation holes, where
/// code (and further strings) can appear. It removes <c>//</c> and <c>///</c> line comments, <c>/* */</c> block
/// comments and Razor <c>@* *@</c> comments.
/// </para>
/// <para>
/// Removed text is replaced by spaces and every line break is kept, so a match index or line number in the output
/// is the same index or line number in the input.
/// </para>
/// <para>
/// Razor markup is tokenized as if it were C#, which needs three allowances: a <c>//</c> directly after
/// <c>letter-or-digit:</c> is a URL scheme (<c>https://</c>), not a comment — C# code never has that shape; a
/// <c>'</c> is a char literal only when one closes it on the same line, so an apostrophe in prose stays prose; and
/// a regular string ends at a line break, so an unbalanced <c>"</c> in prose cannot swallow the rest of the file.
/// </para>
/// </remarks>
internal static class SourceLint
{
    /// <summary><paramref name="source"/> with every comment replaced by spaces; strings and line breaks are kept.</summary>
    public static string StripComments(string source) => new Scanner(source, blankStrings: false).Run();

    /// <summary>
    /// <paramref name="source"/> with every comment AND the content of every string and char literal replaced by
    /// spaces. Delimiters and interpolation holes are kept, so the code around and inside a string still reads.
    /// For lints that look for identifiers and must not be satisfied (or tripped) by one mentioned in a message.
    /// </summary>
    public static string StripCommentsAndStrings(string source) => new Scanner(source, blankStrings: true).Run();

    /// <summary>The file at <paramref name="path"/>, comments stripped.</summary>
    public static string ReadWithoutComments(string path) => StripComments(File.ReadAllText(path));

    /// <summary>The 1-based line number containing <paramref name="index"/> in <paramref name="text"/>.</summary>
    public static int LineAt(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private sealed class Scanner(string source, bool blankStrings)
    {
        private readonly StringBuilder output = new(source.Length);
        private int position;

        public string Run()
        {
            ScanCode(inHole: false);
            return output.ToString();
        }

        private char At(int offset) => position + offset < source.Length ? source[position + offset] : '\0';

        /// <summary>
        /// Code until end of input or, inside an interpolation hole, until the unmatched <c>}</c> that closes the
        /// hole (left unconsumed for the caller).
        /// </summary>
        private void ScanCode(bool inHole)
        {
            var depth = 0;
            while (position < source.Length)
            {
                var c = source[position];
                if (inHole && c == '{')
                {
                    depth++;
                }
                else if (inHole && c == '}')
                {
                    if (depth == 0)
                    {
                        return;
                    }

                    depth--;
                }

                if (c == '/' && At(1) == '/' && !IsUrlScheme())
                {
                    BlankUntil(LineEnd());
                }
                else if (c == '/' && At(1) == '*')
                {
                    BlankUntil(BlockEnd("*/", position + 2));
                }
                else if (c == '@' && At(1) == '*')
                {
                    BlankUntil(BlockEnd("*@", position + 2));
                }
                else if (!TryString() && !TryChar())
                {
                    Copy(1);
                }
            }
        }

        private bool IsUrlScheme() =>
            position >= 2 && source[position - 1] == ':' && char.IsAsciiLetterOrDigit(source[position - 2]);

        private int LineEnd()
        {
            var end = position;
            while (end < source.Length && source[end] is not ('\n' or '\r'))
            {
                end++;
            }

            return end;
        }

        private int BlockEnd(string terminator, int from)
        {
            var end = source.IndexOf(terminator, from, StringComparison.Ordinal);
            return end < 0 ? source.Length : end + terminator.Length;
        }

        private bool TryString()
        {
            var start = position;
            var cursor = position;
            var dollars = 0;
            var verbatim = false;
            while (cursor < source.Length && (source[cursor] == '$' || (source[cursor] == '@' && !verbatim)))
            {
                if (source[cursor] == '$')
                {
                    dollars++;
                }
                else
                {
                    verbatim = true;
                }

                cursor++;
            }

            if (cursor >= source.Length || source[cursor] != '"')
            {
                return false;
            }

            var quotes = 0;
            while (cursor + quotes < source.Length && source[cursor + quotes] == '"')
            {
                quotes++;
            }

            Copy(cursor - start);
            if (quotes >= 3 && !verbatim)
            {
                RawString(quotes, dollars);
            }
            else if (quotes == 2 && !verbatim)
            {
                Copy(2);
            }
            else
            {
                Copy(1);
                QuotedString(verbatim, interpolated: dollars > 0);
            }

            return true;
        }

        private void QuotedString(bool verbatim, bool interpolated)
        {
            while (position < source.Length)
            {
                var c = source[position];
                if (!verbatim && c == '\\')
                {
                    Content(Math.Min(2, source.Length - position));
                }
                else if (verbatim && c == '"' && At(1) == '"')
                {
                    Content(2);
                }
                else if (c == '"')
                {
                    Copy(1);
                    return;
                }
                else if (!verbatim && c is '\n' or '\r')
                {
                    return;
                }
                else if (interpolated && c == '{' && At(1) == '{')
                {
                    Content(2);
                }
                else if (interpolated && c == '{')
                {
                    Hole(braces: 1);
                }
                else
                {
                    Content(1);
                }
            }
        }

        private void RawString(int quotes, int dollars)
        {
            Copy(quotes);
            while (position < source.Length)
            {
                var run = RunOf(source[position]);
                if (source[position] == '"' && run >= quotes)
                {
                    Copy(run);
                    return;
                }

                if (dollars > 0 && source[position] == '{' && run >= dollars)
                {
                    Content(run - dollars);
                    Hole(dollars);
                }
                else
                {
                    Content(source[position] is '"' or '{' ? run : 1);
                }
            }
        }

        private void Hole(int braces)
        {
            Copy(braces);
            ScanCode(inHole: true);
            Copy(Math.Min(RunOf('}'), braces));
        }

        private int RunOf(char c)
        {
            var run = 0;
            while (position + run < source.Length && source[position + run] == c)
            {
                run++;
            }

            return run;
        }

        private bool TryChar()
        {
            if (source[position] != '\'')
            {
                return false;
            }

            var cursor = position + 1;
            if (cursor < source.Length && source[cursor] == '\\')
            {
                cursor += 2;
                while (cursor < source.Length && cursor - position <= 10 && source[cursor] is not ('\'' or '\n' or '\r'))
                {
                    cursor++;
                }
            }
            else if (cursor < source.Length && source[cursor] is not ('\'' or '\n' or '\r'))
            {
                cursor++;
            }
            else
            {
                return false;
            }

            if (cursor >= source.Length || source[cursor] != '\'')
            {
                return false;
            }

            Copy(1);
            Content(cursor - position);
            Copy(1);
            return true;
        }

        private void Copy(int count)
        {
            output.Append(source, position, count);
            position += count;
        }

        private void Content(int count)
        {
            if (blankStrings)
            {
                Blank(count);
            }
            else
            {
                Copy(count);
            }
        }

        private void BlankUntil(int end) => Blank(end - position);

        private void Blank(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var c = source[position + i];
                output.Append(c is '\n' or '\r' ? c : ' ');
            }

            position += count;
        }
    }
}
