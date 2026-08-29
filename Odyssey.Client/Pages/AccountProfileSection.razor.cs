using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Dtos.Application;
using Odyssey.Client.Components;
using Odyssey.Client.Models;

namespace Odyssey.Client.Pages;

public partial class AccountProfileSection
{
    /// <summary>The canonical profile as last read from the server.</summary>
    [Parameter, EditorRequired] public ProfileDto Profile { get; set; } = new();

    /// <summary>Raised with the freshly-saved server values so the page's header can re-render.</summary>
    [Parameter] public EventCallback<ProfileDto> ProfileChanged { get; set; }

    // The working copy. Seeded from Profile and reseeded whenever the page hands down a new one.
    private ProfileDto _draft = new();
    private ProfileDto _original = new();
    private bool _saving;
    private bool _saved;

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(_original, Profile) || Matches(_original, Profile))
            return;

        _draft = Profile with { };
        _original = Profile;
    }

    private static bool Matches(ProfileDto a, ProfileDto b) =>
        a.FirstName == b.FirstName
        && a.MiddleName == b.MiddleName
        && a.LastName == b.LastName
        && a.DisplayName == b.DisplayName
        && a.Title == b.Title
        && a.BirthDate == b.BirthDate
        && a.Sex == b.Sex;

    private Dictionary<string, string> Errors => ProfileValidation.Validate(_draft).Errors;

    private bool Dirty => !Matches(_draft, _original);

    private bool CanSave
    {
        get
        {
            var (errors, complete) = ProfileValidation.Validate(_draft);
            return Dirty && errors.Count == 0 && complete && !_saving;
        }
    }

    private string PreviewName =>
        ProfileValidation.ResolveName(_draft) is { Length: > 0 } resolved ? resolved : "Your name";

    private string PreviewHint =>
        string.IsNullOrWhiteSpace(_draft.DisplayName)
            ? "Using your first name — set a display name to override"
            : "Using your display name";

    private void OnFieldsChanged(ProfileDto _) => _saved = false;

    private async Task SaveAsync()
    {
        var (errors, complete) = ProfileValidation.Validate(_draft);
        if (errors.Count > 0 || !complete)
            return;

        _saving = true;
        var saved = await ProfileApi.SaveAsync(_draft);
        _saving = false;

        if (!saved.IsSuccess)
        {
            Snackbar.Add($"Couldn't save your profile: {saved.Error}", Severity.Error);
            return;
        }

        // Refetch the canonical server values so the header/avatar re-render without a reload (spec §3).
        var fresh = await ProfileApi.GetAsync() is { IsSuccess: true, Value: { } value } ? value : _draft with { };
        _draft = fresh with { };
        _original = fresh;
        await ProfileChanged.InvokeAsync(fresh);

        _saved = true;
        StateHasChanged();
        await Task.Delay(OdsTiming.ConfirmFlashMs);
        _saved = false;
    }
}
