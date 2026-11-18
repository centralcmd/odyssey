using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints for the album icon work (issue #406): the album glyph has one declaration, and an album
/// <c>PUT</c> body is built in only two places.
/// </summary>
public class AlbumSourceLintTests
{
    /// <summary>
    /// Every client source file the album-glyph lint reads: razor, C#, CSS and JS under
    /// <c>Odyssey.Client/</c>, excluding build output and vendored <c>wwwroot/lib/</c>.
    /// </summary>
    private static List<string> GlyphScanSet() =>
        Directory.EnumerateFiles(ClientSource.Root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".css", StringComparison.Ordinal) || path.EndsWith(".js", StringComparison.Ordinal))
            .Where(path => !IsExcluded(path))
            .ToList();

    /// <summary>
    /// <c>PhotoAlbumIcons.Default</c> (<c>Odyssey.Dtos</c>) is the album glyph's single declaration (§9.4).
    /// Comments are not exempt: say "album glyph" in prose.
    /// </summary>
    [Fact]
    public void No_client_file_spells_the_album_glyph_literal()
    {
        var files = GlyphScanSet();
        Assert.NotEmpty(files);
        Assert.Contains(files, path => Path.GetFileName(path) == "NavModel.cs");
        Assert.Contains(files, path => Path.GetFileName(path) == "PhotoLibraryOverview.razor");

        var offenders = files
            .Where(path => File.ReadAllText(path).Contains("photo_album", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(ClientSource.Root, path))
            .ToList();
        Assert.True(offenders.Count == 0, "Use PhotoAlbumIcons.Default instead of the literal in:\n" + string.Join('\n', offenders));
    }

    /// <summary>
    /// <c>PUT /api/albums/{id}</c> is full replacement, so a hand-built body that forgets a field clears it.
    /// Only the album form (which owns every field) and <c>AlbumWrites.From</c> may build one (§3.4).
    /// </summary>
    [Fact]
    public void Only_the_album_form_and_AlbumWrites_build_an_album_update()
    {
        string[] allowed = ["AlbumWrites.cs", "AlbumFormDialog.razor.cs"];
        var files = Directory.EnumerateFiles(ClientSource.Root, "*.*", SearchOption.AllDirectories)
            .Where(path => (path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal)) && !IsExcluded(path))
            .ToList();

        var builders = files.Where(path => BuildsAnUpdate(File.ReadAllText(path))).Select(Path.GetFileName).ToList();
        Assert.Contains("AlbumWrites.cs", builders);
        Assert.Contains("AlbumFormDialog.razor.cs", builders);

        var offenders = builders.Where(name => !allowed.Contains(name)).ToList();
        Assert.True(offenders.Count == 0, "Build an album update with AlbumWrites.From(album) in:\n" + string.Join('\n', offenders));
    }

    [Theory]
    [InlineData("var u = new UpdatePhotoAlbum { Name = a.Name };")]
    [InlineData("await Albums.UpdateAsync(id, new UpdatePhotoAlbum\n{")]
    [InlineData("UpdatePhotoAlbum u = new() { Name = n };")]
    public void Each_seeded_update_construction_is_caught(string code) => Assert.True(BuildsAnUpdate(code));

    private static bool BuildsAnUpdate(string code) =>
        Regex.IsMatch(code, @"\bnew\s+UpdatePhotoAlbum\b") || Regex.IsMatch(code, @"\bUpdatePhotoAlbum\s+\w+\s*=\s*new\s*\(");

    private static bool IsExcluded(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
            || path.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
            || path.Contains($"{sep}wwwroot{sep}lib{sep}", StringComparison.Ordinal);
    }
}
