using Odyssey.Testing;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The shared comment stripper every source-lint reads through (<c>TestShared/SourceLint.cs</c>). A lint is only as
/// good as this: a stripper that eats code produces false negatives, one that keeps comments false positives.
/// </summary>
public sealed class SourceLintTests
{
    [Theory]
    [InlineData("var x = 1; // trailing", "var x = 1;            ")]
    [InlineData("// whole line", "             ")]
    [InlineData("/// <summary>doc</summary>", "                          ")]
    [InlineData("a /* block */ b", "a             b")]
    [InlineData("a /** doc */ b", "a            b")]
    [InlineData("<p>@* razor *@</p>", "<p>           </p>")]
    [InlineData("x = 1 /*/ still comment */;", "x = 1                     ;")]
    public void Removes_every_comment_kind(string source, string expected) =>
        Assert.Equal(expected, SourceLint.StripComments(source));

    [Theory]
    [InlineData("var url = \"https://example.com\"; Call();")]
    [InlineData("var s = \"/* not a comment */\";")]
    [InlineData("var s = \"@* not razor *@\";")]
    [InlineData("var s = \"escaped \\\" // quote\";")]
    [InlineData("var s = @\"C:\\path // here\";")]
    [InlineData("var s = @\"quote \"\" // inside\";")]
    [InlineData("var s = $\"{a} // {b}\";")]
    [InlineData("var s = $@\"{a} // \"\"x\"\"\";")]
    [InlineData("var s = @$\"{a} // \";")]
    [InlineData("var s = $\"{{literal}} // {x}\";")]
    [InlineData("var s = $\"{(flag ? \"a//b\" : \"c\")} // tail\";")]
    [InlineData("var s = \"\"\"raw \" and \"\" // not comment\"\"\";")]
    [InlineData("var s = $$\"\"\"{ {{value}} } // raw\"\"\";")]
    [InlineData("var e = \"\"; var f = \"//\";")]
    [InlineData("var c = '/'; var d = '\\''; var e = '\"'; var f = \"//\";")]
    [InlineData("var u = '\\u002F'; var s = \"/*\";")]
    [InlineData("<a href=\"https://example.com\">https://example.com</a>")]
    public void Keeps_code_and_literals_untouched(string source) =>
        Assert.Equal(source, SourceLint.StripComments(source));

    [Fact]
    public void Strips_a_comment_after_a_string_that_contains_a_url()
    {
        const string source = "Load(\"https://x/y\"); // Load(\"forbidden\")";

        var stripped = SourceLint.StripComments(source);

        Assert.Equal("Load(\"https://x/y\");                     ", stripped);
        Assert.DoesNotContain("forbidden", stripped);
    }

    [Fact]
    public void Strips_comments_inside_an_interpolation_hole()
    {
        const string source = "var s = $\"\"\"\n{Call(/* why */ x)}\n\"\"\";";

        Assert.Equal("var s = $\"\"\"\n{Call(          x)}\n\"\"\";", SourceLint.StripComments(source));
    }

    [Fact]
    public void Multiline_constructs_keep_their_line_breaks()
    {
        const string source = "a\n/* one\r\ntwo */\nb @* three\nfour *@ c\nvar s = @\"x\ny\"; // z\nd";

        var stripped = SourceLint.StripComments(source);

        Assert.Equal(source.Length, stripped.Length);
        Assert.Equal(source.Split('\n').Length, stripped.Split('\n').Length);
        Assert.Equal("a\n      \r\n      \nb         \n        c\nvar s = @\"x\ny\";     \nd", stripped);
        Assert.Equal(SourceLint.LineAt(source, source.IndexOf('d')), SourceLint.LineAt(stripped, stripped.IndexOf('d')));
    }

    [Fact]
    public void Raw_strings_span_lines_and_keep_their_content()
    {
        const string source = "var s = \"\"\"\n    // kept\n    /* kept */\n    \"\"\"; // gone";

        Assert.Equal("var s = \"\"\"\n    // kept\n    /* kept */\n    \"\"\";        ", SourceLint.StripComments(source));
    }

    [Fact]
    public void A_raw_string_closes_only_on_its_own_quote_count()
    {
        const string source = "var s = \"\"\"\"a \"\"\" b\"\"\"\"; // gone";

        Assert.Equal("var s = \"\"\"\"a \"\"\" b\"\"\"\";        ", SourceLint.StripComments(source));
    }

