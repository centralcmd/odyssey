using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Books;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Library;

public partial class BookDialog
{
    [Inject] private IBooksApiClient Books { get; set; } = default!;
    [Inject] private IReferenceDataCache ReferenceData { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationState { get; set; } = default!;
    [Inject] private ITagQuickCreate<ExistingBookTag> TagCreator { get; set; } = default!;

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>The book to edit, read whole; <c>null</c> for a new one.</summary>
    [Parameter] public ExistingBook? Book { get; set; }

    /// <summary>The book tags the picker offers (all of them; archived ones only where the book already carries one).</summary>
    [Parameter] public IReadOnlyList<ExistingBookTag> Tags { get; set; } = [];

    /// <summary>Raised with the saved book (created or updated).</summary>
    [Parameter] public EventCallback<ExistingBook> OnSaved { get; set; }

    private bool IsEdit => Book is not null;

    private bool _shown;
    private bool _discardOpen;
    private bool _reloadOpen;
    private bool _conflict;

    private int _version;
    private string? _title;
    private string? _publisher;
    private string? _year;
    private string? _isbn;
    private string? _format;
    private string? _status;
    private string? _description;
    private string? _notes;
    private string? _mainAuthorId;
    private IReadOnlyCollection<string> _tagIds = [];

    /// <summary>What the draft was seeded from — Cancel asks first only when it differs.</summary>
    private string _seed = string.Empty;

    /// <summary><c>books.tags.read</c>, taken once at open: it decides whether <c>tagIds</c> is sent at all.</summary>
    private bool _tagsSnapshot;

    /// <summary><c>books.tags.create</c>, taken once at open: whether the Tags field offers its inline create row.</summary>
    private bool _canCreateTags;

    /// <summary>Tags minted from the field in this dialog, under their temporary ids until the save resolves them.</summary>
    private List<OdsOption> _createdTags = [];

    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ExistingContact> _contacts = [];
    private IReadOnlyList<OdsOption> _authorOptions = [];
    private bool _contactsLoading = true;

    private bool _wasOpen;

    // Shown when the host opens it; after that the form's own close (and the discard confirm) drive it.
    protected override void OnParametersSet()
    {
        if (Open && !_wasOpen)
        {
            _shown = true;
        }

        _wasOpen = Open;
    }

    protected override async Task OnInitializedAsync()
    {
        TagCreator.OnCreateFailed = OnTagCreateFailed;
        Seed(Book);
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        var user = await AuthenticationState.GetUserAsync();
        UseClaims(user.HasPermission(PermissionClaims.BookTagsRead), user.HasPermission(PermissionClaims.BookTagsCreate));
        UseContacts(await ReferenceData.ContactsAsync());
    }

    /// <summary>Test seam: the claim snapshot and the contacts, without an auth state or a cache.</summary>
    internal void UseContext(bool canReadTags, IEnumerable<ExistingContact> contacts, bool canCreateTags = false)
    {
        UseClaims(canReadTags, canCreateTags);
        UseContacts(contacts);
        StateHasChanged();
    }

    // The one place the two claims combine: create is offered only on a field that is shown, i.e. with books.tags.read.
    private void UseClaims(bool canReadTags, bool canCreateTags)
    {
        _tagsSnapshot = canReadTags;
        _canCreateTags = canReadTags && canCreateTags;
    }

    /// <summary>Whether the Tags field offers its inline create row — for tests.</summary>
    internal bool OffersTagCreate => _canCreateTags;

    private void UseContacts(IEnumerable<ExistingContact> contacts)
    {
        _contacts = [.. contacts];
        _authorOptions = [.. AuthorCandidates(Book, _contacts).Select(OdsContactOptions.From)];
        _contactsLoading = false;
    }

    /// <summary>
    /// The Main author picker: active contacts, the book's contributors left out (a contact credited twice is a 422),
    /// and the current main author kept even when archived since.
    /// </summary>
    internal static IEnumerable<ExistingContact> AuthorCandidates(ExistingBook? book, IEnumerable<ExistingContact> contacts)
    {
        var contributors = book?.Contributors.Select(c => c.Contact.ContactId).ToHashSet() ?? [];
        var current = book?.MainAuthor.ContactId;
        return contacts
            .Where(c => !contributors.Contains(c.ContactId) && (c.Archived is null || c.ContactId == current))
            .OrderBy(c => c.ResolvedDisplayName, StringComparer.CurrentCultureIgnoreCase);
    }

    private void Seed(ExistingBook? book)
    {
        _version = book?.Version ?? 0;
        _title = book?.Title;
        _publisher = book?.Publisher;
        _year = BookVisuals.YearText(book?.Year);
        _isbn = book?.Isbn;
        // A new book defaults to Paperback / Owned.
        _format = (book?.Format ?? BookFormat.Paperback).ToString();
        _status = (book?.Status ?? BookStatus.Owned).ToString();
        _description = book?.Description;
        _notes = book?.Notes;
        _mainAuthorId = book?.MainAuthor.ContactId.ToString();
        _tagIds = [.. (book?.TagIds ?? []).Select(id => id.ToString())];
        _errors.Clear();
        _conflict = false;
        _seed = Snapshot();
    }

    private string Snapshot() => string.Join('\u001f',
        _title, _publisher, _year, _isbn, _format, _status, _description, _notes, _mainAuthorId, string.Join(',', _tagIds.Order()));

    internal bool Dirty => Snapshot() != _seed;

    // ── Field state ─────────────────────────────────────────────────────────────────────────

    private void Touch(string field) => _errors.Remove(field);

    private string? Err(string field) => _errors.GetValueOrDefault(field);

    private void OnAuthorChanged(string? value)
    {
        _mainAuthorId = value;
        Touch("mainAuthorContactId");
    }

    private void OnTagsChanged(IReadOnlyCollection<string> values)
    {
        _tagIds = values ?? [];
        Touch("tagIds");
    }

    private string? YearError => BookVisuals.ParseYear(_year, out _);

    private string YearHelp =>
        BookVisuals.ParseYear(_year, out var year) is null && year is < 0
            ? $"Shown as {BookVisuals.Year(year.Value)}."
            : "Use a minus sign for BC — −44 is 44 BC.";

    private (string? Normalized, string? Error) IsbnCheck => BookVisuals.Isbn(_isbn);

    private string? IsbnError => string.IsNullOrWhiteSpace(_isbn) ? null : IsbnCheck.Error;

    private string? IsbnHelp =>
        IsbnCheck.Normalized is { } saved && saved != _isbn?.Trim()
            ? $"Saved as {saved}."
            : "Hyphens and spaces are fine; it is saved as digits.";

    private IReadOnlyList<OdsOption> TagOptions
    {
        get
        {
            var known = BookVisuals.TagOptions(Tags, t => _tagIds.Contains(t.BookTagId.ToString()));
            return [.. _createdTags.Where(c => known.All(o => o.Value != c.Value)), .. known];
        }
    }

    // An archived or unknown tag the book already carries survives Clear: the picker cannot offer it again.
    // A tag minted here is neither — it is in the options, so it can be removed like any other.
    private bool PreserveTag(string id) =>
        _createdTags.All(c => c.Value != id) && !Tags.Any(t => t.BookTagId.ToString() == id && t.Archived is null);

    private OdsOption? CreateTagOption(string text, string? kind)
    {
        var option = TagCreator.Begin(text);
        if (option is not null)
        {
            _createdTags.Add(option);
            StateHasChanged();
        }

        return option;
    }

    private void OnTagCreateFailed(string tempId)
    {
        _createdTags.RemoveAll(o => o.Value == tempId);
        _tagIds = [.. _tagIds.Where(id => id != tempId)];
        _ = InvokeAsync(StateHasChanged);
    }

    // A book tag name is unique across active and archived tags (the book-tag service refuses either).
    private string? BlockedTagName(string text) =>
        TagNameRule.Blocked(
            Tags.Select(t => new TagNameEntry(t.Name, t.Archived is not null))
                .Concat(_createdTags.Select(o => new TagNameEntry(o.Label, false))),
            text, TagNameRule.Pages.Book);

    private string? TagHelp =>
        _tagIds.Count >= BookLimits.MaxTags * 4 / 5 ? $"{_tagIds.Count} of {BookLimits.MaxTags} tags." : null;

    // ── Save ────────────────────────────────────────────────────────────────────────────────

    private bool Validate()
    {
        if (string.IsNullOrWhiteSpace(_title))
        {
            _errors["title"] = "Enter a title.";
        }

        if (YearError is { } yearError)
        {
            _errors["year"] = yearError;
        }

        if (IsbnError is { } isbnError)
        {
            _errors["isbn"] = isbnError;
        }

        if (!Guid.TryParse(_mainAuthorId, out _))
        {
            _errors["mainAuthorContactId"] = "Pick the main author.";
        }

        if (_tagIds.Count > BookLimits.MaxTags)
        {
            _errors["tagIds"] = $"A book carries at most {BookLimits.MaxTags} tags.";
        }

        return _errors.Count == 0;
    }

    private List<Guid> TagGuids => [.. _tagIds.Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)];

