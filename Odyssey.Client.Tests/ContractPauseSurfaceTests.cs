using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client half of issue #140 — the contract pause: the status registry (including the neutral
/// fallback §14's release coupling turns on), the summary rollup's fifth bucket and its run-rate
/// caveat, and the row-menu / write-path branches on <c>ContractsCard</c>.
/// </summary>
/// <remarks>
/// The registry and the summary RENDER, because both are ordinary components with ordinary
/// parameters. The page's own branches are source lints for the reason
/// <see cref="ContractsCardRowActionTests"/> records: <c>ContractsCard</c> is an <c>@page</c> whose
/// rows arrive through <c>OdsInfiniteList</c>, which materialises nothing without a JS observer
/// bUnit has no real implementation of, so a render test there asserts against an empty list and
/// passes whatever the branch says. The lint's limit is honest — it proves the branch is present and
/// correctly shaped, not that it fires — and what makes it worth having is that the defect it guards
/// is a literal in source: a <c>PUT</c> that forgets to carry a stamp forward.
/// </remarks>
public class ContractPauseSurfaceTests
{
    static ContractPauseSurfaceTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    // ── The status registry ───────────────────────────────────────────────────

    [Fact]
    public void Paused_reads_as_its_own_amber_row_never_as_Active()
    {
        var paused = OdsContractStatus.Meta(ContractStatus.Paused);

        Assert.Equal("Paused", paused.Label);
        // Pending (amber) — the tone a paused record already carries, so no new status hue enters.
        Assert.Equal("pending", paused.Tone);
        Assert.Equal("pause_circle", paused.Icon);
        Assert.False(paused.Unknown);
        Assert.NotEqual(OdsContractStatus.Meta(ContractStatus.Active), paused);
    }

    /// <summary>
    /// §14's release coupling, closed in the client. <see cref="ContractStatus"/> lives in
    /// <c>Odyssey.Dtos</c> and appends server-side, so a client behind the deployment can be handed a
    /// member it has never heard of. Resolving one to the Active row would paint a green "Active"
    /// pill on a contract the server has just excluded from the run rate — a WRONG state, not a
    /// degraded one, and precisely the state the feature exists to show.
    /// </summary>
    [Fact]
    public void An_unknown_member_fails_neutrally_under_its_own_name()
    {
        // An ordinal no member of this build's enum carries — the shape a newer server sends.
        var future = (ContractStatus)99;

        var meta = OdsContractStatus.Meta(future);

        Assert.True(meta.Unknown);
        Assert.Equal("outline", meta.Tone);
        Assert.Equal(future.ToString(), meta.Label);
        Assert.NotEqual(OdsContractStatus.Meta(ContractStatus.Active).Label, meta.Label);
        Assert.NotEqual(OdsContractStatus.Meta(ContractStatus.Active).Tone, meta.Tone);
    }

    /// <summary>
    /// The reading order has to cover the whole vocabulary: it drives the status filter's options and
    /// the summary's status rows, so a member missing from it is a state the reader can neither
    /// filter for nor see counted. It is deliberately NOT the ordinal order — an ordinal is a wire
    /// contract and Paused is appended at 4 rather than renumbered to sit beside Active.
    /// </summary>
    [Fact]
    public void The_display_order_covers_every_declared_status()
    {
        Assert.Equal(
            Enum.GetValues<ContractStatus>().OrderBy(s => (int)s),
            OdsContractStatus.Order.OrderBy(s => (int)s));

        // The LIFECYCLE order (issue #145 §8), shared with the server's list sort — not the ordinal,
        // which would put the two earliest states last because Draft and Ready are appended at 5 and 6.
        Assert.Equal(
            [ContractStatus.Draft, ContractStatus.Ready, ContractStatus.Upcoming, ContractStatus.Active,
             ContractStatus.Paused, ContractStatus.Expired, ContractStatus.Archived],
            OdsContractStatus.Order);

        // Read from the shared rank rather than copied: a local list here would let the filter and the
        // summary pills disagree with the order the sorted list comes back in.
        Assert.Same(ContractStatusOrder.Order, OdsContractStatus.Order);
    }

    [Fact]
    public void The_chip_carries_the_meaning_as_visible_text()
    {
        using var ctx = NewContext();

        var chip = ctx.Render<OdsContractStatusChip>(p => p
            .Add(c => c.Status, ContractStatus.Paused)
            .Add(c => c.Compact, true));

        Assert.Contains("Paused", chip.Markup, StringComparison.Ordinal);
        Assert.Contains("pending", chip.Find("span.odc-chip").GetAttribute("class")!, StringComparison.Ordinal);
        // The dot is decorative — the label is what conveys the state.
        Assert.Equal("true", chip.Find("span.odc-chip-dot").GetAttribute("aria-hidden"));
    }

