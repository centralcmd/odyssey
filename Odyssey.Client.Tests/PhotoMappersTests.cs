using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Unit coverage for <see cref="PhotoMappers"/> — the single source of truth for the Photos DTO
/// mappings shared by the Photos library and the Journal photo integration. The full-body PUT built by
/// <see cref="PhotoMappers.ToUpdate"/> must preserve every editable field (a divergence silently wipes
/// data on a favourite/archive toggle — PR #326), map the nullable timestamps to their bool arms, and
/// deliberately leave <c>FileName</c> unset so a metadata toggle never touches the file name.
/// </summary>
public class PhotoMappersTests
{
    private static ExistingPhoto SamplePhoto(DateTime? archived = null, DateTime? favourited = null) => new()
    {
        PhotoId = Guid.NewGuid(),
        FileId = Guid.NewGuid(),
        FileName = "beach.jpg",
        Title = "Beach",
        Caption = "A day out",
        TakenAt = new DateTime(2026, 6, 1, 9, 30, 0, DateTimeKind.Utc),
        CapturedLatitude = 59.91,
        CapturedLongitude = 10.75,
        LocationName = "Oslo",
        PixelWidth = 4032,
        PixelHeight = 3024,
        Archived = archived,
        Favourited = favourited,
        CreatedByUserId = "user-1",
        CreatedByName = "Ada",
        CreatedAt = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc),
        TagIds = [Guid.NewGuid(), Guid.NewGuid()],
        PersonContactIds = [Guid.NewGuid()],
        AlbumIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
    };

    [Fact]
    public void ToUpdate_preserves_every_editable_field()
    {
        var p = SamplePhoto();

        var u = PhotoMappers.ToUpdate(p);

        Assert.Equal(p.Title, u.Title);
        Assert.Equal(p.Caption, u.Caption);
        Assert.Equal(p.TakenAt, u.TakenAt);
        Assert.Equal(p.CapturedLatitude, u.CapturedLatitude);
        Assert.Equal(p.CapturedLongitude, u.CapturedLongitude);
        Assert.Equal(p.LocationName, u.LocationName);
        Assert.Equal(p.PixelWidth, u.PixelWidth);
        Assert.Equal(p.PixelHeight, u.PixelHeight);
        Assert.Equal(p.TagIds, u.TagIds);
        Assert.Equal(p.PersonContactIds, u.PersonContactIds);
        Assert.Equal(p.AlbumIds, u.AlbumIds);
    }

    [Fact]
    public void ToUpdate_leaves_FileName_unset_so_a_toggle_never_renames_the_file()
    {
        var u = PhotoMappers.ToUpdate(SamplePhoto());

        Assert.Null(u.FileName);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ToUpdate_maps_nullable_timestamps_to_their_bool_arms(bool archived, bool favourited)
    {
        var when = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);
        var p = SamplePhoto(archived ? when : null, favourited ? when : null);

        var u = PhotoMappers.ToUpdate(p);

        Assert.Equal(archived, u.Archived);
        Assert.Equal(favourited, u.Favourite);
    }

    [Fact]
    public void ToSummary_projects_ids_state_and_counts()
    {
        var p = SamplePhoto(favourited: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        var s = PhotoMappers.ToSummary(p);

        Assert.Equal(p.PhotoId, s.PhotoId);
        Assert.Equal(p.FileId, s.FileId);
        Assert.Equal(p.Title, s.Title);
        Assert.Equal(p.Archived, s.Archived);
        Assert.Equal(p.Favourited, s.Favourited);
        Assert.Equal(p.CreatedByUserId, s.CreatedByUserId);
        Assert.Equal(p.TagIds, s.TagIds);
        Assert.Equal(p.PersonContactIds, s.PersonContactIds);
        Assert.Equal(p.PersonContactIds.Count, s.PersonCount);
        Assert.Equal(p.AlbumIds.Count, s.AlbumCount);
    }

    [Fact]
    public void MinimalSummary_carries_the_ids_and_safe_defaults()
    {
        var photoId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        var s = PhotoMappers.MinimalSummary(photoId, fileId);

        Assert.Equal(photoId, s.PhotoId);
        Assert.Equal(fileId, s.FileId);
        // Null, not empty: the author is genuinely unknown on this path, and the attribution columns
        // became nullable when they turned into real foreign keys to AspNetUsers. The empty-string
        // placeholder only ever existed because the property used to be `required string`.
        Assert.Null(s.CreatedByUserId);
        Assert.Equal(0, s.PersonCount);
        Assert.Equal(0, s.AlbumCount);
        Assert.Null(s.Archived);
        Assert.Null(s.Favourited);
    }
}
