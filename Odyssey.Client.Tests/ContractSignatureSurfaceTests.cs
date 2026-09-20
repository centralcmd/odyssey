using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
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
/// The client half of issue #145 — the contract signature lifecycle: the two new status rows, the
/// run-rate caveat that names what they exclude, the dialog's date pair and its three guards, and
/// the <c>ContractsCard</c> branches the one-click path is built from.
/// </summary>
/// <remarks>
/// The registry, the summary and the dialog RENDER, because all three are ordinary components with
/// ordinary parameters. The page's own branches are source lints, for the reason
/// <see cref="ContractPauseSurfaceTests"/> already records: <c>ContractsCard</c> is an <c>@page</c>
/// whose rows arrive through <c>OdsInfiniteList</c>, which materialises nothing without a JS
/// observer bUnit has no real implementation of — so a render test there asserts against an empty
/// list and passes whatever the branch says. The lint's limit is stated rather than papered over: it
/// proves the branch is present and correctly shaped, not that it fires. What makes it worth having
/// is that the defect it guards is a literal in source — a <c>PUT</c> that forgets to carry a stamp
/// forward — and that the lint's own teeth are proved by
/// <c>ContractPauseSurfaceTests.The_carry_forward_lint_rejects_a_block_missing_any_one_stamp</c>
/// rather than assumed.
/// </remarks>
public class ContractSignatureSurfaceTests
{
    static ContractSignatureSurfaceTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    // ── The status vocabulary ─────────────────────────────────────────────────

    /// <summary>
    /// Both states read under their own names and tones. No new hue enters: Draft takes the neutral
    /// outline (on file, nothing agreed) and Ready the pending amber a pause already gets, because it
    /// is waiting on something a person has to do.
    /// </summary>
    [Theory]
    [InlineData(ContractStatus.Draft, "Draft", "outline", "edit_note")]
    [InlineData(ContractStatus.Ready, "Ready", "pending", "draw")]
    public void Each_signature_state_reads_under_its_own_name(
        ContractStatus status, string label, string tone, string icon)
    {
        var meta = OdsContractStatus.Meta(status);

        Assert.Equal(label, meta.Label);
        Assert.Equal(tone, meta.Tone);
        Assert.Equal(icon, meta.Icon);
        Assert.False(meta.Unknown);
        Assert.NotEqual(OdsContractStatus.Meta(ContractStatus.Active), meta);
    }

    /// <summary>
    /// The chip carries the meaning as visible TEXT — the dot is decorative. A status conveyed by
    /// colour alone fails SC 1.4.1, and "waiting on a signature" is exactly the state a reader opens
    /// the page to find.
    /// </summary>
    [Theory]
    [InlineData(ContractStatus.Draft, "Draft")]
    [InlineData(ContractStatus.Ready, "Ready")]
    public void The_chip_carries_the_signature_state_as_visible_text(ContractStatus status, string label)
    {
        using var ctx = NewContext();

        var chip = ctx.Render<OdsContractStatusChip>(p => p
            .Add(c => c.Status, status)
            .Add(c => c.Compact, true));

        Assert.Contains(label, chip.Markup, StringComparison.Ordinal);
        Assert.Equal("true", chip.Find("span.odc-chip-dot").GetAttribute("aria-hidden"));
    }

    // ── The summary rollup ────────────────────────────────────────────────────

