using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// No menu or select-style trigger renders an interactive control inside another one (issue #255,
/// WCAG 4.1.2 and 2.4.3).
///
/// <para>
/// MudMenu wraps a custom <c>ActivatorContent</c> in its own
/// <c>&lt;div tabindex="0" role="button" aria-haspopup="menu" aria-expanded&gt;</c>. Every Ods menu
/// and select put a real <c>&lt;button&gt;</c> inside that div, so each control was two tab stops
/// (an unnamed "button", then the real one — 25 extra on a 25-row table), a screen reader announced
/// a button nested in a button, and <c>aria-expanded</c> sat on the div rather than the focused,
/// named control. OdsMenu now uses MudMenu's built-in icon-button activator, and every select-style
/// control is built on <see cref="OdsPopupMenu"/>, whose trigger is a sibling of the MudMenu.
/// </para>
///
/// <para>
/// Each case is asserted closed AND open, over the whole rendered tree including the portaled
/// popover, so a row, a chip's remove button or a search box that ends up inside another control is
/// caught as well as the trigger itself. The source lint at the end is what keeps a NEW surface from
/// reintroducing the wrapper this suite cannot know to render.
/// </para>
/// </summary>
public class NestedInteractiveControlTests
{
    /// <summary>What counts as interactive: natively focusable controls, anything given a button
    /// role, and anything given a tabindex.</summary>
    internal const string InteractiveSelector =
        "button, a[href], input, select, textarea, [role='button'], [tabindex]";

    public static TheoryData<string> Cases => [.. CaseTable.Keys];

    /// <summary>A control under test: its trigger, how to render it, and which popup it opens —
    /// <c>"menu"</c> (single-choice rows, or OdsMenu's actions) or <c>"dialog"</c> (a panel).</summary>
    internal sealed record Case(string Trigger, Action<RenderTreeBuilder> Build, string Popup);

    internal static readonly IReadOnlyList<OdsOption> Options =
    [
        new("a", "Alpha"),
        new("b", "Bravo"),
        new("c", "Charlie"),
    ];