    // ── The summary rollup ────────────────────────────────────────────────────

    /// <summary>
    /// Paused is a real fifth bucket with its own row, and "Ending soon" still sits directly after
    /// Active as the slice it is.
    /// </summary>
    [Fact]
    public void The_status_breakdown_carries_a_Paused_row()
    {
        using var ctx = NewContext();

        var view = ctx.Render<ContractsSummaryView>(p => p
            .Add(v => v.Summary, Summary(paused: 2))
            .Add(v => v.FormatMoney, Money));

        // The "By status" tile — the second breakdown in the grid.
        var statusTile = view.FindAll(".odc-breakdown")
            .Single(t => t.QuerySelector(".odc-breakdown-ov")?.TextContent.Trim() == "By status");
        var labels = statusTile.QuerySelectorAll(".odc-breakdown-label")
            .Select(e => e.TextContent.Trim()).ToList();

        Assert.Contains("Paused", labels);
        Assert.Contains("Ending soon · 45d", labels);
        Assert.Equal(labels.IndexOf("Active") + 1, labels.IndexOf("Ending soon · 45d"));

        // The count comes off the server's own bucket.
        var row = statusTile.QuerySelectorAll(".odc-breakdown-row")
            .Single(r => r.TextContent.Contains("Paused", StringComparison.Ordinal));
        Assert.Equal("2", row.QuerySelector(".odc-breakdown-n")!.TextContent.Trim());
    }

    /// <summary>
    /// A run rate that quietly dropped between two visits reads as a pricing error, so the tiles say
    /// why. Named for the same reason an unconvertible currency is named rather than folded in.
    /// </summary>
    [Fact]
    public void The_run_rate_tiles_name_the_paused_contracts_they_exclude()
    {
        using var ctx = NewContext();

        var withPaused = ctx.Render<ContractsSummaryView>(p => p
            .Add(v => v.Summary, Summary(paused: 3))
            .Add(v => v.FormatMoney, Money));
        Assert.Contains("3 paused excluded", withPaused.Markup, StringComparison.Ordinal);

        // …and says nothing when there is nothing to exclude.
        var without = ctx.Render<ContractsSummaryView>(p => p
            .Add(v => v.Summary, Summary(paused: 0))
            .Add(v => v.FormatMoney, Money));
        Assert.DoesNotContain("paused excluded", without.Markup, StringComparison.Ordinal);
    }

    // ── The page's branches ───────────────────────────────────────────────────