    /// <summary>
    /// Both are REAL buckets with their own rows, and they lead the list: the reading order is the
    /// lifecycle rank, so the two earliest states come first rather than last behind Archived, which
    /// is where an ordinal sort would put two appended members.
    /// </summary>
    [Fact]
    public void The_status_breakdown_leads_with_Draft_and_Ready()
    {
        using var ctx = NewContext();

        var view = ctx.Render<ContractsSummaryView>(p => p
            .Add(v => v.Summary, Summary(draft: 4, ready: 2))
            .Add(v => v.FormatMoney, Money));

        var statusTile = view.FindAll(".odc-breakdown")
            .Single(t => t.QuerySelector(".odc-breakdown-ov")?.TextContent.Trim() == "By status");
        var labels = statusTile.QuerySelectorAll(".odc-breakdown-label")
            .Select(e => e.TextContent.Trim()).ToList();

        Assert.Equal("Draft", labels[0]);
        Assert.Equal("Ready", labels[1]);
        // Ending soon is still the slice it is, directly after Active.
        Assert.Equal(labels.IndexOf("Active") + 1, labels.IndexOf("Ending soon · 45d"));

        // The counts come off the server's own buckets, never re-derived here.
        Assert.Equal("4", CountFor(statusTile, "Draft"));
        Assert.Equal("2", CountFor(statusTile, "Ready"));
    }

    /// <summary>
    /// A run rate that quietly dropped between two visits reads as a pricing error, so the tile says
    /// why — the same reason an unconvertible currency is named rather than folded in at 1:1. A draft
    /// may be fully priced; its price is a quote, not a commitment.
    /// </summary>
    [Fact]
    public void The_run_rate_tiles_name_the_unsigned_contracts_they_exclude()
    {
        using var ctx = NewContext();

        // Draft and Ready are counted together — the reader cares that unsigned agreements are out,
        // not which side of the signature line each one sits on.
        var unsigned = ctx.Render<ContractsSummaryView>(p => p
            .Add(v => v.Summary, Summary(draft: 2, ready: 1))
            .Add(v => v.FormatMoney, Money));
        Assert.Contains("3 unsigned excluded", unsigned.Markup, StringComparison.Ordinal);

        // Beside a pause, both are named and neither displaces the other.
        var both = ctx.Render<ContractsSummaryView>(p => p
            .Add(v => v.Summary, Summary(paused: 1, draft: 2))
            .Add(v => v.FormatMoney, Money));
        Assert.Contains("1 paused, 2 unsigned excluded", both.Markup, StringComparison.Ordinal);

        // …and nothing is said when there is nothing to exclude.
        var neither = ctx.Render<ContractsSummaryView>(p => p
            .Add(v => v.Summary, Summary())
            .Add(v => v.FormatMoney, Money));
        Assert.DoesNotContain("excluded", neither.Markup, StringComparison.Ordinal);
    }

    // ── The dialog ────────────────────────────────────────────────────────────

