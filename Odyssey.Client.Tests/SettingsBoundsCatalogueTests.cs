using System.Reflection;
using Odyssey.Dtos.Application;
using Odyssey.Client.Pages;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// AC 22(c) — the <strong>fourth end</strong> of a shared bound pair (issue #437 Goal 4).
///
/// <para>
/// A single <see cref="SystemSettingsBounds"/> pair serves four consumers: the <c>[Range]</c> on the
/// write DTO, the registry descriptor, the read-path clamp in the domain lookups, and this catalogue's
/// <c>Min</c>/<c>Max</c> — which the rendered control's bound AND the page's own range check both
/// resolve from. The server-side guard ties the first three together; a client row could still hold a
/// literal that silently disagrees with all of them.
/// </para>
///
/// <para>
/// A reflection test rather than a source lint, deliberately. <c>Odyssey.Client</c> grants
/// <c>InternalsVisibleTo</c> to this assembly, and a text lint could not express the selector anyway:
/// <c>Field: nameof(SystemSettingsUpdate.X)</c> only becomes "X is an <c>int?</c>" by reflecting over
/// the DTO. It cannot see a literal that <em>coincidentally</em> equals the constant — constants inline
/// — but drift is the property worth guarding, and value equality catches drift the moment either end
/// moves.
/// </para>
/// </summary>
public class SettingsBoundsCatalogueTests
{
    private static IReadOnlyList<Settings.SettingItem> NumericRows =>
        Settings.AllItems
            .Where(item => item.Field is not null)
            .Where(item => typeof(SystemSettingsUpdate)
                .GetProperty(item.Field!, BindingFlags.Public | BindingFlags.Instance)?.PropertyType
                == typeof(int?))
            .ToList();

    private static int Bound(string field, string end)
    {
        var constant = typeof(SystemSettingsBounds)
            .GetField(field + end, BindingFlags.Public | BindingFlags.Static);

        Assert.True(constant is not null, $"SystemSettingsBounds.{field}{end} does not exist.");
        return (int)constant!.GetRawConstantValue()!;
    }

    /// <summary>
    /// Scope, stated rather than implied: 41 of the catalogue's 49 numeric rows. The eight
    /// <c>CapacityLimit?</c> rows are correctly excluded by the <c>int?</c> selector — their properties
    /// carry no <c>[Range]</c> at all, so their <c>Min: 1, Max: 1_000_000</c> is a client-only invention
    /// with no server end to name.
    /// </summary>
    [Fact]
    public void The_guard_covers_every_int_row()
    {
        Assert.Equal(41, NumericRows.Count);
        Assert.Equal(
            8,
            Settings.AllItems.Count(item => item.Control == Settings.SettingControl.Capacity));
    }