    internal static readonly Dictionary<string, Case> CaseTable = new()
    {
        [nameof(OdsMenu)] = new("button.mud-icon-button", b =>
        {
            b.OpenComponent<OdsMenu>(0);
            b.AddComponentParameter(1, nameof(OdsMenu.Items), (IReadOnlyList<OdsMenuItem>)
            [
                new OdsMenuItem { Icon = "edit", Label = "Edit" },
                new OdsMenuItem { Divider = true },
                new OdsMenuItem { Icon = "delete", Label = "Delete", Danger = true },
            ]);
            b.AddComponentParameter(2, nameof(OdsMenu.AriaLabel), "Row actions");
            b.CloseComponent();
        }, "menu"),
        [nameof(OdsMultiSelect)] = new("button.odc-ms-trigger", b =>
        {
            b.OpenComponent<OdsMultiSelect>(0);
            b.AddComponentParameter(1, nameof(OdsMultiSelect.Label), "Status");
            b.AddComponentParameter(2, nameof(OdsMultiSelect.Options), Options);
            b.AddComponentParameter(3, nameof(OdsMultiSelect.Values), (IReadOnlyCollection<string>)["a"]);
            b.CloseComponent();
        }, "dialog"),
        [nameof(OdsSortSelect<object>)] = new("button.odc-sortsel-trigger", b =>
        {
            b.OpenComponent<OdsSortSelect<object>>(0);
            b.AddComponentParameter(1, nameof(OdsSortSelect<object>.Fields), (IReadOnlyList<OdsSortField<object>>)
            [
                new OdsSortField<object> { Key = "name", Label = "Name" },
                new OdsSortField<object> { Key = "date", Label = "Date", Type = OdsSortType.Date },
            ]);
            b.CloseComponent();
        }, "menu"),
        [nameof(OdsTypeSelect)] = new("button.odc-select-trigger", b =>
        {
            b.OpenComponent<OdsTypeSelect>(0);
            b.AddComponentParameter(1, nameof(OdsTypeSelect.Label), "Type");
            b.AddComponentParameter(2, nameof(OdsTypeSelect.Value), "a");
            b.AddComponentParameter(3, nameof(OdsTypeSelect.Types), (IReadOnlyList<OdsTypeOption>)
            [
                new OdsTypeOption { Key = "a", Label = "Alpha", Icon = "home", Color = "red", Soft = "pink" },
                new OdsTypeOption { Key = "b", Label = "Bravo", Icon = "work", Color = "blue", Soft = "azure" },
            ]);
            b.CloseComponent();
        }, "menu"),
        [nameof(OdsPageSizeSelect)] = new("button.odc-rpp-trigger", b =>
        {
            b.OpenComponent<OdsPageSizeSelect>(0);
            b.CloseComponent();
        }, "menu"),
        [nameof(OdsInlineSelect)] = new("button.odc-rpp-trigger", b =>
        {
            b.OpenComponent<OdsInlineSelect>(0);
            b.AddComponentParameter(1, nameof(OdsInlineSelect.Prefix), "View");
            b.AddComponentParameter(2, nameof(OdsInlineSelect.Value), "a");
            b.AddComponentParameter(3, nameof(OdsInlineSelect.Options), Options);
            b.CloseComponent();
        }, "menu"),
        // Two chips, each with its own remove button, sit in the same box as the trigger — the
        // shape where a wrapper would nest the most controls.
        [nameof(OdsTagMultiSelect)] = new("button.odc-tagms-trigger", b =>
        {
            b.OpenComponent<OdsTagMultiSelect>(0);
            b.AddComponentParameter(1, nameof(OdsTagMultiSelect.Label), "Tags");
            b.AddComponentParameter(2, nameof(OdsTagMultiSelect.Options), Options);
            b.AddComponentParameter(3, nameof(OdsTagMultiSelect.Value), (IReadOnlyCollection<string>)["a", "b"]);
            b.CloseComponent();
        }, "dialog"),
        [nameof(OdsMoneyField)] = new("button.odc-money-cur", b =>
        {
            b.OpenComponent<OdsMoneyField>(0);
            b.AddComponentParameter(1, nameof(OdsMoneyField.Label), "Amount");
            b.AddComponentParameter(2, nameof(OdsMoneyField.Value), "10");
            b.AddComponentParameter(3, nameof(OdsMoneyField.ValueChanged), EventCallback.Factory.Create<string>(new object(), _ => { }));
            b.AddComponentParameter(4, nameof(OdsMoneyField.Currency), "USD");
            b.AddComponentParameter(5, nameof(OdsMoneyField.CurrencyOptions), (IReadOnlyList<OdsOption>)[new("USD", "US dollar"), new("EUR", "Euro")]);
            b.AddComponentParameter(6, nameof(OdsMoneyField.CurrencyChanged), EventCallback.Factory.Create<string>(new object(), _ => { }));
            b.CloseComponent();
        }, "dialog"),
        [nameof(AccountSmartTagAdder)] = new("button.odc-smarttags-add", b =>
        {
            b.OpenComponent<AccountSmartTagAdder>(0);
            b.AddComponentParameter(1, nameof(AccountSmartTagAdder.Options), Options);
            b.AddComponentParameter(2, nameof(AccountSmartTagAdder.SelectedIds), (IReadOnlyCollection<string>)["a"]);
            b.CloseComponent();
        }, "dialog"),
    };

    internal static IRenderedComponent<IComponent> Render(BunitContext ctx, string name) =>
        ctx.Render(builder =>
        {
            // MudBlazor portals an open popover into this provider; without it the open-state
            // assertions would pass vacuously against a popover that renders nowhere.
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenRegion(1);
            CaseTable[name].Build(builder);
            builder.CloseRegion();
        });

    internal static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    /// <summary>
    /// Every interactive element that has an interactive ancestor within the same root, described
    /// for the failure message. Ancestry stops at the root so a host's own focus container (a
    /// MudDialog's <c>tabindex="-1"</c> focus trap) does not count against the control under test.
    /// </summary>
    internal static IReadOnlyList<string> NestedInteractive(IEnumerable<IElement> roots) =>
        [.. roots
            .SelectMany(root => root.QuerySelectorAll(InteractiveSelector).Select(element => (root, element)))
            .Select(pair => (pair.element, outer: Ancestors(pair.element, pair.root)
                .FirstOrDefault(a => a.Matches(InteractiveSelector))))
            .Where(pair => pair.outer is not null)
            .Select(pair =>
                $"<{pair.element.LocalName} class=\"{pair.element.ClassName}\"> inside " +
                $"<{pair.outer!.LocalName} class=\"{pair.outer.ClassName}\" role=\"{pair.outer.GetAttribute("role")}\">")
            .Distinct()];

