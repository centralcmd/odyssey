using System.Net;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Fields = Odyssey.Dtos.Journal.ContactConversionFields;

namespace Odyssey.Client.Pages.Finance;

public partial class ContactConvertDialog
{
    /// <summary>The label each <see cref="ContactConversionFields"/> name renders as — keyed on the shared constants.</summary>
    private static readonly IReadOnlyDictionary<string, string> FieldLabels = new Dictionary<string, string>
    {
        [Fields.FirstName] = "First name",
        [Fields.MiddleName] = "Middle name",
        [Fields.LastName] = "Last name",
        [Fields.DateOfBirth] = "Date of birth",
        [Fields.DateOfDeath] = "Date of death",
        [Fields.Sex] = "Sex",
        [Fields.Title] = "Title",
        [Fields.Company] = "Company",
        [Fields.LegalName] = "Legal name",
        [Fields.OrganizationNumber] = "Organization number",
        [Fields.Website] = "Website",
        [Fields.EstablishedDate] = "Established date",
        [Fields.DissolvedDate] = "Dissolved date",
    };

    private static readonly HashSet<string> DateFields =
        [Fields.DateOfBirth, Fields.DateOfDeath, Fields.EstablishedDate, Fields.DissolvedDate];

    [Inject] private IContactsApiClient Contacts { get; set; } = default!;

    [Inject] private IPhotosApiClient Photos { get; set; } = default!;

    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>The contact to convert; the target is always the other type.</summary>
    [Parameter, EditorRequired] public ExistingContact Contact { get; set; } = default!;

    /// <summary>Raised with the converted contact. The host refreshes its row and announces it.</summary>
    [Parameter] public EventCallback<ExistingContact> OnConverted { get; set; }

    /// <summary>
    /// Raised when the server refused the conversion as stale or blocked: the row the host shows is out
    /// of date (its type may even have changed), so the host re-reads it and its menu follows.
    /// </summary>
    [Parameter] public EventCallback<Guid> OnRefused { get; set; }

    private ContactConversionPreview? _preview;
    private bool _loadFailed;
    private bool _reloading;
    private bool _reloadFailed;
    private int _previewVersion;
    private string _statusMessage = string.Empty;
    private bool _carryDates = true;
    private bool _stale;
    private ContactTypeChangeBlockers _blockers = new();
    private List<string> _photoTitles = [];
    private Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    private string _legalName = string.Empty;
    private string _firstName = string.Empty;
    private string _lastName = string.Empty;

    private ContactType TargetType => Contact.Type == ContactType.Person ? ContactType.Organization : ContactType.Person;

    private bool ToOrganization => TargetType == ContactType.Organization;

    private string TargetNoun => ToOrganization ? "organization" : "person";

    // Only the direction a blocker applies to can be blocked by it.
    private int BlockingCount => ToOrganization ? _blockers.PhotoPersonTags.Count : _blockers.HomeownerAssociations.Count;

    private bool IsBlocked => BlockingCount > 0;

    /// <summary>Whether the server sent the identifiers — it withholds them without the read claim.</summary>
    private bool BlockingListed => ToOrganization
        ? _blockers.PhotoPersonTags.PhotoIds.Count > 0
        : _blockers.HomeownerAssociations.Properties.Count > 0;

    private bool BlockingTruncated => ToOrganization
        ? _blockers.PhotoPersonTags.Truncated
        : _blockers.HomeownerAssociations.Truncated;

    private IReadOnlyList<string> BlockingNames => ToOrganization
        ? _photoTitles
        : _blockers.HomeownerAssociations.Properties.Select(p => p.Name).ToList();

    private string SubtitleText
    {
        get
        {
            if (IsBlocked)
            {
                return ToOrganization
                    ? "Photos tag this contact as a person. Remove those tags, then convert."
                    : "This contact is the homeowner association of a property. Change the association, then convert.";
            }

            var image = ToOrganization ? "picture" : "logo";
            return $"{Contact.ResolvedDisplayName} becomes {(ToOrganization ? "an organization" : "a person")}. "
                   + $"Its links, aliases, contact details and {image} are kept.";
        }
    }

    private bool LowConfidenceSplit =>
        _preview?.Notes.Any(n => n.Code == ContactConversionCodes.NameSplitLowConfidence) == true;

    private bool OneWordLegalName =>
        _preview?.DerivationIssues.Any(i => string.Equals(i.Field, Fields.LastName, StringComparison.OrdinalIgnoreCase)) == true;