    /// <summary>
    /// Every <c>int?</c> row resolves its <c>Min</c>/<c>Max</c> from the shared pair.
    ///
    /// <para>
    /// <strong>Carve-out:</strong> a row declaring <c>MaxFrom</c> is exempt from the <c>Max</c> half,
    /// and one declaring <c>MinFrom</c> from the <c>Min</c> half. Those rows resolve their effective
    /// bound from a server-published ceiling or floor at runtime; the static value is the
    /// null-<c>_dto</c> load-phase fallback, pinned to that ceiling rather than to the <c>[Range]</c>.
    /// For two of them it deliberately disagrees — <c>photoMaxLinksPerKind</c> holds 50 and
    /// <c>photoMaxAlbumMembers</c> 1000 against a <c>[Range](1, 100000)</c>, because both are
    /// tighten-only caps whose real bound is a compile-time constant. Without the carve-out an
    /// implementer must either widen those controls 2000-fold until the DTO lands — destroying a
    /// documented invariant — or ship the guard red.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_int_row_resolves_its_bounds_from_the_shared_pair()
    {
        var offenders = new List<string>();

        foreach (var item in NumericRows)
        {
            var field = item.Field!;

            if (item.MaxFrom is null && item.Max != Bound(field, "Max"))
            {
                offenders.Add($"{item.Key}.Max = {item.Max}, expected {Bound(field, "Max")}");
            }

            if (item.MinFrom is null && item.Min != Bound(field, "Min"))
            {
                offenders.Add($"{item.Key}.Min = {item.Min}, expected {Bound(field, "Min")}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Catalogue rows whose static bound disagrees with SystemSettingsBounds — the control would "
            + "offer a value the server rejects, or refuse one it accepts: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// The exempted rows keep one assertion, in the direction the carve-out permits: the load-phase
    /// fallback must lie INSIDE its pair.
    ///
    /// <para>
    /// After the carve-out nothing else checks it in either direction, and it is what
    /// <c>MaxFor</c>/<c>MinFor</c> return for the whole load phase and whenever the DTO is null. A
    /// fallback edited outside the legal range would render an illegal <c>max</c> on the control and
    /// drive the page's own range check against a bound the server rejects. Equality against the real
    /// ceiling is unavailable here — <c>RequestCapCeilings</c> lives in <c>Odyssey.Api</c> and is
    /// injected — so containment is the strongest available assertion that does not re-impose the
    /// equality the carve-out exists to avoid.
    /// </para>
    /// </summary>
    [Fact]
    public void An_exempted_rows_load_phase_fallback_stays_inside_its_pair()
    {
        var exempt = NumericRows.Where(item => item.MaxFrom is not null || item.MinFrom is not null).ToList();
        Assert.Equal(9, exempt.Count);

        foreach (var item in exempt)
        {
            var field = item.Field!;
            var min = Bound(field, "Min");
            var max = Bound(field, "Max");

            if (item.MaxFrom is not null)
            {
                Assert.InRange(item.Max, min, max);
            }

            if (item.MinFrom is not null)
            {
                Assert.InRange(item.Min, min, max);
            }
        }
    }

    /// <summary>
    /// The inverse assertion, which is the omission that would actually hurt: every row whose field has
    /// a published ceiling or floor on the read DTO must declare the matching <c>MaxFrom</c>/
    /// <c>MinFrom</c>.
    ///
    /// <para>
    /// Without it, a server that publishes a ceiling reaches a control that ignores it — the row offers
    /// the wide <c>[Range]</c> and the save is rejected by a bound the field never warned about. That is
    /// the exact defect <c>MinFrom</c> was added for.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_row_with_a_published_bound_declares_the_matching_resolver()
    {
        var offenders = new List<string>();

        foreach (var item in NumericRows)
        {
            var hasCeiling = typeof(SystemSettingsDto).GetProperty(item.Field + "Ceiling") is not null;
            var hasFloor = typeof(SystemSettingsDto).GetProperty(item.Field + "Floor") is not null;

            if (hasCeiling && item.MaxFrom is null)
            {
                offenders.Add($"{item.Key} has a published ceiling but no MaxFrom");
            }

            if (hasFloor && item.MinFrom is null)
            {
                offenders.Add($"{item.Key} has a published floor but no MinFrom");
            }
        }

        Assert.True(offenders.Count == 0,
            "Rows ignoring a bound the server publishes — the control offers a value the API rejects: "
            + string.Join("; ", offenders));
    }

    /// <summary>
    /// The three Subscriptions rows exist, in their own group, with the bounds they advertise. Appended
    /// rather than filed beside the other finance groups: this catalogue is ordered
    /// wave-chronologically, so appending is the convention.
    /// </summary>
    [Fact]
    public void The_subscriptions_group_carries_the_three_rows()
    {
        var section = Assert.Single(Settings.Sections, s => s.Group == "Subscriptions");

        Assert.Equal(
            new[]
            {
                "subscriptionRenewalWindowDays",
                "subscriptionMaxSummaryRenewals",
                "subscriptionMaxSummarySubscriptions",
            },
            section.Items.Select(item => item.Key).ToArray());

        Assert.All(section.Items, item => Assert.Equal(Settings.SettingControl.Number, item.Control));
        Assert.All(section.Items, item => Assert.Equal(Settings.SettingClaim.Count, item.Claim));

        // The renewals cap is bounded at 50, not the 100000 its sibling summary caps carry.
        Assert.Equal(50, section.Items.Single(item => item.Key == "subscriptionMaxSummaryRenewals").Max);
    }
}