    private static IEnumerable<IElement> Ancestors(IElement element, IElement root)
    {
        for (var parent = element.ParentElement; parent is not null && parent != root.ParentElement; parent = parent.ParentElement)
            yield return parent;
    }

    private static void AssertNoNesting(IRenderedComponent<IComponent> cut)
    {
        cut.Render();
        var nested = NestedInteractive(cut.Nodes.OfType<IElement>());
        Assert.True(nested.Count == 0, "Interactive element nested in another: " + string.Join("; ", nested));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_closed_control_has_no_nested_interactive_element(string name)
    {
        var ctx = NewContext();
        var cut = Render(ctx, name);

        AssertNoNesting(cut);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_open_control_has_no_nested_interactive_element(string name)
    {
        var ctx = NewContext();
        var cut = Render(ctx, name);

        cut.Find(CaseTable[name].Trigger).Click();

        // The open popover really rendered — otherwise this would pass against nothing.
        cut.Render();
        Assert.NotEmpty(cut.FindAll(".mud-popover-open"));
        AssertNoNesting(cut);
    }

    /// <summary>
    /// MudMenu's activator wrapper is exactly the element this issue removed: an unnamed
    /// <c>role="button"</c> tab stop that held the real trigger.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void No_menu_activator_wrapper_is_rendered(string name)
    {
        var ctx = NewContext();
        var cut = Render(ctx, name);

        Assert.Empty(cut.FindAll(".mud-menu-activator"));
        Assert.Empty(cut.FindAll("div[role='button']"));
    }

    /// <summary>
    /// The popup state is exposed on the focused, named control itself: <c>aria-haspopup</c> always,
    /// and <c>aria-expanded</c> tracking whether the popover is open.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void The_trigger_button_itself_exposes_aria_expanded(string name)
    {
        var ctx = NewContext();
        var cut = Render(ctx, name);
        var selector = CaseTable[name].Trigger;

        var trigger = cut.Find(selector);
        Assert.Equal("button", trigger.LocalName);
        Assert.False(string.IsNullOrEmpty(trigger.GetAttribute("aria-haspopup")));
        Assert.Equal("false", trigger.GetAttribute("aria-expanded"));

        trigger.Click();

        Assert.Equal("true", cut.Find(selector).GetAttribute("aria-expanded"));
    }

    /// <summary>
    /// The one trigger is the only tab stop the closed control contributes: every other element in
    /// its tab order is a chip's remove button (OdsTagMultiSelect), never a wrapper.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void The_closed_control_has_exactly_one_popup_tab_stop(string name)
    {
        var ctx = NewContext();
        var cut = Render(ctx, name);

        var popupStops = cut.FindAll("[aria-haspopup]")
            .Where(e => e.GetAttribute("tabindex") != "-1" && !e.HasAttribute("disabled"))
            .ToList();
        Assert.Single(popupStops);
    }

    /// <summary>
    /// The positive control: the helper has to FIND the defect this suite guards against, or every
    /// "no nesting" assertion above could be passing vacuously.
    /// </summary>
    [Fact]
    public void The_nesting_check_flags_the_old_activator_shape()
    {
        var ctx = NewContext();
        var cut = ctx.Render(builder => builder.AddMarkupContent(0,
            "<div class=\"mud-menu-activator\" tabindex=\"0\" role=\"button\"><button type=\"button\">Status</button></div>" +
            "<label><input type=\"checkbox\" /> fine</label>"));

        var nested = NestedInteractive(cut.Nodes.OfType<IElement>());

        Assert.Single(nested);
        Assert.StartsWith("<button", nested[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The trigger's <c>aria-haspopup</c> must name the role of the popup it controls (WCAG 4.1.2):
    /// a single-choice list is a <c>menu</c> of <c>menuitem*</c> rows and nothing else; a checkbox
    /// panel or a search-plus-listbox is a <c>dialog</c>. MudMenu hard-codes <c>role="menu"</c> on its
    /// list, which is why the select-style controls no longer use it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void The_popup_role_matches_the_trigger_promise(string name)
    {
        var ctx = NewContext();
        var cut = Render(ctx, name);
        var expected = CaseTable[name].Popup;

        Assert.Equal(expected, cut.Find(CaseTable[name].Trigger).GetAttribute("aria-haspopup"));

        cut.Find(CaseTable[name].Trigger).Click();
        cut.Render();

        var controls = cut.Find(CaseTable[name].Trigger).GetAttribute("aria-controls");
        Assert.False(string.IsNullOrEmpty(controls));
        var popup = cut.Find($"#{controls}");
        Assert.Equal(expected, popup.GetAttribute("role"));

        if (expected == "menu")
        {
            // A menu owns menu items (optionally in groups) — never a checkbox, a textbox or a listbox.
            Assert.All(popup.QuerySelectorAll(InteractiveSelector),
                e => Assert.StartsWith("menuitem", e.GetAttribute("role") ?? "", StringComparison.Ordinal));
            Assert.Empty(popup.QuerySelectorAll("[role='listbox'], [role='option']"));
        }
        else
        {
            // A dialog is named — by the trigger, or its own label — exactly once: no second
            // labelled group inside it repeating the name.
            Assert.True(popup.HasAttribute("aria-labelledby") || popup.HasAttribute("aria-label"));
            Assert.Empty(popup.QuerySelectorAll("[role='group'][aria-label], [role='group'][aria-labelledby]"));
        }
        Assert.Empty(cut.FindAll("[role='menu'] [role='dialog'], [role='menu'] input"));
    }

    /// <summary>
    /// OdsMenu's trigger moved from OdsIconButton (Size Sm) to MudMenu's built-in icon button. Both
    /// render MudBlazor's small icon button with the default colour, so the target (3px padding
    /// around a 20px glyph, 26px — above WCAG 2.5.8's 24px) and the ink are unchanged; this pins
    /// that the two render the same MudBlazor classes apart from the activator marker.
    /// </summary>
    [Fact]
    public void The_row_menu_trigger_renders_like_the_small_icon_button_it_replaced()
    {
        var ctx = NewContext();
        var menu = Render(ctx, nameof(OdsMenu)).Find("button.mud-icon-button");
        var icon = ctx.Render<OdsIconButton>(p => p
            .Add(b => b.Icon, Icons.Material.Filled.MoreVert)
            .Add(b => b.AriaLabel, "Row actions")
            .Add(b => b.Size, OdsSize.Sm)).Find("button");

        var menuClasses = menu.ClassList.Where(c => c != "mud-menu-icon-button-activator").OrderBy(c => c);
        Assert.Equal(icon.ClassList.OrderBy(c => c), menuClasses);
        Assert.Contains("mud-icon-button-size-small", menu.ClassList);
    }

    /// <summary>
    /// The source half: a custom <c>ActivatorContent</c> is how the wrapper gets rendered, so no
    /// client file may use one — in markup, or set from C# through a render-tree builder. There is no
    /// allow-list: OdsPopupMenu itself is built on MudPopover, not MudMenu. A menu that needs its own
    /// trigger goes on OdsPopupMenu; an icon menu uses MudMenu's <c>Icon</c> + <c>AriaLabel</c>.
    /// </summary>
    [Fact]
    public void No_client_source_uses_a_custom_menu_activator()
    {
        var markup = ClientSource.RazorFiles()
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"<ActivatorContent\b"));
        var code = ClientSource.SourceFiles()
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal))
            .Where(file => File.ReadLines(file)
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                .Any(line => Regex.IsMatch(line, @"\bActivatorContent\b")));
        var offenders = markup.Concat(code)
            .Select(file => Path.GetRelativePath(ClientSource.Root, file))
            .ToList();

        Assert.True(offenders.Count == 0,
            "MudMenu ActivatorContent wraps its content in a role=\"button\" tab stop (issue #255); use " +
            "OdsPopupMenu or MudMenu's built-in Icon activator instead: " + string.Join(", ", offenders));
    }

    /// <summary>The lint's scan set is real — a path change must not leave it passing over nothing.</summary>
    [Fact]
    public void The_activator_lint_scans_the_popup_host_itself()
    {
        Assert.Contains(ClientSource.RazorFiles(), f => f.EndsWith("OdsPopupMenu.razor", StringComparison.Ordinal));
        Assert.Contains(ClientSource.SourceFiles(), f => f.EndsWith("OdsPopupMenu.razor.cs", StringComparison.Ordinal));
    }
}
