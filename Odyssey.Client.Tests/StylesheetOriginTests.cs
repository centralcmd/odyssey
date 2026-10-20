using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client's stylesheets load nothing from another origin. <c>THIRD-PARTY-NOTICES.md</c> states the
/// shipped app makes no third-party requests at runtime, which is why the webfonts are self-hosted, and
/// design-system CSS is now ported by hand from an external artifact whose own stylesheets do import from
/// Google Fonts and CDNs. An <c>@import</c> or <c>url()</c> carried across in such a port would make every
/// page view a request to that host; this lint is what stops it.
/// </summary>
public class StylesheetOriginTests
{
    private static readonly Regex ExternalReference = new(
        @"(@import\s+(url\(\s*)?|url\(\s*)['""]?(https?:)?//",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void No_client_stylesheet_references_another_origin()
    {
        var offenders = Directory.EnumerateFiles(ClientSource.Root, "*.css", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .SelectMany(file =>
            {
                var css = Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
                return ExternalReference.Matches(css)
                    .Select(m => $"{ClientSource.Relative(file)}:{ClientSource.LineAt(css, m.Index)}");
            })
            .ToList();

        Assert.True(offenders.Count == 0,
            "Client stylesheets must not load from another origin; self-host the asset instead: "
            + string.Join(", ", offenders));
    }

    private static bool IsBuildOutput(string file)
    {
        var segments = Path.GetRelativePath(ClientSource.Root, file).Split(Path.DirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj");
    }
}
