using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// An album's icon and colour (issue #404): the album glyph rule, the service's own guards for non-HTTP
/// callers, and the list of every column that holds a <see cref="TagIcons"/> key.
/// </summary>
public class PhotoAlbumIconColorTests
{
    [Fact]
    public void The_album_default_is_not_a_catalogue_member()
    {
        Assert.False(TagIcons.IsKnown(PhotoAlbumIcons.Default));
        Assert.DoesNotContain(TagIcons.All, option => option.Key == PhotoAlbumIcons.Default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not_an_icon_zq")]
    [InlineData(TagIcons.Default)]
    [InlineData(PhotoAlbumIcons.Default)]
    [InlineData("Landscape")]
    public void Glyph_falls_back_to_the_album_default(string? key) =>
        Assert.Equal(PhotoAlbumIcons.Default, PhotoAlbumIcons.Glyph(key));

    [Fact]
    public void Glyph_returns_a_known_key_itself() => Assert.Equal("landscape", PhotoAlbumIcons.Glyph("landscape"));

    /// <summary>
    /// Every entity property holding a catalogue key, declared once (issue #404 §7.11). A key retirement
    /// enumerates its tables from this list, so a sixth holder must be added here deliberately.
    /// </summary>
    [Fact]
    public void Every_catalogue_key_column_is_declared()
    {
        string[] declared =
        [
            $"{nameof(TransactionTag)}.{nameof(TransactionTag.Icon)}",
            $"{nameof(JournalTag)}.{nameof(JournalTag.Icon)}",
            $"{nameof(JournalTaskTag)}.{nameof(JournalTaskTag.Icon)}",
            $"{nameof(PhotoTag)}.{nameof(PhotoTag.Icon)}",
            $"{nameof(PhotoAlbum)}.{nameof(PhotoAlbum.Icon)}",
        ];

        using var context = TestContextFactory.Create();
        var actual = context.Model.GetEntityTypes()
            .SelectMany(type => type.GetProperties()
                .Where(property => property.Name == "Icon" && property.ClrType == typeof(string))
                .Select(property => $"{type.ClrType.Name}.{property.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(actual);
        Assert.Equal(declared.Order(StringComparer.Ordinal), actual);
    }

    [Theory]
    [InlineData("<svg>", null, "Icon")]
    [InlineData(TagIcons.Default, null, "Icon")]
    [InlineData(null, "#12345Z", "Color")]
    [InlineData(null, "", "Color")]
    public async Task Create_and_update_refuse_an_invalid_value_before_touching_the_database(string? icon, string? color, string field)
    {
        await using var context = TestContextFactory.Create();
        var service = new PhotoAlbumService(context, new ClientDateHandlingTests.StubJournalLimits());
        var existing = await service.Create(new NewPhotoAlbum { Name = "Keep", Icon = "home", Color = "#0369A1" }, "user");

        var create = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.Create(new NewPhotoAlbum { Name = "New", Icon = icon, Color = color, PhotoIds = [Guid.NewGuid()] }, "user"));
        var update = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.Update(existing.PhotoAlbumId, new UpdatePhotoAlbum { Name = "Keep", Icon = icon, Color = color, PhotoIds = [Guid.NewGuid()] }, "user"));

        foreach (var error in new[] { create, update })
        {
            Assert.True(error.Errors!.ContainsKey(field));
        }

        Assert.Equal(1, context.PhotoAlbums.Count());
        var stored = Assert.Single(context.PhotoAlbums);
        Assert.Equal("home", stored.Icon);
        Assert.Equal("#0369A1", stored.Color);
    }

    [Fact]
    public async Task Writes_store_the_colour_upper_case_and_reads_normalise_stale_values()
    {
        await using var context = TestContextFactory.Create();
        var service = new PhotoAlbumService(context, new ClientDateHandlingTests.StubJournalLimits());

        var created = await service.Create(new NewPhotoAlbum { Name = "A", Icon = "landscape", Color = "#abcdef" }, "user");
        Assert.Equal("#ABCDEF", Assert.Single(context.PhotoAlbums).Color);
        Assert.Equal("#ABCDEF", created.Color);

        var stored = Assert.Single(context.PhotoAlbums);
        stored.Icon = "retired_key";
        stored.Color = "#GGGGGG";
        await context.SaveChangesAsync();

        var read = await service.Get(created.PhotoAlbumId);
        Assert.Null(read!.Icon);
        Assert.Null(read.Color);
        var row = Assert.Single((await service.ListAsync(new AlbumsQueryParams())).Items);
        Assert.Null(row.Icon);
        Assert.Null(row.Color);
    }
}
