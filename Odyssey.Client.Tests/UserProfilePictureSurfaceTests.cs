using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The four client surfaces the user profile picture touches (issue #94 §3) — <c>/account</c>'s
/// control and page header, and <c>/users</c>' list rows and detail panel.
///
/// <para>
/// These are source lints because the defects they catch <b>render perfectly</b>. A picture passed a
/// tint as a <c>Class</c> instead of <c>Bg</c>/<c>Fg</c> shows a plausible neutral monogram; a
/// page-level failed flag blanks every row only once an image actually 404s; a decorative image given
/// a descriptive alt reads twice to a screen reader and looks identical to everyone else.
/// </para>
/// </summary>
public class UserProfilePictureSurfaceTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([ClientSource.Root, .. parts]));

    private static string WithoutComments(string source)
    {
        source = Regex.Replace(source, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(source, @"^\s*///?.*$", string.Empty, RegexOptions.Multiline);
    }

    // ── AC 33: alt text follows what the image IS ─────────────────────────────────────────────────

    /// <summary>
    /// AC 33. Three surfaces, three answers, and each is the caller's call rather than something the
    /// mark guesses: the <c>/account</c> picture is the subject of its own controls, so it carries a
    /// descriptive alt; a <c>/users</c> row image is decorative because the adjacent cell names the
    /// person; the detail panel's neighbour is a <b>mono user id</b>, so there it carries the subject's
    /// resolved display name.
    /// </summary>
    [Fact]
    public void Each_surface_names_its_image_according_to_what_sits_beside_it()
    {
        var account = WithoutComments(Read("Pages", "AccountProfileSection.razor"));
        var rows = WithoutComments(Read("Pages", "Users.razor"));
        var detail = WithoutComments(Read("Pages", "UserDetailPanel.razor"));

        // The subject of its own controls — never decorative.
        Assert.Contains("Alt=\"Your profile picture\"", account, StringComparison.Ordinal);

        // Decorative: the name cell beside it already identifies the person, so a descriptive alt would
        // announce the same person twice.
        Assert.Contains("<UserProfileMark User=\"user\"", rows, StringComparison.Ordinal);
        Assert.Contains("Alt=\"\"", rows, StringComparison.Ordinal);

        // NOT decorative: the neighbour is a mono user id, not a name.
        Assert.Contains("Alt=\"@UserDisplay.DisplayName(User)\"", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("<UserProfileMark User=\"User\" CanReadProfileImages=\"CanReadProfileImages\"\n"
            + "                         Size=\"OdsSize.Lg\" Alt=\"\"", detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page header's mark stays <c>aria-hidden</c> and its image <c>alt=""</c>: the header title
    /// beside it already names the person, so the mark is decorative whichever of the two states it
    /// renders. Getting this wrong announces the user's name twice on every page load.
    /// </summary>
    [Fact]
    public void The_account_page_header_mark_stays_decorative()
    {
        var markup = WithoutComments(Read("Pages", "Account.razor"));
        var leading = markup[markup.IndexOf("<Leading>", StringComparison.Ordinal)..];
        leading = leading[..leading.IndexOf("</Leading>", StringComparison.Ordinal)];

        Assert.Contains("aria-hidden=\"true\"", leading, StringComparison.Ordinal);
        Assert.Contains("alt=\"\"", leading, StringComparison.Ordinal);
        Assert.Contains("loading=\"lazy\"", leading, StringComparison.Ordinal);
        Assert.Contains("decoding=\"async\"", leading, StringComparison.Ordinal);
    }

    // ── AC 38: the tint survives the move ─────────────────────────────────────────────────────────

    /// <summary>
    /// AC 38. The header monogram keeps its <c>.ph-avatar</c> tone, and <c>Account.razor.css</c>
    /// retains no dead rule.
    ///
    /// <para>
    /// The tone matters because it is a <i>soft</i> <c>color-mix</c> tint, not the avatar component's
    /// solid primary fill — taking the default would silently change the monogram's appearance and its
    /// 4.5:1 pairing, with nothing failing. The design system's Account kit keeps the existing
    /// <c>.ph-avatar</c> span and renders the picture <b>inside</b> it, which preserves the tone by
    /// construction and leaves the rule live rather than dead.
    /// </para>
    /// </summary>
    [Fact]
    public void The_account_header_keeps_its_own_tone_and_its_rule_stays_live()
    {
        var markup = WithoutComments(Read("Pages", "Account.razor"));
        var css = Read("Pages", "Account.razor.css");

        // The rule is still USED — so it is not dead, and the tint it carries is still the one in force.
        Assert.Contains("class=\"ph-avatar\"", markup, StringComparison.Ordinal);
        Assert.Contains(".ph-avatar {", css, StringComparison.Ordinal);
        Assert.Contains("color-mix(in srgb, var(--mud-palette-primary) 14%, transparent)", css, StringComparison.Ordinal);

        // ...and the picture fills the same circle, so the mark does not change size or shape between
        // its two states.
        Assert.Contains(".ph-avatar img {", css, StringComparison.Ordinal);
        Assert.Contains("object-fit: cover;", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// The profile card's 64px monogram span is <b>gone</b>, not merely unused. Its replacement is the
    /// design-system field, whose avatar is the <c>lg</c> step written as an inline style — so a
    /// leftover rule would be dead and unreachable, and the tint it carried has to travel as the
    /// field's <c>Bg</c>/<c>Fg</c> pair instead.
    /// </summary>
    [Fact]
    public void The_profile_cards_old_monogram_rule_is_removed_and_its_tint_travels_as_a_value()
    {
        var css = Read("Pages", "AccountProfileSection.razor.css");
        var markup = Read("Pages", "AccountProfileSection.razor");

        Assert.DoesNotContain(".acc-avatar-xl {", css, StringComparison.Ordinal);
        Assert.DoesNotContain("acc-avatar-xl", markup, StringComparison.Ordinal);

        // Scoped CSS does not cross a component boundary, so the tint is a VALUE, not a class.
        Assert.Contains(
            "Bg=\"color-mix(in srgb, var(--mud-palette-primary) 14%, transparent)\"",
            markup, StringComparison.Ordinal);
        Assert.Contains("Fg=\"var(--mud-palette-primary)\"", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 36's automatable half. The <c>/users</c> role tint has to survive the move to
    /// <c>OdsAvatar</c>'s <c>Bg</c>/<c>Fg</c>: the <c>usr-av-*</c> rules are scoped to the page, and a
    /// <c>Class</c> would compile, render, and silently produce the default neutral tint on every row.
    /// </summary>
    [Fact]
    public void The_users_row_tint_travels_as_values_and_the_dead_rules_are_removed()
    {
        var mark = WithoutComments(Read("Pages", "UserProfileMark.razor"));
        var css = Read("Pages", "Users.razor.css");

        Assert.Contains("Bg=\"@UserDisplay.AvatarBg(User.Role)\"", mark, StringComparison.Ordinal);
        Assert.Contains("Fg=\"@UserDisplay.AvatarFg(User.Role)\"", mark, StringComparison.Ordinal);

        // The scoped rules the tints came from are GONE, not merely unused: the mark is a separate
        // component now, so a surviving `.usr-av-*` would be dead and unreachable while still reading
        // as the source of truth. Same rule as `.acc-avatar-xl` above.
        Assert.DoesNotContain(".usr-av-owner", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".avatar {", css, StringComparison.Ordinal);

        // Comments stripped: the doc comment on the replacement legitimately NAMES the removed helper
        // to say why it is gone, and a lint a doc comment can fail is a lint that gets the doc deleted.
        Assert.DoesNotContain(
            "AvatarClass", WithoutComments(Read("Pages", "UserDisplay.cs")), StringComparison.Ordinal);
    }

    /// <summary>
    /// The four role tints, pinned by VALUE. <c>UserDisplay</c> is now the only copy — the scoped rules
    /// they were carried across from are deleted — so nothing else can catch a drift, and a silently
    /// changed tint is exactly the class of defect that renders perfectly.
    /// </summary>
    [Theory]
    [InlineData("Owner", "color-mix(in srgb, var(--mud-palette-primary) 14%, transparent)", "var(--mud-palette-primary)")]
    [InlineData("Admin", "color-mix(in srgb, var(--mud-palette-tertiary) 18%, transparent)", "var(--mud-palette-tertiary)")]
    [InlineData("User", "color-mix(in srgb, var(--mud-palette-secondary) 16%, transparent)", "var(--mud-palette-secondary)")]
    [InlineData("Guest", "var(--mud-palette-action-disabled-background)", "var(--mud-palette-text-secondary)")]
    public void Each_role_keeps_the_tint_its_scoped_rule_declared(string role, string bg, string fg)
    {
        // Through the helper rather than by reading the switch arms: Guest is the DEFAULT arm, so a
        // source match would have to special-case it — and the thing under test is the value a caller
        // receives, not how the switch is written.
        Assert.Equal(bg, UserDisplayAccessor.AvatarBg(role));
        Assert.Equal(fg, UserDisplayAccessor.AvatarFg(role));
    }

    /// <summary>
    /// <c>UserDisplay</c> is <c>internal</c> to the client, and the test assembly sees it through
    /// <c>InternalsVisibleTo</c> — this shim just keeps the reflection-free call readable.
    /// </summary>
    private static class UserDisplayAccessor
    {
        public static string AvatarBg(string role) => Odyssey.Client.Pages.UserDisplay.AvatarBg(role);

        public static string AvatarFg(string role) => Odyssey.Client.Pages.UserDisplay.AvatarFg(role);
    }

    // ── The token drives every state, and the failed flag is per row ──────────────────────────────

    /// <summary>
    /// Nothing probes the byte endpoint to discover whether a picture exists. The token is the presence
    /// signal — an <c>&lt;img&gt;</c> plus an error handler cannot drive a button label, and probing a
    /// 50-row page would mean a mostly-404 request per row.
    /// </summary>
    [Fact]
    public void Presence_is_the_token_and_both_halves_of_renderable_are_checked()
    {
        foreach (var (name, source) in new[]
        {
            ("the /users mark", Read("Pages", "UserProfileMark.razor")),
            ("the /account control", Read("Pages", "AccountProfileSection.razor.cs")),
        })
        {
            var text = WithoutComments(source);

            // The SUBJECT half — the server nulls this for a subject the read would refuse.
            Assert.True(
                text.Contains("ProfileImageVersion", StringComparison.Ordinal),
                $"{name} does not read the image-version token.");

            // The CALLER half — a session predating the claim's deploy does not hold it, and without
            // this check a /users render fires one request per row that all 403.
            Assert.True(
                text.Contains("CanReadProfileImages", StringComparison.Ordinal),
                $"{name} emits an image without checking the caller's claim.");
        }
    }

    /// <summary>
    /// The failed-image flag is <b>per mark</b>, keyed on the token. One page-level flag would blank
    /// every avatar on a 50-row table after a single failure — and keying on nothing at all would
    /// re-request an image that is genuinely gone on every unrelated re-render.
    /// </summary>
    [Fact]
    public void The_failed_image_state_is_per_mark_and_reset_when_the_token_changes()
    {
        var mark = WithoutComments(Read("Pages", "UserProfileMark.razor"));

        Assert.Contains("private bool _imageFailed;", mark, StringComparison.Ordinal);
        Assert.Contains("_failedFor != User.ProfileImageVersion", mark, StringComparison.Ordinal);
        Assert.Contains("OnError=\"OnImageError\"", mark, StringComparison.Ordinal);
    }

    /// <summary>
    /// A load failure is silent on every surface. An identity token that cannot load is not the user's
    /// problem to act on, so there is no toast, no alert and no broken-image glyph — just the monogram.
    /// </summary>
    [Fact]
    public void A_failed_image_surfaces_nothing_to_the_user()
    {
        var mark = WithoutComments(Read("Pages", "UserProfileMark.razor"));
        var field = WithoutComments(Read("Components", "OdsProfilePictureField.razor"));

        foreach (var text in new[] { mark, field })
        {
            Assert.DoesNotContain("Snackbar", text, StringComparison.Ordinal);
            Assert.DoesNotContain("role=\"alert\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("broken_image", text, StringComparison.Ordinal);
        }
    }

    // ── The URL is never hand-built ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>?v=</c> key is what makes the browser re-request after a replace — without it the
    /// <c>src</c> string is unchanged, Blazor emits no DOM update, and the read path's
    /// <c>no-cache</c>/<c>ETag</c> machinery never comes into play — so the URL belongs to the typed
    /// client.
    /// </summary>
    [Fact]
    public void No_surface_hand_builds_a_profile_image_url()
    {
        var offenders = ClientSource.SourceFiles()
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"""[^""]*api/profile-images"))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Profile-image URLs must come from IProfileApiClient.ImageUrl: " + string.Join(", ", offenders));
    }

    // ── The pre-claim window is an honest dead end, not a silent one ──────────────────────────────

    /// <summary>
    /// A session that predates the read claim's deploy would upload successfully, receive a <c>200</c>
    /// and a token, and see nothing. The control is disabled with a reason rather than accepting a
    /// write it cannot show.
    ///
    /// <para>
    /// <b>Folding the claim into the token is not the fix</b> — a <c>null</c> token would flip the
    /// button back to "Add picture" after a successful upload, which is worse than an honest disabled
    /// state. And the same disable covers <b>Remove</b>, so the reason has to say how to clear it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_pre_claim_window_disables_the_control_and_says_how_to_clear_it()
    {
        var code = Read("Pages", "AccountProfileSection.razor.cs");
        var markup = Read("Pages", "AccountProfileSection.razor");

        Assert.Contains("ImageControlsDisabled => !CanReadProfileImages", code, StringComparison.Ordinal);
        Assert.Contains("Disabled=\"@ImageControlsDisabled\"", markup, StringComparison.Ordinal);

        // Self-healing in one sign-out, and the message says so rather than reading as a failure the
        // user caused.
        Assert.Contains("Sign out and back in", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removal deletes the stored bytes and is not undone by re-picking the same crop, so it sits
    /// behind a confirmation — owned by the page, which knows whose picture it is, rather than by the
    /// field, which raises <c>OnRemove</c> and nothing else.
    /// </summary>
    [Fact]
    public void Removing_a_picture_is_confirmed_and_announced()
    {
        var markup = Read("Pages", "AccountProfileSection.razor");
        var code = Read("Pages", "AccountProfileSection.razor.cs");
        var field = WithoutComments(Read("Components", "OdsProfilePictureField.razor"));

        Assert.Contains("_removeOpen", markup, StringComparison.Ordinal);
        Assert.Contains("OdsButtonVariant.Danger", markup, StringComparison.Ordinal);
        Assert.Contains("RemoveImageConfirmedAsync", code, StringComparison.Ordinal);

        // /account has no general-purpose status region to borrow — its only one is a static info alert
        // — so the completed removal goes through the reusable live announcer.
        Assert.Contains("<OdsLiveAnnouncer", markup, StringComparison.Ordinal);
        Assert.Contains("Profile picture removed", code, StringComparison.Ordinal);

        // The field itself owns no confirmation and no announcer.
        Assert.DoesNotContain("OdsLiveAnnouncer", field, StringComparison.Ordinal);
        Assert.DoesNotContain("OdsFormDialog", field, StringComparison.Ordinal);
    }

    /// <summary>
    /// The PAGE owns the token, not the section (issue #94 §3 step 6). The control lives in the profile
    /// section and the mark in the header's Leading fragment — different components — so a
    /// section-local token would satisfy the upload and leave the header showing the monogram, and no
    /// acceptance criterion would catch it.
    /// </summary>
    [Fact]
    public void The_page_owns_the_image_token_so_the_header_re_renders_with_it()
    {
        var section = Read("Pages", "AccountProfileSection.razor.cs");
        var page = Read("Pages", "Account.razor.cs");

        // The section raises the whole DTO up rather than keeping the new token to itself.
        Assert.Contains(
            "ProfileChanged.InvokeAsync(Profile with { ProfileImageVersion = version })",
            section, StringComparison.Ordinal);
        Assert.Contains(
            "ProfileChanged.InvokeAsync(Profile with { ProfileImageVersion = null })",
            section, StringComparison.Ordinal);

        // ...and the page's header reads the page's own copy.
        Assert.Contains("_profile.ProfileImageVersion is { } version", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The preview is the <c>lg</c> step, and it is not configurable. <c>OdsAvatar</c> writes its size
    /// as an <i>inline</i> style, which no class rule beats and which a page's scoped CSS would not
    /// match anyway — so a larger preview needs a new size step on the component and on the
    /// design-system Avatar, not a local override.
    /// </summary>
    [Fact]
    public void The_profile_picture_field_pins_the_lg_size_step()
    {
        var field = WithoutComments(Read("Components", "OdsProfilePictureField.razor"));

        Assert.Equal(2, Regex.Matches(field, @"Size=""OdsSize\.Lg""").Count);
        Assert.DoesNotContain("public OdsSize Size", field, StringComparison.Ordinal);
    }
}
