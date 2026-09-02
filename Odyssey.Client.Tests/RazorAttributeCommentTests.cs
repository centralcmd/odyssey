using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lint against a Razor comment placed <b>inside a component's attribute list</b>:
/// <code>
/// &lt;OdsInfoTile Icon="shield"
///              @* why this label … *@
///              Label="@Label"&gt;
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Razor does not strip a <c>@* … *@</c> there. It parses the whole comment as an <b>attribute
/// name</b>, so the component is handed a parameter called "@* why this label … *@" and
/// <c>ComponentProperties.SetProperties</c> throws
/// <c>InvalidOperationException: Object of type 'X' does not have a property matching the name …</c>
/// — at render time, in the browser, taking the whole component subtree down with it.
/// </para>
/// <para>
/// It belongs in a lint for the same reason <see cref="RazorStringBindingTests"/> does: <b>the file
/// compiles</b>. Nothing in the build, and no test that does not actually render the component,
/// notices — the first symptom is an unhandled exception in the WASM renderer. A comment one line
/// higher, above the tag, is always valid; the fix is never to delete the explanation.
/// </para>
/// <para>
/// The scan walks each element's opening tag by hand rather than with one regex: attribute values
/// are quoted and may legitimately contain <c>@*</c>-looking text, and a nested <c>&lt;</c> means the
/// tag never closed the way a naive match assumed.
/// </para>
/// </remarks>
public class RazorAttributeCommentTests
{
    [Fact]
    public void No_razor_comment_sits_inside_a_component_or_element_attribute_list()
    {
        var offenders = new List<string>();

        foreach (var file in ClientSource.RazorFiles())
        {
            var text = File.ReadAllText(file);

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '<' || i + 1 >= text.Length || !IsTagNameStart(text[i + 1]))
                {
                    continue;
                }

                // Advance past the tag name to where attributes begin.
                var cursor = i + 1;
                while (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] is '.' or '_'))
                {
                    cursor++;
                }

                var start = cursor;
                var commented = false;

                while (cursor < text.Length)
                {
                    var c = text[cursor];
                    if (c == '"')
                    {
                        // Skip the quoted value wholesale: "@*" inside one is data, not a comment.
                        var close = text.IndexOf('"', cursor + 1);
                        if (close < 0)
                        {
                            break;
                        }

                        cursor = close + 1;
                        continue;
                    }

                    if (c == '>' || c == '<')
                    {
                        break;
                    }

                    if (c == '@' && cursor + 1 < text.Length && text[cursor + 1] == '*')
                    {
                        commented = true;
                        break;
                    }

                    cursor++;
                }

                if (commented)
                {
                    offenders.Add($"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, start)}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A @* … *@ comment inside an element's attribute list is parsed as an ATTRIBUTE NAME, so "
            + "the component throws at render time with 'does not have a property matching the "
            + "name …'. It compiles, so only this lint or an actual render catches it. Move the "
            + "comment to the line above the opening tag — never delete it. Found at:\n"
            + string.Join("\n", offenders));
    }

    private static bool IsTagNameStart(char c) => char.IsLetter(c);
}
