using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Dtos.Application;
using Odyssey.Client.Pages;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The /users edit panel's account-disable copy, rendered (issue #442).
///
/// <para>
/// These pin the two sentences the disable fix corrected. Before it, both surfaces promised an
/// immediate sign-out that the server never delivered — disabling wrote a lockout, and a lockout is
/// only consulted at sign-in, so an already-issued cookie kept working indefinitely. The server now
/// rotates the security stamp, and <c>SecurityStampValidator</c>'s one-minute interval makes the
/// promise a BOUNDED one. A copy claim about what an administrator's click does to a live session is
/// the kind an incident response is planned around, so it is asserted rather than trusted: nothing
/// else in the build fails when this text drifts back to the stronger claim the server cannot keep.
/// </para>
/// </summary>
public class UserEditPanelTests
{
    /// <summary>The "Account enabled" toggle's helper line — what disabling does, before it is done.</summary>
    private const string ToggleDescription =
        "Disabling applies a backend lockout and ends the user's active sessions within a minute.";

    /// <summary>The guard's confirmation line — the same promise, at the point of no return.</summary>
    private const string Confirmation =
        "Disable this account — the user is locked out, and their active sessions end within a minute.";

    /// <summary>
    /// The claims the server cannot keep. A disabled user is not signed out by the act of disabling;
    /// their session is refused on the validator's next re-check, which is up to a minute away.
    /// </summary>
    private static readonly string[] OverpromisingPhrases =
        ["signs the user out", "signed out", "immediately", "at once"];

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();

        // Loose: the panel's only interop is MudBlazor's own initialisation (the role MudSelect), and
        // none of these assertions read an invocation.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        // The role picker is a MudSelect, and MudBlazor's popover service refuses to initialise one
        // without a provider hosted somewhere in the tree — the app's MainLayout supplies it. Hosted
        // once per context, since a second subscriber to its section outlet throws.
        ctx.Render<MudPopoverProvider>();
        return ctx;
    }

    private static ExistingUser AUser(bool enabled) => new()
    {
        Id = "u-1",
        UserName = "ada.lovelace",
        DisplayName = "Ada Lovelace",
        Email = "ada@example.test",
        EmailConfirmed = true,
        Enabled = enabled,
        Role = "User",
    };

    private static IRenderedComponent<UserEditPanel> Render(BunitContext ctx, bool enabled = true) =>
        ctx.Render<UserEditPanel>(parameters => parameters
            .Add(panel => panel.User, AUser(enabled))
            .Add(panel => panel.Roles, [new ExistingRole { Name = "User" }, new ExistingRole { Name = "Admin" }]));

    /// <summary>
    /// Collapses runs of whitespace, so an assertion pins the words rather than the Razor source's
    /// line wrapping — prose in markup carries its newlines and indentation into the render.
    /// </summary>
    private static string Flatten(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(markup, @"\s+", " ").Trim();

    /// <summary>The checkbox inside the toggle row carrying the given title.</summary>
    private static AngleSharp.Dom.IElement Toggle(IRenderedComponent<UserEditPanel> panel, string title) =>
        panel.FindAll(".usr-toggle-row")
            .First(toggleRow => toggleRow.QuerySelector(".usr-toggle-ttl")!.TextContent.Trim() == title)
            .QuerySelector("input[type=checkbox]")!;

    /// <summary>
    /// The helper line states the bounded promise before the toggle is touched — this is the only
    /// place an administrator learns that a disable does not end a session on its next request.
    /// </summary>
    [Fact]
    public async Task TheEnabledToggle_DescribesABoundedSessionEnd()
    {
        await using var ctx = NewContext();
        var panel = Render(ctx);

        var description = panel.FindAll(".usr-toggle-row")
            .First(toggleRow => toggleRow.QuerySelector(".usr-toggle-ttl")!.TextContent.Trim() == "Account enabled")
            .QuerySelector(".usr-toggle-desc")!;

        Assert.Equal(ToggleDescription, Flatten(description.TextContent));
    }

    /// <summary>
    /// The guard's confirmation carries the same bounded promise, and it is the copy that matters
    /// most: it is read at the moment the administrator commits, which is when they decide whether
    /// disabling alone is enough to contain an incident.
    /// </summary>
    [Fact]
    public async Task DisablingAnEnabledAccount_ConfirmsABoundedSessionEnd()
    {
        await using var ctx = NewContext();
        var panel = Render(ctx);

        Toggle(panel, "Account enabled").Change(false);

        var confirmations = panel.FindAll(".usr-guard-list li");
        Assert.Equal(Confirmation, Flatten(Assert.Single(confirmations).TextContent));
    }

    /// <summary>
    /// Neither surface may claim the sign-out the server does not perform. Asserted as a negative over
    /// the whole panel, because the drift this guards against is a reworded sentence, not a deleted
    /// one — an equality assertion on new wording would simply be updated alongside it.
    /// </summary>
    [Fact]
    public async Task NeitherSurface_PromisesAnImmediateSignOut()
    {
        await using var ctx = NewContext();
        var panel = Render(ctx);

        Toggle(panel, "Account enabled").Change(false);

        var markup = Flatten(panel.Markup);
        foreach (var phrase in OverpromisingPhrases)
        {
            Assert.DoesNotContain(phrase, markup, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The guard is scoped to the one transition that ends sessions. Re-enabling a disabled account and
    /// changing an unrelated flag both leave it absent — a guard that fired on every edit would be
    /// dismissed unread, and this is the panel's mirror of the server-side test that keeps stamp
    /// rotation off the unrelated branches.
    /// </summary>
    [Fact]
    public async Task TheGuard_AppearsOnlyWhenAnEnabledAccountIsBeingDisabled()
    {
        await using var ctx = NewContext();

        var enabling = Render(ctx, enabled: false);
        Toggle(enabling, "Account enabled").Change(true);
        Assert.Empty(enabling.FindAll(".usr-guard-list li"));

        var confirming = Render(ctx);
        Toggle(confirming, "Email confirmed").Change(false);
        Assert.Empty(confirming.FindAll(".usr-guard-list li"));
    }
}
