namespace Odyssey.Client.Tests;

/// <summary>
/// Locates the files in <c>Odyssey Design System/</c> that the drift guards compare the client with.
///
/// <para>
/// The design system moved from the standalone Claude Design project to a Design System artifact,
/// and the artifact's export lays the stylesheets out differently: the theme tokens are declared in
/// <c>tokens.css</c>, which <c>colors_and_type.css</c> now only imports, and <c>components.css</c>
/// became <c>components/bundle.css</c>. A guard pointed at the old path would fail on the first such
/// export for want of a file, not for any drift. Each member resolves whichever layout is checked in,
/// preferring the artifact's, so the guards keep reading real declarations across that export.
/// </para>
/// </summary>
internal static class DesignSystemSource
{
    /// <summary>The design system's token declarations, relative to its root.</summary>
    public static string TokensCss => FirstPresent("tokens.css", "colors_and_type.css");

    /// <summary>The design system's component stylesheet, relative to its root.</summary>
    public static string ComponentsCss => FirstPresent(Path.Combine("components", "bundle.css"), "components.css");

    /// <summary>Absolute path of <paramref name="relativePath"/> inside the design system.</summary>
    public static string File(string relativePath) =>
        ClientSource.Sibling(Path.Combine("Odyssey Design System", relativePath));

    private static string FirstPresent(string artifactLayout, string standaloneLayout) =>
        System.IO.File.Exists(File(artifactLayout)) ? artifactLayout : standaloneLayout;
}
