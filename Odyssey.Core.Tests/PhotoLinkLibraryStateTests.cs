using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The photo-link projections carry the library photo's own favourite and archive state, so the shared photo tile draws
/// its heart and its "Archived" badge from the record read alone, without one <c>GET /api/photos/{id}</c> per photo.
/// <see cref="PhotoLookup.ResolveAsync"/> feeds the journal entry, recipe and book links; the calendar projection reads
/// the photos itself.
/// </summary>
public class PhotoLinkLibraryStateTests
{
    private static readonly DateTime FavouritedAt = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ArchivedAt = new(2026, 5, 2, 9, 0, 0, DateTimeKind.Utc);

    private static Photo NewPhoto(string fileName, DateTime? favourited = null, DateTime? archived = null) => new()
    {
        PhotoId = Guid.NewGuid(),
        StoredObjectId = Guid.NewGuid(),
        FileName = fileName,
        ContentType = "image/jpeg",
        SizeBytes = 10,
        Sha256Hash = new string('a', 64),
        Favourited = favourited,
        Archived = archived,
        CreatedAt = FavouritedAt,
    };

    [Fact]
    public async Task The_lookup_resolves_each_photos_favourite_and_archived_state_with_its_label()
    {
        await using var context = TestContextFactory.CreateJournal();
        var marked = NewPhoto("marked.jpg", FavouritedAt, ArchivedAt);
        var plain = NewPhoto("plain.jpg");
        context.Photos.AddRange(marked, plain);
        await context.SaveChangesAsync();

        var info = await new PhotoLookup(context).ResolveAsync([marked.PhotoId, plain.PhotoId, Guid.NewGuid()]);

        Assert.Equal(2, info.Count);
        Assert.Equal(new PhotoLinkInfo(marked.PhotoId, null, "marked.jpg", FavouritedAt, ArchivedAt), info[marked.PhotoId]);
        Assert.Equal(new PhotoLinkInfo(plain.PhotoId, null, "plain.jpg", null, null), info[plain.PhotoId]);
    }

    [Fact]
    public async Task A_calendar_events_photo_links_carry_each_photos_favourite_and_archived_state()
    {
        await using var context = TestContextFactory.CreateJournal();
        var marked = NewPhoto("marked.jpg", FavouritedAt, ArchivedAt);
        var plain = NewPhoto("plain.jpg");
        context.Photos.AddRange(marked, plain);
        var eventId = Guid.NewGuid();
        context.CalendarEventPhotos.AddRange(
            new CalendarEventPhoto { CalendarEventId = eventId, PhotoId = marked.PhotoId, Position = 0, CreatedAt = FavouritedAt },
            new CalendarEventPhoto { CalendarEventId = eventId, PhotoId = plain.PhotoId, Position = 1, CreatedAt = FavouritedAt });
        await context.SaveChangesAsync();
        var dto = new ExistingCalendarEvent { CalendarEventId = eventId, CalendarId = Guid.NewGuid(), Title = "Picnic" };

        await CalendarLinkProjection.FillDetailAsync(context, dto, CancellationToken.None);

        Assert.Equal(
            [(marked.PhotoId, (DateTime?)FavouritedAt, (DateTime?)ArchivedAt), (plain.PhotoId, null, null)],
            dto.Photos!.Select(p => (p.PhotoId, p.Favourited, p.Archived)));
    }
}
