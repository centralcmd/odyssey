using System.Security.Claims;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The one read rule for calendar links (issue #521 §5.3, SF3, A7), applied on every action that returns an event or a
/// series: ids, positions, link dates and counts are calendar data under <c>calendar.read</c>; a file's name, type, size
/// and upload time need <c>files.read</c>; a photo's title and file name need <c>photos.read</c>, and without it
/// <c>ThumbnailsReady</c> is false — the shape of <c>JournalEntriesController.ApplyThumbnailRule</c>.
/// </summary>
internal static class CalendarLinkVisibility
{
    public static CalendarLinkClaims LinkClaims(this ClaimsPrincipal user) =>
        new(user.HasPermission(PermissionClaims.FilesRead), user.HasPermission(PermissionClaims.PhotosRead));

    public static void ApplyLinkVisibility(this ClaimsPrincipal user, ExistingCalendarEvent? dto) =>
        Apply(user, dto?.Attachments, dto?.Photos);

    public static void ApplyLinkVisibility(this ClaimsPrincipal user, ExistingRecurrencePattern? dto) =>
        Apply(user, dto?.Attachments, dto?.Photos);

    public static void ApplyLinkVisibility(this ClaimsPrincipal user, IEnumerable<ExistingCalendarEvent> rows)
    {
        foreach (var row in rows)
        {
            user.ApplyLinkVisibility(row);
        }
    }

    public static void ApplyLinkVisibility(this ClaimsPrincipal user, IEnumerable<ExistingRecurrencePattern> rows)
    {
        foreach (var row in rows)
        {
            user.ApplyLinkVisibility(row);
        }
    }

    private static void Apply(ClaimsPrincipal user, List<CalendarLinkFileDto>? files, List<CalendarLinkPhotoDto>? photos)
    {
        if (files is not null && !user.HasPermission(PermissionClaims.FilesRead))
        {
            foreach (var file in files)
            {
                file.FileName = null;
                file.ContentType = null;
                file.SizeBytes = null;
                file.UploadedAtUtc = null;
            }
        }

        if (photos is not null && !user.HasPermission(PermissionClaims.PhotosRead))
        {
            foreach (var photo in photos)
            {
                photo.Title = null;
                photo.FileName = null;
                photo.ThumbnailsReady = false;
            }
        }
    }
}
