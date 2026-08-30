using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The four Email transport rows on <c>/settings</c>, the destructive save gate in front of them, and
/// the two signals beside them (issue #8 — ACs 18, 19, 20, 21, 22, 23, 24, 25).
/// </summary>
public class EmailTransportSurfaceTests : IDisposable
{
    /// <summary>Restores the page's browser-check seam — it is <c>internal static</c>, so a test that moved it and did not put it back would change what every later test sees.</summary>
    public void Dispose() => Settings.InteractiveCheck = static () => OperatingSystem.IsBrowser();

    private const string SecurityClaim = PermissionClaims.SystemSettingsSecurityUpdate;

    // ── AC 16's client half, and AC 22 ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.EmailSmtpHost))]
    [InlineData(nameof(SystemSettingsUpdate.EmailSmtpPort))]
    [InlineData(nameof(SystemSettingsUpdate.EmailUseStartTls))]
    [InlineData(nameof(SystemSettingsUpdate.EmailClientBaseUrl))]
    public void EachTransportSettingHasExactlyOneCatalogueRow(string fieldName) =>
        Assert.Single(Settings.AllItems, item => item.Field == fieldName);

    /// <summary>
    /// AC 22. The port row's bounds name <see cref="SystemSettingsBounds"/> rather than restating its
    /// numbers, so the <c>[Range]</c>, the registry descriptor, the send-path clamp and this control
    /// are one pair with four consumers rather than four literals that can drift.
    /// </summary>
    [Fact]
    public void ThePortRow_TakesItsBoundsFromTheSharedPair()
    {
        var row = Settings.AllItems.Single(item => item.Field == nameof(SystemSettingsUpdate.EmailSmtpPort));

        Assert.Equal(SystemSettingsBounds.EmailSmtpPortMin, row.Min);
        Assert.Equal(SystemSettingsBounds.EmailSmtpPortMax, row.Max);
    }

