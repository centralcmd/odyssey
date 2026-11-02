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

    [Inject] private ILocationsApiClient Locations { get; set; } = default!;

    /// <summary>An existing place, a staged new one, or nothing.</summary>
    [Parameter] public LocationPick? Value { get; set; }

    [Parameter] public EventCallback<LocationPick?> ValueChanged { get; set; }

    [Parameter] public string Label { get; set; } = "Place";

    [Parameter] public string? Help { get; set; }

    [Parameter] public string? Error { get; set; }

    [Parameter] public bool Required { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public string Placeholder { get; set; } = "Search places…";

    /// <summary>Offer <c>Create place "…"</c>. The caller gates it on <c>locations.create</c>.</summary>
    [Parameter] public bool AllowCreate { get; set; }

    /// <summary>Called with a staged name, so the parent can announce <i>‹name› will be added when you save</i>.</summary>
    [Parameter] public EventCallback<string> OnStage { get; set; }

    [Parameter] public LocationPickFilter Filter { get; set; } = LocationPickFilter.All;

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
        : null;

    /// <summary>The pick for a combobox value — the inverse of <see cref="ToComboValue"/>.</summary>
    internal static LocationPick? FromComboValue(string? value) =>
        string.IsNullOrEmpty(value) ? null
        : value.StartsWith(StagedPrefix, StringComparison.Ordinal) ? LocationPick.Staged(value[StagedPrefix.Length..])
        : Guid.TryParse(value, out var id) ? LocationPick.Existing(id)
        : null;

    private string? ComboValue => ToComboValue(Value);

    private IReadOnlyList<OdsOption> CurrentOptions
    {
        get
        {
            // A staged value names itself; it was never on the server.
            if (Value is { IsStaged: true } staged)
                return [StagedOption(staged.NewName!)];
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
            var ids = new[] { helpId, errorId, DescribedById }
                .Where(v => !string.IsNullOrEmpty(v)).ToArray();
            return ids.Length == 0 ? null : string.Join(' ', ids);
        }
    }

    private string EffectiveEmptyText => Filter == LocationPickFilter.AddressComplete
        ? "No saved place with a full address matches."
        : "No places match";

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
        && (filter != LocationPickFilter.AddressComplete || LocationVisuals.IsAddressComplete(l));

    /// <summary>One server page of active places for the query — never the unpaged list (issue #383 AC 13).</summary>
    private async Task<IReadOnlyList<OdsOption>> SearchAsync(string query, CancellationToken ct)
    {
        var result = await Locations.ListAsync(1, PageSize, query, [nameof(ArchivalStatus.Active)], ct: ct);
        if (!result.IsSuccess)
        {
            // A cancelled request comes back as a failed result, not an exception; report it as the
            // cancellation it is, so OdsAsyncSearch treats it as superseded rather than as a failure (B1).
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(result.Error);
        }

        return [.. (result.Value?.Items ?? []).Where(l => Offerable(l, Filter, ExcludeIds)).Select(LocationVisuals.OptionFor)];
    }

    /// <summary>Renders a stored id outside the loaded options, archived ones included.</summary>
    private async Task<OdsOption?> ResolveAsync(string value)
    {
        if (!Guid.TryParse(value, out var id))
            return null;
        var result = await Locations.GetAsync(id);
        if (!result.IsSuccess || result.Value is not { } place)
            return null;
        var option = LocationVisuals.OptionFor(place);
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
        var pick = FromComboValue(value);
        Value = pick;
        return ValueChanged.InvokeAsync(pick);
    }
}
