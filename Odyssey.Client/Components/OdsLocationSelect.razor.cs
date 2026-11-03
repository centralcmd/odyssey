using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Components;

public partial class OdsLocationSelect
{
    /// <summary>The disclosure on the create row — part of its accessible name (issue #383 §3.2, SEC F5).</summary>
    public const string CreateDisclosure = "Adds a saved place that everyone who can see locations can see";

    /// <summary>One page per request (issue #383 §10).</summary>
    public const int PageSize = 20;

    private const string StagedPrefix = "new:";

    // A staged Kartverket address (design system · isStagedAddress): `addr:‹source reference›`.
    private const string AddressPrefix = "addr:";

    /// <summary>The address search's disclosure under the field (issue #383 v5 §3.3a) — always visible while it is on.</summary>
    public const string AddressSearchDisclosure = "Address search sends what you type to Kartverket.";

    /// <summary>The Kartverket group's note when the address search failed, in address mode.</summary>
    public const string AddressSearchUnavailable = "Address search is unavailable — enter the address by hand";

    /// <summary>The same note in place mode, which has no hand-entry row (issue #383 v6 §3.2a).</summary>
    public const string PlaceAddressSearchUnavailable = "Address search is unavailable — pick a saved place or create one";

    /// <summary>The address picker's final row.</summary>
    public const string EnterByHandLabel = "Enter an address by hand…";

    /// <summary>Characters before the address search is sent (design system · LocationSelect, 3).</summary>
    public const int AddressMinChars = 3;

    public const string SavedGroup = "saved";
    public const string KartverketGroup = "kartverket";

    internal static readonly IReadOnlyList<OdsOptionGroup> AddressGroups =
    [
        new(SavedGroup, "Saved places"),
        new(KartverketGroup, "Addresses from Kartverket"),
    ];

    [Inject] private ILocationsApiClient Locations { get; set; } = default!;

    /// <summary>An existing place, a staged new one, or nothing.</summary>
    [Parameter] public LocationPick? Value { get; set; }

    [Parameter] public EventCallback<LocationPick?> ValueChanged { get; set; }

    /// <summary>The field label. Default <c>Place</c>, or <c>Address</c> in address mode.</summary>
    [Parameter] public string? Label { get; set; }

    /// <summary><see cref="LocationSelectMode.Place"/> (default) or the address field of a contact or property.</summary>
    [Parameter] public LocationSelectMode Mode { get; set; } = LocationSelectMode.Place;

    /// <summary>
    /// Address mode: the final <i>Enter an address by hand…</i> row, called with the typed text. Offered only with
    /// <see cref="AllowCreate"/>.
    /// </summary>
    [Parameter] public EventCallback<string> OnEnterByHand { get; set; }

    /// <summary>Address mode: the whole pause before the address search is sent — the design's 400 ms; a test passes zero.</summary>
    [Parameter] public int AddressDebounceMs { get; set; } = 400;

    [Parameter] public string? Help { get; set; }

    [Parameter] public string? Error { get; set; }