    internal NewBook BuildNew()
    {
        BookVisuals.ParseYear(_year, out var year);
        return new NewBook
        {
            Title = _title!.Trim(),
            Publisher = Clean(_publisher),
            Year = year,
            Isbn = Clean(_isbn),
            Format = Enum.Parse<BookFormat>(_format!),
            Status = Enum.Parse<BookStatus>(_status!),
            Description = Clean(_description),
            Notes = Clean(_notes),
            MainAuthorContactId = Guid.Parse(_mainAuthorId!),
            Contributors = [],
            PhotoIds = [],
            CoverPhotoId = null,
            AttachmentFileIds = [],
            TagIds = _tagsSnapshot ? TagGuids : [],
        };
    }

    internal UpdateBook BuildUpdate()
    {
        BookVisuals.ParseYear(_year, out var year);
        return new UpdateBook
        {
            Version = _version,
            Title = _title!.Trim(),
            Publisher = Clean(_publisher),
            Year = year,
            Isbn = Clean(_isbn),
            Format = Enum.Parse<BookFormat>(_format!),
            Status = Enum.Parse<BookStatus>(_status!),
            Description = Clean(_description),
            Notes = Clean(_notes),
            MainAuthorContactId = Guid.Parse(_mainAuthorId!),
            // Never edited here: null leaves each set as it is.
            Contributors = null,
            PhotoIds = null,
            CoverPhotoId = null,
            AttachmentFileIds = null,
            TagIds = _tagsSnapshot ? TagGuids : null,
        };
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private async Task<bool> SaveAsync()
    {
        _errors.Clear();
        if (!Validate())
        {
            StateHasChanged();
            return false;
        }

        if (_createdTags.Count > 0)
        {
            // Inline-created tags hand the picker a temporary id; wait for their POSTs, then map to the real ids.
            await TagCreator.WhenSettledAsync();
            _tagIds = [.. _tagIds.Select(TagCreator.Resolve).Where(id => id is not null).Select(id => id!)];
            _createdTags = TagCreator.ResolveCreated(_createdTags);
        }

        ApiResult<ExistingBook> result = IsEdit
            ? await Books.UpdateAsync(Book!.BookId, BuildUpdate())
            : await Books.CreateAsync(BuildNew());

        if (!result.IsSuccess && result.Problem?.Code == BookRecord.VersionConflictCode)
        {
            _conflict = true;
            StateHasChanged();
            return false;
        }

        var outcome = result.IsSuccess ? ApiResult.Success(result.Status) : ApiResult.Failure(result.Status, result.Problem!);
        if (!outcome.ToastOrFields(Snackbar, IsEdit ? "Unable to save the book" : "Unable to add the book", AssignServerError,
                IsEdit ? "Book saved" : "Book added"))
        {
            StateHasChanged();
            return false;
        }

        _seed = Snapshot();
        if (result.Value is { } saved)
        {
            await OnSaved.InvokeAsync(saved);
        }

        _shown = false;
        return true;
    }

    private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "publisher", "year", "isbn", "format", "status", "description", "notes", "mainAuthorContactId", "tagIds",
    };

