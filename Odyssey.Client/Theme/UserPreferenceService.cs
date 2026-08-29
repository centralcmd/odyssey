using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Client.Services;

using Odyssey.Dtos.Application;
using Odyssey.ApiClient.Resources;

namespace Odyssey.Client.Theme;

/// <summary>
/// Owns the user's global preferences (dark mode + default/main currency), backed by
/// the <c>api/user-preferences/preferences-page</c> store. The last-loaded value is
/// exposed synchronously via <see cref="Current"/> so component initializers can paint
/// the right theme/currency without waiting on the async reload (no flash).
/// </summary>
public interface IUserPreferenceService
{
    /// <summary>Raised after a successful save; the argument is the new dark-mode value.</summary>
    event Action<bool>? DarkModeChanged;
    /// <summary>Immediately applies a dark-mode value visually without persisting it.</summary>
    void PreviewDarkMode(bool isDarkMode);
    Task<bool> GetDarkModePreferencesAsync();
    Task LoadUserPreferencesAsync();
    /// <summary>Persists the preference and returns true on success.</summary>
    Task<bool> SaveUserPreferencesAsync(UserPreferencesPage userPreferences);
    /// <summary>The last-loaded preferences, or the dark-first defaults before any load.
    /// Always non-null, so component initializers can read it synchronously.</summary>
    UserPreferencesPage Current { get; }
    /// <summary>The user's saved default currency, or null if preferences could not be loaded.</summary>
    string? DefaultCurrency { get; }
    /// <summary>The user's saved main currency used to display total net worth, assets and liabilities, or null if preferences could not be loaded.</summary>
    string? MainCurrency { get; }
}

public sealed record UserPreferencesPage(bool DarkModeEnabled, string DefaultCurrency = "NOK", string MainCurrency = "NOK");

public sealed class UserPreferenceService(IUserPreferencesApiClient preferences, ISnackbar snackbar, AuthenticationStateProvider authenticationStateProvider) : IUserPreferenceService
{
    private const string PreferencesPageKey = "preferences-page";

    // Dark is the Odyssey default (the DS is dark-first) — applies pre-auth, e.g.
    // the login/register pages, and to any user without a saved preference.
    private static readonly UserPreferencesPage DefaultUserPreferences = new(true, "NOK", "NOK");

    private bool isLoaded;

    public event Action<bool>? DarkModeChanged;

    public void PreviewDarkMode(bool isDarkMode) => DarkModeChanged?.Invoke(isDarkMode);

    public UserPreferencesPage Current { get; private set; } = DefaultUserPreferences;

    public string? DefaultCurrency => isLoaded ? Current.DefaultCurrency : null;

    public string? MainCurrency => isLoaded ? Current.MainCurrency : null;

    public async Task<bool> GetDarkModePreferencesAsync()
    {
        if (!isLoaded)
        {
            await LoadUserPreferencesAsync();
        }

        return Current.DarkModeEnabled;
    }

    public async Task LoadUserPreferencesAsync()
    {
        try
        {
            var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync();
            var isAuthenticated = authenticationState.User.Identity?.IsAuthenticated ?? false;
            if (!isAuthenticated)
            {
                return;
            }

            var result = await preferences.GetAsync<UserPreferenceResponse>(PreferencesPageKey);

            // 404 = no preference saved yet (normal for a fresh user) → use defaults
            // silently. Any other failure still surfaces a toast and leaves isLoaded false
            // so a later call can retry: never cache defaults over a user's real saved
            // theme/currency.
            if (result.Status == HttpStatusCode.NotFound)
            {
                Current = DefaultUserPreferences;
                isLoaded = true;
                return;
            }

            if (!result.IsSuccess)
            {
                snackbar.Add($"Unable to load preferences: {result.Error}", Severity.Error);
                return;
            }

            var response = result.Value;
            if (string.IsNullOrWhiteSpace(response?.PreferencesJson))
            {
                Current = DefaultUserPreferences;
                isLoaded = true;
                return;
            }

            Current = JsonSerializer.Deserialize<UserPreferencesPage>(response.PreferencesJson)
                      ?? DefaultUserPreferences;
            isLoaded = true;
        }
        catch (HttpRequestException ex)
        {
            snackbar.Add($"Unable to load preferences: {ex.Message}", Severity.Error);
        }
        catch (JsonException ex)
        {
            snackbar.Add($"Unable to read preferences: {ex.Message}", Severity.Error);
        }
    }

    public async Task<bool> SaveUserPreferencesAsync(UserPreferencesPage userPreferences)
    {
        var request = new UserPreferenceRequest(JsonSerializer.Serialize(userPreferences));

        try
        {
            var saved = await preferences.PutAsync(PreferencesPageKey, request);
            if (!saved.IsSuccess)
            {
                snackbar.Add($"Unable to save preferences: {saved.Error}", Severity.Error);
                return false;
            }

            await LoadUserPreferencesAsync();
            DarkModeChanged?.Invoke(userPreferences.DarkModeEnabled);
            return true;
        }
        catch (HttpRequestException ex)
        {
            snackbar.Add($"Unable to save preferences: {ex.Message}", Severity.Error);
            return false;
        }
    }
}