    /// <summary>
    /// The create dialog carries the pair as ordinary, OPTIONAL date fields — the path for a paper
    /// contract signed last month. Neither is required; leaving both blank records a draft, which is
    /// the normal case and is what the helper text says.
    /// </summary>
    [Fact]
    public void The_dialog_offers_both_signature_dates_as_optional_fields()
    {
        var (dialog, _) = RenderDialog();

        Assert.Contains("Ready for signature", dialog.Markup, StringComparison.Ordinal);
        Assert.Contains("Signed", dialog.Markup, StringComparison.Ordinal);
        Assert.Contains("Leave blank while it is still being drafted.", dialog.Markup, StringComparison.Ordinal);
        Assert.Contains("stays out of the run rate", dialog.Markup, StringComparison.Ordinal);
        // The subtitle says what omitting them does, so the default is a choice rather than an accident.
        Assert.Contains("record it as a draft", dialog.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// In edit mode the dialog SEEDS from the record, which is what makes the full-replacement write
    /// safe: a field edit that reopened with empty date fields would clear both stamps on save and
    /// flip a signed contract to Draft.
    /// </summary>
    /// <summary>
    /// <b>AC 23, the behavioural half.</b> Saving an unrelated field edit through the edit dialog
    /// leaves a signed contract's stamps intact.
    ///
    /// <para>
    /// This is the regression the DTO change would otherwise cause, asserted on the write itself
    /// rather than on the source: the body that reaches the API client still carries both stamps, so
    /// the contract stays signed and stays in the run rate. A dialog that reopened with empty date
    /// fields, or built its <c>UpdateContract</c> without them, would clear both on save and flip the
    /// contract to <c>Draft</c> — for a reader who only renamed it.
    /// </para>
    /// </summary>
    [Fact]
    public void An_unrelated_edit_saved_through_the_dialog_leaves_both_stamps_intact()
    {
        var ready = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        var signed = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);
        var contract = Contract(ready, signed);

        var (dialog, client) = RenderDialog(contract);

        // Rename it — an edit that has nothing to do with the signature — and save.
        // Input, not Change: the plain text fields bind on input.
        dialog.FindAll("input[type=text]")[0].Input("Fibre broadband — renewed");
        ClickFooter(dialog, "Save changes");

        client.Verify(
            c => c.UpdateAsync(
                contract.ContractId,
                It.Is<UpdateContract>(u => u.Ready == ready && u.Signed == signed),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The three guards run client-side too, on the same body and in the same order the server uses,
    /// so the message lands on the control that caused it rather than only in a toast. A refused body
    /// never reaches the API client at all.
    /// </summary>
    [Fact]
    public void The_dialog_refuses_a_signed_date_with_no_ready_date()
    {
        var contract = Contract(ready: null, signed: null);
        var (dialog, client) = RenderDialog(contract);

        // A signed date with no ready date is the shape the server refuses with
        // contract_signed_requires_ready.
        // The pickers in document order: Starts, Ends, Ready for signature, Signed.
        SetDate(dialog, SignedPickerIndex, new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc));
        ClickFooter(dialog, "Save changes");

        Assert.Contains("needs a ready date too", dialog.Markup, StringComparison.Ordinal);
        client.Verify(
            c => c.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateContract>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── The page's branches ───────────────────────────────────────────────────

    /// <summary>
    /// The one-click path is offered in lifecycle order, ONE STEP AT A TIME: a contract with neither
    /// stamp is offered "Mark ready for signature", one already ready is offered "Mark signed", and a
    /// contract already signed is never offered "Mark ready".
    /// </summary>
    [Fact]
    public void The_row_menu_offers_the_signature_path_one_step_at_a_time()
    {
        var source = CardSource();

        Assert.Matches(
            new Regex(@"if\s*\(\s*c\.Signed is null && c\.Ready is null\s*\)[\s\S]{0,400}?Label\s*=\s*""Mark ready for signature"""),
            source);
        Assert.Matches(
            new Regex(@"else if\s*\(\s*c\.Signed is null\s*\)[\s\S]{0,400}?Label\s*=\s*""Mark signed"""),
            source);

        // Clearing is offered wherever EITHER stamp exists, in any state — it is never refused, which
        // is what stops an archived or expired contract being stranded holding one.
        Assert.Matches(
            new Regex(@"if\s*\(\s*c\.Signed is not null \|\| c\.Ready is not null\s*\)[\s\S]{0,500}?Label\s*=\s*c\.Signed is not null \? ""Unsign"" : ""Clear ready date"""),
            source);
    }

    /// <summary>
    /// "Mark signed" stamps <c>Ready</c> too when it is missing. The server refuses a <c>Signed</c>
    /// without one (<c>contract_signed_requires_ready</c>), so a ONE-CLICK action that did not would
    /// be able to compose a write the server rejects — a dead button rather than a shortcut. An
    /// existing ready date is kept, so signing never rewrites when the contract was sent out.
    /// </summary>
    [Fact]
    public void Mark_signed_supplies_the_ready_stamp_it_needs_and_keeps_an_existing_one()
    {
        var source = CardSource();

        Assert.Matches(
            new Regex(@"Task MarkSigned\(ContractListItem c\)[\s\S]{0,400}?\(d, now\) => \(d\.Ready \?\? now, d\.Signed \?\? now\)"),
            source);

        // Marking ready is idempotent the way a pause is: a repeat keeps the ORIGINAL stamp, so
        // "ready since" — which the header's Awaiting-signature row counts from — never resets.
        Assert.Matches(
            new Regex(@"Task MarkReady\(ContractListItem c\)[\s\S]{0,400}?\(d, now\) => \(d\.Ready \?\? now, d\.Signed\)"),
            source);

        // Unsign clears BOTH; clearing Signed alone would leave a Ready state nobody asked for.
        Assert.Matches(
            new Regex(@"Task Unsign\(ContractListItem c\)[\s\S]{0,400}?\(_, _\) => \(null, null\)"),
            source);
    }

    /// <summary>
    /// The widened archive rule, mirrored client-side from the SHARED predicate rather than
    /// re-derived: an ended, an archived OR an unsigned contract can be archived, because abandoning
    /// a negotiation is the likeliest reason to archive a draft and a draft typically has no end date
    /// at all — the un-widened rule would offer a step the reader could never take.
    /// </summary>
    [Fact]
    public void Archive_is_offered_for_an_unsigned_contract()
    {
        var source = CardSource();

        Assert.Matches(new Regex(@"hasEnded \|\| archived \|\| unsigned"), source);

        // "Unsigned" is the SHARED predicate the money roll-ups gate on, never a local re-test of the
        // two stamps — that is how the menu and the server end up disagreeing.
        Assert.Matches(
            new Regex(@"var unsigned = ContractStatusOrder\.IsUnsigned\(c\.Status\);"),
            source);
    }

    /// <summary>
    /// The header's "Awaiting signature" group: Ready contracts at WARNING severity, because an
    /// unreturned signature has stalled — unlike a pause, which is a state someone chose and which
    /// reads as information.
    ///
    /// <para>
    /// DRAFTS ARE DELIBERATELY ABSENT. A draft is work in progress — nobody is waiting on anyone —
    /// and listing every one would turn the panel into a second contract list. The status filter is
    /// where you go looking for those.
    /// </para>
    /// </summary>
    [Fact]
    public void The_header_signal_surfaces_Ready_contracts_and_not_drafts()
    {
        var source = CardSource();

        Assert.Matches(
            new Regex(@"c\.Status == ContractStatus\.Ready && c\.Ready is not null[\s\S]{0,500}?""Awaiting signature"", PageHeaderSeverity\.Warning"),
            source);
        Assert.DoesNotMatch(
            new Regex(@"ContractStatus\.Draft[\s\S]{0,300}?""Awaiting signature"""),
            source);
    }

    /// <summary>
    /// An unsigned row stays at FULL brightness and is marked by a dashed edge instead. Dimming is
    /// reserved for archived, and the two readings are opposites: an archived record is finished, an
    /// unsigned one is the row most likely to need attention.
    /// </summary>
    [Fact]
    public void An_unsigned_row_is_marked_by_a_dashed_edge_not_by_dimming()
    {
        var markup = Source("ContractsCard.razor");

        Assert.Matches(new Regex(@"var unsigned = ContractStatusOrder\.IsUnsigned\(c\.Status\);"), markup);
        Assert.Matches(new Regex(@"Class=""@\(unsigned \? ""con-unsigned"" : null\)"""), markup);
        // Dimming still follows the archive stamp ALONE.
        Assert.Matches(new Regex(@"Dimmed=""archived"""), markup);
        Assert.DoesNotMatch(new Regex(@"Dimmed=""[^""]*unsigned"), markup);

        // And the modifier exists in the stylesheet, or the class would be inert.
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));
        Assert.Matches(new Regex(@"\.odc-record\.con-unsigned\s*\{[^}]*border-style:\s*dashed"), css);
    }

    /// <summary>
    /// One tile per STORED stamp, so nothing is lost when the derived status can only report one of
    /// them — a signed contract that is also archived still says when each happened. A plain Draft
    /// adds no tile: it has no stamp, and the Status tile has already said so.
    /// </summary>
    [Fact]
    public void Each_stored_signature_stamp_gets_its_own_tile()
    {
        var markup = Source("ContractsCard.razor");

        Assert.Matches(
            new Regex(@"@if\s*\(detail\.Ready is \{ \} \w+\)[\s\S]{0,600}?Icon=""draw""[\s\S]{0,400}?Label=""Ready for signature"""),
            markup);
        Assert.Matches(
            new Regex(@"@if\s*\(detail\.Signed is \{ \} \w+\)[\s\S]{0,600}?Icon=""history_edu""[\s\S]{0,400}?Label=""Signed"""),
            markup);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // MudBlazor's own registrations supply ISnackbar and IDialogService; substituting a mock for
        // either breaks the providers the modal renders through.
        ctx.Services.AddMudServices();
        return ctx;
    }

    /// <summary>
    /// The "Signed" picker's position among the dialog's date pickers, in document order:
    /// Starts · Ends · Ready for signature · Signed. Named rather than inlined so the guard test says
    /// which control it is driving.
    /// </summary>
    private const int SignedPickerIndex = 3;

    private static ExistingContract Contract(DateTime? ready, DateTime? signed) => new()
    {
        ContractId = Guid.NewGuid(),
        Name = "Fibre broadband",
        Type = ContractType.Service,
        Status = signed is null ? ContractStatus.Draft : ContractStatus.Active,
        StartDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        Ready = ready,
        Signed = signed,
        CreatedAtUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static (IRenderedComponent<DialogHost> Cut, Mock<IContractsApiClient> Client) RenderDialog(
        ExistingContract? contract = null)
    {
        var ctx = NewContext();
        var client = new Mock<IContractsApiClient>();
        client
            .Setup(c => c.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateContract>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));
        client
            .Setup(c => c.CreateAsync(It.IsAny<NewContract>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<DialogHost>(p => p.Add(h => h.Contract, contract));
        return (cut, client);
    }

    /// <summary>Submits through the modal's own footer button — the path a user takes.</summary>
    private static void ClickFooter(IRenderedComponent<DialogHost> cut, string label) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(label, StringComparison.Ordinal)).Click();

    /// <summary>
    /// Types into the Nth date picker, through its own editable input, so the path under test is the
    /// one a reader takes. Change, not Input: the picker commits on change while the plain text
    /// fields bind on input.
    /// </summary>
    private static void SetDate(IRenderedComponent<DialogHost> cut, int index, DateTime value) =>
        cut.FindAll(".mud-picker input")[index]
            .Change(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>The dialog beside MudBlazor's providers, which portal the modal it renders into.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingContract? Contract { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<CreateContractDialog>(2);
            builder.AddComponentParameter(3, nameof(CreateContractDialog.Contract), Contract);
            builder.AddComponentParameter(4, nameof(CreateContractDialog.Open), true);
            builder.CloseComponent();
        }
    }

    private static string Money(decimal value, string? code) => $"{code} {value:0.00}";

    private static string CountFor(AngleSharp.Dom.IElement statusTile, string label) =>
        statusTile.QuerySelectorAll(".odc-breakdown-row")
            .Single(r => r.QuerySelector(".odc-breakdown-label")?.TextContent.Trim() == label)
            .QuerySelector(".odc-breakdown-n")!.TextContent.Trim();

    private static ContractSummary Summary(int paused = 0, int draft = 0, int ready = 0) => new()
    {
        TotalContracts = 6 + paused + draft + ready,
        CountsByStatus = new ContractStatusCounts
        {
            Active = 3, Upcoming = 1, Expired = 1, Archived = 1, Paused = paused,
            Draft = draft, Ready = ready, EndingSoon = 1,
        },
        CountsByType = [new ContractTypeCount { Type = ContractType.Rental, Count = 2 }],
        RunRate = new ContractRunRate { BaseCurrency = "USD", Monthly = 1000m, Yearly = 12000m },
        EndingWindowDays = 45,
        ChargeWindowDays = 45,
    };

    private static string CardSource() => Source("ContractsCard.razor.cs");

    /// <summary>
    /// The page source with comments stripped: its own doc comments legitimately DISCUSS these
    /// branches, and a lint a comment can satisfy is a lint that proves nothing.
    /// </summary>
    private static string Source(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", fileName));
        source = Regex.Replace(source, @"@\*[\s\S]*?\*@", string.Empty);
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*(///?).*$", string.Empty, RegexOptions.Multiline);
        return source;
    }
}
