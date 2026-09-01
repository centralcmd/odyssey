using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// What <c>OdsTagMultiSelect</c>'s chips actually SAY — the shared component's own resolution rule,
/// rendered rather than derived.
/// </summary>
/// <remarks>
/// <para>
/// The regression this exists for: a selected id was resolved only against a host's stored records, so
/// a value the user had just picked read as "Unavailable" — the surface claiming not to know a record
/// it was about to save successfully. That was found on the insurance link pickers, whose dialog this
/// PR removed; the coverage belongs here rather than there, because the resolution is the SHARED
/// component's (<c>Selected</c> maps each id through <c>Options</c>, falling back to
/// <c>UnknownLabel</c>) and five other dialogs still render it — transaction tags, Journal contacts,
/// Photos people and albums.
/// </para>
/// <para>
/// Two rules, both invisible except at render: an id present in <c>Options</c> reads as its label, and
/// an id absent from them reads as <c>UnknownLabel</c> — never, in either case, as the raw GUID.
/// </para>
/// </remarks>
public class OdsTagMultiSelectChipTests
{
    private const string KnownId = "11111111-1111-1111-1111-111111111111";
    private const string StrangerId = "22222222-2222-2222-2222-222222222222";

    private static IRenderedComponent<PickerHost> Render(
        IReadOnlyCollection<string> value, bool templated = false, Func<string, bool>? locked = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        return ctx.Render<PickerHost>(p => p
            .Add(h => h.Value, value)
            .Add(h => h.Templated, templated)
            .Add(h => h.Locked, locked));
    }

    private static string ChipText(IRenderedComponent<PickerHost> cut) =>
        string.Join(" ", cut.FindAll(".odc-tagms-chip, .odc-tagms-tchip").Select(c => c.TextContent));

    /// <summary>The case that was broken outright: a picked, known id reads by name.</summary>
    [Fact]
    public void A_selected_id_present_in_the_options_renders_by_label()
    {
        var text = ChipText(Render([KnownId]));

        Assert.Contains("Sam Rivera", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Unavailable", text, StringComparison.Ordinal);
        Assert.DoesNotContain(KnownId, text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An id the options do not carry reads as the caller's UnknownLabel. The raw id is never shown —
    /// it is the one thing the fallback exists to keep out of the UI.
    /// </summary>
    [Fact]
    public void A_selected_id_absent_from_the_options_renders_as_the_unknown_label()
    {
        var text = ChipText(Render([StrangerId]));

        Assert.Contains("Unavailable", text, StringComparison.Ordinal);
        Assert.DoesNotContain(StrangerId, text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same rule holds when a host supplies a ChipTemplate: the template owns the body,
    /// the component still owns which option it is handed.</summary>
    [Fact]
    public void A_templated_chip_resolves_against_the_options_too()
    {
        var text = ChipText(Render([KnownId, StrangerId], templated: true));

        Assert.Contains("Sam Rivera", text, StringComparison.Ordinal);
        Assert.DoesNotContain(KnownId, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(StrangerId, text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A member the write path refuses to remove carries NO remove control — an affordance that
    /// silently no-ops is worse than none, since the chip reappears on reload.
    /// </summary>
    [Fact]
    public void A_locked_member_is_rendered_without_a_remove_control()
    {
        var open = Render([KnownId, StrangerId], locked: id => id == StrangerId);

        var removeLabels = open.FindAll("button[aria-label^='Remove']")
            .Select(b => b.GetAttribute("aria-label"))
            .ToList();

        Assert.Contains(removeLabels, l => l!.Contains("Sam Rivera", StringComparison.Ordinal));
        Assert.DoesNotContain(removeLabels, l => l!.Contains("Unavailable", StringComparison.Ordinal));
    }

    /// <summary>The picker beside MudBlazor's providers, which portal its popover.</summary>
    public sealed class PickerHost : ComponentBase
    {
        [Parameter] public IReadOnlyCollection<string> Value { get; set; } = [];

        [Parameter] public bool Templated { get; set; }

        [Parameter] public Func<string, bool>? Locked { get; set; }

        private static readonly IReadOnlyList<OdsOption> Options =
            [new OdsOption(KnownId, "Sam Rivera") { Sub = "Person" }];

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsTagMultiSelect>(1);
            builder.AddComponentParameter(2, nameof(OdsTagMultiSelect.Id), "tms");
            builder.AddComponentParameter(3, nameof(OdsTagMultiSelect.Label), "Beneficiaries");
            builder.AddComponentParameter(4, nameof(OdsTagMultiSelect.Value), Value);
            builder.AddComponentParameter(5, nameof(OdsTagMultiSelect.Options), Options);
            builder.AddComponentParameter(6, nameof(OdsTagMultiSelect.UnknownLabel), "Unavailable");
            builder.AddComponentParameter(7, nameof(OdsTagMultiSelect.Noun), "beneficiary");
            builder.AddComponentParameter(8, nameof(OdsTagMultiSelect.NounPlural), "beneficiaries");
            if (Locked is not null)
            {
                builder.AddComponentParameter(9, nameof(OdsTagMultiSelect.PreserveOnClear), Locked);
            }

            if (Templated)
            {
                // A template that renders the option's own label — the shape every real ChipTemplate
                // has, reduced to the part these tests are about.
                builder.AddComponentParameter(10, nameof(OdsTagMultiSelect.ChipTemplate),
                    (RenderFragment<string>)(id => b => b.AddContent(0, LabelFor(id))));
            }

            builder.CloseComponent();
        }

        private static string LabelFor(string id) =>
            Options.FirstOrDefault(o => o.Value == id)?.Label ?? "Unavailable";
    }
}
