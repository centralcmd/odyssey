using System.Text.RegularExpressions;
using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client surfaces a contact image touches (issue #86 §3, §5.7) — the record card's image mark, the
/// avatar component's move to a plain <c>&lt;img&gt;</c>, the crop dialog's accessibility contract, and
/// the claim/archival gating on the row menu.
///
/// <para>
/// These are source lints where the defect IS a literal in source — a missing <c>loading</c> attribute,
/// a cap typed as a number, an <c>OdsModal</c> hand-rolled instead of composed. A behavioural test
/// cannot see any of those; it would render exactly as well without them and simply stop honouring the
/// contract.
/// </para>
/// </summary>
public class ContactAvatarSurfaceTests
{
    private static string Component(string name) =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Components", name));

    private static string Page(string name) =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", name));

    /// <summary>
    /// The source with its comments removed. Documentation legitimately DISCUSSES the very things these
    /// lints look for — <c>role="dialog"</c>, <c>&lt;input type="range"&gt;</c>, a megabyte figure — and
    /// a test a doc comment can fail is a test that gets the docs deleted. Same reason
    /// <see cref="SettingFieldTests"/> strips them.
    /// </summary>
    private static string WithoutComments(string source)
    {
        source = Regex.Replace(source, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(source, @"^\s*///?.*$", string.Empty, RegexOptions.Multiline);
    }

    // ── OdsAvatar renders an <img>, not MudImage (AC 33) ──────────────────────────────────────────

    [Fact]
    public void OdsAvatar_renders_a_plain_img_with_lazy_loading_and_an_error_hook()
    {
        var markup = Component("OdsAvatar.razor");

        // MudImage exposes none of loading, decoding or onerror naturally, and this was the client's
        // only MudImage call site — OdsPhotoTile and JournalPhotoGallery already use a bare <img>.
        Assert.DoesNotContain("<MudImage", markup, StringComparison.Ordinal);
        Assert.Contains("<img src=\"@Src\"", markup, StringComparison.Ordinal);
        Assert.Contains("loading=\"lazy\"", markup, StringComparison.Ordinal);
        Assert.Contains("decoding=\"async\"", markup, StringComparison.Ordinal);
        Assert.Contains("@onerror=", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MudImage_has_no_call_sites_left_in_the_client()
    {
        var offenders = ClientSource.RazorFilesIn("Pages", "Components")
            .Where(file => File.ReadAllText(file).Contains("<MudImage", StringComparison.Ordinal))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "MudImage is back: it exposes no loading/decoding/onerror, which every image surface needs. "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void OdsAvatar_exposes_the_three_parameters_the_design_system_gained()
    {
        var markup = Component("OdsAvatar.razor");

        Assert.Contains("public bool Square", markup, StringComparison.Ordinal);
        Assert.Contains("public OdsAvatarFit Fit", markup, StringComparison.Ordinal);
        Assert.Contains("public EventCallback OnError", markup, StringComparison.Ordinal);
    }

    // ── The record card's image mark (AC 31, 32) ──────────────────────────────────────────────────

    [Fact]
    public void The_record_card_falls_back_to_its_type_glyph_when_the_image_fails()
    {
        var markup = Component("OdsRecordCard.razor");

        // Under no-cache a deleted image 404s on EVERY revalidation rather than hiding behind a cached
        // 200, so the fallback is a mechanism the state needs, not a nicety.
        Assert.Contains("@onerror=\"OnImageError\"", markup, StringComparison.Ordinal);
        Assert.Contains("!_imageFailed", markup, StringComparison.Ordinal);
        Assert.Contains("else if (!string.IsNullOrEmpty(Icon))", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_record_cards_image_mark_is_decorative_and_lazy()
    {
        var markup = WithoutComments(Component("OdsRecordCard.razor"));
        var imgTag = markup[markup.IndexOf("<img src=\"@image.Src\"", StringComparison.Ordinal)..];
        imgTag = imgTag[..imgTag.IndexOf("/>", StringComparison.Ordinal)];

        // alt="" because the adjacent cell already names the record; lazy because a 50-row view under
        // no-cache would otherwise issue fifty conditional GETs at once.
        Assert.Contains("alt=\"\"", imgTag, StringComparison.Ordinal);
        Assert.Contains("loading=\"lazy\"", imgTag, StringComparison.Ordinal);
        Assert.Contains("decoding=\"async\"", imgTag, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OdsAvatarFit.Cover, true, "odc-record-mark img round")]
    [InlineData(OdsAvatarFit.Contain, false, "odc-record-mark img ground")]
    public void The_marks_class_names_the_framing_and_only_the_framing(OdsAvatarFit fit, bool round, string expected)
    {
        // A person's photo is a circle; an organization's logo is a contained rounded rect on a neutral
        // ground. What changes is the mark's CONTENT and its own ground — never the card's accent.
        var image = new OdsRecordImage("https://example.test/avatar", fit, round);
        var method = typeof(OdsRecordCard).GetMethod(
            "MarkClass",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.Equal(expected, method!.Invoke(null, [image]));
    }

    [Fact]
    public void The_contacts_card_still_passes_the_type_accent_alongside_the_image()
    {
        var markup = Page("ContactsCard.razor");

        // Accent/AccentSoft set --rec/--rec-soft for every icon chip and chart in the card, so they are
        // not the image's to take. Icon is passed too, because it is the fallback.
        Assert.Contains("Image=\"@AvatarImage(c)\"", markup, StringComparison.Ordinal);
        Assert.Contains("Icon=\"@typeMeta.Icon\"", markup, StringComparison.Ordinal);
        Assert.Contains("Accent=\"@typeMeta.Color\"", markup, StringComparison.Ordinal);
        Assert.Contains("AccentSoft=\"@typeMeta.Soft\"", markup, StringComparison.Ordinal);
    }

    // ── The crop dialog's accessibility contract (AC 34, 35) ──────────────────────────────────────

    [Fact]
    public void The_crop_dialog_composes_the_form_dialog_rather_than_hand_rolling_a_dialog()
    {
        var markup = WithoutComments(Page("ContactAvatarDialog.razor"));

        // overlay-focus.js already owns focus trapping and focus return, and OdsModal owns the Escape
        // handling — so Escape closing and returning focus to the invoking control comes for free, and
        // there is no keyboard trap to get wrong a second time.
        Assert.Contains("<OdsFormDialog", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"dialog\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_crop_controls_are_native_range_inputs_each_labelled_and_value_texted()
    {
        var markup = WithoutComments(Page("ContactAvatarDialog.razor"));

        foreach (var id in new[] { "cav-zoom", "cav-x", "cav-y" })
        {
            Assert.Contains($"for=\"{id}\"", markup, StringComparison.Ordinal);
            Assert.Contains($"id=\"{id}\" type=\"range\"", markup, StringComparison.Ordinal);
        }

        // Arrow / Home / End work BY CONSTRUCTION on a native range. That is why the widget is built
        // from three of them rather than from a custom draggable surface.
        Assert.Equal(3, Regex.Matches(markup, @"type=""range""").Count);
        Assert.Equal(3, Regex.Matches(markup, @"aria-valuetext=").Count);
    }

    [Fact]
    public void The_preview_canvas_is_hidden_from_assistive_tech_and_described_in_text_beside_it()
    {
        var markup = Page("ContactAvatarDialog.razor");

        // A canvas exposes no accessible structure at all, and the three ranges already carry the state.
        Assert.Contains("<canvas @ref=\"_canvas\" aria-hidden=\"true\">", markup, StringComparison.Ordinal);
        Assert.Contains("class=\"cav-state\">@StateText<", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_failure_is_announced_in_place_and_a_field_failure_is_not()
    {
        var markup = Page("ContactAvatarDialog.razor");
        var code = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "ContactAvatarDialog.razor.cs"));

        // Not attributable to a control → role="alert", focus stays put, and the text is the server's
        // own ProblemDetails message, which names the actual limit.
        Assert.Contains("class=\"cav-alert\" role=\"alert\"", markup, StringComparison.Ordinal);
        Assert.Contains("result.Problem?.Detail", code, StringComparison.Ordinal);

        // Attributable to a control → rendered on that control and focus MOVES there. Deliberately not
        // role="alert" as well: focusing a control whose description is the error already announces it,
        // and both would announce it twice.
        Assert.Contains("_focusDropzone = true", code, StringComparison.Ordinal);
        Assert.Contains("focusWithin", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_upload_field_links_its_error_to_its_dropzone()
    {
        var markup = Component("OdsFileUpload.razor");

        Assert.Contains("aria-invalid=", markup, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"@ErrorId\"", markup, StringComparison.Ordinal);
    }

    // ── No client-side copy of a server cap (AC 38's other half) ──────────────────────────────────

    [Fact]
    public void The_crop_dialog_names_no_limit_as_a_literal()
    {
        var markup = WithoutComments(Page("ContactAvatarDialog.razor"));
        var code = WithoutComments(
            File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "ContactAvatarDialog.razor.cs")));

        foreach (var (name, text) in new[] { ("markup", markup), ("code-behind", code) })
        {
            Assert.False(
                Regex.IsMatch(text, @"\d+ MB", RegexOptions.None),
                $"The crop dialog's {name} states a literal megabyte limit; interpolate the effective cap.");
            Assert.False(
                Regex.IsMatch(text, @"\d+L? \* 1024 \* 1024", RegexOptions.None),
                $"The crop dialog's {name} holds a byte-sized cap; it belongs on ContactAvatarLimits.");
            Assert.DoesNotContain("1024 ×", text, StringComparison.Ordinal);
        }

        // The stored cap comes from the live instance value, tightened by the shared constant. min is
        // the only correct direction: a surface may be stricter, but it must never override a cap an
        // administrator has lowered.
        Assert.Contains("UploadLimits.GetAsync()", code, StringComparison.Ordinal);
        Assert.Contains("TightenTo(ContactAvatarLimits.MaxAvatarMegabytes)", code, StringComparison.Ordinal);
        Assert.Contains("Math.Min(", code, StringComparison.Ordinal);
    }

    // ── Row-menu gating (AC 39) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Avatar_actions_are_gated_on_contacts_update_and_hidden_on_an_archived_contact()
    {
        var code = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "ContactsCard.razor.cs"));
        var block = code[code.IndexOf("if (_canUpdate && c.Archived is null)", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("\n        }\n", StringComparison.Ordinal)];

        // Archival is a DISPLAY state: the image keeps rendering, but it stops being editable like
        // everything else on the record.
        Assert.Contains("OpenAvatarDialog", block, StringComparison.Ordinal);
        Assert.Contains("RequestAvatarRemoval", block, StringComparison.Ordinal);
    }

    [Fact]
    public void An_archived_contacts_image_still_renders()
    {
        var code = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "ContactsCard.razor.cs"));
        var mark = code[code.IndexOf("private OdsRecordImage? AvatarImage(", StringComparison.Ordinal)..];
        mark = mark[..mark.IndexOf("\n\n", StringComparison.Ordinal)];

        // The mark is keyed on AvatarFileId alone. Archival must not reach it — that would DELETE the
        // picture from the UI, which is a lifecycle change dressed up as a display one.
        Assert.DoesNotContain("Archived", mark, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_an_image_is_confirmed_before_it_happens()
    {
        var markup = Page("ContactsCard.razor");

        // Removal deletes the FILE, not just the reference — the one contact-image action that is not
        // undone by re-picking the same crop.
        Assert.Contains("_avatarRemoveOpen", markup, StringComparison.Ordinal);
        Assert.Contains("RemoveAvatarConfirmedAsync", markup, StringComparison.Ordinal);
        Assert.Contains("OdsButtonVariant.Danger", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_an_image_invalidates_the_contacts_session_cache()
    {
        var code = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "ContactsCard.razor.cs"));

        // AC 41: a second open surface showing the same contact has to pick up the new AvatarFileId
        // without a manual reload. AvatarSavedAsync goes through RefreshContactAsync, which invalidates.
        var saved = code[code.IndexOf("private async Task AvatarSavedAsync()", StringComparison.Ordinal)..];
        Assert.Contains("RefreshContactAsync", saved[..200], StringComparison.Ordinal);

        var refresh = code[code.IndexOf("private async Task RefreshContactAsync(", StringComparison.Ordinal)..];
        Assert.Contains("ReferenceData.InvalidateContacts()", refresh[..600], StringComparison.Ordinal);
    }

    // ── The image URL is never hand-built ─────────────────────────────────────────────────────────

    [Fact]
    public void No_surface_hand_builds_an_avatar_url()
    {
        // The key (?v={avatarFileId}) is what makes revalidation after a replace a guaranteed 304
        // rather than a full re-download, so the URL belongs to the typed client.
        var offenders = ClientSource.RazorFilesIn("Pages", "Components")
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"""[^""]*api/contacts/[^""]*avatar"))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Avatar URLs must come from IContactsApiClient.AvatarUrl: " + string.Join(", ", offenders));
    }
}