    /// <summary>
    /// Pause is enterable from Active ALONE, so the action is absent elsewhere rather than
    /// disabled-with-a-reason like Archive: "this contract is upcoming" is not an instruction the
    /// reader can act on. Resume is offered wherever a stamp exists, in ANY state — clearing is never
    /// refused, which is what stops an archived or expired contract being stranded holding one.
    /// </summary>
    [Fact]
    public void Pause_is_offered_from_Active_alone_and_Resume_wherever_a_stamp_exists()
    {
        var source = CardSource();

        Assert.Matches(
            new Regex(@"if\s*\(\s*c\.Status\s*==\s*ContractStatus\.Active\s*\)[\s\S]{0,400}?Label\s*=\s*""Pause"""),
            source);
        Assert.Matches(
            new Regex(@"else if\s*\(\s*c\.Paused is not null\s*\)[\s\S]{0,400}?Label\s*=\s*""Resume"""),
            source);

        // Resume is never offered disabled: there would be nothing to instruct.
        Assert.DoesNotMatch(new Regex(@"Label\s*=\s*""Resume""[\s\S]{0,200}?Disabled\s*=\s*true"), source);

        // An unsigned contract cannot be paused, and since the design system's menu rule the item is
        // ABSENT there rather than dimmed with a reason under it (Odyssey Design System ·
        // components/Menu). The branch stays keyed on `unsigned`, so the item is withheld for the
        // right reason and not by an accident of the Active test above; what it must not carry is an
        // explanation only an opened menu could deliver.
        Assert.Matches(
            new Regex(@"else if\s*\(\s*unsigned && c\.Paused is null\s*\)[\s\S]{0,400}?Label\s*=\s*""Pause""[\s\S]{0,300}?Disabled\s*=\s*true"),
            source);
        Assert.DoesNotContain("Only a signed contract in force can be paused", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The defect this file exists for.</b> <c>PUT /api/contracts/{id}</c> is a full replacement,
    /// so an omitted flag does not mean "leave alone" — it means <i>clear</i>. Every write path that
    /// is not itself toggling a stamp has to carry that stamp forward, or archiving a paused contract
    /// silently resumes it, and resuming an archived one silently restores it.
    /// </summary>
    [Fact]
    public void Every_contract_write_carries_both_stamps_forward()
    {
        foreach (var (file, source) in new[]
                 {
                     ("ContractsCard.razor.cs", CardSource()),
                     ("CreateContractDialog.razor.cs", Source("CreateContractDialog.razor.cs")),
                 })
        {
            var blocks = UpdateContractBlocks(source);
            Assert.NotEmpty(blocks);

            foreach (var block in blocks)
            {
                foreach (var (member, consequence) in RequiredStamps)
                {
                    Assert.True(
                        block.Contains(member, StringComparison.Ordinal),
                        $"{file}: an UpdateContract built without {member} would {consequence} on save.");
                }
            }
        }
    }

    /// <summary>
    /// The four stamps every <c>UpdateContract</c> has to carry, and what omitting each one does. The
    /// last two are issue #145's: a write that omits them clears both signature stamps, flips a
    /// signed contract to <c>Draft</c> and drops it out of the run rate — for a reader who only
    /// clicked Archive or Pause.
    /// </summary>
    private static readonly (string Member, string Consequence)[] RequiredStamps =
    [
        ("IsArchived", "unarchive"),
        ("IsPaused", "resume"),
        ("Ready", "clear the ready date"),
        ("Signed", "unsign the contract"),
    ];

    private static IReadOnlyList<string> UpdateContractBlocks(string source) =>
        [.. Regex.Matches(source, @"new UpdateContract\s*\{[\s\S]*?\n\s*\};").Select(m => m.Value)];

    /// <summary>
    /// The lint above has TEETH, proved rather than assumed. A source lint that happens to pass
    /// against code already written tells you nothing about whether it would catch the regression it
    /// exists for — so this feeds it a block with each member removed in turn and asserts it rejects
    /// every one. Without this, a typo in the member name would leave a green test guarding nothing.
    /// </summary>
    [Fact]
    public void The_carry_forward_lint_rejects_a_block_missing_any_one_stamp()
    {
        const string Complete = """
                    var update = new UpdateContract
                    {
                        Name = d.Name,
                        IsArchived = d.Archived is not null,
                        IsPaused = d.Paused is not null,
                        Ready = d.Ready,
                        Signed = d.Signed,
                    };
            """;

        // The shape the real lint reads must match it as written …
        var whole = Assert.Single(UpdateContractBlocks(Complete));
        Assert.All(RequiredStamps, s => Assert.Contains(s.Member, whole, StringComparison.Ordinal));

        // … and fail once any single member is taken out.
        foreach (var (member, _) in RequiredStamps)
        {
            var mutated = Regex.Replace(Complete, $@"^\s*{member} = .*$\r?\n", string.Empty, RegexOptions.Multiline);
            Assert.NotEqual(Complete, mutated);

            var block = Assert.Single(UpdateContractBlocks(mutated));
            Assert.False(
                block.Contains(member, StringComparison.Ordinal),
                $"removing {member} must make the lint's own predicate false");
        }
    }

    /// <summary>
    /// The pause stamp gets its own tile, beside the archive one, rather than being folded into the
    /// derived Status tile. One tile per STORED stamp is what keeps a contract that is both paused
    /// and archived from losing the date the other state began — the derived status can only report
    /// one of them.
    /// </summary>
    [Fact]
    public void A_stored_pause_stamp_gets_its_own_tile()
    {
        var markup = Source("ContractsCard.razor");

        Assert.Matches(
            new Regex(@"@if\s*\(detail\.Paused is \{ \} \w+\)[\s\S]{0,500}?Icon=""pause_circle""[\s\S]{0,300}?Label=""Paused"""),
            markup);
        // Still independent of the archive tile, not an either/or.
        Assert.Matches(new Regex(@"@if\s*\(detail\.Archived is \{ \} \w+\)"), markup);

        // And the card stays at FULL BRIGHTNESS: dimming follows archive alone, which is most of what
        // separates a suspended agreement from a retired record.
        Assert.Matches(new Regex(@"Dimmed=""archived"""), markup);
        Assert.DoesNotMatch(new Regex(@"Dimmed=""[^""]*[Pp]aused"), markup);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static string Money(decimal value, string? code) => $"{code} {value:0.00}";

    private static ContractSummary Summary(int paused, int draft = 0, int ready = 0) => new()
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

    /// <summary>
    /// The page source with comments stripped: its own doc comments legitimately DISCUSS the pause
    /// branches, and a lint a comment can satisfy is a lint that proves nothing.
    /// </summary>
    private static string CardSource() => Source("ContractsCard.razor.cs");

    private static string Source(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", fileName));
        source = Regex.Replace(source, @"@\*[\s\S]*?\*@", string.Empty);
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*(///?).*$", string.Empty, RegexOptions.Multiline);
        return source;
    }
}
