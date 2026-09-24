using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The party tiles in <see cref="ContractDetailView"/> (issue #122 §3): the ROLE as the overline, the
/// non-default term beneath it, the kind and record type demoted to the caption, and the always-visible
/// ⋯ menu that replaced the hover-revealed detach icon.
/// </summary>
/// <remarks>
/// <para>
/// These RENDER the component, because that is the rule: the affordance has to be in the markup
/// unconditionally, which a derivation test could not tell apart from one that is merely styled to
/// zero opacity.
/// </para>
/// <para>
/// The rule most worth pinning is the UNRECOGNISED-role tile. Ordinals append server-side, so a client
/// older than the deployment is handed a member it has never heard of — and the <c>PUT</c> is a full
/// replacement, so a dialog that cannot name the role would silently rewrite it. Edit is therefore
/// withheld while Detach stays, the same reasoning the insurance tiles apply to an unresolvable
/// TARGET, extended to an unresolvable ROLE.
/// </para>
/// </remarks>
public class ContractPartyTileTests
{
    // See InsurancePolicyPartyTileTests for why this ceiling is raised: it is about scheduling
    // starvation on a contended runner, not about how long an assertion takes to settle.
    static ContractPartyTileTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid ContractId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PartyId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid AccountId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>A fixed "today" so the past-term rendering never depends on the wall clock.</summary>
    private static readonly DateTime Today = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private static ExistingContractParty Party(
        ContractPartyRole role = ContractPartyRole.Employer,
        DateTime? from = null,
        DateTime? to = null,
        bool resolved = true) => new()
        {
            ContractPartyId = PartyId,
            ContractId = ContractId,
            Kind = ContractPartyKind.Account,
            Account = resolved
                ? new ContractAccountReference
                {
                    AccountId = AccountId,
                    Name = "Everyday Checking",
                    // A real type, so the caption assertion has something to read: the tile states the
                    // ACCOUNT's own type, and the enum's zero member would state nothing.
                    Type = AccountType.CheckingAccount,
                }
                : null,
            Role = role,
            FromDate = from,
            ToDate = to,
        };

    private static IRenderedComponent<DetailHost> Render(
        ExistingContractParty party, bool canWrite = true, bool canView = false,
        bool? canViewAccounts = null, bool? canViewContacts = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(Mock.Of<Odyssey.ApiClient.Resources.IContractsApiClient>());
        ctx.Services.AddSingleton<TimeProvider>(new FixedTime(Today));
        // The Documents section renders in the same detail view, and its files table resolves the
        // issuer options and the contacts.create claim for its Edit dialog (issue #146).
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton(Mock.Of<IContactQuickCreate>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedOut());

        return ctx.Render<DetailHost>(p => p
            .Add(h => h.Party, party)
            .Add(h => h.CanWrite, canWrite)
            .Add(h => h.CanViewAccounts, canViewAccounts ?? canView)
            .Add(h => h.CanViewContacts, canViewContacts ?? canView));
    }

    /// <summary>The same fixture with SEVERAL parties, for the cases about tile order.</summary>
    private static IRenderedComponent<DetailHost> RenderMany(List<ExistingContractParty> parties)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(Mock.Of<Odyssey.ApiClient.Resources.IContractsApiClient>());
        ctx.Services.AddSingleton<TimeProvider>(new FixedTime(Today));
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton(Mock.Of<IContactQuickCreate>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedOut());

        return ctx.Render<DetailHost>(p => p
            .Add(h => h.Party, parties[0])
            .Add(h => h.Parties, parties)
            .Add(h => h.CanWrite, true));
    }

    /// <summary>The role WORDS in render order.</summary>
    private static List<string> RoleWords(IRenderedComponent<DetailHost> cut) =>
        [.. cut.FindAll(".con-role > span").Select(r => r.TextContent.Trim())];

    /// <summary>A party with its own id, so several can sit on one contract without colliding.</summary>
    private static ExistingContractParty PartyWith(Guid id, ContractPartyRole role) => new()
    {
        ContractPartyId = id,
        ContractId = ContractId,
        Kind = ContractPartyKind.Account,
        Account = new ContractAccountReference
        {
            AccountId = AccountId,
            Name = "Everyday Checking",
            Type = AccountType.CheckingAccount,
        },
        Role = role,
    };

    // ── Object parties (issue #169) ──────────────────────────────────────────

    /// <summary>
    /// An OBJECT role — the thing the agreement is about rather than a side of it — takes the
    /// <c>object</c> class on the tile and the overline, and its overline is the role WORD alone: the
    /// design system retired the outward-arrow mark it once carried, so no glyph rides beside it.
    /// </summary>
    [Theory]
    [InlineData(ContractPartyRole.Object)]
    [InlineData(ContractPartyRole.Property)]
    [InlineData(ContractPartyRole.Collateral)]
    public void An_object_role_takes_the_object_class_and_no_glyph(ContractPartyRole role)
    {
        var cut = Render(Party(role));

        Assert.Contains("object", cut.Find(".con-party-tile").ClassName, StringComparison.Ordinal);
        var overline = cut.Find(".con-role");
        Assert.Contains("object", overline.ClassName, StringComparison.Ordinal);
        Assert.Empty(overline.QuerySelectorAll(".material-icons"));
        Assert.Equal(PartyRoleLabel.For(role), overline.TextContent.Trim());
    }

    /// <summary>
    /// The negative half: a party that IS a side of the agreement does not take the class. Without
    /// this, a rule that classed every tile would satisfy the case above while saying nothing.
    /// </summary>
    [Theory]
    [InlineData(ContractPartyRole.Employer)]
    [InlineData(ContractPartyRole.Guarantor)]
    [InlineData(ContractPartyRole.Other)]
    public void A_counterparty_role_carries_no_object_mark(ContractPartyRole role)
    {
        var cut = Render(Party(role));

        Assert.DoesNotContain("object", cut.Find(".con-party-tile").ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("object", cut.Find(".con-role").ClassName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Object parties LEAD the section, and the counterparties keep the order the server sent. The
    /// thing contracted over is what a reader scans a tenancy or a loan for, and it is the one tile
    /// that is not a counterparty.
    /// </summary>
    [Fact]
    public void Object_parties_lead_the_section_and_the_rest_keep_their_order()
    {
        var cut = RenderMany(
        [
            PartyWith(Guid.Parse("11111111-0000-0000-0000-000000000001"), ContractPartyRole.Landlord),
            PartyWith(Guid.Parse("11111111-0000-0000-0000-000000000002"), ContractPartyRole.Tenant),
            PartyWith(Guid.Parse("11111111-0000-0000-0000-000000000003"), ContractPartyRole.Property),
            PartyWith(Guid.Parse("11111111-0000-0000-0000-000000000004"), ContractPartyRole.Broker),
        ]);

        Assert.Equal(
            ["Property", "Landlord", "Tenant", "Broker"],
            RoleWords(cut));
    }

    /// <summary>
    /// A role this build cannot name is NOT guessed into the object group: it renders as an
    /// unrecognised role and stays where it was. Classifying an ordinal newer than this client would
    /// assert a grouping the client has no basis for, and would put an unnameable tile at the top of
    /// the section.
    /// </summary>
    [Fact]
    public void An_unrecognised_role_is_not_treated_as_an_object_party()
    {
        var cut = RenderMany(
        [
            PartyWith(Guid.Parse("22222222-0000-0000-0000-000000000001"), ContractPartyRole.Landlord),
            PartyWith(Guid.Parse("22222222-0000-0000-0000-000000000002"), (ContractPartyRole)int.MaxValue),
        ]);

        Assert.Equal(
            ["Landlord", "Unrecognised role"],
            RoleWords(cut));
    }

    /// <summary>AC 1 — a stated role is the overline; the record's own TYPE is the caption.</summary>
    /// <remarks>
    /// The caption states the type alone. The party KIND is already carried by the tile's icon and by
    /// the record it names, so a <c>"Account · Checking"</c> caption spends its one line restating the
    /// glyph — the design system's <c>PartyTile</c> reads <c>typeLabel || kindLabel</c>, and the policy
    /// party tiles already pass the bare type label. The negative half is the assertion that matters:
    /// the separator is what a reintroduced prefix would bring back.
    /// </remarks>
    [Fact]
    public void A_stated_role_leads_the_tile_and_the_record_type_is_the_caption()
    {
        var cut = Render(Party());

        Assert.Equal("Employer", cut.Find(".con-role").TextContent.Trim());
        Assert.DoesNotContain("unset", cut.Find(".con-role").ClassName, StringComparison.Ordinal);
        Assert.Equal("Checking", cut.Find(".con-party-tile .odc-infotile-foot").TextContent.Trim());
        Assert.DoesNotContain("Account ·", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The kind is the caption's FALLBACK, not its prefix: a party whose target did not resolve has no
    /// type to state, and an empty caption would leave the tile saying nothing about what it links.
    /// </summary>
    [Fact]
    public void An_unresolved_target_falls_back_to_the_kind_for_its_caption()
    {
        var cut = Render(Party(resolved: false));

        Assert.Equal("Party", cut.Find(".con-party-tile .odc-infotile-foot").TextContent.Trim());
    }

    /// <summary>
    /// AC 2 — a party whose role this build cannot name reads as a stated ABSENCE and carries no term
    /// line. Since issue #157 that is the version-skew case alone: <c>Unspecified</c> is retired, so
    /// the only unnameable ordinals are a retired one the migration missed and a member newer than
    /// this client. Either way the state is in TEXT rather than colour alone.
    /// </summary>
    [Fact]
    public void An_unnameable_role_reads_as_an_absence_and_shows_no_term_line()
    {
        var cut = Render(Party((ContractPartyRole)int.MaxValue));

        var role = cut.Find(".con-role");
        Assert.Equal("Unrecognised role", role.TextContent.Trim());
        Assert.Contains("unset", role.ClassName, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".con-term"));
    }

    /// <summary>The default term is both dates null and needs no line; any other term gets one.</summary>
    [Fact]
    public void A_non_default_term_gets_its_own_line()
    {
        var cut = Render(Party(from: new DateTime(2026, 3, 1), to: new DateTime(2026, 12, 31)));

        Assert.Equal("Mar 1 – Dec 31 2026", cut.Find(".con-term").TextContent.Trim());
    }

    /// <summary>
    /// A party whose term closed before today is still a party of record, drawn quieter — AND says so
    /// in text.
    /// </summary>
    /// <remarks>
    /// The strike-through and the muted tile tone are presentation: the term text reads identically
    /// whether the party is still in the role or has left it, so on their own a screen-reader user
    /// hears no difference (WCAG 1.3.1 / 1.4.1, both Level A). The <c>sr-only</c> assertion is the
    /// half that matters — a regression that drops the span while leaving the <c>past</c> class is
    /// invisible in a diff and invisible on screen, and a class-only test would pass straight through
    /// it.
    /// </remarks>
    [Fact]
    public void A_closed_past_term_is_marked_as_past_and_says_so_in_text()
    {
        var cut = Render(Party(to: Today.AddDays(-1)));

        Assert.Contains("past", cut.Find(".con-term").ClassName, StringComparison.Ordinal);
        Assert.Contains("ended", cut.Find(".con-term .sr-only").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The negative counterpart: a term still running carries NO past marker and NO screen-reader
    /// cue. Without it, an implementation that announced "(ended)" on every term would satisfy the
    /// case above while telling every reader the opposite of the truth.
    /// </summary>
    [Fact]
    public void A_term_still_running_carries_no_past_marker_and_no_screen_reader_cue()
    {
        var cut = Render(Party(from: Today.AddDays(-10), to: Today.AddDays(10)));

        Assert.DoesNotContain("past", cut.Find(".con-term").ClassName, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".con-term .sr-only"));
    }

    /// <summary>
    /// AC 6 — an ordinal outside this build's registry renders honestly, offers Detach and WITHHOLDS
    /// Edit party, with no exception thrown and the rest of the tile still rendering.
    /// </summary>
    [Fact]
    public void An_unknown_role_withholds_edit_but_keeps_detach()
    {
        var cut = Render(Party((ContractPartyRole)int.MaxValue));

        Assert.Equal("Unrecognised role", cut.Find(".con-role").TextContent.Trim());
        Assert.Contains("Everyday Checking", cut.Markup, StringComparison.Ordinal);

        var labels = MenuLabels(cut);
        Assert.DoesNotContain("Edit party", labels);
        Assert.Contains("Detach party", labels);
    }

    /// <summary>
    /// A role the type REJECTS is still editable — that party is precisely the one the edit dialog
    /// exists to correct, and withholding the affordance would strand it. Only the unnameable case
    /// (below) loses the edit, because a client that cannot name a role cannot round-trip it through
    /// a full-replacement PUT without silently rewriting it.
    /// </summary>
    [Fact]
    public void A_role_the_contract_type_rejects_is_still_editable()
    {
        var cut = Render(Party(ContractPartyRole.Landlord));

        Assert.Contains("Edit party", MenuLabels(cut));
    }

    /// <summary>
    /// AC 5 — the ⋯ menu is in the markup for every party, always, and the old hover-revealed detach
    /// icon is gone. A menu that appears only on hover is invisible to a touch user.
    /// </summary>
    [Fact]
    public void The_menu_is_always_rendered_and_the_hover_detach_icon_is_gone()
    {
        var cut = Render(Party());

        Assert.Single(cut.FindAll(".con-tile-menu"));
        Assert.Empty(cut.FindAll(".con-party-detach"));
        Assert.Contains("Actions for Employer Everyday Checking", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 12 — the menu's accessible name goes through <c>PartyRoleLabel</c>, so a screen-reader user
    /// is never read the sentinel a sighted one is deliberately never shown.
    /// </summary>
    [Fact]
    public void The_menus_accessible_name_uses_the_same_role_words_the_tile_shows()
    {
        var cut = Render(Party((ContractPartyRole)int.MaxValue));

        Assert.Contains("Actions for Unrecognised role Everyday Checking", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Unspecified", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Archiving does not lock a party.</b> The server refuses no party write on an archived
    /// contract — archival hides it from the default list, it does not freeze it — so Edit and
    /// Detach are both live, and neither carries a disabled state or a reason for one.
    ///
    /// <para>
    /// The detail view no longer takes an archive flag at all, so the archived case is the same
    /// render as any other; what this pins is that nothing re-introduces a gate under a different
    /// name.
    /// </para>
    /// </summary>
    [Fact]
    public void Party_writes_are_live_whatever_the_contracts_archive_state()
    {
        var cut = Render(Party());
        var labels = MenuLabels(cut);

        Assert.Contains("Edit party", labels);
        Assert.Contains("Detach party", labels);
        Assert.DoesNotContain("Unarchive", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[aria-disabled='true']"));
    }

    /// <summary>
    /// AC 17 — a caller without contracts.update sees the tiles in full, with no ⋯ menu at all and no
    /// broken or empty affordance in its place. The role and term still read.
    /// </summary>
    [Fact]
    public void Read_only_tiles_carry_no_menu_but_still_show_the_role_and_term()
    {
        var cut = Render(Party(from: new DateTime(2026, 3, 1)), canWrite: false);

        Assert.Empty(cut.FindAll(".con-tile-menu"));
        Assert.Equal("Employer", cut.Find(".con-role").TextContent.Trim());
        Assert.Equal("from Mar 1 2026", cut.Find(".con-term").TextContent.Trim());
    }

    /// <summary>
    /// View opens the party's own page — Accounts for an account party — and leads the menu, as in
    /// the design system's <c>PartyTile</c>.
    /// </summary>
    [Fact]
    public void View_leads_the_menu_and_opens_the_accounts_page_for_an_account_party()
    {
        var cut = Render(Party(), canView: true);

        var labels = MenuLabels(cut);
        Assert.Equal("View", labels[0]);

        cut.FindAll(".mud-menu-item").First(i => i.TextContent.Contains("View", StringComparison.Ordinal)).Click();
        var nav = cut.Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("/accounts", nav.Uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// View is navigation, not a write: a read-only caller gets a menu holding it alone. Before
    /// View existed such a caller got no menu at all (the case above), and the write items stay
    /// withheld from them.
    /// </summary>
    [Fact]
    public void A_read_only_caller_who_may_open_the_target_gets_view_and_nothing_else()
    {
        var cut = Render(Party(), canWrite: false, canView: true);

        Assert.Equal(["View"], MenuLabels(cut));
    }

    /// <summary>
    /// View is ABSENT — not dimmed — when the caller may not open the target's page, and for a party
    /// whose target did not resolve, which names no record to open.
    /// </summary>
    [Fact]
    public void View_is_absent_without_the_targets_read_claim_or_a_resolved_target()
    {
        Assert.DoesNotContain("View", MenuLabels(Render(Party(), canView: false)));
        Assert.DoesNotContain("View", MenuLabels(Render(Party(resolved: false), canView: true)));
    }

    /// <summary>A contact ("institution") party, for the View cases that turn on the party's KIND.</summary>
    private static ExistingContractParty ContactParty() => new()
    {
        ContractPartyId = PartyId,
        ContractId = ContractId,
        Kind = ContractPartyKind.Institution,
        Institution = new ContractContactReference
        {
            ContactId = AccountId,
            Name = "First National Bank",
            Type = Odyssey.Dtos.ContactType.Organization,
        },
        Role = ContractPartyRole.Employer,
    };

    /// <summary>A contact party's View opens the Contacts page — the other arm of the route choice.</summary>
    [Fact]
    public void View_opens_the_contacts_page_for_a_contact_party()
    {
        var cut = Render(ContactParty(), canViewContacts: true);

        Assert.Equal("View", MenuLabels(cut)[0]);
        cut.FindAll(".mud-menu-item").First(i => i.TextContent.Contains("View", StringComparison.Ordinal)).Click();
        Assert.EndsWith("/contacts", cut.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each kind is gated on ITS OWN target's claim: accounts.read opens nothing for a contact party,
    /// and contacts.read opens nothing for an account party. A swapped claim check fails here.
    /// </summary>
    [Fact]
    public void View_is_gated_on_the_claim_for_the_partys_own_kind()
    {
        Assert.DoesNotContain("View", MenuLabels(Render(ContactParty(), canViewAccounts: true, canViewContacts: false)));
        Assert.DoesNotContain("View", MenuLabels(Render(Party(), canViewAccounts: false, canViewContacts: true)));
        Assert.Contains("View", MenuLabels(Render(Party(), canViewAccounts: true, canViewContacts: false)));
    }

    /// <summary>
    /// A party whose target did not resolve still shows its ROLE — a top-level field that does not
    /// depend on the reference — and keeps Copy ID and Detach, which need only the link.
    /// </summary>
    [Fact]
    public void An_unresolved_target_keeps_its_role_and_the_link_only_actions()
    {
        var cut = Render(Party(resolved: false));

        Assert.Equal("Employer", cut.Find(".con-role").TextContent.Trim());

        var labels = MenuLabels(cut);
        Assert.DoesNotContain("Edit party", labels);
        Assert.DoesNotContain("Copy name", labels);
        Assert.Contains("Copy ID", labels);
        Assert.Contains("Detach party", labels);
    }

    /// <summary>
    /// The wrapper's id is the anchor a detach returns focus to (WCAG 2.4.3): the tile that had focus
    /// is destroyed by the re-render, so the neighbour is re-found by id afterwards. It has to stay
    /// per-party and stable, or the focus return silently lands nowhere.
    /// </summary>
    [Fact]
    public void The_menu_carries_a_stable_per_party_focus_anchor()
    {
        var cut = Render(Party());

        Assert.Equal($"con-party-{PartyId}", cut.Find(".con-tile-menu").GetAttribute("id"));
    }

    private static List<string> MenuLabels(IRenderedComponent<DetailHost> cut)
    {
        cut.Find(".con-tile-menu button").Click();
        cut.WaitForElement("div.mud-menu-item");
        // The item's own body span, not the whole item: OdsMenu renders the leading and trailing
        // glyphs as aria-hidden Material Icons LIGATURES, so an item's raw TextContent reads
        // "link_offDetach party" — the ligature text, which no user ever sees or hears.
        return [.. cut.FindAll(".mud-menu-item .odc-menu-item-body > span:first-child")
            .Select(node => node.TextContent.Trim())
            .Where(text => text.Length > 0)];
    }

    private sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    /// <summary>
    /// The detail view next to a MudPopoverProvider. MudBlazor portals an OPEN menu into that
    /// provider, so without one in the same tree the items render nowhere and every menu assertion
    /// would pass vacuously against an empty popover.
    /// </summary>
    private sealed class SignedOut : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    public sealed class DetailHost : ComponentBase
    {
        [Parameter] public ExistingContractParty Party { get; set; } = default!;

        /// <summary>
        /// Several parties, for the cases about ORDER. Unset renders <see cref="Party"/> alone, which
        /// is what every single-tile case wants.
        /// </summary>
        [Parameter] public List<ExistingContractParty>? Parties { get; set; }

        [Parameter] public bool CanWrite { get; set; }

        /// <summary>Stands in for accounts.read.</summary>
        [Parameter] public bool CanViewAccounts { get; set; }

        /// <summary>Stands in for contacts.read.</summary>
        [Parameter] public bool CanViewContacts { get; set; }

        public List<ExistingContractParty> Edited { get; } = [];

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<ContractDetailView>(1);
            builder.AddComponentParameter(2, nameof(ContractDetailView.Contract), new ExistingContract
            {
                ContractId = ContractId,
                Name = "Employment agreement",
                CreatedAtUtc = Today.AddYears(-1),
                Parties = Parties ?? [Party],
            });
            builder.AddComponentParameter(3, nameof(ContractDetailView.CanWrite), CanWrite);
            builder.AddComponentParameter(5, nameof(ContractDetailView.CanViewAccounts), CanViewAccounts);
            builder.AddComponentParameter(6, nameof(ContractDetailView.CanViewContacts), CanViewContacts);
            if (CanWrite)
            {
                builder.AddComponentParameter(4, nameof(ContractDetailView.OnEditParty),
                    EventCallback.Factory.Create<ExistingContractParty>(this, Edited.Add));
            }
            builder.CloseComponent();
        }
    }
}
