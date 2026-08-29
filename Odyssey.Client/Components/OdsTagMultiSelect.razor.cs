using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsTagMultiSelect
{
    /// <summary>Visible label rendered above the control.</summary>
    [Parameter] public string? Label { get; set; }

    /// <summary>Accessible name for a label-less usage (e.g. a dense grid cell). Applied as the
    /// activator's <c>aria-label</c> when no visible <see cref="Label"/> is set.</summary>
    [Parameter] public string? AriaLabel { get; set; }

    /// <summary>Id applied to the activator button, so focus can be restored to this cell.</summary>
    [Parameter] public string? Id { get; set; }

    /// <summary>Selected tag ids. Bindable via @bind-Value.</summary>
    [Parameter] public IReadOnlyCollection<string> Value { get; set; } = [];

    [Parameter] public EventCallback<IReadOnlyCollection<string>> ValueChanged { get; set; }

    /// <summary>The selectable tags.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<OdsOption> Options { get; set; } = [];

    /// <summary>Placeholder shown in the control box when nothing is selected.</summary>
    [Parameter] public string Placeholder { get; set; } = "No tags";

    /// <summary>Label next to the "+" affordance when the box is empty.</summary>
    [Parameter] public string AddLabel { get; set; } = "Add tag";

    /// <summary>
    /// Enables an inline "Create …" row when the query matches no option.
    /// Receives the typed text; return the new option (or null to skip).
    /// </summary>
    [Parameter] public Func<string, OdsOption?>? OnCreate { get; set; }

    /// <summary>Prefix for the create row label.</summary>
    [Parameter] public string CreateLabel { get; set; } = "Create";

    /// <summary>Helper text below the control (replaced by <see cref="Error"/> when set).</summary>
    [Parameter] public string? Help { get; set; }

    /// <summary>Error message — flips the field to its error state and replaces the helper.</summary>
    [Parameter] public string? Error { get; set; }

    [Parameter] public bool Required { get; set; }

    /// <summary>Render an "Optional" marker next to the label.</summary>
    [Parameter] public bool Optional { get; set; }

    [Parameter] public bool Disabled { get; set; }

    /// <summary>Text shown when the search matches no tags and create is unavailable.</summary>
    [Parameter] public string EmptyText { get; set; } = "No tags match";

    [Parameter] public string? Class { get; set; }

    private MudMenu _menu = default!;
    private bool _open;
    private string _query = string.Empty;

    // Mirror the controlled Value into a local set so toggling a row updates the
    // chips immediately; OnParametersSet re-syncs to the parent.
    private HashSet<string> _selected = [];

    protected override void OnParametersSet() => _selected = [.. Value];

    private string FieldClass
    {
        get
        {
            var cls = "odc-field";
            if (!string.IsNullOrEmpty(Error))
                cls += " error";
            if (!string.IsNullOrEmpty(Class))
                cls += " " + Class;
            return cls;
        }
    }

    private string ControlClass
    {
        get
        {
            var cls = "odc-tagms-control";
            if (_open)
                cls += " open";
            if (_selected.Count == 0)
                cls += " placeholder";
            return cls;
        }
    }

    private string? Message => string.IsNullOrEmpty(Error) ? Help : Error;

    private string SearchPlaceholder => OnCreate is not null ? "Search or add a tag…" : "Search tags…";

    private string CreateRowLabel => $"{CreateLabel} \"{_query.Trim()}\"";

    private static string RemoveLabel(OdsOption option) => $"Remove {option.Label}";

    // Selected options in the parent's order, falling back to a bare id if the
    // option list hasn't loaded the tag yet (so a chip never silently vanishes).
    private IReadOnlyList<OdsOption> Selected => Value
        .Select(v => Options.FirstOrDefault(o => o.Value == v) ?? new OdsOption(v, v))
        .ToList();

    private IEnumerable<OdsOption> Filtered => string.IsNullOrWhiteSpace(_query)
        ? Options
        : Options.Where(o => o.Label.Contains(_query.Trim(), StringComparison.OrdinalIgnoreCase));

    private bool ShowCreate => OnCreate is not null
        && !string.IsNullOrWhiteSpace(_query)
        && !Options.Any(o => string.Equals(o.Label, _query.Trim(), StringComparison.OrdinalIgnoreCase));

    private Task Toggle(string value)
    {
        if (!_selected.Remove(value))
            _selected.Add(value);
        return Emit();
    }

    private Task Remove(string value)
    {
        _selected.Remove(value);
        return Emit();
    }

    private Task Clear()
    {
        _selected.Clear();
        return Emit();
    }

    private Task Create()
    {
        if (OnCreate is null || string.IsNullOrWhiteSpace(_query))
            return Task.CompletedTask;

        var created = OnCreate(_query.Trim());
        _query = string.Empty;
        if (created is null)
            return Task.CompletedTask;

        _selected.Add(created.Value);
        return Emit();
    }

    private Task OnSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && ShowCreate)
            return Create();
        return Task.CompletedTask;
    }

    private Task Done() => _menu.CloseMenuAsync();

    // Emit the next set in the parent's existing order (preserving it) plus any
    // newly-added ids appended, so chip order stays stable across edits.
    private Task Emit()
    {
        var ordered = Value.Where(_selected.Contains)
            .Concat(_selected.Where(v => !Value.Contains(v)))
            .ToList();
        return ValueChanged.InvokeAsync(ordered);
    }
}