    private bool HasLifecycleDates =>
        _preview is not null
        && (_preview.ConvertedFields.Any(f => DateFields.Contains(f.From)) || _preview.LostFields.Any(DateFields.Contains));

    private IReadOnlyList<ContactConvertedField> DateMoves =>
        _preview?.ConvertedFields.Where(f => DateFields.Contains(f.From)).ToList() ?? [];

    private int RemappedTotal => _preview is null
        ? 0
        : _preview.RemappedLabels.Addresses + _preview.RemappedLabels.Emails + _preview.RemappedLabels.Phones;

    private static string FieldLabel(string field) => FieldLabels.GetValueOrDefault(field, field);

    private string? FieldError(params string[] fields)
    {
        foreach (var field in fields)
        {
            if (_errors.TryGetValue(field, out var message))
            {
                return message;
            }
        }

        return null;
    }

    protected override async Task OnInitializedAsync()
    {
        if (!await LoadPreviewAsync(seedNames: true))
        {
            _loadFailed = true;
        }
    }

    /// <summary>
    /// Loads the preview for the current options. Returns false when the request failed or a newer
    /// request superseded it — a slow response is never applied over a faster, later one.
    /// </summary>
    private async Task<bool> LoadPreviewAsync(bool seedNames)
    {
        var version = ++_previewVersion;
        var result = await Contacts.PreviewConversionAsync(Contact.ContactId, TargetType, _carryDates);
        if (version != _previewVersion)
        {
            return false;
        }

        if (!result.IsSuccess || result.Value is null)
        {
            return false;
        }

        _preview = result.Value;
        _blockers = _preview.Blockers;
        if (seedNames)
        {
            _legalName = _preview.OrganizationDetails?.LegalName ?? string.Empty;
            _firstName = _preview.PersonDetails?.FirstName ?? string.Empty;
            _lastName = _preview.PersonDetails?.LastName ?? string.Empty;
        }

        _errors = _preview.DerivationIssues
            .Where(issue => !DateFields.Contains(issue.Field) || _carryDates)
            .Where(issue => !string.Equals(issue.Field, Fields.LastName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(issue => issue.Field, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Message, StringComparer.OrdinalIgnoreCase);

        await LoadPhotoTitlesAsync();
        return true;
    }

    /// <summary>
    /// The blocker carries photo ids, which mean nothing to a person. The ids are present only for a
    /// <c>photos.read</c> holder, so resolving them through the photos list is never a new disclosure.
    /// </summary>
    private async Task LoadPhotoTitlesAsync()
    {
        var ids = _blockers.PhotoPersonTags.PhotoIds;
        if (!ToOrganization || ids.Count == 0)
        {
            _photoTitles = [];
            return;
        }

        // The list serves active photos only; an archived one keeps its id in the blocker and is named
        // generically below — it still counts, because restoring it would bring the tag back.
        var list = await Photos.ListAsync(
            1, ContactConversionLimits.MaxBlockerItems, personIds: [Contact.ContactId.ToString()]);
        var titles = list.IsSuccess && list.Value is not null
            ? list.Value.Items.ToDictionary(p => p.PhotoId, p => string.IsNullOrWhiteSpace(p.Title) ? "Untitled photo" : p.Title!)
            : [];
        // An id the active list did not return is archived or beyond its first page; it is named
        // generically rather than guessed at.
        _photoTitles = ids.Select(id => titles.GetValueOrDefault(id, "Photo")).ToList();
    }

    /// <summary>
    /// Re-reads the preview for the new date option. The form stays as it is while the request runs
    /// (the checkbox is disabled, so requests cannot overlap), and a failure keeps the previous preview
    /// and option with an inline note instead of replacing the dialog — the user's typed names survive.
    /// The outcome is announced, since the lists below change without the focus moving.
    /// </summary>
    private async Task CarryDatesChangedAsync(bool carry)
    {
        var previous = _carryDates;
        _carryDates = carry;
        _reloading = true;
        _reloadFailed = false;

        // Render the busy state (checkbox disabled, option as clicked) before the request. Besides
        // showing it, this keeps the parent's rendered value in step with OdsCheckbox's own: without an
        // intermediate render, reverting the option after a failure would not reach the checkbox.
        StateHasChanged();
        await Task.Yield();

        var loaded = await LoadPreviewAsync(seedNames: false);

        _reloading = false;
        if (!loaded)
        {
            _carryDates = previous;
            _reloadFailed = true;
            _statusMessage = "The preview could not be updated. The dates setting was left as it was.";
            return;
        }

        var moved = DateMoves.Count;
        var removed = _preview!.LostFields.Count;
        _statusMessage = $"Preview updated: {moved} date{(moved == 1 ? "" : "s")} carried over, "
                         + $"{removed} field{(removed == 1 ? "" : "s")} removed.";
    }

    private async Task<bool> SubmitAsync()
    {
        // Close is the only action on a refusal, a stale preview or a failed load.
        if (IsBlocked || _stale || _loadFailed || _preview is null)
        {
            return true;
        }

        _errors = new(StringComparer.OrdinalIgnoreCase);
        if (!ValidateNames())
        {
            return false;
        }

        var request = new ContactConversionRequest
        {
            TargetType = TargetType,
            // Echoed verbatim — the server compares it exactly against the stored value.
            ExpectedUpdatedAt = _preview.UpdatedAt,
            CarryLifecycleDates = _carryDates,
            LegalName = ToOrganization ? Override(_legalName, _preview.OrganizationDetails?.LegalName) : null,
            FirstName = ToOrganization ? null : Override(_firstName, _preview.PersonDetails?.FirstName, force: OneWordLegalName),
            LastName = ToOrganization ? null : Override(_lastName, _preview.PersonDetails?.LastName, force: OneWordLegalName),
        };

        var result = await Contacts.ConvertAsync(Contact.ContactId, request);
        if (result.IsSuccess && result.Value is { } converted)
        {
            await OnConverted.InvokeAsync(converted);
            return true;
        }

        await HandleFailureAsync(result);

        // The form shell re-renders only itself after a refused submit; the body this component owns
        // (the refusal, the stale alert, the field errors) needs its own render.
        StateHasChanged();
        return false;
    }

    /// <summary>Sends a name only when it differs from the derived one, so an untouched field stays derived.</summary>
    private static string? Override(string value, string? derived, bool force = false)
    {
        var trimmed = value.Trim();
        return force || !string.Equals(trimmed, derived?.Trim(), StringComparison.Ordinal) ? trimmed : null;
    }

    private bool ValidateNames()
    {
        if (ToOrganization)
        {
            if (string.IsNullOrWhiteSpace(_legalName))
                _errors[Fields.LegalName] = "Legal name is required.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_firstName))
                _errors[Fields.FirstName] = "First name is required.";
            if (string.IsNullOrWhiteSpace(_lastName))
                _errors[Fields.LastName] = "Last name is required.";
        }

        return _errors.Count == 0;
    }

    private async Task HandleFailureAsync(ApiResult<ExistingContact> result)
    {
        var problem = result.Problem;
        if (result.Status == HttpStatusCode.Conflict && problem?.Code == ContactConversionCodes.Stale)
        {
            _stale = true;
            await OnRefused.InvokeAsync(Contact.ContactId);
            return;
        }

        if (result.Status == HttpStatusCode.Conflict && problem?.Code == ContactConversionCodes.Blocked)
        {
            _blockers = new ContactTypeChangeBlockers
            {
                PhotoPersonTags = problem.Extension<ContactPhotoTagBlocker>("photoPersonTags") ?? new(),
                HomeownerAssociations = problem.Extension<ContactHomeownerAssociationBlocker>("homeownerAssociations") ?? new(),
            };
            await LoadPhotoTitlesAsync();
            await OnRefused.InvokeAsync(Contact.ContactId);
            return;
        }

        // A 400 / 422 keyed on a name or date field belongs on that field; anything the dialog cannot
        // place is toasted, so a rejection never vanishes (the ToastOrFields contract).
        ApiResult.Failure(result.Status, problem ?? new ApiProblem { Detail = result.Error })
            .ToastOrFields(Snackbar, "Unable to convert the contact", AssignFieldError);
    }

    private bool AssignFieldError(string field, string message)
    {
        if (string.IsNullOrEmpty(field))
        {
            return false;
        }

        // Model validation keys by property (PascalCase), the service by the camelCase field name.
        var key = char.ToLowerInvariant(field[0]) + field[1..];
        if (!FieldLabels.ContainsKey(key))
        {
            return false;
        }

        _errors[key] = message;
        return true;
    }
}
