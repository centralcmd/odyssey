using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The frontend half of issue #308 — the contract's smart-tag scope (Odyssey Design System ·
/// docs/components-contract-smart-tag-scope): the display rules, <c>OdsSmartTagScopeField</c>, the
/// Edit contract dialog's opt-in sync prompt, and the Edit smart-tag scope dialog's PUT.
/// </summary>
public class ContractSmartTagScopeSurfaceTests
{
    private static readonly Guid ContractId = Guid.Parse("5a5a5a5a-0000-0000-0000-000000000308");

    private static DateTime Day(int year, int month, int day) => new(year, month, day);

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static readonly OdsScopeTerm Term = new(Day(2021, 3, 1), Day(2026, 8, 31), null);

    // ── Display rules ────────────────────────────────────────────────────────

    [Fact]
    public void Describe_states_the_relation_in_words()
    {
        Assert.Equal("Starts with the term, and runs 4 months past the term’s end (31 Aug 2026).",
            OdsSmartTagScope.Describe(new(Day(2021, 3, 1), Day(2026, 12, 31)), Term));
        Assert.Equal("Starts with the term, and stays open after the term ends (31 Aug 2026).",
            OdsSmartTagScope.Describe(new(Day(2021, 3, 1), null), Term));
        Assert.Equal("Starts with the term, and ends with it.",
            OdsSmartTagScope.Describe(new(Day(2021, 3, 1), Day(2026, 8, 31)), Term));
        Assert.Equal("A one-off contract has no term, so transactions of any date match.",
            OdsSmartTagScope.Describe(OdsScopeRange.Open, new(null, null, Day(2025, 5, 1))));
    }

    [Theory]
    [InlineData(2021, 3, 1, 2026, 8, 31, "same as the term")]
    [InlineData(2021, 3, 1, 2026, 12, 31, "includes dates after the term")]
    [InlineData(2020, 1, 1, null, null, null, "includes dates before and after the term")]
    [InlineData(2022, 1, 1, 2025, 1, 1, "narrower than the term")]
    public void Relation_places_the_scope_against_the_term(
        int sy, int sm, int sd, int? ey, int? em, int? ed, string expected)
    {
        var scope = new OdsScopeRange(Day(sy, sm, sd), ey is { } y ? Day(y, em!.Value, ed!.Value) : null);

        Assert.Equal(expected, OdsSmartTagScope.Relation(scope, Term));
    }

    [Fact]
    public void Relation_names_a_one_off()
    {
        var oneOff = new OdsScopeTerm(null, null, Day(2025, 5, 1));

        Assert.Equal("one-off contract, no date rule", OdsSmartTagScope.Relation(OdsScopeRange.Open, oneOff));
        Assert.Equal("one-off contract", OdsSmartTagScope.Relation(new(Day(2025, 1, 1), null), oneOff));
    }

    [Fact]
    public void FormatRange_reads_each_shape()
    {
        Assert.Equal("1 Sep 2025 – 31 Aug 2026", OdsSmartTagScope.FormatRange(new(Day(2025, 9, 1), Day(2026, 8, 31))));
        Assert.Equal("from 1 Sep 2025, no end", OdsSmartTagScope.FormatRange(new(Day(2025, 9, 1), null)));
        Assert.Equal("until 31 Aug 2026", OdsSmartTagScope.FormatRange(new(null, Day(2026, 8, 31))));
        Assert.Equal("any date", OdsSmartTagScope.FormatRange(OdsScopeRange.Open));
    }

    [Fact]
    public void Validate_uses_the_servers_rules_and_words()
    {
        Assert.Equal((null, SmartTagScopeRules.InvertedMessage),
            OdsSmartTagScope.Validate(new(Day(2027, 7, 1), Day(2027, 6, 30))));
        Assert.Equal((null, null), OdsSmartTagScope.Validate(new(Day(2027, 6, 30), Day(2027, 6, 30))));
        Assert.Equal((SmartTagScopeRules.OutOfRangeMessage, null),
            OdsSmartTagScope.Validate(new(Day(1899, 12, 31), null)));
    }

