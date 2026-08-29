using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Application;

namespace Odyssey.Api.Preferences;

/// <summary>
/// Reads and upserts the authenticated caller's own per-page-key <see cref="UserPreference"/> row.
/// Operates strictly on the row keyed by the caller's user-id and the given page key — there is no
/// cross-user path.
/// </summary>
public sealed class UserPreferencesService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public UserPreferencesService(OdysseyContext context, TimeProvider timeProvider)
    {
        this.context = context;
        this.timeProvider = timeProvider;
    }

    /// <summary>Read the caller's preference for <paramref name="pageKey"/>; null if never saved.</summary>
    public async Task<UserPreferenceResponse?> GetAsync(string userId, string pageKey, CancellationToken cancellationToken)
    {
        var preference = await context.UserPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.UserId == userId && item.Key == pageKey, cancellationToken);

        return preference is null ? null : ToDto(preference);
    }

    /// <summary>Create or replace the caller's preference for <paramref name="pageKey"/>.</summary>
    public async Task<UserPreferenceResponse> UpsertAsync(
        string userId, string pageKey, string preferencesJson, CancellationToken cancellationToken)
    {
        var preference = await context.UserPreferences
            .FirstOrDefaultAsync(item => item.UserId == userId && item.Key == pageKey, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (preference is null)
        {
            preference = new UserPreference
            {
                UserPreferenceId = Guid.NewGuid(),
                UserId = userId,
                Key = pageKey,
                PreferencesJson = preferencesJson,
                UpdatedAt = now,
            };

            context.UserPreferences.Add(preference);
        }
        else
        {
            preference.PreferencesJson = preferencesJson;
            preference.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);

        return ToDto(preference);
    }

    private static UserPreferenceResponse ToDto(UserPreference preference) =>
        new(preference.Key, preference.PreferencesJson ?? string.Empty, preference.UpdatedAt);
}
