using System.Reflection;
using Odyssey.Dtos.Application;
using Odyssey.Client.Pages;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client mirror of the server's registry guard tests (issue #421 Wave 0b, AC 14).
///
/// <para>
/// Before the catalogue carried its own accessors, every setting key was named in <strong>three</strong>
/// places with no compile-time link between them — the catalogue, <c>ApplyLoaded</c>, and the
/// <c>SystemSettingsUpdate</c> initialiser in <c>Save</c>. Both omissions failed badly and neither was
/// caught by anything:
/// </para>
///
/// <list type="bullet">
/// <item>
/// A key with no <c>Load</c>: <c>ErrorFor</c> → <c>Capacity(key)</c> used to index the draft
/// dictionary, so a missing entry threw <see cref="KeyNotFoundException"/> from a <em>render</em> path —
/// and there is no <c>ErrorBoundary</c> anywhere in the client to catch it. A blank page.
/// </item>
/// <item>
/// A key with no <c>Write</c>: silent. The row edits, flags dirty, saves green, and changes nothing.
/// </item>
/// </list>
///
/// <para>
/// These are real reflection tests rather than the source-lints this project usually falls back to,
/// because <c>Odyssey.Client</c> already grants <c>InternalsVisibleTo</c> to this assembly — so the
/// catalogue can be inspected directly instead of being pattern-matched as text.
/// </para>
/// </summary>
public class SettingsCatalogueTests
{
    private static IReadOnlyList<Settings.SettingItem> AllItems => Settings.AllItems.ToList();

    /// <summary>
    /// Rows that are actions rather than stored settings — the database-export button. It has no
    /// backing key, so it is exempt from the accessor rules below.
    /// </summary>
    private static bool IsStoredSetting(Settings.SettingItem item) =>
        item.Control != Settings.SettingControl.Export;

