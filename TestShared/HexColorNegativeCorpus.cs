namespace Odyssey.TestShared;

/// <summary>
/// The ONE list of colour inputs every layer must reject (issues #392 AC 5, #394 AC 12): the server's
/// write attribute and service guard, every read projection, and the client's style sink. Linked into
/// each test project that checks a colour, so the layers cannot be tested against different lists.
/// </summary>
public static class HexColorNegativeCorpus
{
    public static IReadOnlyList<string> All { get; } =
    [
        "",
        "#FFF",
        "#FFFFFFFF",
        "#77AA33FF",
        "FFFFFF",
        " #FFFFFF",
        "#FFFFFF ",
        "#FFFFFF\n",
        "#FFFFF\n",
        "#FFFFF\u0000",
        "#GGGGGG",
        "red",
        "rgb(0,0,0)",
        "#FFFFFF;",
        "#FFF)",
        "red;x:y",
        "#fff;a",
        "#ＦＦＦＦＦＦ",
        "#٠١٢٣٤٥",
    ];

    /// <summary>The corpus as xUnit theory data.</summary>
    public static IEnumerable<object[]> TheoryData => All.Select(value => new object[] { value });
}
