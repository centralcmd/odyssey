using System.Security.Cryptography;
using Odyssey.Context;
using Odyssey.TestData.Catalog;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic standalone library photos for the Photos module (issue #321), on top of the journal
/// photos (which are shared library records, created by <see cref="JournalEntryGenerator"/>). Exercises
/// every read surface: titled/captioned photos with extracted-style metadata, photo tags (incl. one
/// archived), a person link (to the seeded <c>Jane Smith (Landlord)</c> Person contact), and two
/// albums with covers and ordered membership. Each photo provisions its own backing Files-store record
/// (a real renderable PNG) returned separately for the finance context.
/// </summary>
public static class PhotoGenerator
{
    public sealed record Result(
        IReadOnlyList<Photo> Photos,
        IReadOnlyList<PhotoTag> Tags,
        IReadOnlyList<PhotoTagLink> TagLinks,
        IReadOnlyList<PhotoPerson> People,
        IReadOnlyList<PhotoAlbum> Albums,
        IReadOnlyList<PhotoAlbumItem> AlbumItems,
        IReadOnlyList<FileBlob> FileBlobs,
        IReadOnlyList<FileMetadata> FileMetadata);

    private sealed record TagSpec(string Name, string? Description, bool Archived);

    private sealed record PhotoSpec(
        string Key,
        string Title,
        string? Caption,
        int DaysBeforeAnchor,
        string? LocationName,
        double? Latitude,
        double? Longitude,
        string[] TagNames,
        bool LinkLandlordPerson,
        bool Archived);

    private sealed record AlbumSpec(string Key, string Name, string? Description, string[] PhotoKeys, string CoverKey);

    private static readonly TagSpec[] TagDefinitions =
    [
        new("Landscape", "Scenery and nature", false),
        new("Family", "People and gatherings", false),
        new("Travel", "Trips and journeys", false),
        new("Architecture", "Buildings and structures", false),
        new("Unsorted", "Not yet filed", true),
    ];

    private static readonly PhotoSpec[] PhotoDefinitions =
    [
        new("coast-sunrise", "Coast at sunrise", "First light over the bay.", 300, "Whitby, North Yorkshire", 54.4863, -0.6133, ["Landscape", "Travel"], false, false),
        new("old-harbour", "Old harbour", "The harbour wall at low tide.", 299, "Whitby, North Yorkshire", 54.4880, -0.6150, ["Travel", "Architecture"], false, false),
        new("lighthouse", "Lighthouse", "The east pier light.", 298, "Whitby, North Yorkshire", 54.4900, -0.6100, ["Landscape"], false, false),
        new("family-picnic", "Family picnic", "A long lunch in the garden.", 120, "Home", null, null, ["Family"], true, false),
        new("birthday-dinner", "Birthday dinner", "Candles and cake.", 60, "Home", null, null, ["Family"], false, false),
        new("city-walk", "City walk", "Afternoon among the old facades.", 45, "City centre", null, null, ["Architecture"], false, false),
        new("old-scan", "Unsorted scan", null, 800, null, null, null, ["Unsorted"], false, true),
    ];

    private static readonly AlbumSpec[] AlbumDefinitions =
    [
        new("coastal-trip", "Coastal trip", "A long weekend on the coast.", ["coast-sunrise", "old-harbour", "lighthouse"], "lighthouse"),
        new("family-moments", "Family moments", "Gatherings worth keeping.", ["family-picnic", "birthday-dinner"], "birthday-dinner"),
    ];

    public static Guid PhotoIdFor(string key) => DeterministicGuid.From($"photo::library::{key}");

    public static Guid TagIdFor(string name) => DeterministicGuid.From($"photo-tag::{name}");

    public static Guid AlbumIdFor(string key) => DeterministicGuid.From($"photo-album::{key}");

    public static Result Generate(DateTime anchor)
    {
        var authorId = DemoUsers.All.First(u => u.Role == "Owner").Id;

        var tags = TagDefinitions
            .Select(spec => new PhotoTag
            {
                PhotoTagId = TagIdFor(spec.Name),
                Name = spec.Name,
                Description = spec.Description,
                Archived = spec.Archived ? anchor.AddMonths(-9) : null,
            })
            .ToList();

        var photos = new List<Photo>();
        var tagLinks = new List<PhotoTagLink>();
        var people = new List<PhotoPerson>();
        var blobs = new List<FileBlob>();
        var metadata = new List<FileMetadata>();

        var colourSeed = 0;
        foreach (var spec in PhotoDefinitions)
        {
            var photoId = PhotoIdFor(spec.Key);
            var takenAt = anchor.AddDays(-spec.DaysBeforeAnchor);
            var (blob, meta) = BuildImageFile(spec.Key, authorId, takenAt, colourSeed++);
            blobs.Add(blob);
            metadata.Add(meta);

            photos.Add(new Photo
            {
                PhotoId = photoId,
                FileId = meta.Id,
                Title = spec.Title,
                Caption = spec.Caption,
                TakenAt = DateTime.SpecifyKind(takenAt, DateTimeKind.Unspecified),
                CapturedLatitude = spec.Latitude,
                CapturedLongitude = spec.Longitude,
                LocationName = spec.LocationName,
                PixelWidth = DemoImages.PhotoSize,
                PixelHeight = DemoImages.PhotoSize,
                Archived = spec.Archived ? takenAt.AddDays(1) : null,
                CreatedByUserId = authorId,
                CreatedAt = takenAt,
                UpdatedAt = takenAt,
            });

            foreach (var tagName in spec.TagNames.Distinct())
            {
                tagLinks.Add(new PhotoTagLink
                {
                    PhotoTagLinkId = DeterministicGuid.From($"photo-tag-link::{spec.Key}::{tagName}"),
                    PhotoId = photoId,
                    PhotoTagId = TagIdFor(tagName),
                });
            }

            if (spec.LinkLandlordPerson)
            {
                people.Add(new PhotoPerson
                {
                    PhotoPersonId = DeterministicGuid.From($"photo-person::{spec.Key}"),
                    PhotoId = photoId,
                    ContactId = Contacts.IdFor(Contacts.Landlord),
                });
            }
        }

        var albums = new List<PhotoAlbum>();
        var albumItems = new List<PhotoAlbumItem>();
        foreach (var spec in AlbumDefinitions)
        {
            var albumId = AlbumIdFor(spec.Key);
            var createdAt = anchor.AddDays(-250);
            albums.Add(new PhotoAlbum
            {
                PhotoAlbumId = albumId,
                Name = spec.Name,
                Description = spec.Description,
                CoverPhotoId = PhotoIdFor(spec.CoverKey),
                CreatedByUserId = authorId,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });

            for (var i = 0; i < spec.PhotoKeys.Length; i++)
            {
                albumItems.Add(new PhotoAlbumItem
                {
                    PhotoAlbumItemId = DeterministicGuid.From($"photo-album-item::{spec.Key}#{i}"),
                    PhotoAlbumId = albumId,
                    PhotoId = PhotoIdFor(spec.PhotoKeys[i]),
                    Position = i,
                    CreatedAt = createdAt,
                });
            }
        }

        return new Result(photos, tags, tagLinks, people, albums, albumItems, blobs, metadata);
    }

    private static (FileBlob Blob, FileMetadata Metadata) BuildImageFile(
        string key, string uploaderId, DateTime uploadedAt, int colourSeed)
    {
        var blobId = DeterministicGuid.From($"photo-blob::{key}");
        var metadataId = DeterministicGuid.From($"photo-file::{key}");

        var content = DemoImages.GradientPng(DemoImages.PhotoSize, colourSeed);

        var blob = new FileBlob { Id = blobId, Content = content };
        var metadata = new FileMetadata
        {
            Id = metadataId,
            UploadedByUserId = uploaderId,
            FileName = $"{key}.png",
            ContentType = "image/png",
            SizeBytes = content.LongLength,
            Sha256Hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            FileBlobId = blobId,
            Description = "Demo library photo.",
            UploadedAtUtc = uploadedAt,
        };
        return (blob, metadata);
    }
}
