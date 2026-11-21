using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsCardSelect
{
    /// <summary>The options, in display order.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<OdsCardSelectOption> Options { get; set; } = [];

    /// <summary>Selected value. Bindable via @bind-Value.</summary>
    [Parameter] public string? Value { get; set; }

    [Parameter] public EventCallback<string> ValueChanged { get; set; }

    /// <summary>Accessible name, when there is no visible label to point <see cref="AriaLabelledBy"/> at.</summary>
    [Parameter] public string? AriaLabel { get; set; }

    /// <summary>Id of the visible label that names the group — preferred over <see cref="AriaLabel"/>.</summary>
    [Parameter] public string? AriaLabelledBy { get; set; }

    /// <summary>Exposes the group as required (<c>aria-required</c>); the label's <c>*</c> is visual only.</summary>
    [Parameter] public bool Required { get; set; }

    /// <summary>Group accent — icon colour, and the border when <see cref="AccentLine"/> is absent.</summary>
    [Parameter] public string? Accent { get; set; }

    /// <summary>Group border colour when selected / hovered — defaults to <see cref="Accent"/>.</summary>
    [Parameter] public string? AccentLine { get; set; }

    /// <summary>Group soft tint — the selected card's background.</summary>
    [Parameter] public string? AccentSoft { get; set; }

    /// <summary>Fixed column count; null (default) fills the row with one column per option.</summary>
    [Parameter] public int? Columns { get; set; }

    /// <summary>Caps each card's width, in px — pair with <see cref="Center"/> for a balanced two-up.</summary>
    [Parameter] public int? MaxItemWidth { get; set; }

    /// <summary>Centre the grid instead of stretching it.</summary>
    [Parameter] public bool Center { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? UserAttributes { get; set; }

    private ElementReference[] _buttonRefs = [];

    protected override void OnParametersSet()
    {
        if (_buttonRefs.Length != Options.Count)
        {
            _buttonRefs = new ElementReference[Options.Count];
        }
    }

    private string GroupStyle
    {
        get
        {
            var track = MaxItemWidth is { } width ? $"{width}px" : "1fr";
            var repeat = Columns is { } columns ? columns.ToString(System.Globalization.CultureInfo.InvariantCulture) : "auto-fit";
            var style = $"grid-template-columns:repeat({repeat}, minmax(0, {track}));";
            style += Declaration("--odc-cardsel-accent", Accent);
            style += Declaration("--odc-cardsel-line", AccentLine ?? Accent);
            style += Declaration("--odc-cardsel-soft", AccentSoft);
            return style;
        }
    }

    private static string? OptionStyle(OdsCardSelectOption option)
    {
        var style = Declaration("--odc-cardsel-accent", option.Color)
                    + Declaration("--odc-cardsel-line", option.Line ?? option.Color)
                    + Declaration("--odc-cardsel-soft", option.Soft);
        return style.Length == 0 ? null : style;
    }

    // One custom-property declaration, or nothing when the value is empty or could break out of it:
    // a `;` or brace would start another declaration, and url()/image-set()/@import would fetch a
    // resource from a style attribute. The accents are colours and var() references, never those.
    private static string Declaration(string property, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.IndexOfAny([';', '{', '}', '<', '>', '"', '\\', '@']) >= 0
            || value.Contains("url(", StringComparison.OrdinalIgnoreCase)
            || value.Contains("image", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $"{property}:{value};";
    }

    // The selected card is the group's tab stop; with nothing selected yet the first enabled card
    // takes it, so the group is never unreachable from the keyboard.
    private bool TabStop(int index)
    {
        var selected = SelectedIndex;
        if (selected >= 0)
        {
            return index == selected;
        }
        for (var i = 0; i < Options.Count; i++)
        {
            if (!Options[i].Disabled)
            {
                return i == index;
            }
        }
        return false;
    }

    private int SelectedIndex
    {
        get
        {
            for (var i = 0; i < Options.Count; i++)
            {
                if (Options[i].Value == Value)
                {
                    return i;
                }
            }
            return -1;
        }
    }

    // The next enabled card from `from` in `direction`, wrapping; -1 when every card is disabled.
    private int Step(int from, int direction)
    {
        var count = Options.Count;
        for (var offset = 1; offset <= count; offset++)
        {
            var candidate = ((from + direction * offset) % count + count) % count;
            if (!Options[candidate].Disabled)
            {
                return candidate;
            }
        }
        return -1;
    }

    private Task Select(OdsCardSelectOption option)
    {
        if (option.Disabled || option.Value == Value)
        {
            return Task.CompletedTask;
        }
        Value = option.Value;
        return ValueChanged.InvokeAsync(option.Value);
    }

    // Keys act from the FOCUSED card, not the selected one: with nothing selected yet the first
    // enabled card holds the tab stop, and arrows from it must still move (WCAG 2.1.1).
    private async Task OnKeyDown(KeyboardEventArgs e, int focusedIndex)
    {
        var nextIndex = e.Key switch
        {
            "ArrowRight" or "ArrowDown" => Step(focusedIndex, +1),
            "ArrowLeft" or "ArrowUp" => Step(focusedIndex, -1),
            "Home" => Step(-1, +1),
            "End" => Step(Options.Count, -1),
            _ => -1,
        };
        if (nextIndex < 0)
        {
            return;
        }

        await Select(Options[nextIndex]);
        // APG radiogroup: selection and DOM focus move together (WCAG 2.1.1 / 4.1.2).
        await _buttonRefs[nextIndex].FocusAsync();
    }
}