    [Fact]
    public void The_wire_form_is_utc_midnight()
    {
        var wire = OdsSmartTagScope.ToWire(new DateTime(2027, 6, 30, 15, 45, 0));

        Assert.Equal(Utc(2027, 6, 30), wire);
        Assert.Equal(DateTimeKind.Utc, wire!.Value.Kind);
        Assert.Null(OdsSmartTagScope.ToWire(null));
    }

    [Fact]
    public void FromTerm_is_the_servers_rule()
    {
        Assert.Equal(new OdsScopeRange(Day(2021, 3, 1), null),
            OdsSmartTagScope.FromTerm(new(Day(2021, 3, 1), Day(2020, 1, 1), null)));
        Assert.True(OdsSmartTagScope.FromTerm(new(Day(2021, 3, 1), Day(2022, 1, 1), Day(2021, 6, 1))).IsOpen);
    }

    // ── The field ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_field_labels_both_sides_and_carries_the_relation_in_a_live_region()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsSmartTagScopeField>(p => p
            .Add(f => f.Value, new OdsScopeRange(Day(2021, 3, 1), null))
            .Add(f => f.Term, Term));

        Assert.Contains("Match from", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Match until", cut.Markup, StringComparison.Ordinal);
        var summary = cut.Find(".odc-stscope-summary");
        Assert.Equal("polite", summary.GetAttribute("aria-live"));
        Assert.Contains("stays open after the term ends", summary.TextContent, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find(".odc-stscope-strip").GetAttribute("aria-hidden"));
        Assert.Contains("open-r", cut.FindAll(".odc-stscope-bar.scope").Single().ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_shortcuts_disable_when_they_would_do_nothing_and_raise_the_next_scope()
    {
        await using var ctx = NewContext();
        OdsScopeRange? raised = null;
        var cut = ctx.Render<OdsSmartTagScopeField>(p => p
            .Add(f => f.Value, OdsSmartTagScope.FromTerm(Term))
            .Add(f => f.Term, Term)
            .Add(f => f.ValueChanged, (OdsScopeRange next) => raised = next));

        var copy = cut.FindAll("button").Single(b => b.TextContent.Contains("Copy the contract term", StringComparison.Ordinal));
        Assert.True(copy.HasAttribute("disabled"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Remove both limits", StringComparison.Ordinal)).Click();
        Assert.Equal(OdsScopeRange.Open, raised);

        // The clicked shortcut disables itself, so focus moves to "Match from" rather than dropping
        // to the document (WCAG 2.4.3).
        var focused = Assert.Single(ctx.JSInterop.Invocations["odsFocusById"]);
        Assert.Equal(cut.FindComponents<OdsDateField>()[0].Instance.Id, focused.Arguments[0]);
    }

    // ── The sync prompt ──────────────────────────────────────────────────────

    [Fact]
    public void The_sync_prompt_stays_hidden_until_the_term_changes()
    {
        var stored = new OdsScopeRange(Day(2021, 3, 1), null);
        var ongoing = new OdsScopeTerm(Day(2021, 3, 1), null, null);

        Assert.False(OdsSmartTagScopeSyncPrompt.ShouldShow(ongoing, ongoing, stored));
        // The motivating case: an end date is added. The prompt offers to follow it, unticked.
        Assert.True(OdsSmartTagScopeSyncPrompt.ShouldShow(ongoing, ongoing with { EndDate = Day(2026, 8, 31) }, stored));
        // A changed term whose scope would equal the stored one has nothing to offer.
        Assert.False(OdsSmartTagScopeSyncPrompt.ShouldShow(
            ongoing, ongoing with { StartDate = new DateTime(2021, 3, 1, 9, 30, 0) }, stored));
    }

    [Fact]
    public async Task The_sync_prompt_renders_unchecked_and_names_both_ranges()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<OdsSmartTagScopeSyncPrompt>(p => p
            .Add(s => s.Before, new OdsScopeTerm(Day(2021, 3, 1), null, null))
            .Add(s => s.After, new OdsScopeTerm(Day(2021, 3, 1), Day(2026, 8, 31), null))
            .Add(s => s.Scope, new OdsScopeRange(Day(2021, 3, 1), null)));

        Assert.Contains("It stays from 1 Mar 2021, no end", cut.Find(".odc-stsync-desc").TextContent, StringComparison.Ordinal);
        Assert.Contains("Also set the smart-tag scope to 1 Mar 2021 – 31 Aug 2026", cut.Markup, StringComparison.Ordinal);
        Assert.False(cut.FindComponent<OdsCheckbox>().Instance.Checked);

        // Announced when it appears (4.1.3), and the checkbox is described by the sentence (1.3.1).
        Assert.Equal("status", cut.Find(".odc-stsync").ParentElement!.GetAttribute("role"));
        var descId = cut.Find(".odc-stsync-desc").Id;
        Assert.Equal(descId, cut.Find("input[type=checkbox]").GetAttribute("aria-describedby"));
        Assert.Equal(cut.Find(".odc-stsync-title").Id, cut.Find(".odc-stsync").GetAttribute("aria-labelledby"));
    }

    [Fact]
    public async Task The_sync_prompts_status_region_exists_before_the_band_does()
    {
        await using var ctx = NewContext();
        var unchanged = new OdsScopeTerm(Day(2021, 3, 1), null, null);
        var cut = ctx.Render<OdsSmartTagScopeSyncPrompt>(p => p
            .Add(s => s.Before, unchanged)
            .Add(s => s.After, unchanged)
            .Add(s => s.Scope, new OdsScopeRange(Day(2021, 3, 1), null)));

        Assert.Empty(cut.FindAll(".odc-stsync"));
        Assert.Single(cut.FindAll("[role=status]"));
    }

    // ── The Edit contract dialog ─────────────────────────────────────────────

    [Fact]
    public async Task Editing_the_term_carries_the_scope_forward_unless_the_prompt_is_ticked()
    {
        var (cut, contracts) = RenderEditContract(Contract(scopeEnd: null, endDate: null));
        UpdateContract? sent = null;
        contracts.Setup(c => c.UpdateAsync(ContractId, It.IsAny<UpdateContract>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, UpdateContract, CancellationToken>((_, u, _) => sent = u)
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));

        await SetDateAsync(cut, "Ends", Day(2026, 8, 31));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Save changes", StringComparison.Ordinal)).Click();

        Assert.NotNull(sent);
        Assert.Equal(Day(2026, 8, 31), sent!.EndDate);
        Assert.Equal(Utc(2021, 3, 1), sent.SmartTagScopeStart);
        Assert.Null(sent.SmartTagScopeEnd);
    }

    [Fact]
    public async Task Ticking_the_prompt_writes_the_scope_the_new_term_gives()
    {
        var (cut, contracts) = RenderEditContract(Contract(scopeEnd: null, endDate: null));
        UpdateContract? sent = null;
        contracts.Setup(c => c.UpdateAsync(ContractId, It.IsAny<UpdateContract>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, UpdateContract, CancellationToken>((_, u, _) => sent = u)
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));

        await SetDateAsync(cut, "Ends", Day(2026, 8, 31));
        var prompt = cut.FindComponent<OdsSmartTagScopeSyncPrompt>();
        await cut.InvokeAsync(() => prompt.Instance.CheckedChanged.InvokeAsync(true));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Save changes", StringComparison.Ordinal)).Click();

        Assert.Equal(Utc(2021, 3, 1), sent!.SmartTagScopeStart);
        Assert.Equal(Utc(2026, 8, 31), sent.SmartTagScopeEnd);
    }

    // ── The Edit smart-tag scope dialog ──────────────────────────────────────

    [Fact]
    public async Task The_scope_dialog_saves_only_the_scope_and_carries_everything_else_forward()
    {
        var contract = Contract(scopeEnd: Utc(2026, 8, 31), endDate: Day(2026, 8, 31));
        contract.ReferenceNumber = "AGR-1";
        contract.Paused = Day(2026, 1, 1);
        contract.Ready = Day(2021, 2, 1);
        contract.Signed = Day(2021, 2, 2);
        var (cut, contracts) = RenderScopeDialog(contract);
        UpdateContract? sent = null;
        contracts.Setup(c => c.UpdateAsync(ContractId, It.IsAny<UpdateContract>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, UpdateContract, CancellationToken>((_, u, _) => sent = u)
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));

        var save = SaveScopeButton(cut);
        Assert.True(save.HasAttribute("disabled"));

        await SetScopeAsync(cut, new(Day(2021, 3, 1), Day(2027, 6, 30)));
        SaveScopeButton(cut).Click();

        Assert.NotNull(sent);
        Assert.Equal(Utc(2027, 6, 30), sent!.SmartTagScopeEnd);
        Assert.Equal(DateTimeKind.Utc, sent.SmartTagScopeEnd!.Value.Kind);
        Assert.Equal(Utc(2021, 3, 1), sent.SmartTagScopeStart);
        Assert.Equal("Employment", sent.Name);
        Assert.Equal("AGR-1", sent.ReferenceNumber);
        Assert.Equal(Day(2026, 8, 31), sent.EndDate);
        Assert.True(sent.IsPaused);
        Assert.Equal(Day(2021, 2, 2), sent.Signed);
    }

    [Fact]
    public async Task An_inverted_scope_shows_the_order_error_live_and_holds_the_save()
    {
        var (cut, contracts) = RenderScopeDialog(Contract(scopeEnd: Utc(2026, 8, 31), endDate: Day(2026, 8, 31)));

        await SetScopeAsync(cut, new(Day(2027, 7, 1), Day(2027, 6, 30)));

        Assert.Contains(SmartTagScopeRules.InvertedMessage, cut.Markup, StringComparison.Ordinal);
        Assert.True(SaveScopeButton(cut).HasAttribute("disabled"));
        contracts.Verify(c => c.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateContract>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ExistingContract Contract(DateTime? scopeEnd, DateTime? endDate) => new()
    {
        ContractId = ContractId,
        Name = "Employment",
        Type = ContractType.Employment,
        StartDate = Day(2021, 3, 1),
        EndDate = endDate,
        SmartTagScopeStart = Utc(2021, 3, 1),
        SmartTagScopeEnd = scopeEnd,
        CreatedAtUtc = Utc(2021, 1, 1),
    };

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static AngleSharp.Dom.IElement SaveScopeButton(IRenderedComponent<DialogHost> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains("Save scope", StringComparison.Ordinal));

    private static Task SetScopeAsync(IRenderedComponent<DialogHost> cut, OdsScopeRange scope)
    {
        var field = cut.FindComponent<OdsSmartTagScopeField>();
        return cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(scope));
    }

    private static Task SetDateAsync(IRenderedComponent<DialogHost> cut, string label, DateTime value)
    {
        var field = cut.FindComponents<OdsDateField>().Single(f => f.Instance.Label == label);
        return cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value));
    }

    private static (IRenderedComponent<DialogHost>, Mock<IContractsApiClient>) RenderScopeDialog(ExistingContract contract) =>
        RenderHost(contract, scopeDialog: true);

    private static (IRenderedComponent<DialogHost>, Mock<IContractsApiClient>) RenderEditContract(ExistingContract contract) =>
        RenderHost(contract, scopeDialog: false);

    private static (IRenderedComponent<DialogHost>, Mock<IContractsApiClient>) RenderHost(ExistingContract contract, bool scopeDialog)
    {
        var ctx = NewContext();
        var contracts = new Mock<IContractsApiClient>();
        contracts.Setup(c => c.UpdateAsync(ContractId, It.IsAny<UpdateContract>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));
        ctx.Services.AddSingleton(contracts.Object);

        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.Contract, contract)
            .Add(h => h.ScopeDialog, scopeDialog));
        return (cut, contracts);
    }

    /// <summary>
    /// The dialogs beneath a <c>MudDialogProvider</c> in one tree: <c>OdsFormDialog</c> composes an
    /// INLINE <c>MudDialog</c>, which renders through the provider — without it the body is absent and
    /// every assertion would pass vacuously.
    /// </summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingContract Contract { get; set; } = default!;

        [Parameter] public bool ScopeDialog { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            if (ScopeDialog)
            {
                builder.OpenComponent<EditSmartTagScopeDialog>(2);
                builder.AddComponentParameter(3, nameof(EditSmartTagScopeDialog.Open), true);
                builder.AddComponentParameter(4, nameof(EditSmartTagScopeDialog.Contract), Contract);
                builder.CloseComponent();
            }
            else
            {
                builder.OpenComponent<CreateContractDialog>(5);
                builder.AddComponentParameter(6, nameof(CreateContractDialog.Open), true);
                builder.AddComponentParameter(7, nameof(CreateContractDialog.Contract), Contract);
                builder.CloseComponent();
            }
        }
    }
}
