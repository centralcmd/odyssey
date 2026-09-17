using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Dtos.Application;
using Odyssey.Client.Components;
using Odyssey.Client.Models;
using Odyssey.ApiClient;

namespace Odyssey.Client.Pages;

public partial class AccountProfileSection
{
    /// <summary>The canonical profile as last read from the server.</summary>
    [Parameter, EditorRequired] public ProfileDto Profile { get; set; } = new();

    /// <summary>
    /// Raised with the freshly-saved server values so the page's header can re-render. It carries the
    /// profile <b>picture</b> token too (issue #94 §3 step 6): the control is here and the header mark
    /// is on the page, so a section-local token would satisfy the upload and leave the header stale.
    /// </summary>
    [Parameter] public EventCallback<ProfileDto> ProfileChanged { get; set; }

    /// <summary>The caller's own user id — the read endpoint's target, never a write's.</summary>
    [Parameter] public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Whether this principal holds <c>profile-images.read</c>. Claims are baked into the auth cookie
    /// at sign-in, so a session that predates the claim's deploy does not hold it (issue #94 §10.17).
    /// </summary>
    [Parameter] public bool CanReadProfileImages { get; set; }

    // The working copy. Seeded from Profile and reseeded whenever the page hands down a new one.
    private ProfileDto _draft = new();
    private ProfileDto _original = new();
    private bool _saving;
    private bool _saved;

    // ── Profile picture (issue #94 §3) ──────────────────────────────────────────────────────────
    private bool _cropOpen;
    private bool _removeOpen;
    private bool _uploading;
    private bool _removing;
    private string? _live;

    /// <summary>
    /// What the mark should render. Both halves of "renderable" must hold: the SUBJECT half is the
    /// token on the profile, the CALLER half is the claim. Without the claim check the control would
    /// emit an <c>&lt;img&gt;</c> that 403s.
    /// </summary>
    private Guid? RenderableVersion =>
        CanReadProfileImages ? Profile.ProfileImageVersion : null;

    private string? ImageSrc =>
        RenderableVersion is { } version && !string.IsNullOrEmpty(UserId)
            ? ProfileApi.ImageUrl(UserId, version)
            : null;

    /// <summary>
    /// The one honest dead end. Writes are claim-free while the read is gated, so a pre-deploy session
    /// would upload successfully, receive a 200 and a token, and see nothing. On /users the image is
    /// decorative and a monogram is fine; here the image IS the feature, so the control is disabled
    /// rather than accepting a write it cannot show.
    /// </summary>
    private bool ImageControlsDisabled => !CanReadProfileImages;

    private const string ClaimStaleReason = "Sign out and back in to manage your profile picture.";

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

    // ── Profile picture ─────────────────────────────────────────────────────────────────────────

    private void OpenCropDialog() => _cropOpen = true;

    private void RequestRemoval() => _removeOpen = true;

    /// <summary>
    /// The self-scoped POST. No user id in route or body — there is nothing to tamper with.
    /// </summary>
    private async Task<ApiResult> UploadImageAsync(ApiUpload upload)
    {
        _uploading = true;
        try
        {
            var result = await ProfileApi.UploadImageAsync(upload);
            if (result.IsSuccess && result.Value is { } version)
            {
                // Raised to the PAGE, which owns the canonical profile behind the header's Leading
                // fragment, so the control and the header both re-key their URL and re-render with no
                // reload and no sign-out. Without the re-key the src string would be unchanged, Blazor
                // would emit no DOM update, and the browser would never re-request.
                _pendingVersion = version.ImageVersion;
                return ApiResult.Success(result.Status);
            }

            return result.IsSuccess
                ? ApiResult.Success(result.Status)
                : ApiResult.Failure(result.Status, result.Problem!);
        }
        finally
        {
            _uploading = false;
        }
    }

    /// <summary>Held between the upload completing and the dialog's OnSaved, which is where it is raised.</summary>
    private Guid? _pendingVersion;

    private async Task ImageSavedAsync()
    {
        if (_pendingVersion is not { } version)
        {
            return;
        }

        _pendingVersion = null;
        await ProfileChanged.InvokeAsync(Profile with { ProfileImageVersion = version });
    }

    private async Task<bool> RemoveImageConfirmedAsync()
    {
        _removing = true;
        try
        {
            var result = await ProfileApi.DeleteImageAsync();

            // A 404 means it is already gone, which is what the user asked for — the intent is
            // satisfied either way, so it is not surfaced as a failure.
            if (!result.IsSuccess && result.Status != System.Net.HttpStatusCode.NotFound)
            {
                Snackbar.Add($"Couldn't remove your picture: {result.Error}", Severity.Error);
                return false;
            }

            await ProfileChanged.InvokeAsync(Profile with { ProfileImageVersion = null });

            // /account has no general-purpose status region to borrow, so the completed removal goes
            // through the reusable live announcer.
            _live = "Profile picture removed";
            return true;
        }
        finally
        {
            _removing = false;
        }
    }

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
