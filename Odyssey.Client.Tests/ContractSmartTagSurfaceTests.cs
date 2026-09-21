using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The contract host of the shared smart-tags section (issue #166), rendered rather than derived.
/// </summary>
/// <remarks>
/// <para>
/// One component serves both hosts, which is the design system's own instruction — so what needs
/// pinning is not the markup but the things that differ, and every one of them is a rule rather than
/// a styling choice: that the contract host talks to the CONTRACT endpoints and the CONTRACT cap;
/// that the cap sentence names the effective number rather than a literal; that a <b>degraded</b>
/// limits read stops the pre-check instead of guessing a ceiling; that a refused write shows the
/// server's own sentence where the reader is still looking; and that the empty copy claims no
/// contract scope, because the match is by tag and has none.
/// </para>
/// </remarks>
public class ContractSmartTagSurfaceTests
{
    private static readonly Guid ContractId = Guid.NewGuid();
    private static readonly Guid RentTagId = Guid.NewGuid();
    private static readonly Guid UtilitiesTagId = Guid.NewGuid();
    private static readonly Guid SpareTagId = Guid.NewGuid();

    private static readonly Guid RetiredTagId = Guid.NewGuid();

    private static ExistingTransactionTag Tag(Guid id, string name, bool archived = false) => new()
    {
        TransactionTagId = id,
        Name = name,
        Archived = archived ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
    };

    // ── The host boundary ────────────────────────────────────────────────────

    /// <summary>
    /// The contract host reads the CONTRACT watchlist and the CONTRACT cap. Reaching the account
    /// client or the account cache would silently show one record's configuration on another's page,
    /// and pre-check it against the wrong administrator-set number.
    /// </summary>
    [Fact]
    public void The_contract_host_reads_the_contract_endpoints_and_the_contract_cap()
    {
        var harness = Render();

        harness.Contracts.Verify(
            c => c.ListSmartTagsAsync(ContractId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        harness.Accounts.Verify(
            a => a.ListSmartTagsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.ContractLimits.Verify(l => l.GetAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        harness.AccountLimits.Verify(a => a.GetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// An add goes to the contract's own scoped route, carrying the contract id from the parameter
    /// rather than any id the tag row happens to hold.
    /// </summary>
    [Fact]
    public void Adding_a_tag_posts_to_the_contracts_scoped_route()
    {
        var harness = Render();

        harness.Add(SpareTagId);

        harness.Contracts.Verify(
            c => c.AddSmartTagAsync(ContractId, SpareTagId, It.IsAny<CancellationToken>()), Times.Once);
        harness.Accounts.Verify(
            a => a.AddSmartTagAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The option pool draws on the catalogue with ARCHIVED tags excluded — retired vocabulary the
    /// server would refuse anyway, and which the user could not manage from the tags page.
    ///
    /// <para>
    /// The fixture seeds a real archived tag, which is the whole point: without one in the INPUT the
    /// assertion could not fail, and the filter it exists to pin could be deleted with the test still
    /// green. (Raised by the test reviewer on this PR — the first version of this test had exactly
    /// that hole.)
    /// </para>
    /// </summary>
    [Fact]
    public void The_adder_is_offered_the_unarchived_catalogue()
    {
        var harness = Render();

        var adder = harness.Cut.FindComponent<AccountSmartTagAdder>().Instance;
        Assert.Equal(
            ["Groceries", "Rent", "Utilities"],
            adder.Options.Select(o => o.Label).OrderBy(l => l, StringComparer.Ordinal));
        Assert.DoesNotContain("Retired", adder.Options.Select(o => o.Label));
        Assert.Equal(2, adder.SelectedIds.Count);
    }

    // ── The cap ──────────────────────────────────────────────────────────────

    /// <summary>
    /// At the cap, the advisory names the EFFECTIVE number. The cap under test is deliberately not
    /// the shipped 20: a test that only passed at the default would pass equally well against a
    /// component that had gone back to a constant.
    /// </summary>
    [Fact]
    public void At_the_cap_the_advisory_names_the_effective_number()
    {
        var harness = Render(cap: new ContractLimits(2, IsDegraded: false));

        var markup = harness.Cut.Markup;
        Assert.Contains("maximum of 2 tags", markup, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"maximum of {SystemSettingsDefaults.ContractMaxSmartTagsPerContract}",
            markup,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The advisory reads on the BAR, not only inside the popover — the popover closes on the click
    /// that would have prompted the question, so "why can I not add another" has to be answerable
    /// without reopening it.
    /// </summary>
    [Fact]
    public void The_cap_advisory_reads_outside_the_popover()
    {
        var harness = Render(cap: new ContractLimits(2, IsDegraded: false));

        Assert.NotEmpty(harness.Cut.FindAll(".odc-smarttags-advisory"));
        // And the adder carries the same sentence, so the two cannot say different things.
        var adder = harness.Cut.FindComponent<AccountSmartTagAdder>().Instance;
        Assert.True(adder.AtCap);
        Assert.Contains("maximum of 2 tags", adder.FootNote!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <b>degraded</b> limits read has no number to pre-check against, so the adder stays open and
    /// the server's conservative bound does the refusing. Falling back to the shipped default here
    /// would block adds an administrator had raised the cap to allow, and would present a guessed
    /// number as the configured one.
    /// </summary>
    [Fact]
    public void A_degraded_limits_read_leaves_the_adder_open_and_says_so()
    {
        // Two tags watched against a fallback of 20 would not be at the cap anyway, so the cap is set
        // to 1 as well: only the degraded flag can be what keeps the rows enabled.
        var harness = Render(cap: new ContractLimits(1, IsDegraded: true));

        Assert.Contains("tag limit is unavailable", harness.Cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("maximum of", harness.Cut.Markup, StringComparison.Ordinal);

        // The adder is what actually blocks a check, so the assertion is on ITS state rather than on
        // popover markup that is not rendered until the menu opens — a DOM query for a blocked row
        // would pass on an empty collection and prove nothing.
        Assert.False(harness.Cut.FindComponent<AccountSmartTagAdder>().Instance.AtCap);
    }

    // ── A refused write ──────────────────────────────────────────────────────

    /// <summary>
    /// The server's own sentence, on the bar. At the cap that sentence carries the effective number,
    /// which is the one thing the client must never restate — and a toast would be gone before the
    /// reader looked back at the control that refused.
    /// </summary>
    [Fact]
    public void A_refused_add_shows_the_servers_own_sentence_on_the_bar()
    {
        const string Refusal = "Tag 'Groceries' is archived and cannot be added as a smart tag.";
        var harness = Render(addResult: ApiResult.Failure(
            HttpStatusCode.UnprocessableEntity, new ApiProblem { Detail = Refusal }));

        harness.Add(SpareTagId);

        var refused = harness.Cut.Find(".odc-smarttags-refused");
        Assert.Contains(Refusal, refused.TextContent, StringComparison.Ordinal);
        Assert.Equal("alert", refused.GetAttribute("role"));
    }

    // ── Copy ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The empty copy must not claim a contract scope. The match is by tag and spans every record
    /// watching it, so "transactions on this contract" would be false — and the default noun
    /// substitution would say exactly that.
    /// </summary>
    [Fact]
    public void The_empty_copy_claims_no_contract_scope()
    {
        var harness = Render(watched: [], emptyDesc:
            "Pin the tags this agreement settles against, and what it actually costs reads here — no filter to rebuild on the Transactions page.");

        var markup = harness.Cut.Markup;
        Assert.Contains("Pin the tags this agreement settles against", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("on this contract carry", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The section stays rendered at zero watched tags: its adder is the only entry point for a first
    /// smart tag, so hiding it when empty would make the feature unreachable from the record.
    /// </summary>
    [Fact]
    public void The_section_renders_its_adder_with_no_tags_watched()
    {
        var harness = Render(watched: []);

        Assert.Contains("No smart tags yet", harness.Cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(harness.Cut.FindAll(".odc-smarttags-adder"));
    }

    // ── The write gate ───────────────────────────────────────────────────────

    /// <summary>
    /// A reader holding <c>contracts.read</c> but not <c>contracts.update</c> keeps the chips and the
    /// ledger and loses every control — the section is informative to them, not inert.
    /// </summary>
    [Fact]
    public void A_read_only_viewer_keeps_the_chips_and_loses_the_controls()
    {
        var harness = Render(canWrite: false);

        Assert.Contains("Rent", harness.Cut.Markup, StringComparison.Ordinal);
        Assert.Empty(harness.Cut.FindAll(".odc-smarttags-adder"));
        Assert.Empty(harness.Cut.FindAll("button[aria-label^='Stop watching']"));
    }

    // ── Accessibility (raised by the accessibility reviewer on this PR) ──────

    /// <summary>
    /// The cap advisory is ANNOUNCED when it appears, not merely present. It is written in response
    /// to an action — the add that reached the cap — and DOM presence is not notification
    /// (WCAG 4.1.3). Polite, not assertive: the refusal band is the one that interrupts.
    /// </summary>
    [Fact]
    public void The_cap_advisory_is_a_status_message()
    {
        var harness = Render(cap: new ContractLimits(2, IsDegraded: false));

        var advisory = harness.Cut.Find(".odc-smarttags-advisory");
        Assert.Equal("status", advisory.GetAttribute("role"));
    }

    /// <summary>
    /// The design system dims this state's glyph and leaves the "No smart tags yet" one bright: the
    /// first is informational, the second is the feature's only entry point, and the icon weight is
    /// what tells them apart.
    /// </summary>
    [Fact]
    public void The_no_matches_state_uses_the_muted_glyph()
    {
        var harness = Render();

        Assert.NotEmpty(harness.Cut.FindAll(".odc-empty.muted-ic"));
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed record Harness(
        IRenderedComponent<AccountSmartTagsSection> Cut,
        Mock<IContractsApiClient> Contracts,
        Mock<IAccountsApiClient> Accounts,
        Mock<IContractLimitsCache> ContractLimits,
        Mock<IAccountLimitsCache> AccountLimits)
    {
        /// <summary>
        /// Checks a row in the adder. Driven through the child's own callback rather than through the
        /// popover's DOM: the list is inside a <c>MudMenu</c> and is not rendered until the menu is
        /// opened, so a DOM-driven version would be testing MudBlazor's portal rather than this
        /// section's routing.
        /// </summary>
        public void Add(Guid tagId)
        {
            var adder = Cut.FindComponent<AccountSmartTagAdder>();
            Cut.InvokeAsync(() => adder.Instance.OnAdd.InvokeAsync(tagId.ToString()))
                .GetAwaiter().GetResult();
        }
    }

    private static Harness Render(
        ContractLimits? cap = null,
        List<ExistingTransactionTag>? watched = null,
        ApiResult? addResult = null,
        bool canWrite = true,
        string? emptyDesc = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var tags = watched ?? [Tag(RentTagId, "Rent"), Tag(UtilitiesTagId, "Utilities")];

        var contracts = new Mock<IContractsApiClient>();
        contracts
            .Setup(c => c.ListSmartTagsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingTransactionTag>>.Success(tags, HttpStatusCode.OK));
        contracts
            .Setup(c => c.AddSmartTagAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(addResult ?? ApiResult.Success(HttpStatusCode.Created));
        contracts
            .Setup(c => c.RemoveSmartTagAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));

        var accounts = new Mock<IAccountsApiClient>();

        var transactions = new Mock<ITransactionsApiClient>();
        transactions
            .Setup(t => t.ListAllAsync(
                It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingTransaction>>.Success([], HttpStatusCode.OK));

        var reference = new Mock<IReferenceDataCache>();
        // The archived entry is load-bearing: it is what makes the exclusion assertion able to fail.
        IReadOnlyList<ExistingTransactionTag> catalogue =
        [
            Tag(RentTagId, "Rent"),
            Tag(UtilitiesTagId, "Utilities"),
            Tag(SpareTagId, "Groceries"),
            Tag(RetiredTagId, "Retired", archived: true),
        ];
        reference
            .Setup(r => r.TransactionTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalogue);

        var contractLimits = new Mock<IContractLimitsCache>();
        contractLimits
            .Setup(l => l.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(cap ?? new ContractLimits(
                SystemSettingsDefaults.ContractMaxSmartTagsPerContract, IsDegraded: false));

        var accountLimits = new Mock<IAccountLimitsCache>();

        ctx.Services.AddSingleton(contracts.Object);
        ctx.Services.AddSingleton(accounts.Object);
        ctx.Services.AddSingleton(transactions.Object);
        ctx.Services.AddSingleton(reference.Object);
        ctx.Services.AddSingleton(contractLimits.Object);
        ctx.Services.AddSingleton(accountLimits.Object);

        var cut = ctx.Render<AccountSmartTagsSection>(p => p
            .Add(s => s.Host, SmartTagHost.Contract)
            .Add(s => s.SubjectId, ContractId)
            .Add(s => s.Chrome, false)
            .Add(s => s.Icon, "local_offer")
            .Add(s => s.CanWrite, canWrite)
            .Add(s => s.EmptyDesc, emptyDesc)
            .Add(s => s.FormatMoney, (decimal v, string? _) => v.ToString("0.00")));

        // OnInitializedAsync early-returns outside the browser, so the load is driven through the
        // section's own public reload — the same entry point its retry button uses.
        cut.InvokeAsync(() => cut.Instance.ReloadAsync()).GetAwaiter().GetResult();

        return new Harness(cut, contracts, accounts, contractLimits, accountLimits);
    }
}
