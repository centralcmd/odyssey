using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="OdsWizardSteps"/> and <see cref="OdsModal.Steps"/> (Odyssey Design System · components/WizardSteps and
/// Modal <c>steps</c>, version 1795486190-6f31): a <c>nav</c> of an <c>ol</c>, the current step's
/// <c>aria-current="step"</c>, every state in words as well as the disc, and the wizard dialog's head, width class
/// and footer.
/// </summary>
public class OdsWizardStepsTests
{
    private static readonly IReadOnlyList<OdsWizardStep> Three =
    [
        new("Details", State: OdsWizardStepState.Done),
        new("Ingredients", Meta: "12 lines"),
        new("Method"),
    ];

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    [Fact]
    public async Task It_is_a_named_nav_of_an_ordered_list_with_the_current_step_marked()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsWizardSteps>(p => p.Add(w => w.Steps, Three).Add(w => w.Current, 1).Add(w => w.AriaLabel, "Recipe steps"));

        var nav = cut.Find("nav.odc-wiz");
        Assert.Equal("Recipe steps", nav.GetAttribute("aria-label"));
        var items = cut.FindAll("nav > ol.odc-wiz-list > li.odc-wiz-step");
        Assert.Equal(3, items.Count);
        Assert.Equal([null, "step", null], items.Select(li => li.QuerySelector(".odc-wiz-btn")!.GetAttribute("aria-current")));
    }

    [Fact]
    public async Task Each_step_reads_its_position_label_and_status()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsWizardSteps>(p => p.Add(w => w.Steps, Three).Add(w => w.Current, 1));

        var texts = cut.FindAll(".odc-wiz-text").Select(t => t.TextContent).ToList();
        Assert.Equal("Step 1 of 3: Details, completed", texts[0]);
        Assert.Equal("Step 2 of 3: Ingredients, 12 lines", texts[1]);
        Assert.Equal("Step 3 of 3: Method", texts[2]);
    }

    [Fact]
    public async Task States_are_drawn_on_the_disc_and_the_line_fills_up_to_the_current_step()
    {
        await using var ctx = NewContext();
        IReadOnlyList<OdsWizardStep> steps = [new("Details", State: OdsWizardStepState.Done), new("Ingredients", State: OdsWizardStepState.Error), new("Method")];
        var cut = ctx.Render<OdsWizardSteps>(p => p.Add(w => w.Steps, steps).Add(w => w.Current, 2).Add(w => w.ErrorText, "Fix 2 fields"));

        var items = cut.FindAll("li.odc-wiz-step");
        Assert.Contains("is-done", items[0].ClassList);
        Assert.Equal("check", items[0].QuerySelector(".odc-wiz-disc")!.TextContent.Trim());
        Assert.Contains("is-error", items[1].ClassList);
        Assert.Equal("priority_high", items[1].QuerySelector(".odc-wiz-disc")!.TextContent.Trim());
        // The error is in words, not only the glyph and colour.
        Assert.Equal(", Fix 2 fields", items[1].QuerySelector(".odc-wiz-meta")!.TextContent);
        Assert.Contains("is-current", items[2].ClassList);
        Assert.Equal("3", items[2].QuerySelector(".odc-wiz-disc")!.TextContent.Trim());
        Assert.Equal([true, true, false], items.Select(li => li.ClassList.Contains("is-travelled")));
        // The line joins steps: none after the last. Discs and lines are hidden from assistive tech.
        Assert.Equal(2, cut.FindAll(".odc-wiz-line[aria-hidden=true]").Count);
        Assert.All(cut.FindAll(".odc-wiz-disc"), d => Assert.Equal("true", d.GetAttribute("aria-hidden")));
    }

    [Fact]
    public async Task A_done_step_that_is_current_reads_as_current()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsWizardSteps>(p => p.Add(w => w.Steps, Three).Add(w => w.Current, 0));

        var first = cut.Find("li.odc-wiz-step");
        Assert.Contains("is-current", first.ClassList);
        Assert.DoesNotContain("is-done", first.ClassList);
        Assert.Equal("1", first.QuerySelector(".odc-wiz-disc")!.TextContent.Trim());
        // Current wins: no "completed" while the step is the one being edited.
        Assert.Equal("Step 1 of 3: Details", first.QuerySelector(".odc-wiz-text")!.TextContent);
    }

    [Fact]
    public async Task A_finished_step_says_completed_in_words_not_only_in_its_check_disc()
    {
        // WCAG 1.3.1 (PR #598 review): the check disc is aria-hidden, so "done" ends the step's text, sr-only.
        await using var ctx = NewContext();
        IReadOnlyList<OdsWizardStep> steps = [new("Details", State: OdsWizardStepState.Done), new("Ingredients", State: OdsWizardStepState.Error), new("Method", Meta: "3 steps")];
        var cut = ctx.Render<OdsWizardSteps>(p => p.Add(w => w.Steps, steps).Add(w => w.Current, 2));

        var items = cut.FindAll("li.odc-wiz-step");
        var completed = items[0].QuerySelector(".odc-wiz-text > .odc-sr-only")!;
        Assert.Equal(", completed", completed.TextContent);
        Assert.Equal("Step 1 of 3: Details, completed", items[0].QuerySelector(".odc-wiz-text")!.TextContent);
        Assert.DoesNotContain("completed", items[1].TextContent, StringComparison.Ordinal);
        Assert.Equal("Step 3 of 3: Method, 3 steps", items[2].QuerySelector(".odc-wiz-text")!.TextContent);
    }

    [Fact]
    public async Task With_on_step_every_step_is_a_button_that_opens_it()
    {
        await using var ctx = NewContext();
        int? opened = null;
        var cut = ctx.Render<OdsWizardSteps>(p => p.Add(w => w.Steps, Three).Add(w => w.Current, 0).Add(w => w.OnStep, (int i) => opened = i));

        var buttons = cut.FindAll("button.odc-wiz-btn");
        Assert.Equal(3, buttons.Count);
        Assert.All(buttons, b => Assert.Equal("button", b.GetAttribute("type")));
        buttons[2].Click();
        Assert.Equal(2, opened);
    }

    [Fact]
    public async Task Without_on_step_the_path_is_a_read_only_indicator()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsWizardSteps>(p => p.Add(w => w.Steps, Three).Add(w => w.Current, 1));

        Assert.Empty(cut.FindAll("button"));
        Assert.Equal(3, cut.FindAll("span.odc-wiz-btn.static").Count);
    }

    [Fact]
    public async Task A_modal_with_steps_is_a_wizard_with_the_path_in_its_header()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<WizardHost>(p => p.Add(h => h.WithSteps, true));

        var dialog = cut.Find(".mud-dialog.ods-modal");
        Assert.Contains("wizard", dialog.ClassList);
        var head = cut.Find(".mud-dialog-title.ods-modal-head");
        Assert.Contains("has-steps", head.ClassList);
        // The path sits in the header band (never the scrolling body), after the title row.
        var steps = head.QuerySelector(".ods-modal-steps > nav.odc-wiz");
        Assert.NotNull(steps);
        Assert.Null(cut.Find(".mud-dialog-content").QuerySelector("nav.odc-wiz"));
        Assert.Contains("mud-dialog-width-md", dialog.ClassList);
    }

    [Fact]
    public async Task A_modal_without_steps_is_unchanged()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<WizardHost>(p => p.Add(h => h.WithSteps, false));

        var dialog = cut.Find(".mud-dialog.ods-modal");
        Assert.DoesNotContain("wizard", dialog.ClassList);
        Assert.DoesNotContain("has-steps", cut.Find(".ods-modal-head").ClassList);
        Assert.Empty(cut.FindAll(".ods-modal-steps"));
        Assert.Contains("mud-dialog-width-sm", dialog.ClassList);
    }

    [Fact]
    public void The_wizard_css_matches_the_design_system_frame()
    {
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot/css/odyssey-components.css"));

        Assert.Contains(".mud-dialog.ods-modal.wizard { max-width: 760px; }", css);
        Assert.Contains(".mud-dialog .mud-dialog-title.ods-modal-head.has-steps { flex-wrap: wrap;", css);
        Assert.Contains(".ods-modal-steps { flex: 1 0 100%;", css);
        Assert.Matches(@"\.mud-dialog\.ods-modal\.wizard \.mud-dialog-actions\.ods-modal-foot \{[^}]*flex-wrap: wrap", css);

        // The phone block closes: an unclosed @media swallows every rule after it into a phone-only query, which
        // silently unstyles the whole app on a desktop.
        var phone = css.IndexOf("  .odc-wiz-line { margin-left: var(--space-1);", StringComparison.Ordinal);
        Assert.True(phone >= 0);
        Assert.StartsWith("}", css[(css.IndexOf('\n', phone) + 1)..]);
        var code = System.Text.RegularExpressions.Regex.Replace(css, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.Equal(code.Count(c => c == '{'), code.Count(c => c == '}'));
    }

    public sealed class WizardHost : ComponentBase
    {
        [Parameter] public bool WithSteps { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<OdsModal>(2);
            builder.AddComponentParameter(3, nameof(OdsModal.Open), true);
            builder.AddComponentParameter(4, nameof(OdsModal.OnClose), EventCallback.Factory.Create(this, () => { }));
            builder.AddComponentParameter(5, nameof(OdsModal.Title), (RenderFragment)(b => b.AddContent(0, "New recipe")));
            if (WithSteps)
            {
                builder.AddComponentParameter(6, nameof(OdsModal.Steps), (RenderFragment)(b =>
                {
                    b.OpenComponent<OdsWizardSteps>(0);
                    b.AddComponentParameter(1, nameof(OdsWizardSteps.Steps), Three);
                    b.AddComponentParameter(2, nameof(OdsWizardSteps.Current), 0);
                    b.CloseComponent();
                }));
            }

            builder.AddComponentParameter(7, nameof(OdsModal.ChildContent), (RenderFragment)(b => b.AddContent(0, "Body")));
            builder.CloseComponent();
        }
    }
}