    private bool AssignServerError(string field, string message)
    {
        if (!KnownFields.Contains(field))
        {
            return false;
        }

        _errors[field] = message;
        return true;
    }

    // ── Close, discard and reload ───────────────────────────────────────────────────────────

    /// <summary>
    /// The form closed (Cancel, Esc, the close button, or a save). With unsaved changes it reopens behind a confirm —
    /// the draft lives in this component, so <i>Keep editing</i> brings it back untouched.
    /// </summary>
    private async Task OnShownChanged(bool shown)
    {
        _shown = shown;
        if (shown)
        {
            return;
        }

        if (Dirty)
        {
            _discardOpen = true;
            return;
        }

        await OpenChanged.InvokeAsync(false);
    }

    private void OnDiscardOpenChanged(bool open)
    {
        _discardOpen = open;
        if (!open)
        {
            // Keep editing.
            _shown = true;
        }
    }

    private async Task<bool> DiscardAsync()
    {
        _discardOpen = false;
        Seed(Book);
        await OpenChanged.InvokeAsync(false);
        return true;
    }

    // The confirm stacks over the form, which stays open behind it with the draft.
    private void AskReloadAsync() => _reloadOpen = true;

    private async Task<bool> ReloadAsync()
    {
        _reloadOpen = false;
        if (Book is null)
        {
            return true;
        }

        var latest = await Books.GetAsync(Book.BookId);
        if (latest.OrToast(Snackbar, "Couldn’t read the latest version") is { } fresh)
        {
            Seed(fresh);
            _authorOptions = [.. AuthorCandidates(fresh, _contacts).Select(OdsContactOptions.From)];
        }

        StateHasChanged();
        return true;
    }
}
