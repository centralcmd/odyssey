using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Application;
using Odyssey.Client.Components;
using Odyssey.Client.Pages;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Credentials group on <c>/settings</c> (issue #444 §16 ACs 16, 35, 39–41), plus the source-lints
/// that pin the two properties no render can prove.
/// </summary>
public class SecretSettingsSurfaceTests : IDisposable
{
    /// <summary>
    /// Restores the page's browser-check seam after every test. It is <c>internal static</c>, so a
    /// test that moved it and did not put it back would change what every later test sees — the
    /// process-wide-versus-per-instance hazard CLAUDE.md records for <c>RequestCapCeilings</c>.
    /// </summary>
    public void Dispose() => Settings.InteractiveCheck = static () => OperatingSystem.IsBrowser();

    private static string PageMarkup() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor"));

    private static string PageCode() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.cs"));

    /// <summary>
    /// Strips <c>//</c>, <c>/* … */</c> and Razor's <c>@* … *@</c> comments before a lint reads the
    /// text. Without it these lints are unusable in this codebase: the comment explaining WHY a
    /// construct is forbidden necessarily names the construct, so the lint would fail on its own
    /// rationale and the only way to keep it green would be to stop writing the rationale down.
    /// </summary>
    private static string WithoutComments(string source)
    {
        source = System.Text.RegularExpressions.Regex.Replace(
            source, @"@\*.*?\*@", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        source = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(source, @"//[^\n]*", string.Empty);
    }

    // ── AC 16 — the persisted payload ───────────────────────────────────────────────────────────

    /// <summary>
    /// AC 16. <c>/settings</c> persists its UI state SERVER-side: <c>PageStateService</c> writes
    /// through the API into the user-preferences table on <c>OdysseyContext</c> — the very database
    /// this feature exists to keep plaintext credentials out of, under a <c>UserId</c> with a real FK,
    /// replicated into every backup.
    ///
    /// <para>
    /// A source-lint rather than reflection over <c>SettingsPageState</c>: that type is a
    /// <c>private</c> nested class, and <c>InternalsVisibleTo</c> does not reach private members, so
    /// the reflective form would not compile. The lint pins <c>BuildPageState()</c> instead — the
    /// method whose return value IS the persisted payload — which closes the actual hole.
    /// </para>
    /// </summary>
    [Fact]
    public void BuildPageState_PersistsOnlyTheSearchSectionFlag()
    {
        var code = PageCode();
        var start = code.IndexOf("private SettingsPageState BuildPageState()", StringComparison.Ordinal);
        Assert.True(start >= 0, "BuildPageState was renamed; this lint no longer pins anything.");

        var end = code.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(end > start, "BuildPageState is no longer an object initializer; re-pin this lint.");

        // ONE assignment, and it is that one. Anything else added here is written to durable server
        // storage, which is what makes a credential-shaped member catastrophic rather than untidy.
        var assignments = code[start..end]
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains('=', StringComparison.Ordinal)
                && !line.Contains("=>", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(["SearchOpen = _searchOpen,"], assignments);
    }

    /// <summary>
    /// The search box is EXCLUDED from persistence (issue #444 §10). The lint above pins the payload's
    /// shape but cannot cover a value the user TYPES into a field that is legitimately persisted — and
    /// pasting a credential into a search box named after that credential is a plausible slip on this
    /// page in particular.
    /// </summary>
    [Fact]
    public void TheSearchString_IsNotPersisted()
    {
        var code = WithoutComments(PageCode());

        Assert.DoesNotContain("Search = _search", code, StringComparison.Ordinal);
        Assert.DoesNotContain("state.Search;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("state.Search ", code, StringComparison.Ordinal);

        // …and a change of search text does not queue a save at all.
        var handler = code[code.IndexOf("private void OnSearchChanged", StringComparison.Ordinal)..];
        handler = handler[..handler.IndexOf('\n')];
        Assert.DoesNotContain("PersistPageState", handler, StringComparison.Ordinal);
    }

    // ── AC 39 — @key on both row loops ──────────────────────────────────────────────────────────

    /// <summary>
    /// AC 39. Every per-setting loop on the page carries <c>@key</c>. Without it Blazor re-matches
    /// entries POSITIONALLY when the search filter changes the list — harmless only while every
    /// entry's draft state lives in the page's key-indexed dictionaries. A secret row is the first
    /// one holding state in a component-local field, which turns positional re-matching into a
    /// credential-crossing bug.
    ///
    /// <para>
    /// Asserted on the two <c>OdsSettingField</c> fragments rather than on the loop body: the
    /// plaintext settings render through <c>@SettingField(item)</c>, and a <c>RenderFragment</c>
    /// invocation cannot carry a key — so the key belongs on the component each fragment renders.
    /// </para>
    /// </summary>
    [Fact]
    public void EverySettingsLoop_CarriesAKey()
    {
        var markup = PageMarkup();

        // Both field shapes — the notched frame and the sibling tile — plus the secret row.
        Assert.Equal(2, CountOccurrences(markup, "<OdsSettingField\n        @key=\"item.Key\""));
        Assert.Contains("<OdsSecretSettingField @key=\"item.Key\"", markup, StringComparison.Ordinal);

        // …and nothing renders an OdsSettingField without one.
        Assert.Equal(
            CountOccurrences(markup, "<OdsSettingField"),
            CountOccurrences(markup, "<OdsSettingField\n        @key=\"item.Key\""));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    // ── AC 40 — the group is the INTERSECTION of catalogue and endpoint ─────────────────────────

    /// <summary>
    /// AC 40. The Credentials group SELF-HIDES when the status endpoint returns no keys — which is what
    /// a Production deployment sees, because the server filters the non-Production key out of its
    /// registry.
    ///
    /// <para>
    /// The client cannot make that decision itself: <c>Odyssey.Client</c> never reads
    /// <c>HostEnvironment.Environment</c>, nothing sets the <c>blazor-environment</c> header, and a
    /// WASM-side environment check would be wrong in dev AND Production. The catalogue is necessarily
    /// non-empty (the status DTO carries no title, description or icon), so the intersection is the
    /// only correct mechanism.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenTheEndpointReturnsNoKeys_NoCredentialFieldRenders()
    {
        await using var ctx = NewPageContext(secrets: []);

        var page = ctx.Render<Settings>();

        Assert.Empty(page.FindComponents<OdsSecretSettingField>());
    }

    /// <summary>AC 40, the other half: the field appears when the endpoint reports the key.</summary>
    [Fact]
    public async Task WhenTheEndpointReturnsTheTestKey_ItsFieldRenders()
    {
        await using var ctx = NewPageContext(secrets:
        [
            new SecretSettingStatusDto { Key = SecretSettingKeys.DiagnosticsSelfTest, State = SecretSettingState.NotSet },
        ]);

        var page = ctx.Render<Settings>();

        var rows = page.FindComponents<OdsSecretSettingField>();
        Assert.Single(rows);
        Assert.Equal(SecretSettingKeys.DiagnosticsSelfTest, rows[0].Instance.SecretKey);
        Assert.Contains("Never set.", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key the server reports but the client catalogue does not describe renders nothing: the
    /// intersection cuts both ways, and a row with no title, description or icon would be unusable.
    /// </summary>
    [Fact]
    public async Task AnUncataloguedKey_RendersNoRow()
    {
        await using var ctx = NewPageContext(secrets:
        [
            new SecretSettingStatusDto { Key = "SomeFutureCredential", State = SecretSettingState.Set },
        ]);

        var page = ctx.Render<Settings>();

        Assert.Empty(page.FindComponents<OdsSecretSettingField>());
    }

    /// <summary>
    /// A caller without <c>system-settings.security.update</c> sees no credential field and the page
    /// never calls the status endpoint — they are gated on the write claim, since there is nothing a
    /// read-only caller could do with one.
    /// </summary>
    [Fact]
    public async Task WithoutTheWriteClaim_NoFieldRendersAndTheEndpointIsNotCalled()
    {
        var secrets = new Mock<ISecretSettingsApiClient>(MockBehavior.Strict);

        await using var ctx = NewPageContext(
            secrets: [],
            claims: [PermissionClaims.SystemSettingsRead],
            secretClient: secrets.Object);

        var page = ctx.Render<Settings>();

        Assert.Empty(page.FindComponents<OdsSecretSettingField>());
        secrets.Verify(client => client.GetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Issue #445 — the five rows, their placement and the credential signal ──────────────────

    /// <summary>
    /// Every credential names an EXISTING section (design update 2f61476b). Secrets live in their
    /// subject cards now, so the group is a join key rather than a heading — and a typo in it would
    /// drop the row off the page entirely rather than misplace it, silently, because
    /// <c>SecretsIn</c> simply matches nothing.
    /// </summary>
    [Fact]
    public void EverySecretNamesASectionThatExists()
    {
        var groups = Settings.Sections.Select(section => section.Group).ToHashSet(StringComparer.Ordinal);

        Assert.All(Settings.SecretCatalogue, item =>
            Assert.True(groups.Contains(item.Group),
                $"{item.Key} names section '{item.Group}', which does not exist."));
    }

    /// <summary>
    /// The placement the design update specifies, pinned by key: each credential sits in the card
    /// that answers questions about it. This is a design decision with reasons — the API key beside
    /// the destination it is sent to and the switch that decides whether anything is sent; the relay
    /// pair and the hash key beside the from address and the send limits they authenticate and count;
    /// the pseudonymisation secret beside the export carrying the records it pseudonymises — so it is
    /// asserted rather than left to whoever next edits the catalogue.
    /// </summary>
    [Theory]
    [InlineData(SecretSettingKeys.FileAnalysisApiKey, "File analysis")]
    [InlineData(SecretSettingKeys.EmailUsername, "Email")]
    [InlineData(SecretSettingKeys.EmailPassword, "Email")]
    [InlineData(SecretSettingKeys.EmailRecipientHashKey, "Email")]
    [InlineData(SecretSettingKeys.LegalPseudonymizationSecret, "Data")]
    public void EachCredential_SitsInItsSubjectCard(string key, string group)
    {
        Assert.Equal(group, Settings.SecretCatalogue.Single(item => item.Key == key).Group);
    }

    /// <summary>
    /// There is no Credentials group any more, and nothing renders one. The rows are found by TYPE
    /// wherever they sit, which is what lets grouping stay a presentation choice.
    /// </summary>
    [Fact]
    public async Task NoCredentialsGroupIsRendered()
    {
        await using var ctx = NewPageContext(secrets:
            [.. SecretSettingKeys.AllKeys.Select(key =>
                new SecretSettingStatusDto { Key = key, State = SecretSettingState.NotSet })]);

        var page = ctx.Render<Settings>();

        Assert.NotEmpty(page.FindComponents<OdsSecretSettingField>());
        Assert.DoesNotContain("Credentials", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A search matching only a credential keeps its section on the page. Without this the term
    /// "SMTP password" would filter every plaintext row out of Email, drop the section, and answer
    /// "no settings match" for a field that is right there.
    /// </summary>
    [Fact]
    public async Task ASearchMatchingOnlyACredential_KeepsItsSectionOnThePage()
    {
        await using var ctx = NewPageContext(secrets:
        [
            new SecretSettingStatusDto { Key = SecretSettingKeys.EmailPassword, State = SecretSettingState.NotSet },
        ]);

        var page = ctx.Render<Settings>();

        // The page's search box: OdsSearchField wraps MudTextField with InputType.Search, so the
        // rendered control is a native search input rather than a class this test can name.
        page.Find(".odc-searchfield input").Input("SMTP password");

        var field = Assert.Single(page.FindComponents<OdsSecretSettingField>());
        Assert.Equal(SecretSettingKeys.EmailPassword, field.Instance.SecretKey);
        Assert.DoesNotContain("No settings match", page.Markup, StringComparison.Ordinal);
    }


    /// <summary>
    /// The five migrated credentials render as rows, each with the title, description, consequence and
    /// derivation-key classification the status endpoint deliberately does not carry.
    /// </summary>
    [Fact]
    public async Task TheFiveMigratedCredentials_EachRenderAsARow()
    {
        string[] keys =
        [
            SecretSettingKeys.FileAnalysisApiKey,
            SecretSettingKeys.EmailUsername,
            SecretSettingKeys.EmailPassword,
            SecretSettingKeys.EmailRecipientHashKey,
            SecretSettingKeys.LegalPseudonymizationSecret,
        ];

        await using var ctx = NewPageContext(secrets:
            [.. keys.Select(key => new SecretSettingStatusDto { Key = key, State = SecretSettingState.NotSet })]);

        var page = ctx.Render<Settings>();
        var rows = page.FindComponents<OdsSecretSettingField>();

        // Set equality, not sequence: the fields follow their SECTIONS now, so the render order is
        // the page's section order (Data precedes File analysis and Email), not the catalogue's.
        // Placement is asserted by key in EachCredential_SitsInItsSubjectCard.
        Assert.Equal(
            keys.OrderBy(key => key, StringComparer.Ordinal),
            rows.Select(row => row.Instance.SecretKey).OrderBy(key => key, StringComparer.Ordinal));
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Instance.Consequence)));
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Instance.Affects)));

        // The two derivation keys, and only those two — the classification drives the Clear
        // confirmation's copy, so getting it backwards tells an administrator a permanent loss is
        // recoverable.
        Assert.Equal(
            new[] { SecretSettingKeys.EmailRecipientHashKey, SecretSettingKeys.LegalPseudonymizationSecret }
                .OrderBy(key => key, StringComparer.Ordinal),
            rows.Where(row => row.Instance.IsDerivationKey)
                .Select(row => row.Instance.SecretKey)
                .OrderBy(key => key, StringComparer.Ordinal));
    }

    /// <summary>
    /// The section badge counts every field the card renders, credentials included.
    ///
    /// <para>
    /// It summed the plaintext items alone while the credentials lived in a group of their own; once
    /// they moved into their subject cards that made the Email card announce "5 settings" above eight
    /// fields, which reads as a filter that has silently dropped rows rather than as a card that also
    /// holds credentials. The unsaved badge beside it stays secrets-exempt on purpose — a credential
    /// writes on its own button and never contributes to the page's Save.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSectionCountBadge_CountsTheCredentialsInTheCard()
    {
        var group = Settings.SecretCatalogue
            .Single(item => item.Key == SecretSettingKeys.EmailPassword).Group;

        await using var ctx = NewPageContext(secrets:
        [
            new SecretSettingStatusDto { Key = SecretSettingKeys.EmailUsername, State = SecretSettingState.NotSet },
            new SecretSettingStatusDto { Key = SecretSettingKeys.EmailPassword, State = SecretSettingState.NotSet },
        ]);

        var page = ctx.Render<Settings>();

        var card = page.Find($"section.ss-sect[aria-labelledby='ss-grp-{group}']");

        // Against what the card ACTUALLY renders — every field, plaintext or credential, carries one
        // .odc-sfield-label — rather than against a recount of the catalogue, which would restate the
        // page's own arithmetic instead of checking it.
        var fields = card.QuerySelectorAll(".odc-sfield-label").Length;
        Assert.Equal($"{fields} settings", card.QuerySelector(".ss-sect-count")!.TextContent.Trim());

        // …and the card really is holding the two credentials, so the line above is not agreeing with
        // itself over a card that happens to have none.
        Assert.Equal(2, page.FindComponents<OdsSecretSettingField>().Count);
    }

    /// <summary>
    /// An unreadable credential raises the page-header signal, naming what is broken.
    ///
    /// <para>
    /// The header rollup and NOT <c>BlockingProblems</c>, which is the summary beside a disabled
    /// <b>Save changes</b>: an unreadable credential is an outage whose cause and fix are both outside
    /// a Save, so putting it there would make Save look blocked by something Save cannot fix.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnUnreadableCredential_RaisesTheHeaderSignal_AndNotTheSaveSummary()
    {
        await using var ctx = NewPageContext(secrets:
        [
            new SecretSettingStatusDto
            {
                Key = SecretSettingKeys.EmailPassword, State = SecretSettingState.Unreadable,
            },
        ]);

        var page = ctx.Render<Settings>();
        var header = page.FindComponent<PageHeader>();

        var problem = Assert.Single(header.Instance.Problems!);
        Assert.Equal(PageHeaderSeverity.Error, problem.Severity);
        Assert.Contains("SMTP password", problem.Lead!, StringComparison.Ordinal);
        Assert.Contains("cannot be decrypted", problem.Lead!, StringComparison.Ordinal);
        Assert.Contains("Transactional mail is not sending", problem.Message, StringComparison.Ordinal);

        // WHICH card — the clue that replaced the Credentials group. Without it the rollup names a
        // broken credential on a page of eleven cards and leaves the reader to hunt for it, and the
        // "Fix" jump is the only way to find the row.
        Assert.Equal(
            $"In {Settings.SecretCatalogue.Single(item => item.Key == SecretSettingKeys.EmailPassword).Group}.",
            problem.Where);

        // …and it can be jumped to, which needs the row's anchor to actually be in the document.
        Assert.True(problem.OnView.HasDelegate);
        Assert.NotNull(page.Find("#" + Settings.SecretAnchorId(SecretSettingKeys.EmailPassword)));
    }

    /// <summary>A healthy set of rows raises no signal at all — the rollup must not become ambient.</summary>
    [Fact]
    public async Task ReadableCredentials_RaiseNoHeaderSignal()
    {
        await using var ctx = NewPageContext(secrets:
        [
            new SecretSettingStatusDto { Key = SecretSettingKeys.EmailPassword, State = SecretSettingState.Set },
            new SecretSettingStatusDto { Key = SecretSettingKeys.EmailUsername, State = SecretSettingState.NotSet },
        ]);

        var page = ctx.Render<Settings>();

        Assert.Null(page.FindComponent<PageHeader>().Instance.Problems);
    }

    // ── AC 35 + 41 — the two shared components stayed additive ─────────────────────────────────

    /// <summary>
    /// AC 35. <c>OdsTextInputField</c> renders <c>type="password"</c> when asked and <c>type="text"</c>
    /// by default — the parameter a first draft assumed already existed. The component hardcoded
    /// <c>type="text"</c> and exposed no password mode at all.
    /// </summary>
    [Theory]
    [InlineData(null, "text")]
    [InlineData("password", "password")]
    public async Task OdsTextInputField_RendersTheRequestedType(string? type, string expected)
    {
        await using var ctx = NewComponentContext();

        var field = ctx.Render<OdsTextInputField>(p =>
        {
            p.Add(f => f.Label, "A field");
            if (type is not null)
            {
                p.Add(f => f.Type, type);
            }
        });

        Assert.Equal(expected, field.Find("input.odc-input").GetAttribute("type"));
    }

    /// <summary>
    /// AC 35. The <c>Type</c> parameter stayed ADDITIVE: no existing <c>OdsTextInputField</c> consumer
    /// sets it, so every existing field renders exactly as before.
    ///
    /// <para>
    /// The other half of this lint — that no existing <c>OdsSettingRow</c> supplies the <c>Status</c>
    /// slot — is gone with the slot. #444 added it for the secret row alone; issue #445 reshaped that
    /// row onto the design system's SettingField, leaving <c>Status</c> with no consumer, so it was
    /// removed rather than left as dead API. <c>NoSettingRowConsumer_SuppliesAStatusSlot</c> below is
    /// what remains of the guard.
    /// </para>
    /// </summary>
    [Fact]
    public void TheTypeParameter_IsUsedOnlyByTheSecretField()
    {
        var offenders = ClientSource.SourceFiles()
            .Where(file => !Path.GetFileName(file).StartsWith("OdsSecretSettingField", StringComparison.Ordinal))
            .Where(file => !Path.GetFileName(file).StartsWith("OdsTextInputField", StringComparison.Ordinal))
            .Where(file => !Path.GetFileName(file).StartsWith("OdsSettingRow", StringComparison.Ordinal))
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("<OdsTextInputField", StringComparison.Ordinal)
                    && text.Contains("Type=\"", StringComparison.Ordinal);
            })
            .Select(ClientSource.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "An existing OdsTextInputField consumer sets Type: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The <c>Status</c> slot is gone from <c>OdsSettingRow</c>, and nothing reaches for it. Pinned so
    /// a future edit does not resurrect a slot with no definition behind it — the failure mode would be
    /// silent, since an unmatched component parameter is a compile error only for a typed component and
    /// this one would simply not render.
    /// </summary>
    [Fact]
    public void NoSettingRowConsumer_SuppliesAStatusSlot()
    {
        var settingRow = File.ReadAllText(
            Path.Combine(ClientSource.Root, "Components", "OdsSettingRow.razor"));
        Assert.DoesNotContain("public RenderFragment? Status", settingRow, StringComparison.Ordinal);

        var offenders = ClientSource.SourceFiles()
            .Where(file => File.ReadAllText(file).Contains("<OdsSettingRow", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("Status=\"", StringComparison.Ordinal))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "An OdsSettingRow consumer supplies a Status slot that no longer exists: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// AC 41's rule, re-pinned onto the design system's shape. The secret field's STATE never reaches
    /// the advisory band, whose contract is strictly non-blocking text about a cost — while
    /// <c>Unreadable</c> is a degraded state.
    ///
    /// <para>
    /// The original lint asserted the row supplied <c>Status</c> and no <c>Advisory</c>. Both halves
    /// are obsolete: the field is no longer built on <c>OdsSettingRow</c>, and it now has an advisory
    /// of its own. What survives — and is what the AC was actually protecting — is the CONDITION on
    /// that advisory: <c>NotSet</c> only, <c>Consequence</c> only, with the fault text built in a
    /// separate member that never feeds it. The rendered behaviour is covered by
    /// <c>OdsSecretSettingFieldTests.TheConsequenceAdvisory_RendersOnlyWhileTheRowIsUnset</c>; this
    /// pins the guard in source so it cannot be widened without a reader noticing.
    /// </para>
    /// </summary>
    [Fact]
    public void TheSecretField_AdvisesOnlyWhileUnset_AndNeverFromItsFaultState()
    {
        var source = WithoutComments(File.ReadAllText(
            Path.Combine(ClientSource.Root, "Components", "OdsSecretSettingField.razor.cs")));

        Assert.Contains(
            "State == SecretSettingState.NotSet && !string.IsNullOrWhiteSpace(Consequence) ? Consequence : null",
            source,
            StringComparison.Ordinal);

        Assert.Contains("private string? UnreadableMessage", source, StringComparison.Ordinal);
    }

    // ── AC 38 — the announcement seam ───────────────────────────────────────────────────────────

    /// <summary>
    /// AC 38. The component reaches for neither the page's <c>private</c> <c>Announce()</c> nor the
    /// page-hosted <c>OdsLiveAnnouncer</c> — it cannot, structurally, and the lint keeps a future edit
    /// from trying.
    /// </summary>
    [Fact]
    public void TheSecretRow_HostsNoLiveRegionAndCallsNoPageHelper()
    {
        foreach (var file in new[] { "OdsSecretSettingField.razor", "OdsSecretSettingField.razor.cs" })
        {
            var text = WithoutComments(
                File.ReadAllText(Path.Combine(ClientSource.Root, "Components", file)));
            Assert.DoesNotContain("OdsLiveAnnouncer", text, StringComparison.Ordinal);
            Assert.DoesNotContain("aria-live", text, StringComparison.Ordinal);
        }
    }

    // ── The catalogue join ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The client catalogue and the shared key list name the same keys. The catalogue is what supplies
    /// the title, description and icon the status DTO deliberately does not carry, so a key present on
    /// one side and not the other is a row that either cannot render or never appears.
    /// </summary>
    [Fact]
    public void TheClientCatalogue_MatchesTheDeclaredKeys()
    {
        Assert.Equal(
            SecretSettingKeys.AllKeys.OrderBy(key => key, StringComparer.Ordinal),
            Settings.SecretCatalogue.Select(item => item.Key).OrderBy(key => key, StringComparer.Ordinal));

        Assert.All(Settings.SecretCatalogue, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Title));
            Assert.False(string.IsNullOrWhiteSpace(item.Description));
            Assert.False(string.IsNullOrWhiteSpace(item.Icon));
        });
    }

    /// <summary>
    /// No client-side environment check. One would be wrong in dev AND Production, and it would
    /// duplicate an authority that belongs to the server's registry.
    /// </summary>
    [Fact]
    public void ThePage_MakesNoClientSideEnvironmentCheck()
    {
        foreach (var source in new[] { PageMarkup(), PageCode() })
        {
            var text = WithoutComments(source);
            Assert.DoesNotContain("HostEnvironment", text, StringComparison.Ordinal);
            Assert.DoesNotContain("IsProduction", text, StringComparison.Ordinal);
            Assert.DoesNotContain("blazor-environment", text, StringComparison.Ordinal);
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static BunitContext NewComponentContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    /// <summary>
    /// A rendering host for the whole page. It drives the page through its <c>internal</c>
    /// browser-check seam: <c>OnInitializedAsync</c> early-returns off-browser, which is false in a
    /// test host too, so without the seam a render would stop at the <c>Phase.Loading</c> skeleton with
    /// no rows to assert against.
    /// </summary>
    private static BunitContext NewPageContext(
        IReadOnlyList<SecretSettingStatusDto> secrets,
        IReadOnlyList<string>? claims = null,
        ISecretSettingsApiClient? secretClient = null)
    {
        var ctx = NewComponentContext();

        var settings = new Mock<ISystemSettingsApiClient>();
        settings.Setup(client => client.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<SystemSettingsDto>.Success(new SystemSettingsDto(), HttpStatusCode.OK));

        if (secretClient is null)
        {
            var mock = new Mock<ISecretSettingsApiClient>();
            mock.Setup(client => client.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiResult<List<SecretSettingStatusDto>>.Success(
                    secrets.ToList(), HttpStatusCode.OK));
            secretClient = mock.Object;
        }

        ctx.Services.AddSingleton(settings.Object);
        ctx.Services.AddSingleton(secretClient);
        ctx.Services.AddSingleton(new Mock<IDataExportApiClient>().Object);
        ctx.Services.AddSingleton(new Mock<IPageStateService>().Object);
        ctx.Services.AddSingleton(new Mock<IImportLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IUploadLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IAccountLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IFileAnalysisDisclosureCache>().Object);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(
            new StubAuthenticationStateProvider(claims ??
            [
                PermissionClaims.SystemSettingsRead,
                PermissionClaims.SystemSettingsUpdate,
                PermissionClaims.SystemSettingsSecurityUpdate,
            ]));

        Settings.InteractiveCheck = static () => true;
        return ctx;
    }

    private sealed class StubAuthenticationStateProvider(IReadOnlyList<string> permissions)
        : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(
                permissions.Select(permission => new Claim(PermissionClaims.Type, permission)),
                "Test");

            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
