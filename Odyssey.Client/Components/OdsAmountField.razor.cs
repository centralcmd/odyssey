using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsAmountField
{
    /// <summary>Visible label, rendered above the control.</summary>
    [Parameter] public string? Label { get; set; }

    /// <summary>The raw string value. Bindable via @bind-Value.</summary>
    [Parameter] public string? Value { get; set; }

    [Parameter] public EventCallback<string> ValueChanged { get; set; }

    /// <summary>Adornment shown to the left of the number.</summary>
    [Parameter] public string? Prefix { get; set; }

    /// <summary>Adornment shown to the right of the number (e.g. "%").</summary>
    [Parameter] public string? Suffix { get; set; }

    [Parameter] public string Placeholder { get; set; } = "0.00";

    /// <summary>"md" (default, data-entry rows) or "lg" (a large, focal hero input).</summary>
    [Parameter] public string Size { get; set; } = "md";

    /// <summary>"left" (default) or "right" text alignment for the number.</summary>
    [Parameter] public string Align { get; set; } = "left";

    /// <summary>Allow a leading minus for rates / deltas that can go below zero.</summary>
    [Parameter] public bool AllowNegative { get; set; }

    /// <summary>Current direction — one of <see cref="DirectionOptions"/>' values. With
    /// <see cref="DirectionChanged"/> the left edge becomes a button that flips between the two
    /// states, showing each one's short word where a sign would be.</summary>
    [Parameter] public string? Direction { get; set; }

    /// <summary>Fires with the next direction when the lead is clicked. Omit for no lead.</summary>
    [Parameter] public EventCallback<string> DirectionChanged { get; set; }

    /// <summary>The two states the lead flips between — same shape as OdsMoneyField's. Use where the
    /// record stores a DIRECTION rather than a sign. Exactly two options.</summary>
    [Parameter] public IReadOnlyList<OdsDirectionOption>? DirectionOptions { get; set; }

    /// <summary>Colors the lead and the value by finance semantics; defaults to the active
    /// direction's own tone.</summary>
    [Parameter] public string? Tone { get; set; }

    [Parameter] public string? Help { get; set; }

    /// <summary>Rich helper content (takes precedence over <see cref="Help"/>), e.g. a hint with bold.</summary>
    [Parameter] public RenderFragment? HelpContent { get; set; }

    [Parameter] public string? Error { get; set; }

    [Parameter] public bool Required { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public bool AutoFocus { get; set; }

    /// <summary>
    /// Accessible name for the input where no visible <see cref="Label"/> is set — a hero whose
    /// caption is a separate heading, a dense cell. Without it such a field has NO programmatic name:
    /// a nearby <c>&lt;div&gt;</c> is not a label. Mirrors <c>OdsMoneyField.AriaLabel</c>, so the two
    /// halves of a value control are named the same way whichever unit is showing.
    /// </summary>
    [Parameter] public string? AriaLabel { get; set; }

    [Parameter] public string? Class { get; set; }

    [Parameter] public string? Id { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? UserAttributes { get; set; }

    private string FieldId = default!;

    protected override void OnInitialized() => FieldId = Id ?? $"odc-amount-{Guid.NewGuid():N}";

    // ---- direction lead -------------------------------------------------------------------

    /// <summary>The finance pair, so a caller passing only a direction still gets a sensible − / +.</summary>
    private static readonly IReadOnlyList<OdsDirectionOption> FinanceDirections =
    [
        new() { Value = "expense", Label = "Expense", Sign = "−", Tone = "expense" },
        new() { Value = "income", Label = "Income", Sign = "+", Tone = "income" },
    ];

    private bool DirectionMode => !string.IsNullOrEmpty(Direction) && DirectionChanged.HasDelegate;

    private IReadOnlyList<OdsDirectionOption> Directions =>
        DirectionOptions is { Count: 2 } supplied ? supplied : FinanceDirections;

    private int IndexOfDirection
    {
        get
        {
            var options = Directions;
            for (var i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].Value, Direction, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return 0;
        }
    }

    private OdsDirectionOption CurrentDirection => Directions[IndexOfDirection];

    private OdsDirectionOption NextDirection => Directions[(IndexOfDirection + 1) % Directions.Count];

    private string DirectionAriaLabel =>
        $"{CurrentDirection.Label} — switch to {NextDirection.Label.ToLowerInvariant()}";

    private string DirectionTitle => $"{CurrentDirection.Label} — click to switch";

    private string? EffectiveTone => Tone ?? (DirectionMode ? CurrentDirection.Tone : null);

    private async Task FlipDirectionAsync()
    {
        var next = NextDirection.Value;
        Direction = next;
        await DirectionChanged.InvokeAsync(next);
    }

    private string BoxClass =>
        string.Join(' ', new[]
        {
            "odc-amount",
            Size == "lg" ? "lg" : null,
            string.IsNullOrEmpty(EffectiveTone) ? null : $"tone-{EffectiveTone}",
            string.IsNullOrEmpty(Error) ? null : "error",
            Disabled ? "disabled" : null,
        }.Where(value => !string.IsNullOrEmpty(value)));

    // A visible label already names the input through OdsFieldShell's <label for>, so an aria-label
    // would override it with a second, competing name.
    private string? EffectiveAriaLabel => string.IsNullOrEmpty(Label) ? AriaLabel : null;

    private string? DescribedBy
    {
        get
        {
            var hasHelp = HelpContent is not null || !string.IsNullOrEmpty(Help);
            var hasError = !string.IsNullOrEmpty(Error);
            if (!hasHelp && !hasError)
            {
                return null;
            }
            // OdsFieldShell gives the error its own id only when a help node renders alongside it.
            return hasHelp && hasError ? $"{FieldId}-help {FieldId}-help-error" : $"{FieldId}-help";
        }
    }

    private Task OnInput(ChangeEventArgs e)
    {
        var raw = e.Value?.ToString() ?? "";
        Value = new string(raw.Where(ch => char.IsDigit(ch) || ch is '.' or ',' || (AllowNegative && ch is '-')).ToArray());
        return ValueChanged.InvokeAsync(Value);
    }
}
