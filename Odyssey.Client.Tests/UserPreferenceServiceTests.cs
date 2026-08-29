using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Application;
using Odyssey.Client.Theme;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Covers the branch that decides whether a user's saved theme and currency survive a load.
/// </summary>
/// <remarks>
/// A <c>404</c> means "this user has never saved preferences" and must yield the defaults; any other
/// failure must <b>not</b>, because caching defaults over a real saved preference would silently
/// overwrite it on the next save. Nothing pinned that distinction before — this is a plain DI class
/// with fakeable dependencies and no browser guard, so it needs no render harness to test.
/// </remarks>
public class UserPreferenceServiceTests
{
    /// <summary>Serves a canned outcome for the preference key, and records writes.</summary>
    private sealed class FakePreferences : IUserPreferencesApiClient
    {
        public HttpStatusCode GetStatus { get; set; } = HttpStatusCode.OK;
        public string? StoredJson { get; set; }
        public int PutCount { get; private set; }
        public ApiResult PutResult { get; set; } = ApiResult.Success(HttpStatusCode.NoContent);

        public Task<ApiResult<TValue>> GetAsync<TValue>(string key, CancellationToken ct = default)
        {
            if (GetStatus != HttpStatusCode.OK)
                return Task.FromResult(ApiResult<TValue>.Failure(GetStatus, new ApiProblem { Status = (int)GetStatus }));

            var payload = (TValue?)(object?)new UserPreferenceResponse("preferences-page", StoredJson ?? "", DateTime.UtcNow);
            return Task.FromResult(ApiResult<TValue>.Success(payload, HttpStatusCode.OK));
        }

        public Task<ApiResult> PutAsync(string key, object value, CancellationToken ct = default)
        {
            PutCount++;
            return Task.FromResult(PutResult);
        }
    }

    private static AuthenticationStateProvider SignedIn() => new StubAuth(true);
    private static AuthenticationStateProvider SignedOut() => new StubAuth(false);

    private sealed class StubAuth(bool authenticated) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                authenticated ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], "test") : new ClaimsIdentity())));
    }

    private static UserPreferenceService Create(FakePreferences prefs, AuthenticationStateProvider auth) =>
        new(prefs, Mock.Of<ISnackbar>(), auth);

    [Fact]
    public async Task A_user_with_nothing_saved_gets_the_defaults()
    {
        var prefs = new FakePreferences { GetStatus = HttpStatusCode.NotFound };
        var service = Create(prefs, SignedIn());

        await service.LoadUserPreferencesAsync();

        Assert.True(service.Current.DarkModeEnabled);   // dark-first default
        Assert.Equal("NOK", service.Current.MainCurrency);
    }

    /// <summary>
    /// The distinction that matters: a transient failure must not be mistaken for "nothing saved",
    /// or the next save would persist defaults over the user's real theme and currency.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_failed_load_does_not_report_saved_preferences(HttpStatusCode failure)
    {
        var prefs = new FakePreferences { GetStatus = failure };
        var service = Create(prefs, SignedIn());

        await service.LoadUserPreferencesAsync();

        // DefaultCurrency/MainCurrency stay null on failure — that is how callers tell
        // "not loaded" from "loaded, and the user chose NOK".
        Assert.Null(service.DefaultCurrency);
        Assert.Null(service.MainCurrency);
    }

    [Fact]
    public async Task A_saved_preference_is_applied()
    {
        var prefs = new FakePreferences
        {
            // PascalCase: the service round-trips with default JsonSerializer options, so this is
            // the shape it actually writes. camelCase silently binds nothing and yields the defaults.
            StoredJson = """{"DarkModeEnabled":false,"DefaultCurrency":"USD","MainCurrency":"EUR"}""",
        };
        var service = Create(prefs, SignedIn());

        await service.LoadUserPreferencesAsync();

        Assert.False(service.Current.DarkModeEnabled);
        Assert.Equal("USD", service.DefaultCurrency);
        Assert.Equal("EUR", service.MainCurrency);
    }

    /// <summary>Pre-auth pages (login, register) must not call the store at all.</summary>
    [Fact]
    public async Task An_anonymous_visitor_does_not_hit_the_preference_store()
    {
        var prefs = new FakePreferences { GetStatus = HttpStatusCode.InternalServerError };
        var service = Create(prefs, SignedOut());

        await service.LoadUserPreferencesAsync();

        Assert.True(service.Current.DarkModeEnabled);   // untouched defaults
        Assert.Equal(0, prefs.PutCount);
    }

    [Fact]
    public async Task A_failed_save_reports_failure_and_does_not_raise_DarkModeChanged()
    {
        var prefs = new FakePreferences
        {
            PutResult = ApiResult.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = "nope" }),
        };
        var service = Create(prefs, SignedIn());
        var raised = false;
        service.DarkModeChanged += _ => raised = true;

        var ok = await service.SaveUserPreferencesAsync(new UserPreferencesPage(false));

        Assert.False(ok);
        Assert.False(raised);
    }

    [Fact]
    public async Task A_successful_save_raises_DarkModeChanged()
    {
        var prefs = new FakePreferences { StoredJson = """{"DarkModeEnabled":false}""" };
        var service = Create(prefs, SignedIn());
        bool? raisedWith = null;
        service.DarkModeChanged += v => raisedWith = v;

        var ok = await service.SaveUserPreferencesAsync(new UserPreferencesPage(false));

        Assert.True(ok);
        Assert.False(raisedWith);
    }
}
