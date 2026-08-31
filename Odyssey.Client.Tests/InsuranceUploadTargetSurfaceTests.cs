using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// How the attach dialog presents its target period (issue #26 §3, as revised by the design system).
///
/// <para>
/// The spec said flatly that the dialog renders no target select. The design system landed something
/// narrower, and it is the design system that governs: the picker is gone <em>as a policy-or-period
/// choice</em>, but where the target was <b>inferred</b> rather than chosen — the row-menu entry
/// point — it is offered as a period picker defaulted to that inference, so a late-arriving document
/// can still be filed against an earlier period. Where the user already chose the period, by opening
/// its own document panel, there is nothing to choose and it reads as a fixed "Attaching to" line.
/// </para>
///
/// <para>
/// Both halves are pinned here because either one alone is a plausible-looking regression: a picker
/// on the locked path silently discards the period the user opened, and a fixed line on the row-menu
/// path leaves an inferred target with no way to correct it.
/// </para>
/// </summary>
public class InsuranceUploadTargetSurfaceTests
{
    private static readonly Guid Older = Guid.NewGuid();
    private static readonly Guid Newer = Guid.NewGuid();

    private static ExistingPolicyRenewal Period(Guid id, int year) => new()
    {
        PolicyRenewalId = id,
        InsurancePolicyId = Guid.Empty,
        FromDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ToDate = new DateTime(year, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        Premium = 1m,
        PremiumCurrencyCode = "USD",
        CoverageAmount = 1m,
        CoverageCurrencyCode = "USD",
        CreatedAtUtc = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static ExistingInsurancePolicy Policy(params ExistingPolicyRenewal[] periods) => new()
    {
        InsurancePolicyId = Guid.NewGuid(),
        Name = "Home cover",
        Insurer = new InsurerReference { ContactId = Guid.NewGuid(), Name = "Insurer" },
        Renewals = [.. periods],
        CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static IRenderedComponent<DialogHost> Render(
        ExistingInsurancePolicy policy, Guid target, bool lockPeriod)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IFilesApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IInsuranceApiClient>());

        var limits = new Mock<IUploadLimitsCache>();
        limits.Setup(l => l.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadLimitsCache.Fallback);
        ctx.Services.AddSingleton(limits.Object);

        return ctx.Render<DialogHost>(p => p
            .Add(h => h.Policy, policy)
            .Add(h => h.RenewalId, target)
            .Add(h => h.LockPeriod, lockPeriod));
    }

    /// <summary>
    /// The dialog next to MudBlazor's providers. MudBlazor portals dialog and select content into
    /// them, so without both in the same tree the body renders nowhere and every assertion here would
    /// pass vacuously against an empty dialog.
    /// </summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingInsurancePolicy Policy { get; set; } = default!;
        [Parameter] public Guid RenewalId { get; set; }
        [Parameter] public bool LockPeriod { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<InsuranceUploadDialog>(2);
            builder.AddComponentParameter(3, nameof(InsuranceUploadDialog.Policy), Policy);
            builder.AddComponentParameter(4, nameof(InsuranceUploadDialog.RenewalId), RenewalId);
            builder.AddComponentParameter(5, nameof(InsuranceUploadDialog.LockPeriod), LockPeriod);
            builder.AddComponentParameter(6, nameof(InsuranceUploadDialog.Open), true);
            builder.CloseComponent();
        }
    }

    private static string Label(int year) =>
        $"Period Jan 01, {year} → Dec 31, {year}";

    [Fact]
    public void Opened_from_a_periods_own_panel_the_target_is_shown_not_offered()
    {
        var cut = Render(Policy(Period(Newer, 2026), Period(Older, 2025)), Older, lockPeriod: true);

        var stated = cut.Find(".ins-attach-target");
        Assert.Contains("Attaching to", stated.TextContent);
        Assert.Contains(Label(2025), stated.TextContent);

        // The user already chose this period by opening its panel; offering a picker here would let a
        // stray change file the document somewhere they never asked for.
        Assert.Empty(cut.FindAll(".mud-select"));
    }

    [Fact]
    public void Opened_from_the_row_menu_the_inferred_target_is_a_picker_defaulted_to_it()
    {
        var cut = Render(Policy(Period(Newer, 2026), Period(Older, 2025)), Newer, lockPeriod: false);

        // Offered, not stated: the target was inferred, so it has to be correctable.
        Assert.Empty(cut.FindAll(".ins-attach-target"));
        Assert.NotEmpty(cut.FindAll(".mud-select"));

        // Defaulted to the inferred period, not merely present — a picker that opened on the wrong
        // period would file the document somewhere the user never looked. Asserted on the bound value
        // rather than the trigger's display text, which MudSelect resolves from its item registry
        // once the list has rendered.
        Assert.Equal(Newer.ToString(), cut.Find(".mud-select input").GetAttribute("value"));

        // Both periods are on offer, so an earlier one is actually reachable. MudSelect renders its
        // items only once opened, so the list has to be opened to see them.
        cut.Find(".mud-select .mud-input-control").MouseDown();
        var options = cut.FindAll(".mud-list-item").Select(o => o.TextContent).ToList();
        Assert.Contains(options, o => o.Contains(Label(2026), StringComparison.Ordinal));
        Assert.Contains(options, o => o.Contains(Label(2025), StringComparison.Ordinal));
    }

    /// <summary>
    /// One period is no choice at all. A picker with a single option is a control that cannot do
    /// anything, and it would push the one fact worth reading down behind a widget.
    /// </summary>
    [Fact]
    public void With_a_single_period_there_is_nothing_to_choose_so_it_reads_as_a_line()
    {
        var cut = Render(Policy(Period(Older, 2025)), Older, lockPeriod: false);

        Assert.Contains(Label(2025), cut.Find(".ins-attach-target").TextContent);
        Assert.Empty(cut.FindAll(".mud-select"));
    }
}
