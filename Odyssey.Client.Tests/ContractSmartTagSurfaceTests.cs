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
/// server's own sentence where the reader is still looking; and — since issue #226 — that the match
/// is the SERVER's scoped read, whose scope, empty reason and totals the section restates rather than
/// re-deriving.
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
    /// The contract host's copy states each of the three rules the server applies (issue #226) —
    /// watched tag, inside the term, contact-party merchant — so a reader knows what will and will not
    /// read here. The old copy's "no filter to rebuild" framing described the tag-only match.
    /// </summary>
    [Theory]
    [InlineData(ContractDetailView.SmartTagsWritableEmptyDesc)]
    [InlineData(ContractDetailView.SmartTagsNoMatchDesc)]
    public void The_contract_copy_names_the_term_and_party_rules(string copy)
    {
        Assert.Contains("inside the term", copy, StringComparison.Ordinal);
        Assert.Contains("contact party", copy, StringComparison.Ordinal);
        Assert.DoesNotContain("no filter to rebuild", copy, StringComparison.Ordinal);
    }

    /// <summary>The host's no-match sentence is what the section shows for an empty, unsearched match.</summary>
    [Fact]
    public void An_empty_match_shows_the_hosts_no_match_sentence()
    {
        var harness = Render(noMatchDesc: ContractDetailView.SmartTagsNoMatchDesc);

        Assert.Contains(ContractDetailView.SmartTagsNoMatchDesc, harness.Cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A header sort goes to the server with its key and direction and restarts at page one; the Tag
    /// column has no server key, so clicking it keeps the current order and issues no read.
    /// </summary>
    [Fact]
    public async Task A_header_sort_reaches_the_server_and_the_tag_column_is_inert()
    {
        var harness = Render(scoped: Envelope(items: [Transaction("Power", -10m)], totalCount: 60));
        var view = harness.Cut.FindComponent<TransactionListView>();
        await harness.Cut.InvokeAsync(() => view.Instance.PageChanged.InvokeAsync(3));
        harness.Contracts.Invocations.Clear();

        await harness.Cut.InvokeAsync(() => view.Instance.SortChanged.InvokeAsync(
            new Odyssey.Client.Components.OdsTableSort("amount", Odyssey.Client.Components.OdsSortDirection.Asc)));
        harness.Contracts.Verify(c => c.ListSmartTagTransactionsAsync(ContractId, 1, It.IsAny<int>(),
            It.IsAny<string?>(), "Amount", "Asc", It.IsAny<CancellationToken>()), Times.Once);

        harness.Contracts.Invocations.Clear();
        await harness.Cut.InvokeAsync(() => view.Instance.SortChanged.InvokeAsync(
            new Odyssey.Client.Components.OdsTableSort("tag", Odyssey.Client.Components.OdsSortDirection.Asc)));
        Assert.Empty(harness.Contracts.Invocations);
        Assert.Equal("amount", harness.Cut.FindComponent<TransactionListView>().Instance.Sort?.Key);
    }

    /// <summary>A page and a page size are sent as the window; a new size restarts at page one.</summary>
    [Fact]
    public async Task Paging_and_page_size_reach_the_server()
    {
        var harness = Render(scoped: Envelope(items: [Transaction("Power", -10m)], totalCount: 300));
        var view = harness.Cut.FindComponent<TransactionListView>();

        await harness.Cut.InvokeAsync(() => view.Instance.PageChanged.InvokeAsync(2));
        harness.Contracts.Verify(c => c.ListSmartTagTransactionsAsync(ContractId, 2, 25,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        await harness.Cut.InvokeAsync(() => view.Instance.PageSizeChanged.InvokeAsync(100));
        harness.Contracts.Verify(c => c.ListSmartTagTransactionsAsync(ContractId, 1, 100,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A failed read clears the previous scope and totals rather than showing them as current.</summary>
    [Fact]
    public void A_failed_read_drops_the_previous_scope_and_totals()
    {
        var harness = Render(scoped: Envelope(items: [Transaction("Power", -10m)],
            byCurrency: [new ContractSmartTagCurrencyTotal { CurrencyCode = "NOK", TransactionCount = 1, TotalOut = 10m, Net = -10m }]));
        harness.Contracts
            .Setup(c => c.ListSmartTagTransactionsAsync(ContractId, It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ContractSmartTagTransactionsResult>.Failure(
                HttpStatusCode.InternalServerError, new ApiProblem { Detail = "boom" }));

        harness.Cut.Render(p => p.Add(s => s.ScopeKey, "changed"));

        Assert.Contains("Could not load matching transactions", harness.Cut.Markup, StringComparison.Ordinal);
        Assert.Empty(harness.Cut.FindAll(".odc-smarttags-scope"));
        Assert.Empty(harness.Cut.FindAll(".odc-smarttags-total"));
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

    // ── The scoped read (issue #226) ─────────────────────────────────────────

    /// <summary>
    /// The contract host reads the SERVER's scoped match — never the tag-only transaction list, which
    /// would bring back every transaction carrying a watched tag regardless of term or merchant.
    /// </summary>
    [Fact]
    public void The_match_is_the_contracts_scoped_read_not_the_tag_filter()
    {
        var harness = Render();

        harness.Contracts.Verify(c => c.ListSmartTagTransactionsAsync(ContractId, 1, It.IsAny<int>(),
            null, "Date", "Desc", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        harness.Transactions.Verify(t => t.ListAllAsync(
            It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
            It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
            It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The scope line restates the response: the last covered day is the one BEFORE the exclusive
    /// bound, and the party count is the server's.
    /// </summary>
    [Fact]
    public void The_scope_line_restates_the_window_and_the_party_count()
    {
        var harness = Render(scoped: Envelope(partyContacts: 3));

        var scope = harness.Cut.Find(".odc-smarttags-scope").TextContent;
        Assert.Contains(ScopeDay(new DateTime(2026, 1, 1)) + " – " + ScopeDay(new DateTime(2026, 12, 31)), scope, StringComparison.Ordinal);
        Assert.Contains("one of 3 contact parties", scope, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "any date — no term set")]
    [InlineData(true, "any date — one-off contract")]
    public void An_unbounded_window_says_why(bool isOneOff, string expected)
    {
        var harness = Render(scoped: Envelope(unbounded: true), isOneOff: isOneOff);

        Assert.Contains(expected, harness.Cut.Find(".odc-smarttags-scope").TextContent, StringComparison.Ordinal);
    }

    /// <summary>No contact party: the body says why nothing can match, and offers the one fix.</summary>
    [Fact]
    public void No_contact_party_blocks_the_body_and_offers_to_add_one()
    {
        var added = false;
        var harness = Render(
            scoped: Envelope(ContractSmartTagEmptyReason.NoContactParties, partyContacts: 0),
            onAddParty: () => added = true);

        var markup = harness.Cut.Markup;
        Assert.Contains("No contact is a party yet", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("No matching transactions", markup, StringComparison.Ordinal);

        harness.Cut.FindAll(".odc-smarttags-blocked button").Single(b => b.TextContent.Contains("Add a party")).Click();
        Assert.True(added);
    }

    [Fact]
    public void A_read_only_viewer_gets_no_fixing_action()
    {
        var harness = Render(scoped: Envelope(ContractSmartTagEmptyReason.NoContactParties, partyContacts: 0), canWrite: false);

        Assert.Contains("No contact is a party yet", harness.Cut.Markup, StringComparison.Ordinal);
        Assert.Empty(harness.Cut.FindAll(".odc-smarttags-blocked button"));
    }

    /// <summary>
    /// An invalid term names both dates — the end day being the one before the exclusive bound — and
    /// the scope line is withheld, since its window would read backwards.
    /// </summary>
    [Fact]
    public void An_invalid_term_names_both_dates_and_offers_to_edit_them()
    {
        var edited = false;
        var harness = Render(
            scoped: Envelope(ContractSmartTagEmptyReason.InvalidTerm,
                from: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                toExclusive: new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc)),
            onEditContract: () => edited = true);

        var markup = harness.Cut.Markup;
        Assert.Contains("The term ends before it starts", markup, StringComparison.Ordinal);
        Assert.Contains($"Start date {ScopeDay(new DateTime(2026, 6, 1))} is after end date {ScopeDay(new DateTime(2026, 3, 1))}", markup, StringComparison.Ordinal);
        Assert.Empty(harness.Cut.FindAll(".odc-smarttags-scope"));

        harness.Cut.FindAll(".odc-smarttags-blocked button").Single(b => b.TextContent.Contains("Edit dates")).Click();
        Assert.True(edited);
    }

    /// <summary>
    /// Without transactions.read the read is never issued — it would be a 403 — and the chips stay,
    /// with a sentence naming the claim.
    /// </summary>
    [Fact]
    public void Without_transactions_read_the_read_is_never_issued()
    {
        var harness = Render(canReadTransactions: false);

        harness.Contracts.Verify(c => c.ListSmartTagTransactionsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        var markup = harness.Cut.Markup;
        Assert.Contains("Transactions are not visible to you", markup, StringComparison.Ordinal);
        Assert.Contains("Rent", markup, StringComparison.Ordinal);
        Assert.Empty(harness.Cut.FindAll(".odc-smarttags-scope"));
    }

    /// <summary>
    /// The bar counts the WHOLE match (the page holds a slice) and shows the server's net in its
    /// currency — never a sum of the page.
    /// </summary>
    [Fact]
    public void The_bar_shows_the_servers_count_and_net_for_one_currency()
    {
        var harness = Render(scoped: Envelope(
            items: [Transaction("Power", -10m)],
            totalCount: 42,
            byCurrency: [new ContractSmartTagCurrencyTotal { CurrencyCode = "NOK", TransactionCount = 42, TotalOut = 1234.5m, Net = -1234.5m }]));

        var total = harness.Cut.Find(".odc-smarttags-total");
        Assert.Contains("42 transactions", total.TextContent, StringComparison.Ordinal);
        Assert.Contains("-1234.50 NOK", total.TextContent, StringComparison.Ordinal);
        Assert.Contains("expense", total.ClassName, StringComparison.Ordinal);
    }

    /// <summary>Mixed currencies have no single sum: the bar shows the count alone.</summary>
    [Fact]
    public void Mixed_currencies_show_the_count_without_a_sum()
    {
        var harness = Render(scoped: Envelope(
            items: [Transaction("Power", -10m), Transaction("Roaming", -5m, "EUR")],
            byCurrency:
            [
                new ContractSmartTagCurrencyTotal { CurrencyCode = "EUR", TransactionCount = 1, TotalOut = 5m, Net = -5m },
                new ContractSmartTagCurrencyTotal { CurrencyCode = "NOK", TransactionCount = 1, TotalOut = 10m, Net = -10m },
            ]));

        var total = harness.Cut.Find(".odc-smarttags-total");
        Assert.Contains("2 transactions", total.TextContent, StringComparison.Ordinal);
        Assert.Empty(harness.Cut.FindAll(".odc-smarttags-total-val"));
    }

    /// <summary>
    /// Search goes to the server and restarts at page one; an emptied result names the term and keeps
    /// the field, so the search can be cleared from where it was typed.
    /// </summary>
    [Fact]
    public async Task A_search_is_sent_to_the_server_and_an_empty_result_names_it()
    {
        var harness = Render(scoped: Envelope(items: [Transaction("Power", -10m)]));
        harness.Contracts
            .Setup(c => c.ListSmartTagTransactionsAsync(ContractId, 1, It.IsAny<int>(), "grid",
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ContractSmartTagTransactionsResult>.Success(Envelope(), HttpStatusCode.OK));

        var field = harness.Cut.FindComponent<Odyssey.Client.Components.OdsSearchField>();
        await harness.Cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync("grid"));

        harness.Contracts.Verify(c => c.ListSmartTagTransactionsAsync(ContractId, 1, It.IsAny<int>(), "grid",
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("No matching transaction mentions “grid”.", harness.Cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(harness.Cut.FindAll(".odc-smarttags-toolbar"));
    }

    /// <summary>A match is rendered as one server page, with the footer pager over the total.</summary>
    [Fact]
    public void A_match_renders_one_server_page_with_a_pager()
    {
        var harness = Render(scoped: Envelope(items: [Transaction("Power", -10m)], totalCount: 60));

        var view = harness.Cut.FindComponent<TransactionListView>().Instance;
        Assert.True(view.ServerPaged);
        Assert.Equal(60, view.TotalCount);
        Assert.NotEmpty(harness.Cut.FindAll("nav.odc-pager"));
    }

    /// <summary>
    /// A party or date change on the contract moves the scope: a new key re-reads, an unchanged one
    /// does not.
    /// </summary>
    [Fact]
    public void A_changed_scope_key_re_reads_the_match()
    {
        var harness = Render();
        harness.Contracts.Invocations.Clear();

        harness.Cut.Render(p => p.Add(s => s.ScopeKey, "party-added"));

        harness.Contracts.Verify(c => c.ListSmartTagTransactionsAsync(ContractId, It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Accessibility and read ordering (raised by the PR review agents) ─────

    private void SetupScoped(Harness harness, ContractSmartTagTransactionsResult envelope, string? search = null) =>
        harness.Contracts
            .Setup(c => c.ListSmartTagTransactionsAsync(ContractId, It.IsAny<int>(), It.IsAny<int>(),
                search ?? It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ContractSmartTagTransactionsResult>.Success(envelope, HttpStatusCode.OK));

    private static string StatusRegion(Harness harness) =>
        harness.Cut.Find(".odc-sr-only[role='status']").TextContent;

    /// <summary>
    /// A server-paged read swaps rows and counts in place, so its outcome is announced through a polite
    /// status region (WCAG 4.1.3) — the count on a match, the no-match sentence otherwise.
    /// </summary>
    [Fact]
    public async Task Each_scoped_read_announces_its_outcome()
    {
        var harness = Render(scoped: Envelope(items: [Transaction("Power", -10m)]));
        Assert.Equal("1 matching transaction.", StatusRegion(harness));

        SetupScoped(harness, Envelope(), search: "grid");
        var field = harness.Cut.FindComponent<Odyssey.Client.Components.OdsSearchField>();
        await harness.Cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync("grid"));

        Assert.Equal("No matching transaction mentions “grid”.", StatusRegion(harness));
    }

    /// <summary>
    /// The fixing action of a blocked state unmounts with it, so when the state clears focus lands on
    /// the scope line rather than falling to the page (WCAG 2.4.3).
    /// </summary>
    [Fact]
    public void Clearing_a_blocked_state_moves_focus_to_the_scope_line()
    {
        var harness = Render(scoped: Envelope(ContractSmartTagEmptyReason.NoContactParties, partyContacts: 0));
        SetupScoped(harness, Envelope(items: [Transaction("Power", -10m)]));

        harness.Cut.Render(p => p.Add(s => s.ScopeKey, "party-added"));

        var scopeLine = harness.Cut.Find(".odc-smarttags-scope");
        Assert.Equal("-1", scopeLine.GetAttribute("tabindex"));
        Assert.Contains(harness.Context.JSInterop.Invocations,
            i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A failed read followed by a successful one shows the result, not the old error.</summary>
    [Fact]
    public void A_successful_re_read_clears_an_earlier_failure()
    {
        var harness = Render();
        harness.Contracts
            .Setup(c => c.ListSmartTagTransactionsAsync(ContractId, It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ContractSmartTagTransactionsResult>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = "boom" }));
        harness.Cut.Render(p => p.Add(s => s.ScopeKey, "first"));
        Assert.NotEmpty(harness.Cut.FindAll(".odc-smarttags-state.error"));

        SetupScoped(harness, Envelope(items: [Transaction("Power", -10m)]));
        harness.Cut.Render(p => p.Add(s => s.ScopeKey, "second"));

        Assert.Empty(harness.Cut.FindAll(".odc-smarttags-state.error"));
        Assert.Contains("Power", harness.Cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Overlapping reads land newest-wins: a slow response to an earlier search must not overwrite the
    /// rows of the search that replaced it.
    /// </summary>
    [Fact]
    public async Task An_older_response_arriving_last_is_discarded()
    {
        var harness = Render(scoped: Envelope(items: [Transaction("Initial", -1m)]));
        var slow = new TaskCompletionSource<ApiResult<ContractSmartTagTransactionsResult>>();
        harness.Contracts
            .Setup(c => c.ListSmartTagTransactionsAsync(ContractId, It.IsAny<int>(), It.IsAny<int>(),
                "older", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(slow.Task);
        SetupScoped(harness, Envelope(items: [Transaction("Newest match", -2m)]), search: "newer");

        var field = harness.Cut.FindComponent<Odyssey.Client.Components.OdsSearchField>();
        var older = harness.Cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync("older"));
        await harness.Cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync("newer"));
        slow.SetResult(ApiResult<ContractSmartTagTransactionsResult>.Success(
            Envelope(items: [Transaction("Stale match", -3m)]), HttpStatusCode.OK));
        await older;

        Assert.Contains("Newest match", harness.Cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale match", harness.Cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>A changed scope can shrink the match, so the re-read starts at page one.</summary>
    [Fact]
    public async Task A_changed_scope_restarts_at_page_one()
    {
        var harness = Render(scoped: Envelope(items: [Transaction("Power", -10m)], totalCount: 60));
        var view = harness.Cut.FindComponent<TransactionListView>();
        await harness.Cut.InvokeAsync(() => view.Instance.PageChanged.InvokeAsync(2));
        harness.Contracts.Invocations.Clear();

        harness.Cut.Render(p => p.Add(s => s.ScopeKey, "date-changed"));

        harness.Contracts.Verify(c => c.ListSmartTagTransactionsAsync(ContractId, 1, It.IsAny<int>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static string ScopeDay(DateTime date) =>
        date.ToString("MMM dd, yyyy", System.Globalization.CultureInfo.CurrentCulture);

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed record Harness(
        IRenderedComponent<AccountSmartTagsSection> Cut,
        Mock<IContractsApiClient> Contracts,
        Mock<IAccountsApiClient> Accounts,
        Mock<IContractLimitsCache> ContractLimits,
        Mock<IAccountLimitsCache> AccountLimits,
        Mock<ITransactionsApiClient> Transactions,
        BunitContext Context)
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
        string? emptyDesc = null,
        string? noMatchDesc = null,
        ContractSmartTagTransactionsResult? scoped = null,
        bool canReadTransactions = true,
        bool isOneOff = false,
        Action? onAddParty = null,
        Action? onEditContract = null)
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
        contracts
            .Setup(c => c.ListSmartTagTransactionsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ContractSmartTagTransactionsResult>.Success(
                scoped ?? Envelope(), HttpStatusCode.OK));

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
        ctx.Services.AddSingleton(new Mock<IPropertiesApiClient>().Object);
        ctx.Services.AddSingleton(new Mock<IPropertyLimitsCache>().Object);
        // The matched rows render through TransactionListView, which resolves its own claims and a
        // clipboard for its row menu.
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(new SignedOut());

        var cut = ctx.Render<AccountSmartTagsSection>(p => p
            .Add(s => s.Host, SmartTagHost.Contract)
            .Add(s => s.SubjectId, ContractId)
            .Add(s => s.Chrome, false)
            .Add(s => s.Icon, "local_offer")
            .Add(s => s.CanWrite, canWrite)
            .Add(s => s.EmptyDesc, emptyDesc)
            .Add(s => s.NoMatchDesc, noMatchDesc)
            .Add(s => s.CanReadTransactions, canReadTransactions)
            .Add(s => s.IsOneOff, isOneOff)
            .Add(s => s.OnAddParty, onAddParty ?? (() => { }))
            .Add(s => s.OnEditContract, onEditContract ?? (() => { }))
            .Add(s => s.FormatMoney, (decimal v, string? code) => $"{v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} {code}".TrimEnd()));

        // OnInitializedAsync early-returns outside the browser, so the load is driven through the
        // section's own public reload — the same entry point its retry button uses.
        cut.InvokeAsync(() => cut.Instance.ReloadAsync()).GetAwaiter().GetResult();

        return new Harness(cut, contracts, accounts, contractLimits, accountLimits, transactions, ctx);
    }

    private sealed class SignedOut : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(
                new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    private static ExistingTransaction Transaction(string description, decimal amount, string currency = "NOK") => new()
    {
        TransactionId = Guid.NewGuid(),
        Description = description,
        Amount = amount,
        CurrencyCode = currency,
        AccountId = Guid.NewGuid(),
        TimeStamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>The server's envelope. The defaults are a healthy, bounded match with nothing in it.</summary>
    private static ContractSmartTagTransactionsResult Envelope(
        ContractSmartTagEmptyReason reason = ContractSmartTagEmptyReason.None,
        DateTime? from = null,
        DateTime? toExclusive = null,
        bool unbounded = false,
        int partyContacts = 1,
        IReadOnlyList<ExistingTransaction>? items = null,
        int? totalCount = null,
        IReadOnlyList<ContractSmartTagCurrencyTotal>? byCurrency = null) => new()
    {
        Scope = new ContractSmartTagScope
        {
            From = unbounded ? null : from ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ToExclusive = unbounded ? null : toExclusive ?? new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            SmartTagCount = 2,
            PartyContactCount = partyContacts,
            EmptyReason = reason,
        },
        Summary = new ContractSmartTagSummary
        {
            TransactionCount = totalCount ?? items?.Count ?? 0,
            ByCurrency = byCurrency ?? [],
        },
        Page = new PagedResult<ExistingTransaction>
        {
            Items = items ?? [],
            Offset = 0,
            Limit = 25,
            TotalCount = totalCount ?? items?.Count ?? 0,
        },
    };
}