    [Fact]
    public void Every_stored_row_declares_a_field_a_load_and_a_write()
    {
        var offenders = AllItems
            .Where(IsStoredSetting)
            .Where(item => item.Field is null || item.Load is null || item.Write is null)
            .Select(item => item.Key)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Rows missing Field/Load/Write — a missing Load blanks the page from a render path, a "
            + "missing Write saves green and changes nothing: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The client counterpart of the server's "exactly one descriptor per field". A writable field with
    /// no row is a setting the page can never save; two rows on one field would fight over it.
    /// </summary>
    [Fact]
    public void Every_writable_field_is_claimed_by_exactly_one_row()
    {
        var offenders = typeof(SystemSettingsUpdate)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (property.Name, Count: AllItems.Count(item => item.Field == property.Name)))
            .Where(entry => entry.Count != 1)
            .Select(entry => $"{entry.Name} ({entry.Count} rows)")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Every SystemSettingsUpdate property must be claimed by exactly one catalogue row: "
            + string.Join(", ", offenders));
    }

    /// <summary>Each row's declared field must actually exist on the write DTO — catches a rename.</summary>
    [Fact]
    public void Every_declared_field_exists_on_the_write_dto()
    {
        var offenders = AllItems
            .Where(item => item.Field is not null)
            .Where(item => typeof(SystemSettingsUpdate).GetProperty(
                item.Field!, BindingFlags.Public | BindingFlags.Instance) is null)
            .Select(item => $"{item.Key} -> {item.Field}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Rows whose Field names no SystemSettingsUpdate property: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Every row needs a non-empty Title AND Description: the page's search matches on both, so a row
    /// with neither is unreachable once an administrator types anything, and the sixteen groups are past
    /// the size where scrolling to find one is realistic.
    /// </summary>
    [Fact]
    public void Every_row_has_a_title_and_a_description()
    {
        var offenders = AllItems
            .Where(item => string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Description))
            .Select(item => item.Key)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Rows missing a Title or Description — search matches on both, so such a row is unreachable: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// Group names must be unique. Two same-named sections would emit duplicate group ids and two
    /// identical headings — which is why the file-analysis rows share ONE section despite splitting
    /// across both write claims.
    /// </summary>
    [Fact]
    public void Group_names_are_unique()
    {
        var duplicates = Settings.Sections
            .GroupBy(section => section.Group, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "Duplicate group names: " + string.Join(", ", duplicates));
    }

    /// <summary>
    /// A successful save must invalidate every session-lifetime cache that mirrors a setting, or an admin
    /// who lowers a cap and then opens the affected surface pre-validates against the old value for the
    /// rest of the session. Three caches now: import limits, upload limits, account limits.
    /// </summary>
    [Fact]
    public void A_successful_save_invalidates_every_client_side_limit_cache()
    {
        var codeBehind = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.cs"));

        Assert.Contains("ImportLimits.Invalidate();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("UploadLimits.Invalidate();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AccountLimits.Invalidate();", codeBehind, StringComparison.Ordinal);
    }

    /// <summary>Keys are the draft-store keys, so a duplicate would make two rows share one draft value.</summary>
    [Fact]
    public void Row_keys_are_unique()
    {
        var duplicates = AllItems
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "Duplicate row keys: " + string.Join(", ", duplicates));
    }

    /// <summary>
    /// A numeric row with no usable upper bound reports "Must be between 1 and 0" for every value.
    /// Rows that resolve their ceiling from the DTO at runtime (<c>MaxFrom</c>) are exempt, but must
    /// still carry a static <c>Max</c> as the load-phase fallback — the Save button evaluates
    /// <c>HasErrors</c> during the Loading phase, before any DTO exists.
    /// </summary>
    [Fact]
    public void Numeric_rows_have_a_usable_upper_bound()
    {
        var offenders = AllItems
            .Where(item => item.Control is Settings.SettingControl.Number
                                        or Settings.SettingControl.Size
                                        or Settings.SettingControl.Capacity)
            .Where(item => item.Max <= item.Min)
            .Select(item => $"{item.Key} (Min={item.Min}, Max={item.Max})")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Numeric rows need Max > Min, including as the MaxFrom load-phase fallback: "
            + string.Join(", ", offenders));
    }

    /// <summary>Same, for the decimal kind, which carries its bounds separately from the int pair.</summary>
    [Fact]
    public void Decimal_rows_have_a_usable_upper_bound()
    {
        var offenders = AllItems
            .Where(item => item.Control == Settings.SettingControl.Decimal)
            .Where(item => item.DecimalMax <= item.DecimalMin)
            .Select(item => $"{item.Key} (Min={item.DecimalMin}, Max={item.DecimalMax})")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Decimal rows need DecimalMax > DecimalMin: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// A round-trip pair names two keys that must both exist in the same section, or
    /// <c>RoundTripError</c> silently compares a real cap against a default-constructed one.
    /// </summary>
    [Fact]
    public void Round_trip_pairs_name_rows_in_their_own_section()
    {
        var offenders = new List<string>();

        foreach (var section in Settings.Sections.Where(s => s.RoundTrip is not null))
        {
            var keys = section.Items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in new[] { section.RoundTrip!.ExportKey, section.RoundTrip.ImportKey })
            {
                if (!keys.Contains(key))
                {
                    offenders.Add($"{section.Group} -> {key}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Round-trip pairs referencing a key absent from their section: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The numeric controls must bind their upper bound through <c>MaxFor(item)</c>, never the static
    /// <c>item.Max</c>.
    ///
    /// <para>
    /// A ceiling-bounded row (issue #421 Wave 3's tighten-only photo caps) carries its real limit on
    /// the read DTO, and <c>MaxFor</c> is what resolves it. Binding the control to <c>item.Max</c>
    /// instead is invisible today because each such row's catalogue literal happens to equal the
    /// current compile-time ceiling — the control would silently start refusing values the API
    /// accepts the moment that ceiling was raised, and the page's own range check (which does go
    /// through <c>MaxFor</c>) would disagree with the control next to it.
    /// </para>
    ///
    /// <para>
    /// A source-lint rather than a reflection test: the defect lives in the markup binding, which is
    /// exactly what reflection over the catalogue cannot see.
    /// </para>
    /// </summary>
    /// <summary>
    /// The ninth guard (issue #434). A row with a server-published FLOOR must bind it through
    /// <c>MinFor(item)</c>, exactly as a ceiling-bounded row binds <c>MaxFor(item)</c>.
    ///
    /// <para>
    /// Before <c>MinFrom</c> existed a published floor had no route to the control at all: the three
    /// numeric controls bound <c>Min="@item.Min"</c> as a static int and <c>ErrorFor</c> compared
    /// against <c>item.Min</c>, so the raise-only mail-throttle row would have offered 1 and then been
    /// rejected with a <c>400</c> the field never warned about. Five sites, not one — the three control
    /// bindings plus <c>ErrorFor</c>'s two comparisons.
    /// </para>
    ///
    /// <para>
    /// <c>DecimalControl</c> is deliberately exempt: it binds <c>item.DecimalMin</c>, and no decimal row
    /// has a server-published floor.
    /// </para>
    /// </summary>
    [Fact]
    public void Numeric_controls_bind_their_floor_through_MinFor()
    {
        var file = Path.Combine(ClientSource.Root, "Pages", "Settings.razor");
        var text = File.ReadAllText(file);

        var offenders = new List<string>();
        const string needle = "Min=\"@item.Min\"";
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            offenders.Add($"Settings.razor:{ClientSource.LineAt(text, i)}");
        }

        Assert.True(offenders.Count == 0,
            "Numeric controls binding the static item.Min instead of MinFor(item) — a DTO-carried floor "
            + "would never reach the control: " + string.Join(", ", offenders));

        // And the page's own range check must resolve through MinFor too, or the control and the error
        // message next to it would disagree.
        var codeBehind = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor.cs"));
        Assert.DoesNotContain("value < item.Min", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("cap.Value < item.Min", codeBehind, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row that publishes a floor must also carry a static <c>Min</c> as the load-phase fallback, for
    /// the same reason <c>MaxFrom</c> rows carry a static <c>Max</c>: the Save button evaluates
    /// <c>HasErrors</c> during the Loading phase, before any DTO exists.
    /// </summary>
    [Fact]
    public void Rows_with_a_published_floor_carry_a_static_fallback()
    {
        var offenders = AllItems
            .Where(item => item.MinFrom is not null)
            .Where(item => item.Min <= 0)
            .Select(item => $"{item.Key} (Min={item.Min})")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Rows with MinFrom need a positive static Min as the load-phase fallback: "
            + string.Join(", ", offenders));

        // The one such row today. Asserted by presence rather than by name only, so deleting MinFrom
        // rather than fixing a failure elsewhere does not quietly satisfy this file.
        Assert.Contains(AllItems, item => item.MinFrom is not null);
    }

    /// <summary>
    /// The advisory slot must be passed <strong>unconditionally</strong>, independently of whether the
    /// row's control renders in <c>Footer</c>.
    ///
    /// <para>
    /// This is the whole reason the advisory is a third fragment rather than a use of <c>Footer</c>.
    /// <c>ChildContent</c> and <c>Footer</c> are strictly either/or, and <c>fileAnalysisProcessor</c> —
    /// the one row the <c>BaseUrl</c> correspondence heuristic targets — is a <c>Text</c> row, so it
    /// already occupies <c>Footer</c>. Gating <c>Advisory</c> on the same condition would leave that row
    /// with nowhere to render the advisory the channel was built for.
    /// </para>
    /// </summary>
    [Fact]
    public void The_advisory_slot_is_independent_of_the_footer_slot()
    {
        var text = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Settings.razor"));

        Assert.Contains("Advisory=\"@AdvisoryFor(item)\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RendersInFooter(item) ? null : AdvisoryFor(item)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RendersInFooter(item) ? AdvisoryFor(item)", text, StringComparison.Ordinal);

        // The row the channel exists for is still a Text row — if that ever stops being true, the
        // reasoning above needs re-checking rather than the test silently going vacuous.
        var processor = Assert.Single(AllItems, item => item.Key == "fileAnalysisProcessor");
        Assert.Equal(Settings.SettingControl.Text, processor.Control);
    }

    [Fact]
    public void Numeric_controls_bind_their_bound_through_MaxFor()
    {
        var file = Path.Combine(ClientSource.Root, "Pages", "Settings.razor");
        var text = File.ReadAllText(file);

        var offenders = new List<string>();
        var needle = "Max=\"@item.Max\"";
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            offenders.Add($"Settings.razor:{ClientSource.LineAt(text, i)}");
        }

        Assert.True(offenders.Count == 0,
            "Numeric controls binding the static item.Max instead of MaxFor(item) — a DTO-carried "
            + "ceiling would never reach the control: " + string.Join(", ", offenders));
    }
}