    [Parameter] public bool Required { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public string? Placeholder { get; set; }

    /// <summary>Offer <c>Create place "…"</c>. The caller gates it on <c>locations.create</c>.</summary>
    [Parameter] public bool AllowCreate { get; set; }

    /// <summary>Called with a staged name, so the parent can announce <i>‹name› will be added when you save</i>.</summary>
    [Parameter] public EventCallback<string> OnStage { get; set; }

    [Parameter] public LocationPickFilter Filter { get; set; } = LocationPickFilter.All;

    /// <summary>Overrides the no-match line — e.g. the weather-point source's <i>No places have coordinates yet</i>.</summary>
    [Parameter] public string? EmptyText { get; set; }

    /// <summary>Ids never offered — the Merge dialog's source.</summary>
    [Parameter] public IReadOnlyCollection<Guid>? ExcludeIds { get; set; }

    /// <summary>Drop the label / help chrome; name the control with <see cref="AriaLabel"/>.</summary>
    [Parameter] public bool Bare { get; set; }

    [Parameter] public string? AriaLabel { get; set; }

    /// <summary>Extra ids the input is described by, after the help/error line (the Merge dialog's description).</summary>
    [Parameter] public string? DescribedById { get; set; }

    /// <summary>The design's 250 ms; a test passes zero.</summary>
    [Parameter] public int DebounceMs { get; set; } = 250;

    [Parameter] public string? Id { get; set; }

    private string _inputId = default!;

    // The options the combobox needs to name the current value: a resolved stored place, a picked row or a
    // staged one. Search rows come from SearchAsync and never need to live here.
    private readonly Dictionary<string, OdsOption> _current = new(StringComparer.Ordinal);

    protected override void OnInitialized() => _inputId = Id ?? $"ods-locsel-{Guid.NewGuid():N}";

    /// <summary>The combobox's string value for a pick: a guid, or <c>new:‹name›</c> for a staged place.</summary>
    internal static string? ToComboValue(LocationPick? pick) =>
        pick is null ? null
        : pick.Id is { } id ? id.ToString()
        : !string.IsNullOrEmpty(pick.NewName) ? StagedPrefix + pick.NewName
        : pick.Candidate is { } candidate ? AddressValue(candidate)
        : null;

    private static string AddressValue(LocationCandidate candidate) =>
        AddressPrefix + (candidate.SourceReference ?? LocationVisuals.CandidateAddress(candidate));

    /// <summary>The pick for a combobox value — the inverse of <see cref="ToComboValue"/>.</summary>
    internal static LocationPick? FromComboValue(string? value) =>
        string.IsNullOrEmpty(value) ? null
        : value.StartsWith(StagedPrefix, StringComparison.Ordinal) ? LocationPick.Staged(value[StagedPrefix.Length..])
        : Guid.TryParse(value, out var id) ? LocationPick.Existing(id)
        : null;

    private string? ComboValue => ToComboValue(Value);

    private bool IsAddressMode => Mode == LocationSelectMode.Address;

    /// <summary>
    /// The Kartverket group and its disclosure — and, in address mode, the hand-entry row: under
    /// <c>locations.create</c>, in either mode (issue #383 v5 §3.3a, v6 §3.2a).
    /// </summary>
    private bool AddressSearchOn => AllowCreate;

    /// <summary>The rows go in the labelled groups: always in address mode, and in place mode with the address search.</summary>
    private bool Grouped => IsAddressMode || AddressSearchOn;

    private string EffectiveLabel => Label ?? (IsAddressMode ? "Address" : "Place");

    private string EffectivePlaceholder => Placeholder ?? (IsAddressMode ? "Search saved places and addresses…" : "Search places…");

    private LocationPickFilter EffectiveFilter => IsAddressMode ? LocationPickFilter.AddressComplete : Filter;

    private string DisclosureId => $"{_inputId}-disclose";

    // The candidates behind the Kartverket rows of the latest search, by their row value.
    private readonly Dictionary<string, LocationCandidate> _candidates = new(StringComparer.Ordinal);

    private OdsComboboxFooter? Footer => IsAddressMode && AddressSearchOn && OnEnterByHand.HasDelegate
        ? new OdsComboboxFooter(EnterByHandLabel, text => OnEnterByHand.InvokeAsync(text)) { Icon = "edit_location_alt" }
        : null;

    /// <summary>
    /// Shows <paramref name="place"/> as the current value's label — after the place was edited through the location
    /// dialog (issue #383 v5 Edit address), so the field names the address it now has.
    /// </summary>
    public void Refresh(ExistingLocation place)
    {
        _current[place.LocationId.ToString()] = SavedOptionFor(place);
        StateHasChanged();
    }

    private IReadOnlyList<OdsOption> CurrentOptions
    {
        get
        {
            // A staged value names itself; it was never on the server.
            if (Value is { IsStaged: true } staged)
                return [StagedOption(staged.NewName!)];
            if (Value is { IsStagedAddress: true } address)
                return [LocationVisuals.StagedAddressOptionFor(ComboValue!, address.Candidate!)];
            return ComboValue is { } v && _current.TryGetValue(v, out var known) ? [known] : [];
        }
    }

    private string? DescribedBy
    {
        get
        {
            // OdsFieldShell's ids: the help line is `‹id›-help`; the error is `‹id›-help-error` beside a
            // help line, else `‹id›-help`.
            var hasHelp = !string.IsNullOrEmpty(Help);
            var helpId = hasHelp ? $"{_inputId}-help" : null;
            var errorId = string.IsNullOrEmpty(Error) ? null : hasHelp ? $"{_inputId}-help-error" : $"{_inputId}-help";
            if (Bare)
                helpId = errorId = null;
            var ids = new[] { helpId, errorId, AddressSearchOn ? DisclosureId : null, DescribedById }
                .Where(v => !string.IsNullOrEmpty(v)).ToArray();
            return ids.Length == 0 ? null : string.Join(' ', ids);
        }
    }

    private string EffectiveEmptyText => EmptyText ?? (EffectiveFilter switch
    {
        LocationPickFilter.AddressComplete => "No saved place with a full address matches.",
        LocationPickFilter.HasCoordinates => "No place with coordinates matches",
        _ => "No places match",
    });

    internal static OdsOption StagedOption(string name) =>
        new(StagedPrefix + name, LocationVisuals.StagedLabel(name))
        {
            Icon = "add_location_alt",
            Sub = "Added when you save",
            AriaLabel = $"{name}, new place, added when you save",
        };

    /// <summary>Whether a place may be offered for a NEW link here.</summary>
    internal static bool Offerable(ExistingLocation l, LocationPickFilter filter, IReadOnlyCollection<Guid>? exclude) =>
        l.Archived is null
        && l.MergedIntoLocationId is null
        && (exclude is null || !exclude.Contains(l.LocationId))
        && (filter != LocationPickFilter.AddressComplete || LocationVisuals.IsAddressComplete(l))
        && (filter != LocationPickFilter.HasCoordinates || l.HasCoordinates);

    /// <summary>One server page of active places for the query — never the unpaged list (issue #383 AC 13).</summary>
    private Task<IReadOnlyList<OdsOption>> SearchAsync(string query, CancellationToken ct) =>
        Grouped ? SearchGroupedAsync(query, ct) : SearchPlacesAsync(query, ct);

    private async Task<IReadOnlyList<OdsOption>> SearchPlacesAsync(string query, CancellationToken ct) =>
        [.. (await SavedPlacesAsync(query, ct)).Select(LocationVisuals.OptionFor)];

    private async Task<IReadOnlyList<ExistingLocation>> SavedPlacesAsync(string query, CancellationToken ct)
    {
        var result = await Locations.ListAsync(1, PageSize, query, [nameof(ArchivalStatus.Active)],
            hasCoordinates: EffectiveFilter == LocationPickFilter.HasCoordinates ? true : null, ct: ct);
        if (!result.IsSuccess)
        {
            // A cancelled request comes back as a failed result, not an exception; report it as the
            // cancellation it is, so OdsAsyncSearch treats it as superseded rather than as a failure (B1).
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(result.Error);
        }

        return [.. (result.Value?.Items ?? []).Where(l => Offerable(l, EffectiveFilter, ExcludeIds))];
    }

    /// <summary>
    /// The grouped picker — address mode (issue #383 v5 §3.3a) and the place picker with the address search (v6
    /// §3.2a): the saved places, and — with the address search on and 3 characters — the Kartverket addresses, both
    /// for the same query and run side by side. Only address results with a Line 1 are listed, and one whose address
    /// equals a listed saved place is dropped, since that place is the one to link. The address search failing is a
    /// note in its group, never a failed field; saved places still list.
    /// </summary>
    private async Task<IReadOnlyList<OdsOption>> SearchGroupedAsync(string query, CancellationToken ct)
    {
        var addresses = AddressSearchOn && query.Trim().Length >= AddressMinChars ? SearchKartverketAsync(query, ct) : null;
        var places = await SavedPlacesAsync(query, ct);
        var rows = places.Select(p => SavedOptionFor(p) with { Group = SavedGroup }).ToList();
        if (addresses is null)
            return rows;

        var candidates = await addresses;
        if (candidates is null)
        {
            rows.Add(new OdsOption($"note:{KartverketGroup}", IsAddressMode ? AddressSearchUnavailable : PlaceAddressSearchUnavailable) { Group = KartverketGroup, IsNote = true });
            return rows;
        }

        var saved = places.Select(p => SameAddressKey(p.Line1, p.PostalCode, p.CountryCode)).ToHashSet(StringComparer.Ordinal);
        _candidates.Clear();
        foreach (var candidate in candidates.Where(c => !saved.Contains(SameAddressKey(c.Line1, c.PostalCode, c.CountryCode ?? "NO"))))
        {
            var value = AddressValue(candidate);
            if (!_candidates.TryAdd(value, candidate))
                continue;
            rows.Add(LocationVisuals.CandidateOptionFor(value, candidate));
        }

        return rows;
    }

    /// <summary>
    /// The address results of the location search, region Norway, after the rest of the 400 ms pause — or null when
    /// it failed. A cancelled search throws, so a superseded query stays silent rather than reading as a failure.
    /// </summary>
    private async Task<IReadOnlyList<LocationCandidate>?> SearchKartverketAsync(string query, CancellationToken ct)
    {
        var extra = Math.Max(0, AddressDebounceMs - DebounceMs);
        if (extra > 0)
            await Task.Delay(extra, ct);
        var result = await Locations.SearchAsync(new LocationSearchRequest { Query = query.Trim(), Region = LocationSearchRegion.Norway }, ct);
        ct.ThrowIfCancellationRequested();
        if (!result.IsSuccess || result.Value is not { } response)
            return null;
        return [.. response.Candidates.Where(c => c.Kind == LocationCandidateKind.Address && !string.IsNullOrWhiteSpace(c.Line1))];
    }

    /// <summary>The design's "same address" test for the dedupe: Line 1, postal code and country, cleaned, any case.</summary>
    private static string SameAddressKey(string? line1, string? postalCode, string? countryCode) =>
        string.Join("|", new[] { line1, postalCode, countryCode }.Select(v => LocationAddressKey.CleanField(v)?.ToUpperInvariant() ?? string.Empty));

    /// <summary>A saved place's row: with its usage in address mode, as a plain place row in place mode.</summary>
    private OdsOption SavedOptionFor(ExistingLocation place) =>
        IsAddressMode ? LocationVisuals.AddressOptionFor(place) : LocationVisuals.OptionFor(place);

    /// <summary>Renders a stored id outside the loaded options, archived ones included.</summary>
    private async Task<OdsOption?> ResolveAsync(string value)
    {
        if (!Guid.TryParse(value, out var id))
            return null;
        var result = await Locations.GetAsync(id);
        if (!result.IsSuccess || result.Value is not { } place)
            return null;
        var option = SavedOptionFor(place);
        _current[value] = option;
        return option;
    }

    private OdsOption? Stage(string text, string? kind)
    {
        var name = LocationTextCleaner.Clean(text);
        if (name is null)
            return null;
        _ = OnStage.InvokeAsync(name);
        return StagedOption(name);
    }

    private Task OnValueChanged(string? value)
    {
        var pick = value is not null && value.StartsWith(AddressPrefix, StringComparison.Ordinal)
            ? _candidates.TryGetValue(value, out var candidate) ? LocationPick.Address(candidate)
              : Value is { IsStagedAddress: true } same && ComboValue == value ? same
              : null
            : FromComboValue(value);
        Value = pick;
        return ValueChanged.InvokeAsync(pick);
    }
}
