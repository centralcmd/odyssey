using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// One blob of persisted UI state per user per page key. Lives in this context alongside
/// <see cref="ApplicationUser"/> so <see cref="UserId"/> can be a real FK with cascade delete — a
/// user's preferences go away with the user, without any application-level purge.
/// </summary>
[Index(nameof(UserId), nameof(Key), IsUnique = true)]
public class UserPreference
{
    public required Guid UserPreferenceId { get; set; }

    /// <summary>FK to <see cref="ApplicationUser.Id"/>; cascade delete.</summary>
    [Length(1, 256)]
    public required string UserId { get; set; }

    [Length(1, 256)]
    public required string Key { get; set; }

    [MaxLength(4096)]
    public string? PreferencesJson { get; set; } = string.Empty;

    public required DateTime UpdatedAt { get; set; }
}