    [Fact]
    public void An_unterminated_regular_string_ends_at_the_line_break()
    {
        const string source = "<p>He said \"hi</p>\n// gone\n<b>kept</b>";

        Assert.Equal("<p>He said \"hi</p>\n       \n<b>kept</b>", SourceLint.StripComments(source));
    }

    [Fact]
    public void An_unterminated_block_comment_runs_to_the_end()
    {
        Assert.Equal("a        ", SourceLint.StripComments("a /* open"));
    }

    [Theory]
    [InlineData("Call(\"secret\");", "Call(\"      \");")]
    [InlineData("Call(@\"a\"\"b\");", "Call(@\"    \");")]
    [InlineData("Call($\"x{Name}y\");", "Call($\" {Name} \");")]
    [InlineData("Call($\"x{Get(\"k\")}y\");", "Call($\" {Get(\" \")} \");")]
    [InlineData("Call(\"\"\"raw\"\"\");", "Call(\"\"\"   \"\"\");")]
    [InlineData("Call($$\"\"\"a{{Id}}b\"\"\");", "Call($$\"\"\" {{Id}} \"\"\");")]
    [InlineData("Is('x') // note", "Is(' ')        ")]
    public void StripCommentsAndStrings_blanks_literal_content_but_keeps_holes(string source, string expected) =>
        Assert.Equal(expected, SourceLint.StripCommentsAndStrings(source));

    [Fact]
    public void Verbatim_identifiers_and_razor_transitions_are_not_strings()
    {
        const string source = "var @class = 1; @if (x) { <p>@Model.Name</p> } // c";

        Assert.Equal("var @class = 1; @if (x) { <p>@Model.Name</p> }     ", SourceLint.StripComments(source));
    }

    [Fact]
    public void A_url_scheme_outside_a_string_is_not_a_comment_but_a_spaced_one_is()
    {
        // The Razor allowance: `letter-or-digit:` directly before `//` reads as a scheme. C# code never has that
        // shape except a label with no space before its comment, which this pins as the known, benign edge (the
        // comment is kept, so a lint can only over-report).
        Assert.Equal("<p>see https://x.example</p>", SourceLint.StripComments("<p>see https://x.example</p>"));
        Assert.Equal("case 1:// x", SourceLint.StripComments("case 1:// x"));
        Assert.Equal("case 1:     ", SourceLint.StripComments("case 1: // x"));
    }

    [Fact]
    public void An_apostrophe_in_prose_is_not_a_char_literal()
    {
        Assert.Equal("<p>Don't                         ", SourceLint.StripComments("<p>Don't // stop at an apostrophe"));
    }

    [Fact]
    public void Shared_stripper_reads_the_real_tree()
    {
        var files = SourceTree.Files("Odyssey.Core").ToList();

        Assert.True(files.Count > 50, $"Scan set unexpectedly small ({files.Count}).");
        Assert.DoesNotContain(files, SourceTree.IsExcluded);
        Assert.All(files.Take(20), file =>
        {
            var text = File.ReadAllText(file);
            var stripped = SourceLint.StripComments(text);
            Assert.Equal(text.Length, stripped.Length);
            Assert.Equal(text.Count(c => c == '\n'), stripped.Count(c => c == '\n'));
        });
    }

    /// <summary>
    /// Agent worktrees are full checkouts under <c>.claude/worktrees/</c>; a repo-wide "exactly one implementation"
    /// lint that descended into them would find every file twice in the main checkout.
    /// </summary>
    [Theory]
    [InlineData(true, ".claude", "worktrees", "agent-1", "Odyssey.Core", "A.cs")]
    [InlineData(true, "Odyssey.Core", "obj", "Release", "A.cs")]
    [InlineData(true, "Odyssey.Core", "bin", "A.cs")]
    [InlineData(true, ".git", "A.cs")]
    [InlineData(true, "Odyssey.Client", "node_modules", "x", "A.js")]
    [InlineData(false, "Odyssey.Core", "Finance", "A.cs")]
    [InlineData(false, ".claude", "skills", "A.md")]
    [InlineData(false, "Odyssey.Core", "Binary", "A.cs")]
    public void The_tree_prunes_what_is_not_source(bool excluded, params string[] parts) =>
        Assert.Equal(excluded, SourceTree.IsExcluded(SourceTree.File(parts)));
}