    /// <summary>
    /// AC 22's source half. Asserting the VALUES above passes for a literal that happens to equal the
    /// constant today; this fails for one, which is the point — a client-side copy of a server bound is
    /// a defect rather than a convenience, and the copy only becomes visible when the server end moves.
    /// </summary>
    [Fact]
    public void TheEmailRows_ContainNoNumericLiteralBound()
    {
        var source = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.cs"));
        var row = Regex.Match(
            source,
            @"new\(""emailSmtpPort"".*?\),\r?\n\s*new\(""emailUseStartTls""",
            RegexOptions.Singleline);

        Assert.True(row.Success, "The emailSmtpPort catalogue row could not be located.");
        Assert.Contains("SystemSettingsBounds.EmailSmtpPortMin", row.Value, StringComparison.Ordinal);
        Assert.Contains("SystemSettingsBounds.EmailSmtpPortMax", row.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Min: 1,", row.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("65535", row.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two string rows accept an EMPTY value, which every other text row on this page treats as an
    /// unfilled field. Without it "mail is not configured" would be unreachable from the UI: the row
    /// would show "Enter a value", that error would disable Save, and clearing a host would be
    /// impossible — configuring mail once would be a one-way door.
    /// </summary>
    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.EmailSmtpHost))]
    [InlineData(nameof(SystemSettingsUpdate.EmailClientBaseUrl))]
    public void TheTwoTextRows_TreatEmptyAsLegal(string fieldName) =>
        Assert.True(Settings.AllItems.Single(item => item.Field == fieldName).AllowEmpty);

    // ── AC 18 — labelling and keyboard reach ─────────────────────────────────────────────────────

    /// <summary>
    /// Every transport row renders with a programmatic label and a control a keyboard can reach. No new
    /// widget is introduced — all four use surfaces the page already had — so this asserts the rows are
    /// actually present and wired, not that <c>OdsSettingField</c> works.
    /// </summary>
    [Fact]
    public async Task EachTransportRow_RendersWithAProgrammaticLabel()
    {
        await using var ctx = NewPageContext(new SystemSettingsDto { EmailSmtpHost = "smtp.example.test" });

        var page = ctx.Render<Settings>();

        foreach (var key in new[] { "emailSmtpHost", "emailSmtpPort", "emailUseStartTls", "emailClientBaseUrl" })
        {
            var title = page.Find($"#ss-ttl-{key}");
            Assert.False(string.IsNullOrWhiteSpace(title.TextContent));
        }
    }

    // ── ACs 23, 24 — the unconfigured-mail signal ────────────────────────────────────────────────

    /// <summary>
    /// AC 23. Exactly ONE entry, at Information rather than Error: a deployment that has not configured
    /// mail yet is incomplete, not broken, and the other entries in this rollup are real outages.
    /// </summary>
    [Fact]
    public async Task WithNoSmtpHost_ThePageHeaderReportsMailAsUnconfigured()
    {
        await using var ctx = NewPageContext(new SystemSettingsDto { EmailSmtpHost = string.Empty });

        var page = ctx.Render<Settings>();
        var header = page.FindComponent<PageHeader>();

        var problem = Assert.Single(header.Instance.Problems!);
        Assert.Equal(PageHeaderSeverity.Information, problem.Severity);
        Assert.Contains("mail is not configured", problem.Lead, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("In Email.", problem.Where);
    }

    [Fact]
    public async Task WithAnSmtpHostSet_ThereIsNoSuchEntry()
    {
        await using var ctx = NewPageContext(new SystemSettingsDto { EmailSmtpHost = "smtp.example.test" });

        var page = ctx.Render<Settings>();

        Assert.Null(page.FindComponent<PageHeader>().Instance.Problems);
    }

    /// <summary>
    /// AC 24. Gated on the write claim, matching the rollup's existing entries and for the same reason:
    /// there is nothing a caller who cannot edit the setting could do about it, so the signal would be
    /// noise rather than information.
    /// </summary>
    [Fact]
    public async Task WithoutTheSecurityWriteClaim_TheEntryIsNotRendered()
    {
        await using var ctx = NewPageContext(
            new SystemSettingsDto { EmailSmtpHost = string.Empty },
            claims: [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);

        var page = ctx.Render<Settings>();

        Assert.Null(page.FindComponent<PageHeader>().Instance.Problems);
    }

    // ── AC 25 — the origin-mismatch hint ─────────────────────────────────────────────────────────

    /// <summary>
    /// AC 25. A HINT: it renders as an advisory, never as an error. It must not set
    /// <c>aria-invalid</c>, must not disable Save and must not appear in the blocking summary — an
    /// operator may legitimately configure a public URL from an internal hostname, or set it ahead of a
    /// DNS cutover.
    /// </summary>
    [Fact]
    public async Task AMismatchedOrigin_ShowsAHint_AndBlocksNothing()
    {
        // bUnit's default base URI is http://localhost/, so a saved public origin is a mismatch.
        await using var ctx = NewPageContext(new SystemSettingsDto
        {
            EmailSmtpHost = "smtp.example.test",
            EmailClientBaseUrl = "https://odyssey.example.net",
        });

        var page = ctx.Render<Settings>();

        var advisory = page.Find("#ss-advisory-emailClientBaseUrl");
        Assert.Contains("differs from the address you are using now", advisory.TextContent, StringComparison.Ordinal);

        // Never an error: no aria-invalid on the control, and Save stays enabled.
        Assert.DoesNotContain("aria-invalid=\"true\"", page.Find("#ss-in-emailClientBaseUrl").OuterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("ss-err-emailClientBaseUrl", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMatchingOrigin_ShowsNoHint()
    {
        await using var ctx = NewPageContext(new SystemSettingsDto
        {
            EmailSmtpHost = "smtp.example.test",
            EmailClientBaseUrl = "http://localhost",
        });

        var page = ctx.Render<Settings>();

        Assert.DoesNotContain("ss-advisory-emailClientBaseUrl", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A server-authored advisory on this field OUTRANKS the client hint. The server channel carries
    /// projection faults — a stored value that cannot be used — and a fault the administrator did not
    /// cause outranks an observation about one that merely looks unusual.
    /// </summary>
    [Fact]
    public async Task AServerAdvisoryOnTheSameField_WinsOverTheHint()
    {
        await using var ctx = NewPageContext(new SystemSettingsDto
        {
            EmailSmtpHost = "smtp.example.test",
            EmailClientBaseUrl = "https://odyssey.example.net",
            Warnings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(SystemSettingsUpdate.EmailClientBaseUrl)] = "The stored value couldn't be read.",
            },
        });

        var page = ctx.Render<Settings>();

        var advisory = page.Find("#ss-advisory-emailClientBaseUrl");
        Assert.Contains("The stored value couldn't be read.", advisory.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("differs from the address", advisory.TextContent, StringComparison.Ordinal);
    }

    // ── ACs 19, 20, 21 — the destructive save gate ───────────────────────────────────────────────

    /// <summary>
    /// AC 19. The announcement is PINNED here rather than left to the implementer: it is what a screen
    /// reader user gets in place of the dialog appearing, and "announced to assistive technology" is
    /// not a testable requirement.
    ///
    /// <para>
    /// It names the CONSEQUENCE, not the dialog. A reader told "a dialog opened" has been told nothing
    /// they cannot already tell, and this dialog exists entirely to report something they did not ask
    /// for.
    /// </para>
    /// </summary>
    [Fact]
    public void TheAnnouncementNamesTheConsequence_ForEachTrigger()
    {
        Assert.Equal(
            "Confirmation required: changing the SMTP host clears the stored SMTP username and password.",
            Settings.ClearConfirmationAnnouncement(OdsCredentialClearReason.Host));

        Assert.Equal(
            "Confirmation required: turning STARTTLS off clears the stored SMTP username and password.",
            Settings.ClearConfirmationAnnouncement(OdsCredentialClearReason.StartTls));
    }

    /// <summary>
    /// The confirm button counts the pending edits it is about to submit, because this gates the page's
    /// single BATCH save rather than a per-field one. A reader who has edited six rows needs to know
    /// all six are going.
    /// </summary>
    [Theory]
    [InlineData(0, "Save and clear")]
    [InlineData(1, "Save 1 change and clear")]
    [InlineData(3, "Save 3 changes and clear")]
    public async Task TheConfirmLabelCountsThePendingEdits(int pending, string expected)
    {
        await using var ctx = NewComponentContext();

        var dialog = ctx.Render<OdsSecretClearOnSaveDialog>(parameters => parameters
            .Add(p => p.Open, false)
            .Add(p => p.PendingCount, pending));

        Assert.Equal(expected, dialog.Instance.ConfirmText);
    }

    /// <summary>
    /// The two triggers get different copy, because they protect against different things: one is a
    /// credential reaching a relay it was not entered for, the other is a credential going over an
    /// unencrypted connection. A reader told the wrong one is being warned about a threat that is not
    /// the one they just created.
    /// </summary>
    [Fact]
    public async Task TheTwoTriggersGetDifferentTitles()
    {
        await using var ctx = NewComponentContext();

        var host = ctx.Render<OdsSecretClearOnSaveDialog>(parameters => parameters
            .Add(p => p.Reason, OdsCredentialClearReason.Host));
        var startTls = ctx.Render<OdsSecretClearOnSaveDialog>(parameters => parameters
            .Add(p => p.Reason, OdsCredentialClearReason.StartTls));

        Assert.Contains("SMTP host", host.Instance.TitleText, StringComparison.Ordinal);
        Assert.Contains("STARTTLS", startTls.Instance.TitleText, StringComparison.Ordinal);
        Assert.NotEqual(host.Instance.TitleText, startTls.Instance.TitleText);
    }

    /// <summary>
    /// AC 21's copy half, and the clause no dialog above this one has to carry: Confirm submits every
    /// pending edit and Cancel discards none of them. Splitting the batch would create partial saves
    /// this page has never had; a Cancel that silently dropped every pending edit would be a worse
    /// surprise than the one being guarded.
    /// </summary>
    [Fact]
    public async Task TheDialogSaysThatCancelDiscardsNothing()
    {
        await using var ctx = NewComponentContext();

        // Beneath a MudDialogProvider, in ONE tree: OdsFormDialog composes OdsModal, which is an
        // INLINE MudDialog, and an inline MudBlazor dialog renders through the provider. Rendering the
        // two as separate roots leaves the dialog body absent and every copy assertion below passing
        // vacuously — which is exactly what it did before this host existed.
        var host = ctx.Render<DialogHost>();

        var markup = host.Markup;
        Assert.Contains("Cancel discards nothing", markup, StringComparison.Ordinal);
        Assert.Contains("in the same", markup, StringComparison.Ordinal);
        Assert.Contains("transaction", markup, StringComparison.Ordinal);
        Assert.Contains("SMTP username and SMTP password", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 20's source half. Neither <c>OdsModal</c> nor <c>OdsFormDialog</c> restores focus, so the
    /// page has to — on Confirm, Cancel and Escape alike, which are one path here because Cancel and
    /// Escape both come back through <c>OpenChanged(false)</c>.
    /// </summary>
    [Fact]
    public void ThePageRestoresFocusOnEveryWayTheDialogCloses()
    {
        var source = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.cs"));

        // Confirm restores it, and does so BEFORE awaiting the save: Save() awaits a network round
        // trip and a confirmation delay, and a reader left with focus on a removed dialog for that
        // whole window has effectively lost it.
        var confirm = Regex.Match(
            source, @"ConfirmClearAsync\(\).*?\n    \}", RegexOptions.Singleline);
        Assert.True(confirm.Success, "ConfirmClearAsync could not be located.");
        Assert.True(
            confirm.Value.IndexOf("RestoreFocusAfterClearDialogAsync", StringComparison.Ordinal)
                < confirm.Value.IndexOf("await Save()", StringComparison.Ordinal),
            "Focus must be restored before the save is awaited.");

        // …and so does the close path Cancel and Escape share.
        var closed = Regex.Match(
            source,
            @"OnClearDialogOpenChanged\(bool open\).*?\n    \}",
            RegexOptions.Singleline);
        Assert.True(closed.Success, "OnClearDialogOpenChanged could not be located.");
        Assert.Contains("RestoreFocusAfterClearDialogAsync", closed.Value, StringComparison.Ordinal);

        // Cancel discards nothing — it must not clear a draft, only the one-shot approval flag.
        Assert.Contains("_clearConfirmed = false;", closed.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyLoaded", closed.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("_texts.Clear", closed.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate itself, driven through the page: editing the SMTP host and pressing <b>Save changes</b>
    /// opens the confirmation and submits NOTHING.
    ///
    /// <para>
    /// This is the assertion the copy tests above cannot make. A dialog that renders the right words
    /// but never intercepts the save would leave the credential cleared without warning, and a gate
    /// that intercepted but still submitted would be worse than none — the administrator would see a
    /// confirmation for a change that had already happened.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EditingTheHostAndSaving_OpensTheConfirmation_AndSubmitsNothing()
    {
        var settings = new Mock<ISystemSettingsApiClient>();
        settings.Setup(client => client.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<SystemSettingsDto>.Success(Valid(), HttpStatusCode.OK));

        await using var ctx = NewPageContext(settings);
        var page = ctx.Render<Settings>();

        page.Find("#ss-in-emailSmtpHost").Input("smtp.new.test");
        page.FindAll("button").Single(button =>
            button.TextContent.Contains("Save changes", StringComparison.Ordinal)).Click();

        // The dialog is open, naming the host variant…
        var dialog = page.FindComponent<OdsSecretClearOnSaveDialog>();
        Assert.True(dialog.Instance.Open);
        Assert.Equal(OdsCredentialClearReason.Host, dialog.Instance.Reason);
        Assert.Equal("smtp.old.test", dialog.Instance.FromHost);
        Assert.Equal("smtp.new.test", dialog.Instance.ToHost);

        // …and nothing has been sent.
        settings.Verify(
            client => client.UpdateAsync(It.IsAny<SystemSettingsUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The control for the test above: a save that trips NEITHER trigger goes straight through. Without
    /// it, the gate could be "always open the dialog" and both tests would still pass.
    /// </summary>
    [Fact]
    public async Task EditingAnUnrelatedRowAndSaving_SubmitsImmediately()
    {
        var settings = new Mock<ISystemSettingsApiClient>();
        settings.Setup(client => client.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<SystemSettingsDto>.Success(Valid(), HttpStatusCode.OK));
        settings.Setup(client => client.UpdateAsync(
                It.IsAny<SystemSettingsUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<SystemSettingsDto>.Success(Valid(), HttpStatusCode.OK));

        await using var ctx = NewPageContext(settings);
        var page = ctx.Render<Settings>();

        page.Find("#ss-in-emailSmtpPort").Input("465");
        page.FindAll("button").Single(button =>
            button.TextContent.Contains("Save changes", StringComparison.Ordinal)).Click();

        Assert.False(page.FindComponent<OdsSecretClearOnSaveDialog>().Instance.Open);
        settings.Verify(
            client => client.UpdateAsync(It.IsAny<SystemSettingsUpdate>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A read DTO in which every row on the page is VALID.
    ///
    /// <para>
    /// Load-bearing for the two save-gate tests, and not obviously so. <c>Save()</c> checks the error
    /// gate before the destructive gate, and <c>HasErrors</c> spans every rendered row — so a
    /// hand-built DTO carrying default zeros fails forty range checks, <c>Save()</c> early-returns, and
    /// both tests pass or fail for a reason that has nothing to do with the confirmation. Building it
    /// from the catalogue rather than by hand also means a row added later cannot silently re-break
    /// them.
    /// </para>
    /// </summary>
    private static SystemSettingsDto Valid()
    {
        var dto = new SystemSettingsDto();
        var properties = typeof(SystemSettingsDto)
            .GetProperties()
            .ToDictionary(property => property.Name, StringComparer.Ordinal);

        // The server-published bounds the catalogue's MaxFrom/MinFrom rows resolve through. Left at
        // zero, a ceiling-bounded row reports "Must be between 1 and 0" on every load.
        foreach (var property in properties.Values.Where(p => p.PropertyType == typeof(int)))
        {
            if (property.Name.EndsWith("Ceiling", StringComparison.Ordinal))
            {
                property.SetValue(dto, 1_000_000);
            }
            else if (property.Name.EndsWith("Floor", StringComparison.Ordinal))
            {
                property.SetValue(dto, 1);
            }
        }

        foreach (var item in Settings.AllItems)
        {
            if (item.Field is not { } field || !properties.TryGetValue(field, out var property))
            {
                continue;
            }

            if (property.PropertyType == typeof(int))
            {
                property.SetValue(dto, Math.Max(item.Min, 1));
            }
            else if (property.PropertyType == typeof(decimal))
            {
                property.SetValue(dto, item.DecimalMin);
            }
            else if (property.PropertyType == typeof(string) && !item.AllowEmpty)
            {
                property.SetValue(dto, "x");
            }
        }

        dto.EmailSmtpHost = "smtp.old.test";
        dto.EmailSmtpPort = 587;
        dto.EmailUseStartTls = true;
        return dto;
    }

    private static BunitContext NewComponentContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static BunitContext NewPageContext(SystemSettingsDto dto, IReadOnlyList<string>? claims = null)
    {
        var settings = new Mock<ISystemSettingsApiClient>();
        settings.Setup(client => client.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<SystemSettingsDto>.Success(dto, HttpStatusCode.OK));

        return NewPageContext(settings, claims);
    }

    private static BunitContext NewPageContext(
        Mock<ISystemSettingsApiClient> settings, IReadOnlyList<string>? claims = null)
    {
        var ctx = NewComponentContext();

        var secrets = new Mock<ISecretSettingsApiClient>();
        secrets.Setup(client => client.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<SecretSettingStatusDto>>.Success([], HttpStatusCode.OK));

        ctx.Services.AddSingleton(settings.Object);
        ctx.Services.AddSingleton(secrets.Object);
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
                SecurityClaim,
            ]));

        Settings.InteractiveCheck = static () => true;
        return ctx;
    }

    /// <summary>
    /// The provider and the dialog in one render tree — see the note at the call site for why they
    /// cannot be two roots.
    /// </summary>
    private sealed class DialogHost : Microsoft.AspNetCore.Components.ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudDialogProvider>(0);
            builder.CloseComponent();

            builder.OpenComponent<OdsSecretClearOnSaveDialog>(1);
            builder.AddComponentParameter(2, nameof(OdsSecretClearOnSaveDialog.Open), true);
            builder.AddComponentParameter(
                3,
                nameof(OdsSecretClearOnSaveDialog.Secrets),
                (IReadOnlyList<string>)["SMTP username", "SMTP password"]);
            builder.AddComponentParameter(4, nameof(OdsSecretClearOnSaveDialog.FromHost), "smtp.old.test");
            builder.AddComponentParameter(5, nameof(OdsSecretClearOnSaveDialog.ToHost), "smtp.new.test");
            builder.CloseComponent();
        }
    }

    private sealed class StubAuthenticationStateProvider(IReadOnlyList<string> permissions)
        : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [.. permissions.Select(permission => new Claim(PermissionClaims.Type, permission))],
                "test"))));
    }
}
