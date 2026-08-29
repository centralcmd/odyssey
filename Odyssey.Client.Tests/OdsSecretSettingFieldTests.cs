using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.Dtos.Application;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The credential row, rendered (issue #444 §16 ACs 35–37, 42–44).
///
/// <para>
/// These are the first rendering tests in this project, and the reason they exist is precedent rather
/// than thoroughness: <c>OdsPasswordChangeForm</c> shipped a defect that threw on <em>every</em>
/// render, took down two live pages, and no test caught it — because a component that cannot render
/// still "passes" a project with no renderer. That risk is sharper here, since the only registered
/// secret key is filtered out of Production, so the app itself never renders this row there.
/// </para>
/// </summary>
public class OdsSecretSettingFieldTests
{
    private const string Key = SecretSettingKeys.DiagnosticsSelfTest;
    private const string Title = "Diagnostics self-test credential";

    /// <summary>What is not working while the row is unset — the amber advisory band's text.</summary>
    private const string Consequence = "Nothing reads this credential, so nothing is affected.";

    /// <summary>What is broken right now when the row cannot be decrypted — the status line's middle sentence.</summary>
    private const string Affects = "No feature is affected by this key.";

    /// <summary>
    /// Collapses runs of whitespace, so an assertion pins the words rather than the Razor source's
    /// line wrapping — prose in markup carries its newlines and indentation into the render.
    /// </summary>
    private static string Flatten(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(markup, @"\s+", " ");

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();

        // Loose, because the component's only interop is the deferred odsFocusById call and MudBlazor's
        // own initialisation — neither of which this suite is asserting the arguments of, except where
        // it explicitly reads Invocations.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static IRenderedComponent<OdsSecretSettingField> Render(
        BunitContext ctx,
        SecretSettingState state = SecretSettingState.NotSet,
        Func<string, Task<ApiResult>>? onSave = null,
        Func<Task<ApiResult>>? onClear = null,
        Action<string>? onAnnounce = null,
        bool isDerivationKey = false) =>
        ctx.Render<OdsSecretSettingField>(parameters =>
        {
            parameters
                .Add(row => row.SecretKey, Key)
                .Add(row => row.Title, Title)
                .Add(row => row.Description, "A test-only credential.")
                .Add(row => row.State, state)
                .Add(row => row.IsDerivationKey, isDerivationKey)
                .Add(row => row.Consequence, Consequence)
                .Add(row => row.Affects, Affects);

            if (state != SecretSettingState.NotSet)
            {
                parameters
                    .Add(row => row.UpdatedAt, new DateTime(2026, 8, 24, 10, 15, 0, DateTimeKind.Utc))
                    .Add(row => row.UpdatedByDisplayName, "Ada Lovelace");
            }

            if (onSave is not null)
            {
                parameters.Add(row => row.OnSave, onSave);
            }

            if (onClear is not null)
            {
                parameters.Add(row => row.OnClear, onClear);
            }

            if (onAnnounce is not null)
            {
                parameters.Add(row => row.OnAnnounce, EventCallback.Factory.Create<string>(ctx, onAnnounce));
            }
        });

    // ── AC 36 — every server status renders its state as TEXT ───────────────────────────────────

    /// <summary>
    /// AC 36. The state is available as TEXT in the accessible tree, never as a bare coloured dot or
    /// icon (WCAG 1.4.1).
    /// </summary>
    [Fact]
    public void NotSet_RendersItsEntryInputInline_WithNothingToClear()
    {
        using var ctx = NewContext();
        var row = Render(ctx);

        // The one state with nothing to protect and something to do, so it costs no click
        // (DS · SecretSettingField). There is no "Set" button to press first.
        Assert.NotNull(row.Find("input.odc-input"));
        Assert.Equal("Never set.", row.Find(".odc-sfield-stamp").TextContent.Trim());

        // …and nothing to clear, because nothing is stored.
        Assert.DoesNotContain("Clear", row.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_RendersAFixedMask_TheAttribution_AndBothActions()
    {
        using var ctx = NewContext();
        var row = Render(ctx, SecretSettingState.Set);

        // A FIXED sixteen bullets whatever the value is: a mask that tracked the real length would
        // leak it. aria-hidden, because a run of bullets says nothing — the sr-only line carries it.
        var mask = row.Find(".odc-secret-mask");
        Assert.Equal(16, mask.TextContent.Trim().Length);
        Assert.Equal("true", mask.GetAttribute("aria-hidden"));
        Assert.Contains("Value stored, hidden", row.Markup, StringComparison.Ordinal);

        Assert.Contains("Ada Lovelace", row.Find(".odc-sfield-stamp").TextContent, StringComparison.Ordinal);
        Assert.Contains("Replace", row.Markup, StringComparison.Ordinal);
        Assert.Contains("Clear", row.Markup, StringComparison.Ordinal);

        // No input until Replace: a stored credential must not be overwritable by a stray keystroke.
        Assert.Empty(row.FindAll("input.odc-input"));
    }

    /// <summary>
    /// <c>Unreadable</c> names the likely cause, what is broken right now, and the remedy — the three
    /// facts an administrator needs and can see nowhere else. The middle one is the caller's
    /// <c>Affects</c> (issue #445): the key's name does not say what fails when it cannot be read.
    /// </summary>
    [Fact]
    public void Unreadable_ExplainsTheCauseTheConsequenceAndTheRemedy()
    {
        using var ctx = NewContext();
        var row = Render(ctx, SecretSettingState.Unreadable);

        Assert.Contains("cannot decrypt", row.Markup, StringComparison.Ordinal);
        Assert.Contains("key ring", row.Markup, StringComparison.Ordinal);
        Assert.Contains(Affects, row.Markup, StringComparison.Ordinal);
        Assert.Contains("only fix", row.Markup, StringComparison.Ordinal);
        Assert.Contains("Replace", row.Markup, StringComparison.Ordinal);
        Assert.Contains("Clear", row.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two non-blocking channels do not bleed into each other (issue #445). An ABSENT row is
    /// healthy, so what it costs goes in the amber advisory band; an UNREADABLE row is a fault, so its
    /// text stays in the status line and the advisory does not render at all. Collapsing the two would
    /// either dress a fault as an advisory or make a healthy state look broken.
    /// </summary>
    [Fact]
    public void TheConsequenceAdvisory_RendersOnlyWhileTheRowIsUnset()
    {
        using var ctx = NewContext();

        var unset = Render(ctx, SecretSettingState.NotSet);
        Assert.Contains(Consequence, unset.Find(".odc-sfield-advisory").TextContent, StringComparison.Ordinal);

        // …and the frame says so, so the band and the block it belongs to read as one thing.
        Assert.Contains("advised", unset.Find("fieldset").GetAttribute("class")!, StringComparison.Ordinal);

        foreach (var configured in new[] { SecretSettingState.Set, SecretSettingState.Unreadable })
        {
            var row = Render(ctx, configured);
            Assert.Empty(row.FindAll(".odc-sfield-advisory"));
            Assert.DoesNotContain(Consequence, row.Markup, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The printable-ASCII rule is named as the value is typed, not left to the store's <c>400</c>
    /// (issue #445 AC 9) — and the offending character is echoed, because "somewhere in what you
    /// pasted" is not actionable. The value is never sent while it violates the rule.
    /// </summary>
    [Fact]
    public void ANonAsciiValue_IsRefusedLocally_WithTheConstraintNamed()
    {
        using var ctx = NewContext();
        var saves = 0;
        var row = Render(ctx, SecretSettingState.NotSet, onSave: _ =>
        {
            saves++;
            return Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent));
        });

        row.Find("input.odc-input").Input("relay-p\u00e5ssword");

        Assert.Contains("printable ASCII", row.Markup, StringComparison.Ordinal);
        Assert.Contains("\u201c\u00e5\u201d", row.Markup, StringComparison.Ordinal);

        // Both routes again: Save refuses to be pressed, and the keyboard path is refused inside
        // SaveAsync — so this cannot pass merely because the button happened to be disabled.
        var save = row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Save"));
        Assert.NotNull(save.GetAttribute("disabled"));
        save.Click();

        row.Find("input.odc-input").KeyDown(Bunit.Key.Enter);

        Assert.Equal(0, saves);
    }

    /// <summary>
    /// The reveal toggle. Not a concession: a mistyped relay password or API key fails silently and the
    /// stored value can never be read back to check, so the one moment it is legible is while it is
    /// being typed. It is disabled while there is nothing to reveal, and its state is exposed as state.
    /// </summary>
    [Fact]
    public void TheRevealToggle_SwitchesTheInputTypeAndReportsItsState()
    {
        using var ctx = NewContext();
        var row = Render(ctx, SecretSettingState.NotSet);

        row.Find("button.mud-button-root").Click();
        Assert.Equal("password", row.Find("input.odc-input").GetAttribute("type"));
        Assert.NotNull(row.Find("button.odc-secret-eye").GetAttribute("disabled"));

        row.Find("input.odc-input").Input("a-value");
        row.Find("button.odc-secret-eye").Click();

        Assert.Equal("text", row.Find("input.odc-input").GetAttribute("type"));
        Assert.Equal("true", row.Find("button.odc-secret-eye").GetAttribute("aria-pressed"));
    }

    /// <summary>
    /// AC 41. The state text goes in <c>OdsSettingRow</c>'s <c>Status</c> slot, NOT <c>Advisory</c> —
    /// whose contract is strictly non-blocking advisory text, while <c>Unreadable</c> is a degraded
    /// state. Asserted on the rendered markup, because the two slots produce different wrappers.
    /// </summary>
    [Fact]
    public void TheUnreadableStateText_RendersInTheErrorChannel_NotTheAdvisoryBand()
    {
        using var ctx = NewContext();
        var row = Render(ctx, SecretSettingState.Unreadable);

        // The condition is a FAULT, so it lands in the error channel and tints the frame — and the
        // advisory band, whose contract is strictly non-blocking text about a cost, does not render
        // at all. That separation is the point: an absent row is healthy and this is an outage.
        Assert.Contains(Affects, row.Find(".odc-sfield-err").TextContent, StringComparison.Ordinal);
        Assert.Empty(row.FindAll(".odc-sfield-advisory"));
        Assert.Contains("unreadable", row.Find("fieldset").GetAttribute("class")!, StringComparison.Ordinal);
    }

    // ── AC 36 — the interaction modes, including the combination a flat state list omitted ──────

    /// <summary>
    /// AC 35 + AC 36. Editing renders the MASKED input — the <c>Type</c> parameter this issue added to
    /// <c>OdsTextInputField</c>, which had no password mode at all before.
    /// </summary>
    [Fact]
    public void Editing_RendersAMaskedInput_NamedByTheLegendLabel()
    {
        using var ctx = NewContext();
        var row = Render(ctx);

        var input = row.Find("input.odc-input");
        Assert.Equal("password", input.GetAttribute("type"));
        Assert.Equal("new-password", input.GetAttribute("autocomplete"));
        Assert.Equal("false", input.GetAttribute("spellcheck"));

        // A real <label for> in the legend, which the SettingField shape makes possible — the row
        // shape had to reach for aria-labelledby because its title was not a label element.
        var label = row.Find("label.odc-sfield-label");
        Assert.Equal(input.GetAttribute("id"), label.GetAttribute("for"));
        Assert.Equal(Title, label.TextContent.Trim());
    }

    /// <summary>
    /// AC 36 — the combination a flat six-state list lost: EDITING OVER AN UNREADABLE ROW, reached via
    /// Replace. The status text must still be present while editing, or the administrator loses the
    /// one explanation of why they are replacing it.
    /// </summary>
    [Fact]
    public void EditingOverAnUnreadableRow_KeepsTheDegradedStatusVisible()
    {
        using var ctx = NewContext();
        var row = Render(ctx, SecretSettingState.Unreadable);

        row.FindAll("button.mud-button-root").First().Click();

        Assert.Equal("password", row.Find("input.odc-input").GetAttribute("type"));
        Assert.Contains("cannot decrypt", row.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row states, in the row itself, that it is outside the page's Save — shown exactly while
    /// there is something to lose. The page's green "Saved" does not mean a credential was written,
    /// and that must be visible rather than inferred.
    /// </summary>
    [Fact]
    public void AnInProgressValue_WarnsThatItIsNotPartOfThePageSave()
    {
        using var ctx = NewContext();
        var row = Render(ctx);

        row.Find("button.mud-button-root").Click();
        Assert.DoesNotContain("odc-secret-note", row.Markup, StringComparison.Ordinal);

        row.Find("input.odc-input").Input("sk-typed");
        Assert.Contains("odc-secret-note", row.Markup, StringComparison.Ordinal);
        Assert.Contains("discards what you have typed", row.Markup, StringComparison.Ordinal);
    }

    // ── AC 37 — focus across the re-render, in both directions ──────────────────────────────────

    /// <summary>
    /// AC 37. Activating <b>Set</b> moves focus to the input, which does not exist when the handler
    /// runs — so the call has to be deferred to <c>OnAfterRenderAsync</c>. A naive
    /// <c>FocusAsync</c> in the handler would target an absent reference.
    /// </summary>
    [Fact]
    public void ActivatingReplace_MovesFocusToTheInput()
    {
        // From SET, because that is now the only state with a transition into entry: NotSet renders
        // its input inline, so there is no focus to move.
        using var ctx = NewContext();
        var row = Render(ctx, SecretSettingState.Set);

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Replace")).Click();

        var inputId = row.Find("input.odc-input").GetAttribute("id");
        Assert.Equal(inputId, LastFocusTarget(ctx));
    }

    /// <summary>
    /// AC 37, the other direction — and the one a handler-time focus call gets wrong for a different
    /// reason: the Set/Replace button is a NEW element after Cancel, so a reference captured earlier is
    /// stale.
    /// </summary>
    [Fact]
    public void Cancel_ReturnsFocusToTheReplaceButton()
    {
        using var ctx = NewContext();
        var row = Render(ctx, SecretSettingState.Set);

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Replace")).Click();
        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Cancel")).Click();

        Assert.Equal(row.Find("button.mud-button-root").GetAttribute("id"), LastFocusTarget(ctx));
        Assert.Empty(row.FindAll("input.odc-input"));
    }

    /// <summary>AC 37. Post-save focus returns to the row's (now Replace) primary action.</summary>
    [Fact]
    public void Save_ReturnsFocusToTheReplaceButton()
    {
        using var ctx = NewContext();
        var row = Render(ctx, SecretSettingState.Set,
            onSave: _ => Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent)));

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Replace")).Click();
        row.Find("input.odc-input").Input("sk-value");
        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Save")).Click();

        Assert.Equal(row.Find("button.mud-button-root").GetAttribute("id"), LastFocusTarget(ctx));
    }

    /// <summary>
    /// The in-progress value is cleared on a successful save — it lives in a component-local field and
    /// must not survive the commit.
    /// </summary>
    [Fact]
    public void ASuccessfulSave_ClearsTheLocalValueAndReturnsToDisplay()
    {
        // From SET: a NotSet field renders its input inline, so "returns to display" is only
        // observable where a display state exists to return to. The page swaps State to Set on the
        // OnChanged refresh, which is the same transition this drives directly.
        using var ctx = NewContext();
        var row = Render(ctx, SecretSettingState.Set,
            onSave: _ => Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent)));

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Replace")).Click();
        row.Find("input.odc-input").Input("sk-value");
        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Save")).Click();

        Assert.Empty(row.FindAll("input.odc-input"));
        Assert.DoesNotContain("sk-value", row.Markup, StringComparison.Ordinal);
    }

    // ── AC 38 + 44 — announcements ──────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 38 + AC 44. The announcement travels through the component's own
    /// <see cref="EventCallback{T}"/> — the page's <c>Announce()</c> is <c>private</c> and its
    /// <c>OdsLiveAnnouncer</c> is hosted once by the page's markup, so a component in
    /// <c>Components/</c> can reach neither — and it NAMES the credential, because a bare
    /// "Credential saved." is identical for every row.
    /// </summary>
    [Fact]
    public void TheSaveAnnouncement_TravelsThroughTheCallbackAndNamesTheCredential()
    {
        using var ctx = NewContext();
        var announcements = new List<string>();
        var row = Render(
            ctx,
            onSave: _ => Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent)),
            onAnnounce: announcements.Add);

        row.Find("input.odc-input").Input("sk-value");
        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Save")).Click();

        Assert.Contains($"{Title} saved.", announcements);
    }

    // ── AC 42 — the four failure statuses ───────────────────────────────────────────────────────

    /// <summary>
    /// AC 42. Each of <c>400</c>, <c>403</c>, <c>429</c> and <c>503</c> produces a NON-EMPTY inline
    /// message. The <c>400</c> arrives keyed on the request DTO property, and the other three carry no
    /// <c>errors</c> entry at all — the <c>503</c> in particular — so a row that only read
    /// <c>ErrorFor</c> would render them blank.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "You do not hold the required permission.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Too many attempts. Try again later.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Persistent key storage is not explicitly configured.")]
    public void ARowLevelFailure_RendersItsDetailAndDoesNotMarkTheFieldInvalid(HttpStatusCode status, string detail)
    {
        using var ctx = NewContext();
        var row = Render(ctx, onSave: _ => Task.FromResult(
            ApiResult.Failure(status, new ApiProblem { Status = (int)status, Detail = detail })));

        row.Find("input.odc-input").Input("sk-value");
        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Save")).Click();

        Assert.Contains(detail, row.Find(".odc-sfield-err").TextContent, StringComparison.Ordinal);

        // aria-invalid is NOT set: these three say nothing about the submitted value, so marking the
        // input invalid would tell a screen-reader user to fix a value that was fine.
        Assert.Null(row.Find("input.odc-input").GetAttribute("aria-invalid"));
    }

    /// <summary>
    /// AC 42, the <c>400</c> half. It IS a statement about the value, so it joins the field-error
    /// channel and sets <c>aria-invalid</c>.
    /// </summary>
    [Fact]
    public void AValidationFailure_RendersOnTheFieldAndSetsAriaInvalid()
    {
        const string message = "Credential 'DiagnosticsSelfTest' must contain printable ASCII characters only.";

        using var ctx = NewContext();
        var row = Render(ctx, onSave: _ => Task.FromResult(ApiResult.Failure(
            HttpStatusCode.BadRequest,
            new ApiProblem
            {
                Status = 400,
                Detail = message,
                Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    [nameof(SecretSettingUpdate.Value)] = [message],
                },
            })));

        row.Find("input.odc-input").Input("sk-value");
        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Save")).Click();

        Assert.Equal("true", row.Find("input.odc-input").GetAttribute("aria-invalid"));
        Assert.Contains(message, row.Markup, StringComparison.Ordinal);
        Assert.Empty(row.FindAll(".odc-secret-error"));
    }

    /// <summary>
    /// A <c>503</c> carries no <c>errors</c> entry at all, which is exactly the case a
    /// setting-key-based join would have rendered blank. Pinned separately from the theory above so the
    /// regression is named.
    /// </summary>
    [Fact]
    public void AFailureWithNoErrorsEntry_StillRendersAMessage()
    {
        using var ctx = NewContext();
        var row = Render(ctx, onSave: _ => Task.FromResult(ApiResult.Failure(
            HttpStatusCode.ServiceUnavailable,
            new ApiProblem { Status = 503, ReasonFallback = "Service Unavailable" })));

        row.Find("input.odc-input").Input("sk-value");
        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Save")).Click();

        Assert.False(string.IsNullOrWhiteSpace(row.Find(".odc-sfield-err").TextContent));
    }

    /// <summary>
    /// Editing after a row-level rejection retires it, so the two error channels can never render at
    /// once.
    ///
    /// <para>
    /// <c>4.1.1 Parsing – Level A</c>. The field error and the row error are separate elements, and
    /// they were reachable together: a <c>429</c> sets the row error, and the local printable-ASCII
    /// check is a live getter over the typed value, so a single accidental curly quote in the retry
    /// raised the field error while the row error was untouched. Two elements rendered, and
    /// <c>aria-describedby</c> resolved only one of them — the other stayed on screen but out of the
    /// control's programmatic description. Clearing the row error on input closes it at the source;
    /// the ids are distinct as well, so a future path that sets both cannot re-open it.
    /// </para>
    /// </summary>
    [Fact]
    public void EditingAfterARowLevelFailure_LeavesOneErrorBlockWithItsOwnId()
    {
        using var ctx = NewContext();
        var row = Render(ctx, onSave: _ => Task.FromResult(ApiResult.Failure(
            HttpStatusCode.TooManyRequests, new ApiProblem { Status = 429, Detail = "Slow down." })));

        var input = row.Find("input.odc-input");
        input.Input("sk-value");
        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Save")).Click();
        Assert.Contains("Slow down.", row.Find(".odc-sfield-err").TextContent, StringComparison.Ordinal);

        // The retry picks up a curly quote — autocorrect or a paste — which the local check refuses.
        row.Find("input.odc-input").Input("sk-value\u2019s");

        var errors = row.FindAll(".odc-sfield-err");
        Assert.Single(errors);
        Assert.DoesNotContain("Slow down.", errors[0].TextContent, StringComparison.Ordinal);

        // The ids the input points at are the ids that exist, and each is used once.
        var described = row.Find("input.odc-input").GetAttribute("aria-describedby")!.Split(' ');
        Assert.Equal(described, described.Distinct());
        Assert.All(
            errors.Select(error => error.Id).Where(id => !string.IsNullOrEmpty(id)),
            id => Assert.Contains(id, described));
    }

    /// <summary>A failed save keeps the typed value, so a rate limit or a transient failure can be retried.</summary>
    [Fact]
    public void AFailedSave_KeepsTheTypedValueForRetry()
    {
        using var ctx = NewContext();
        var row = Render(ctx, onSave: _ => Task.FromResult(ApiResult.Failure(
            HttpStatusCode.TooManyRequests, new ApiProblem { Status = 429, Detail = "Slow down." })));

        row.Find("input.odc-input").Input("sk-value");
        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Save")).Click();

        Assert.Equal("sk-value", row.Find("input.odc-input").GetAttribute("value"));
    }

    /// <summary>
    /// An empty submission is refused locally — clearing is DELETE, never an empty PUT — and it is
    /// refused on BOTH routes into the save.
    ///
    /// <para>
    /// The button is disabled while there is nothing to send (design-system parity), so the pointer
    /// route cannot reach the API at all. That alone would leave the refusal silent, which is why the
    /// keyboard route matters: Enter still calls <c>SaveAsync</c>, and the guard there is what produces
    /// the sentence explaining that clearing is a different action. Testing only the button would have
    /// let that message rot unreachable.
    /// </para>
    /// </summary>
    [Fact]
    public void SavingAnEmptyValue_IsRefusedOnBothRoutes_WithoutCallingTheApi()
    {
        var calls = 0;

        using var ctx = NewContext();
        var row = Render(ctx, onSave: _ =>
        {
            calls++;
            return Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent));
        });

        row.Find("button.mud-button-root").Click();

        // Pointer route: the control says it will not accept the empty value rather than accepting the
        // click and doing nothing.
        var save = row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Save"));
        Assert.NotNull(save.GetAttribute("disabled"));
        save.Click();
        Assert.Equal(0, calls);

        // Keyboard route: reaches SaveAsync, and the guard there explains why nothing was sent.
        row.Find("input.odc-input").KeyDown(Bunit.Key.Enter);

        Assert.Equal(0, calls);
        Assert.Equal("true", row.Find("input.odc-input").GetAttribute("aria-invalid"));
        Assert.Contains("use Clear to remove the stored value", row.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Enter commits and Escape abandons a replace (Odyssey Design System · SecretSettingField). This
    /// row does not participate in the page's Save, so there is no form submit for Enter to fall
    /// through to — without the handler a credential typed and confirmed with Enter is silently lost.
    /// </summary>
    [Fact]
    public void EnterSaves_AndEscapeCancelsAReplace()
    {
        var saved = new List<string>();

        using var ctx = NewContext();
        var row = Render(ctx, SecretSettingState.Set, onSave: value =>
        {
            saved.Add(value);
            return Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent));
        });

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Replace")).Click();
        row.Find("input.odc-input").Input("sk-typed");
        row.Find("input.odc-input").KeyDown(Bunit.Key.Enter);

        Assert.Equal(["sk-typed"], saved);

        // Escape returns a REPLACE to the display state, discarding what was typed.
        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Replace")).Click();
        row.Find("input.odc-input").Input("sk-abandoned");
        row.Find("input.odc-input").KeyDown(Bunit.Key.Escape);

        Assert.Empty(row.FindAll("input.odc-input"));
        Assert.Equal(["sk-typed"], saved);
    }

    /// <summary>
    /// Escape does NOT fire from an unset row: the input is that row's resting state, so there is
    /// nothing to cancel back to and swallowing the key would take it from whatever else wants it.
    /// </summary>
    [Fact]
    public void EscapeFromAnUnsetRow_DoesNotLeaveTheEntryState()
    {
        using var ctx = NewContext();
        var row = Render(ctx, SecretSettingState.NotSet);

        row.Find("input.odc-input").Input("sk-typed");
        row.Find("input.odc-input").KeyDown(Bunit.Key.Escape);

        Assert.NotNull(row.Find("input.odc-input"));
    }

    // ── AC 43 — the destructive action ──────────────────────────────────────────────────────────

    /// <summary>
    /// AC 43. <b>Clear</b> requires an explicit confirmation before issuing the <c>DELETE</c>. It is
    /// built on <c>OdsFormDialog</c> because there is no confirmation dialog in this client to reuse —
    /// <c>Components/</c> contains no <c>ConfirmDialog</c>/<c>OdsConfirm</c> — which keeps the
    /// focus-trap and dismissal behaviour rather than introducing a second dialog pattern.
    /// </summary>
    [Fact]
    public async Task Clear_DoesNotCallTheApiUntilTheConfirmationIsAccepted()
    {
        var cleared = 0;

        // await using: MudDialogProvider registers PointerEventsNoneService, which implements only
        // IAsyncDisposable — a synchronous dispose of the container throws.
        await using var ctx = NewContext();
        var row = RenderWithDialogs(ctx, SecretSettingState.Set, () =>
        {
            cleared++;
            return Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent));
        });

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Clear")).Click();
        Assert.Equal(0, cleared);

        Assert.Contains($"Clear {Title}?", row.Markup, StringComparison.Ordinal);

        row.FindAll("button.mud-button-root")
            .First(button => button.TextContent.Contains("Clear value"))
            .Click();

        Assert.Equal(1, cleared);
    }

    /// <summary>
    /// The gap the PR #450 frontend review found: a failed Clear leaves the confirmation dialog OPEN
    /// (<c>OdsFormDialog</c> closes only on <c>true</c>), so a message rendered only in the row sits
    /// behind the scrim and a sighted administrator sees the spinner stop and nothing else. The
    /// failure must render INSIDE the dialog, where focus already is.
    ///
    /// <para>
    /// The pre-existing failure theory drove <c>onSave</c> only, which is why this was invisible to the
    /// suite as well as to the user.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "You do not hold the required permission.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Too many attempts. Try again later.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Persistent key storage is not explicitly configured.")]
    public async Task AFailedClear_RendersItsFailureInsideTheStillOpenDialog(HttpStatusCode status, string detail)
    {
        await using var ctx = NewContext();
        var row = RenderWithDialogs(ctx, SecretSettingState.Set, () => Task.FromResult(
            ApiResult.Failure(status, new ApiProblem { Status = (int)status, Detail = detail })));

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Clear")).Click();
        row.FindAll("button.mud-button-root")
            .First(button => button.TextContent.Contains("Clear value"))
            .Click();

        // Still open — and the message is inside it, not only in the row underneath.
        Assert.Contains($"Clear {Title}?", row.Markup, StringComparison.Ordinal);
        Assert.Contains(detail, row.Find(".odc-secret-confirm-error").TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exactly ONE live element speaks a failed Clear. The dialog's copy carries no role — the
    /// announcement already travels through <c>OnAnnounce</c> — and the row's copy drops its
    /// <c>role="alert"</c> while it is behind the scrim, so the same failure is not spoken twice.
    /// </summary>
    [Fact]
    public async Task AFailedClear_ProducesExactlyOneLiveAnnouncement()
    {
        var announcements = new List<string>();

        await using var ctx = NewContext();
        var row = RenderWithDialogs(
            ctx,
            SecretSettingState.Set,
            () => Task.FromResult(ApiResult.Failure(
                HttpStatusCode.ServiceUnavailable, new ApiProblem { Status = 503, Detail = "Key ring is not durable." })),
            onAnnounce: announcements.Add);

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Clear")).Click();
        row.FindAll("button.mud-button-root")
            .First(button => button.TextContent.Contains("Clear value"))
            .Click();

        Assert.Empty(row.FindAll(".odc-secret-confirm-error[role='alert']"));
        Assert.Empty(row.FindAll(".odc-secret-error[role='alert']"));
        Assert.Contains(announcements, message => message.Contains("could not be cleared", StringComparison.Ordinal));
    }

    /// <summary>
    /// PR #450 accessibility review (WCAG 2.4.3): dismissing the confirmation WITHOUT clearing moves no
    /// focus of its own. <c>OdsModal</c> already captures whatever opened it and restores that on the
    /// close edge, so a redirect here raced it — and aimed at the primary action rather than the
    /// <b>Clear</b> button the user actually pressed.
    /// </summary>
    [Fact]
    public async Task DismissingTheConfirmation_MovesNoFocusOfItsOwn()
    {
        await using var ctx = NewContext();
        var row = RenderWithDialogs(ctx, SecretSettingState.Set, () =>
            Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent)));

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Clear")).Click();
        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Cancel")).Click();

        Assert.Empty(ctx.JSInterop.Invocations["odsFocusById"]);
    }

    /// <summary>
    /// The one case the row must still handle itself: after a SUCCESSFUL clear the Clear button
    /// <c>OdsModal</c> captured no longer exists — the row falls back to <c>NotSet</c>, which renders
    /// only the primary action — so restoring to it would drop focus to <c>&lt;body&gt;</c>.
    /// </summary>
    [Fact]
    public async Task ASuccessfulClear_MovesFocusToThePrimaryActionButton()
    {
        await using var ctx = NewContext();
        var row = RenderWithDialogs(ctx, SecretSettingState.Set, () =>
            Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent)));

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Clear")).Click();
        row.FindAll("button.mud-button-root")
            .First(button => button.TextContent.Contains("Clear value"))
            .Click();

        Assert.NotEmpty(ctx.JSInterop.Invocations["odsFocusById"]);
    }

    /// <summary>
    /// AC 43 (the copy that is load-bearing). On an <c>Unreadable</c> row, clearing is destructive, not
    /// free: §10 requires the keys volume to be backed up, so the ciphertext becomes readable again the
    /// moment the correct key ring is restored — and Clear deletes it permanently.
    /// </summary>
    [Fact]
    public async Task ClearingAnUnreadableRow_SaysAKeyRingRestoreWouldHaveRecoveredIt()
    {
        // await using: MudDialogProvider registers PointerEventsNoneService, which implements only
        // IAsyncDisposable — a synchronous dispose of the container throws.
        await using var ctx = NewContext();
        var row = RenderWithDialogs(ctx, SecretSettingState.Unreadable, () =>
            Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent)));

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Clear")).Click();

        // Whitespace-normalised: the copy is prose wrapped across several Razor source lines, so the
        // rendered markup carries those line breaks and indentation inside the sentence. Asserting on
        // the raw markup would pin the wrapping rather than the words.
        var copy = Flatten(row.Markup);
        Assert.Contains("Data Protection key ring can still be restored", copy, StringComparison.Ordinal);
        Assert.Contains("clearing it now will not", copy, StringComparison.Ordinal);
        // Text content, not markup: Blazor stamps the scoped-CSS attribute onto the <b>, so the raw
        // markup reads `<b b-xxxxxxxxxx>unreadable</b>` and an element-shaped assertion pins the build.
        Assert.Contains("already unreadable", Flatten(row.Find(".mud-dialog").TextContent),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 43, the derivation-key case — the one place the recoverability classification reaches the UI.
    /// A rotatable credential can be re-pasted; a derivation key cannot, and the data derived from it
    /// becomes permanently un-re-derivable through a button the UI otherwise presents as the remedy.
    /// </summary>
    [Fact]
    public async Task ClearingADerivationKey_SaysThePriorDataBecomesUnRederivable()
    {
        // await using: MudDialogProvider registers PointerEventsNoneService, which implements only
        // IAsyncDisposable — a synchronous dispose of the container throws.
        await using var ctx = NewContext();
        var row = RenderWithDialogs(ctx, SecretSettingState.Set, () =>
            Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent)), isDerivationKey: true);

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Clear")).Click();

        Assert.Contains("permanently un-re-derivable", row.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 14 — the combination that matters most and was previously untested. Clearing an
    /// <c>Unreadable</c> DERIVATION key is the point of no return, reached through the button the UI
    /// otherwise presents as the remedy: the ciphertext is still recoverable if the original key ring
    /// can be restored, and Clear deletes it permanently. Both warnings have to be on screen together.
    /// </summary>
    [Fact]
    public async Task ClearingAnUnreadableDerivationKey_CarriesBothWarnings()
    {
        // await using: MudDialogProvider registers PointerEventsNoneService, which implements only
        // IAsyncDisposable — a synchronous dispose of the container throws.
        await using var ctx = NewContext();
        var row = RenderWithDialogs(ctx, SecretSettingState.Unreadable, () =>
            Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent)), isDerivationKey: true);

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Clear")).Click();

        Assert.Contains("permanently un-re-derivable", row.Markup, StringComparison.Ordinal);
        Assert.Contains("Data Protection key ring can still be restored", row.Markup, StringComparison.Ordinal);
    }

    /// <summary>A rotatable credential does not carry the derivation-key warning.</summary>
    [Fact]
    public async Task ClearingARotatableCredential_DoesNotClaimItIsUnRederivable()
    {
        // await using: MudDialogProvider registers PointerEventsNoneService, which implements only
        // IAsyncDisposable — a synchronous dispose of the container throws.
        await using var ctx = NewContext();
        var row = RenderWithDialogs(ctx, SecretSettingState.Set, () =>
            Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent)));

        row.FindAll("button.mud-button-root").First(button => button.TextContent.Contains("Clear")).Click();

        Assert.Contains("Clear " + Title + "?", row.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("un-re-derivable", row.Markup, StringComparison.Ordinal);
    }

    // ── AC 39 — @key, at the component level ────────────────────────────────────────────────────

    /// <summary>
    /// AC 39 — the credential-crossing bug <c>@key</c> exists to prevent. A secret row is the first row
    /// whose draft state lives in a COMPONENT-LOCAL field rather than in the page's key-indexed
    /// dictionaries, so positional re-matching under a filter change would carry a typed value across
    /// into a different credential's row.
    ///
    /// <para>
    /// Driven through a two-row host built with <see cref="RenderTreeBuilder"/> rather than through the
    /// page, because the registry ships exactly one key — so the page cannot exhibit a reorder, while
    /// the component contract this pins is what the page's <c>@key</c> relies on.
    /// </para>
    /// </summary>
    [Fact]
    public void Reordering_KeyedRows_DoesNotCarryATypedValueIntoAnotherRow()
    {
        using var ctx = NewContext();

        var order = new[] { "alpha", "beta" };
        var host = ctx.Render<KeyedRowHost>(p => p.Add(h => h.Keys, order));

        // Type into the FIRST field's input. Both are NotSet, so both render their input inline.
        host.FindAll("input.odc-input")[0].Input("sk-alpha-secret");
        Assert.Equal("sk-alpha-secret", host.Find("input.odc-input").GetAttribute("value"));

        // A filter change reorders the list. With @key, "alpha"'s component moves with its key.
        host.Render(p => p.Add(h => h.Keys, new[] { "beta", "alpha" }));

        // Exactly one input still carries the typed value…
        var carrying = host.FindAll("input.odc-input")
            .Where(input => input.GetAttribute("value") == "sk-alpha-secret")
            .ToList();
        Assert.Single(carrying);

        // …and it is still alpha's field, not beta's. The label the input points at names it.
        var labelFor = carrying[0].GetAttribute("id");
        var label = host.FindAll("label.odc-sfield-label")
            .Single(candidate => candidate.GetAttribute("for") == labelFor);
        Assert.Contains("alpha", label.TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Renders the row beneath a <c>MudDialogProvider</c>. <c>OdsFormDialog</c> composes
    /// <c>OdsModal</c>, which is an INLINE <c>MudDialog</c> — and an inline MudBlazor dialog renders
    /// through the provider, so without one in the tree the confirmation is simply absent and the
    /// three destructive-action assertions would silently test nothing.
    /// </summary>
    private static IRenderedComponent<DialogHost> RenderWithDialogs(
        BunitContext ctx,
        SecretSettingState state,
        Func<Task<ApiResult>> onClear,
        bool isDerivationKey = false,
        Action<string>? onAnnounce = null) =>
        ctx.Render<DialogHost>(p =>
        {
            p.Add(host => host.State, state)
                .Add(host => host.IsDerivationKey, isDerivationKey)
                .Add(host => host.OnClear, onClear);

            if (onAnnounce is not null)
            {
                p.Add(host => host.OnAnnounce, EventCallback.Factory.Create<string>(ctx, onAnnounce));
            }
        });

    private sealed class DialogHost : ComponentBase
    {
        [Parameter] public SecretSettingState State { get; set; }

        [Parameter] public bool IsDerivationKey { get; set; }

        [Parameter] public Func<Task<ApiResult>>? OnClear { get; set; }

        [Parameter] public EventCallback<string> OnAnnounce { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudDialogProvider>(0);
            builder.CloseComponent();

            builder.OpenComponent<OdsSecretSettingField>(1);
            builder.AddComponentParameter(2, nameof(OdsSecretSettingField.SecretKey), Key);
            builder.AddComponentParameter(3, nameof(OdsSecretSettingField.Title), Title);
            builder.AddComponentParameter(4, nameof(OdsSecretSettingField.State), State);
            builder.AddComponentParameter(5, nameof(OdsSecretSettingField.IsDerivationKey), IsDerivationKey);
            builder.AddComponentParameter(6, nameof(OdsSecretSettingField.OnClear), OnClear);
            builder.AddComponentParameter(7, nameof(OdsSecretSettingField.OnAnnounce), OnAnnounce);
            builder.CloseComponent();
        }
    }

    private static string? LastFocusTarget(BunitContext ctx) =>
        ctx.JSInterop.Invocations["odsFocusById"]
            .Select(invocation => invocation.Arguments[0] as string)
            .LastOrDefault();

    /// <summary>
    /// A minimal two-row host with <c>@key</c> on each row, built in C# because this project has no
    /// Razor SDK and needs none: the point is the keyed rendering, not the markup around it.
    /// </summary>
    private sealed class KeyedRowHost : ComponentBase
    {
        [Parameter] public string[] Keys { get; set; } = [];

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            foreach (var key in Keys)
            {
                builder.OpenComponent<OdsSecretSettingField>(0);
                builder.SetKey(key);
                builder.AddComponentParameter(1, nameof(OdsSecretSettingField.SecretKey), key);
                builder.AddComponentParameter(2, nameof(OdsSecretSettingField.Title), key);
                builder.AddComponentParameter(3, nameof(OdsSecretSettingField.State), SecretSettingState.NotSet);
                builder.CloseComponent();
            }
        }
    }
}
