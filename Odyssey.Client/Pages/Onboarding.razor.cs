using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Dtos.Application;
using Odyssey.Client.Auth;
using Odyssey.Client.Models;

namespace Odyssey.Client.Pages;

public partial class Onboarding
{
    /// <summary>This gate's own route, which is never a valid return target.</summary>
    internal const string OnboardingPath = "/onboarding";

    private ProfileDto _profile = new();
    private Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private bool _isComplete;
    private int _errorCount;
    private bool _attempted;
    private bool _saving;
    private bool _loading = true;
    private string? _saveError;
    private string? _returnUrl;

    private IJSObjectReference? _js;

    // Source order used to move focus to the first offending field on a failed save (spec §3 a11y).
    private static readonly (string Field, string Id)[] FocusOrder =
    [
        (nameof(ProfileDto.FirstName), "onb-first"),
        (nameof(ProfileDto.LastName), "onb-last"),
        (nameof(ProfileDto.MiddleName), "onb-middle"),
        (nameof(ProfileDto.Title), "onb-title"),
        (nameof(ProfileDto.DisplayName), "onb-display"),
        (nameof(ProfileDto.BirthDate), "onb-dob"),
        (nameof(ProfileDto.Sex), "onb-sex"),
    ];

    private string ProgressText => _isComplete ? "Ready to continue" : "Required fields needed";

    // _returnUrl is whatever LocalReturnUrl accepted, so it is either null or a rooted path — the only
    // one of which that isn't "somewhere the user left off" is the dashboard itself.
    private string RequestedLabel =>
        _returnUrl is null or "/" ? "the dashboard" : "where you left off";

    protected override async Task OnInitializedAsync()
    {
        _returnUrl = ReadReturnUrl(NavigationManager.Uri);

        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        if (await Profile.GetAsync() is { IsSuccess: true, Value: { } profile })
        {
            // Already complete (e.g. navigated here directly) → the gate doesn't apply; move on.
            if (profile.IsComplete)
            {
                NavigateOnward();
                return;
            }

            _profile = profile with { };
        }

        Revalidate();
        _loading = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && OperatingSystem.IsBrowser())
        {
            _js = await JS.InvokeAsync<IJSObjectReference>("import", "./js/profile-gate.js");
        }
    }

    private void OnProfileChanged(ProfileDto _)
    {
        Revalidate();
        // Clear a field's error as soon as it becomes valid again (re-checked fully on save).
        if (_attempted)
        {
            _errors = _errors.Where(kv => _liveErrors.ContainsKey(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }
    }

    private Dictionary<string, string> _liveErrors = new(StringComparer.Ordinal);

    private void Revalidate()
    {
        (_liveErrors, _isComplete) = ProfileValidation.Validate(_profile);
        _errorCount = _liveErrors.Count;
    }

    private async Task SaveAsync()
    {
        _attempted = true;
        _saveError = null;
        Revalidate();

        if (_liveErrors.Count > 0)
        {
            _errors = _liveErrors;
            await FocusFirstErrorAsync();
            return;
        }

        _errors = new(StringComparer.Ordinal);
        _saving = true;

        var saved = await Profile.SaveAsync(_profile);
        _saving = false;

        if (saved.IsSuccess)
        {
            NavigateOnward();
            return;
        }

        _saveError = string.IsNullOrWhiteSpace(saved.Error)
            ? "Please check your details and try again."
            : saved.Error;
    }

    private async Task FocusFirstErrorAsync()
    {
        if (_js is null)
        {
            return;
        }

        var first = FocusOrder.FirstOrDefault(entry => _liveErrors.ContainsKey(entry.Field));
        if (first.Id is not null)
        {
            await _js.InvokeVoidAsync("focusById", first.Id);
        }
    }

    private void NavigateOnward() => NavigationManager.NavigateTo(_returnUrl ?? "/");

    /// <summary>
    /// The gate's return target, read out of <paramref name="uri"/>'s query and validated by
    /// <see cref="LocalReturnUrl"/> — the single implementation of this check. Anything that isn't an
    /// app-relative path, and this gate's own route, yield <see langword="null"/> so
    /// <see cref="NavigateOnward"/> falls back to the dashboard.
    /// </summary>
    /// <remarks>
    /// The inline loop this replaced rejected a protocol-relative <c>//evil.example.com</c> but accepted
    /// <c>/\evil.example.com</c>, which a browser's URL parser resolves to a different origin (issue
    /// #408, CWE-601).
    /// </remarks>
    internal static string? ReadReturnUrl(string uri) => LocalReturnUrl.FromQuery(uri, OnboardingPath);

    public async ValueTask DisposeAsync()
    {
        if (_js is not null)
        {
            try
            {
                await _js.DisposeAsync();
            }
            catch (Exception)
            {
                // Circuit already gone; nothing to clean up.
            }
        }
    }
}
