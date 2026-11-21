using Odyssey.Dtos.Books;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>The shared ISBN rule (issue #527 §8.3).</summary>
public class IsbnRulesTests
{
    [Theory]
    [InlineData("978-0-306-40615-7", "9780306406157")]
    [InlineData(" 978 - 0 - 306 - 40615 - 7 ", "9780306406157")]
    [InlineData("0-306-40615-2", "0306406152")]
    [InlineData("0-8044-2957-x", "080442957X")]
    [InlineData("979-10-90636-07-1", "9791090636071")]
    [InlineData("978-0-306-40614-0", "9780306406140")]
    public void Valid_inputs_normalise(string input, string expected)
    {
        Assert.True(IsbnRules.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_is_valid_and_null(string? input)
    {
        Assert.True(IsbnRules.TryNormalize(input, out var normalized));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("9780306406158")]
    [InlineData("030640615X")]
    [InlineData("1234567890123")]
    [InlineData("97803064061")]
    [InlineData("978030640615a")]
    [InlineData("X306406152")]
    [InlineData("\u0669\u0667\u0668\u0660\u0663\u0660\u0666\u0664\u0660\u0666\u0661\u0665\u0667")]
    [InlineData("\uFF19\uFF17\uFF18\uFF10\uFF13\uFF10\uFF16\uFF14\uFF10\uFF16\uFF11\uFF15\uFF17")]
    public void Invalid_inputs_are_refused(string input) => Assert.False(IsbnRules.TryNormalize(input, out _));

    [Theory]
    [InlineData("0306406152", "9780306406157")]
    [InlineData("9780306406157", "0306406152")]
    [InlineData("9791090636071", null)]
    [InlineData("080442957X", "9780804429573")]
    [InlineData("9780804429573", "080442957X")]
    public void Counterpart_converts_978_only(string normalized, string? expected) =>
        Assert.Equal(expected, IsbnRules.Counterpart(normalized));
}
