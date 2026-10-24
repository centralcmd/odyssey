using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Preferences;

/// <summary>
/// Reads and upserts the authenticated caller's own per-page-key <see cref="UserPreference"/> row.
/// Operates strictly on the row keyed by the caller's user-id and the given page key — there is no
/// cross-user path.
/// </summary>
public sealed class UserPreferencesService
{
    /// <summary>
    /// The caller's weather location (issue #348 §4), as <c>{"defaultLocationId": "…"}</c>. Written only by
    /// <c>PUT /api/weather/preferences</c>, which validates it; the generic endpoint refuses the key.
    /// </summary>
    public const string WeatherKey = "weather-preferences";

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

    /// <summary>
    /// Create or replace the caller's preference for <paramref name="pageKey"/>. A server-owned key is a
    /// <c>400</c>: its value is validated by its own endpoint, which this path would bypass.
    /// </summary>
    public Task<UserPreferenceResponse> UpsertAsync(
        string userId, string pageKey, string preferencesJson, CancellationToken cancellationToken) =>
        UpsertCoreAsync(userId, pageKey, preferencesJson, serverWrite: false, cancellationToken);

    /// <summary>The write path for a server-owned key, after its owner has validated the value.</summary>
    internal Task<UserPreferenceResponse> UpsertServerOwnedAsync(
        string userId, string pageKey, string preferencesJson, CancellationToken cancellationToken) =>
        UpsertCoreAsync(userId, pageKey, preferencesJson, serverWrite: true, cancellationToken);

    // The column's collation is case- and trailing-space-insensitive, so "Weather-Preferences " names the
    // same row; compare the same way, against both the requested key and the row it resolved to.
    private static bool IsServerOwned(string key) =>
        string.Equals(key.TrimEnd(), WeatherKey, StringComparison.OrdinalIgnoreCase);

    private async Task<UserPreferenceResponse> UpsertCoreAsync(
        string userId, string pageKey, string preferencesJson, bool serverWrite, CancellationToken cancellationToken)
    {
        if (!serverWrite && IsServerOwned(pageKey))
        {
            throw new DomainValidationException("This preference is set through its own settings.", code: null, field: "pageKey");
        }

        var preference = await context.UserPreferences
            .FirstOrDefaultAsync(item => item.UserId == userId && item.Key == pageKey, cancellationToken);
        if (!serverWrite && preference is not null && IsServerOwned(preference.Key))
        {
            throw new DomainValidationException("This preference is set through its own settings.", code: null, field: "pageKey");
        }

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
