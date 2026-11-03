using System.Security.Claims;
using Odyssey.Core.Locations;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Api;

/// <summary>
/// The caller's <see cref="LocationClaims"/> (issue #380), read off the principal in one place so every
/// controller hands the location rules the same set. A service has no <c>ClaimsPrincipal</c>.
/// </summary>
public static class LocationClaimsPrincipal
{
    public static LocationClaims LocationClaims(this ClaimsPrincipal user) => new()
    {
        LocationsRead = user.HasPermission(PermissionClaims.LocationsRead),
        LocationsCreate = user.HasPermission(PermissionClaims.LocationsCreate),
        LocationsUpdate = user.HasPermission(PermissionClaims.LocationsUpdate),
        ContactsRead = user.HasPermission(PermissionClaims.ContactsRead),
        PropertiesRead = user.HasPermission(PermissionClaims.PropertiesRead),
        CalendarRead = user.HasPermission(PermissionClaims.CalendarRead),
        JournalRead = user.HasPermission(PermissionClaims.JournalRead),
        ContactsUpdate = user.HasPermission(PermissionClaims.ContactsUpdate),
        PropertiesUpdate = user.HasPermission(PermissionClaims.PropertiesUpdate),
        CalendarUpdate = user.HasPermission(PermissionClaims.CalendarUpdate),
        JournalUpdate = user.HasPermission(PermissionClaims.JournalUpdate),
    };
}
