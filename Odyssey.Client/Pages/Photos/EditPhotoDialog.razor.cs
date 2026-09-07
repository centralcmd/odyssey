using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Photos;

public partial class EditPhotoDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }
    [Parameter] public Guid PhotoId { get; set; }
    [Parameter] public IReadOnlyList<OdsOption> TagOptions { get; set; } = [];
    [Parameter] public IReadOnlyList<OdsOption> PeopleOptions { get; set; } = [];
    [Parameter] public IReadOnlyList<OdsOption> AlbumOptions { get; set; } = [];

    /// <summary>Whether the caller may create new photo tags inline (photos.tags.create).</summary>
    [Parameter] public bool CanCreateTags { get; set; }

    /// <summary>Whether the caller may create Person contacts inline from the People picker
    /// (contacts.create). A tagger who would meet a 403 is never shown the create row.</summary>
    [Parameter] public bool CanCreateContacts { get; set; }

    /// <summary>Whether the caller may rename the backing file (files.update). When false the file-name
    /// field is shown read-only and never submitted.</summary>
    [Parameter] public bool CanRenameFile { get; set; }

    [Parameter] public EventCallback OnSaved { get; set; }

    private ExistingPhoto? _photo;
    private Guid _loadedFor;

    private string _title = string.Empty;
    private string _fileName = string.Empty;
    private string _caption = string.Empty;
    private string _location = string.Empty;
    private DateTime? _date;
    private TimeSpan? _time;
    private decimal? _lat;
    private decimal? _lng;
    private IReadOnlyCollection<string> _tags = [];
    private IReadOnlyCollection<string> _people = [];
    private IReadOnlyCollection<string> _albums = [];
    // A mutable copy of the tag options so an inline-created tag ("new:Name") shows immediately.
    private List<OdsOption> _tagOpts = [];

    // People created here, ahead of the host's list refresh, so the chip resolves at once.
    private readonly List<OdsOption> _createdPeople = [];

    private IReadOnlyList<OdsOption> _peopleOpts => _createdPeople.Count == 0
        ? PeopleOptions
        : [.. _createdPeople.Where(c => PeopleOptions.All(o => o.Value != c.Value)), .. PeopleOptions];

    // One row: a photo's people can only ever be Person contacts.
    private static readonly IReadOnlyList<OdsCreateKind> _personCreateKinds =
        [.. OdsTypeRegistries.ContactCreateKinds.Where(k => k.Key == nameof(ContactType.Person))];

    // Prefix marking a tag the user created inline; resolved to a real PhotoTag id on save.
    private const string NewTagPrefix = "new:";

    protected override async Task OnParametersSetAsync()
    {
        if (Open && PhotoId != _loadedFor)
        {
            _loadedFor = PhotoId;
            _photo = await Photos.GetAsync(PhotoId);
            if (_photo is { } p)
            {
                _title = p.Title ?? string.Empty;
                _fileName = p.FileName ?? string.Empty;
                _caption = p.Caption ?? string.Empty;
                _location = p.LocationName ?? string.Empty;
                _date = p.TakenAt;
                _time = p.TakenAt?.TimeOfDay;
                _lat = p.CapturedLatitude is { } la ? (decimal)la : null;
                _lng = p.CapturedLongitude is { } lo ? (decimal)lo : null;
                _tags = [.. p.TagIds.Select(t => t.ToString())];
                _people = [.. p.PersonContactIds.Select(t => t.ToString())];
                _albums = [.. p.AlbumIds.Select(t => t.ToString())];
                _tagOpts = [.. TagOptions];
            }
        }
        else if (!Open)
        {
            _loadedFor = Guid.Empty;
        }
    }

    protected override void OnInitialized() => ContactCreator.OnCreateFailed = OnPersonCreateFailed;

    // A person tagged on a photo IS a Person contact — staged through the shared creator, and its
    // temp id mapped to the real one on save.
    private OdsOption? CreatePersonOption(string name, string? kind)
    {
        var option = ContactCreator.Begin(name, nameof(ContactType.Person));
        if (option is not null)
            _createdPeople.Add(option);
        return option;
    }

    private void OnPersonCreateFailed(string tempId)
    {
        _createdPeople.RemoveAll(o => o.Value == tempId);
        _people = [.. _people.Where(id => id != tempId)];
        StateHasChanged();
    }

    // Adds a provisional option so the chip renders immediately; the real tag is created on save.
    private OdsOption? CreateTagOption(string name, string? kind)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var existing = _tagOpts.FirstOrDefault(o => string.Equals(o.Label, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var option = new OdsOption(NewTagPrefix + trimmed, trimmed);
        _tagOpts.Add(option);
        return option;
    }

    // Split the selection into real ids + inline-created names, POST the new tags (tolerating an
    // already-exists 409), then reload to map every name back to its real PhotoTag id.
    private async Task<List<Guid>> ResolveTagIdsAsync()
    {
        var realIds = new List<Guid>();
        var newNames = new List<string>();
        foreach (var value in _tags)
        {
            if (value.StartsWith(NewTagPrefix, StringComparison.Ordinal))
            {
                newNames.Add(value[NewTagPrefix.Length..]);
            }
            else if (Guid.TryParse(value, out var id))
            {
                realIds.Add(id);
            }
        }

        if (newNames.Count > 0)
        {
            foreach (var name in newNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // 409 = the tag already exists, which is fine — it is mapped by name below either way.
                await PhotoTagWrites.CreateAsync(new TagWrite(name, Description: null, Archived: false));
            }

            var all = (await PhotoTags.ListAllAsync()).ItemsOrToast(Snackbar, "photo tags");
            var byName = all
                .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().PhotoTagId, StringComparer.OrdinalIgnoreCase);
            foreach (var name in newNames)
            {
                if (byName.TryGetValue(name, out var id))
                {
                    realIds.Add(id);
                }
            }
        }

        return [.. realIds.Distinct()];
    }

    private async Task<bool> SaveAsync()
    {
        if (_photo is null)
        {
            return false;
        }

        var tagIds = await ResolveTagIdsAsync();

        // Let any in-flight inline person create land, then map temp ids to real ones; a failed
        // create resolves to null and is dropped rather than posting an id no server issued.
        await ContactCreator.WhenSettledAsync();
        var personIds = _people
            .Select(id => Guid.TryParse(ContactCreator.Resolve(id), out var pid) ? pid : (Guid?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();

        var body = new UpdatePhoto
        {
            // Only submit a rename when the caller can perform one; blank leaves the file name untouched.
            FileName = CanRenameFile && !string.IsNullOrWhiteSpace(_fileName) ? _fileName.Trim() : null,
            Title = string.IsNullOrWhiteSpace(_title) ? null : _title.Trim(),
            Caption = string.IsNullOrWhiteSpace(_caption) ? null : _caption.Trim(),
            LocationName = string.IsNullOrWhiteSpace(_location) ? null : _location.Trim(),
            TakenAt = CombineTakenAt(),
            CapturedLatitude = _lat is { } la ? (double)la : null,
            CapturedLongitude = _lng is { } lo ? (double)lo : null,
            PixelWidth = _photo.PixelWidth,
            PixelHeight = _photo.PixelHeight,
            Archived = _photo.Archived is not null,     // preserve current state
            Favourite = _photo.Favourited is not null,  // preserve current state
            TagIds = [.. tagIds],
            PersonContactIds = [.. personIds],
            AlbumIds = [.. _albums.Select(Guid.Parse)],
        };

        return (await Photos.UpdateAsync(PhotoId, body)).Toast(Snackbar, "Save failed", "Photo updated.");
    }

    private DateTime? CombineTakenAt()
    {
        if (_date is not { } d)
        {
            return null;
        }

        return DateTime.SpecifyKind(d.Date + (_time ?? TimeSpan.Zero), DateTimeKind.Unspecified);
    }
}
