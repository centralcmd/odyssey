using System.Globalization;
using System.Reflection;
using Odyssey.Api.SystemSettings;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;
using Odyssey.Dtos;

namespace Odyssey.Api.Tests;

/// <summary>
/// The guard rails that make <see cref="SystemSettingsRegistry"/> worth having (issue #421 Wave 0).
///
/// <para>
/// Before the registry, adding a setting meant editing five parallel per-field blocks in
/// <c>SystemSettingsService</c>, and <em>nothing enforced that the key appeared in all five</em>. A key
/// present on <see cref="SystemSettingsUpdate"/> but missing from the claim-check block was written with
/// no authorization check at all — no exception, no failing test, an unauthorized write. The existing
/// suites pinned the seeded row count and the read-DTO shape; neither could see that.
/// </para>
///
/// <para>
/// These tests are the checker, so reflection is appropriate here in a way it deliberately is not in
/// the registry itself: the registry uses explicit accessor delegates precisely so a renamed property
/// cannot silently lose its claim, and reflection is what proves the two stayed in step.
/// </para>
/// </summary>
public class SystemSettingsRegistryTests
{
    private static readonly string[] WriteClaims =
        [PermissionClaims.SystemSettingsUpdate, PermissionClaims.SystemSettingsSecurityUpdate];

    /// <summary>
    /// Read-DTO members that are deliberately not registry-backed: the server-set 2FA sibling and the
    /// last-writer summary triple, all assembled by hand in <c>AssembleAsync</c>.
    /// </summary>
    private static readonly string[] NonRegistryDtoProperties =
    [
        nameof(SystemSettingsDto.TwoFactorEnforced),
        // Server-computed hard ceilings, not stored settings (issue #421 Wave 3).
        nameof(SystemSettingsDto.PhotoMaxLinksPerKindCeiling),
        nameof(SystemSettingsDto.PhotoMaxAlbumMembersCeiling),
        // Same, for the upload cap (Wave 4) — computed from startup configuration rather than stored.
        nameof(SystemSettingsDto.UploadMegabytesCeiling),
        // The six issue #434 bound projections. Five ceilings and one floor, all server-computed rather
        // than stored: three are static numbers the [Range] on the write DTO also carries, and three are
        // the pinned end of a single-direction key, which IS the shipped default.
        nameof(SystemSettingsDto.CalendarIcsMaxAggregateExportRowsCeiling),
        nameof(SystemSettingsDto.CalendarIcsMaxAggregateOccurrencesCeiling),
        nameof(SystemSettingsDto.PhotoMetadataReadMegabytesCeiling),
        nameof(SystemSettingsDto.RecurrenceMaxGeneratedOccurrencesCeiling),
        nameof(SystemSettingsDto.ContactVCardMaxRepeatablePropertiesPerEntryCeiling),
        nameof(SystemSettingsDto.EmailMaxTrackedRecipientsFloor),
        // Server-authored advisories, not a setting: read-only, and there is no inbound counterpart.
        nameof(SystemSettingsDto.Warnings),
        // Its structured companion (issue #437 §5 component 7): which fields are not being read from
        // their stored value, and why. Same shape of exclusion as Warnings — server-authored, read-only,
        // no inbound counterpart — so this is an EXCLUSION of a non-setting property, not a relaxation
        // of the property this test asserts.
        nameof(SystemSettingsDto.ProjectionFaults),
        nameof(SystemSettingsDto.UpdatedAt),
        nameof(SystemSettingsDto.UpdatedBy),
        nameof(SystemSettingsDto.UpdatedByDisplayName),
    ];

    /// <summary>
    /// The one that closes the unauthorized-write bug class: a writable field with no descriptor is
    /// never claim-checked, and a field with two would be written twice under possibly different claims.
    /// </summary>
    [Fact]
    public void Every_writable_field_has_exactly_one_descriptor()
    {
        var offenders = typeof(SystemSettingsUpdate)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (property.Name, Count: SystemSettingsRegistry.All.Count(d => d.FieldName == property.Name)))
            .Where(entry => entry.Count != 1)
            .Select(entry => $"{entry.Name} ({entry.Count} descriptors)")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Every SystemSettingsUpdate property must map to exactly one descriptor, or it is written "
            + "without a claim check (none) or twice (many). Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>The read side of the same rule: a field no descriptor projects silently returns its CLR default.</summary>
    [Fact]
    public void Every_read_field_is_projected_by_a_descriptor()
    {
        // The registry is keyed by the WRITE DTO's field names; the read DTO uses the same names for
        // every registry-backed setting, which is what makes this comparison meaningful.
        var projected = SystemSettingsRegistry.All.Select(d => d.FieldName).ToHashSet(StringComparer.Ordinal);

        var missing = typeof(SystemSettingsDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Except(NonRegistryDtoProperties, StringComparer.Ordinal)
            .Where(name => !projected.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "SystemSettingsDto properties with no descriptor to project them (they would always read as "
            + "their CLR default): " + string.Join(", ", missing));
    }

    /// <summary>The registry and the persisted key catalogue must describe the same set of settings.</summary>
    [Fact]
    public void Registry_keys_match_the_key_catalogue_exactly()
    {
        var registryKeys = SystemSettingsRegistry.All.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var catalogueKeys = SystemSettingsKeys.AllKeys.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            catalogueKeys.OrderBy(k => k, StringComparer.Ordinal),
            registryKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>
    /// A descriptor whose claim is misspelled, or is a claim no role holds, gates its field on nothing
    /// reachable — every write to it would 403 for everyone, Admin included.
    /// </summary>
    [Fact]
    public void Every_descriptor_requires_a_real_write_claim_that_some_role_holds()
    {
        var offenders = SystemSettingsRegistry.All
            .Where(d => !WriteClaims.Contains(d.RequiredClaim, StringComparer.Ordinal)
                        || !RolePermissions.AllClaims.Contains(d.RequiredClaim))
            .Select(d => $"{d.FieldName} -> '{d.RequiredClaim}'")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Descriptors whose RequiredClaim is not one of the two system-settings write claims, or is "
            + "absent from RolePermissions.AllClaims: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Every descriptor's default must round-trip through its own parser cleanly.
    ///
    /// <para>
    /// <strong>It asserts the OUTCOME, not the absence of an exception</strong> (issue #437 AC 33).
    /// This test used to be <c>Assert.Null(Record.Exception(() =&gt; descriptor.Project(...)))</c>, and
    /// once <c>Project</c> became non-throwing that assertion could never fail again — reporting green
    /// while the property it names stopped being checked. Asserting <see cref="ProjectionOutcome.Ok"/>
    /// restores it and strengthens it: <c>Ok</c> also means each descriptor's <c>DefaultValue</c> lies
    /// <em>within its own bound pair</em>, so a default that a future edit puts outside its
    /// <c>[Range]</c> is caught at the seam rather than silently clamped on every read.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_descriptor_default_parses_onto_the_read_dto()
    {
        foreach (var descriptor in SystemSettingsRegistry.All)
        {
            var dto = new SystemSettingsDto();

            Assert.Equal(ProjectionOutcome.Ok, descriptor.Project(descriptor.DefaultValue, dto));
        }
    }

    /// <summary>
    /// The crossed-accessor case none of the tests above can see: two same-typed fields whose Read and
    /// Write delegates point at each other's property still satisfy "exactly one descriptor each",
    /// "every field projected", matching keys and valid claims — while silently writing key A from
    /// field B. This writes a distinct sentinel through each descriptor and asserts the value lands on
    /// that descriptor's own key.
    /// </summary>
    [Fact]
    public void Every_descriptor_reads_and_writes_its_own_field()
    {
        foreach (var descriptor in SystemSettingsRegistry.All)
        {
            var request = new SystemSettingsUpdate();
            var sentinel = SetSentinel(descriptor, request);

            // Only this descriptor may see the request as present…
            var alsoPresent = SystemSettingsRegistry.All
                .Where(other => other.Key != descriptor.Key && other.IsPresent(request))
                .Select(other => other.FieldName)
                .ToList();

            Assert.True(alsoPresent.Count == 0,
                $"Setting only {descriptor.FieldName} also made these descriptors report present "
                + $"(crossed Read accessor): {string.Join(", ", alsoPresent)}");

            Assert.True(descriptor.IsPresent(request),
                $"{descriptor.FieldName}: its own Read accessor did not see the value that was set.");

            // …and formatting must yield the sentinel, proving Read points at the property Format uses.
            Assert.Equal(sentinel, descriptor.Format(request));

            // …and projecting it back must land on this descriptor's own read-DTO property.
            var dto = new SystemSettingsDto();
            descriptor.Project(sentinel, dto);

            var landedOn = typeof(SystemSettingsDto)
                .GetProperty(descriptor.FieldName, BindingFlags.Public | BindingFlags.Instance);

            Assert.NotNull(landedOn);
            Assert.Equal(
                sentinel,
                FormatProjected(landedOn!.GetValue(dto)));
        }
    }

    /// <summary>
    /// <c>TouchOnPresenceOnly</c> is the asymmetry issue #349 shipped and issue #343 depends on: the five
    /// original keys bump <c>UpdatedAt</c> on presence, every key added since only on an actual change.
    /// <c>ImportExportSettingsApiTests</c>' GET→PUT no-op assertion excludes exactly those five, so a new
    /// descriptor setting this flag would break it — from a different file, confusingly.
    /// </summary>
    [Fact]
    public void Only_the_five_original_keys_touch_on_presence()
    {
        string[] legacy =
        [
            SystemSettingsKeys.RequireTwoFactor,
            SystemSettingsKeys.RegistrationRequireAdminApproval,
            SystemSettingsKeys.EmailRequireConfirmation,
            SystemSettingsKeys.InsuranceExpiringSoonWindowDays,
            SystemSettingsKeys.InsuranceMaxSummaryPolicies,
        ];

        Assert.Equal(
            legacy.OrderBy(k => k, StringComparer.Ordinal),
            SystemSettingsRegistry.All.Where(d => d.TouchOnPresenceOnly)
                .Select(d => d.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>
    /// Documents the audit set the derived <c>AuditChanges</c> rule produces, so the widening from three
    /// fields to eleven (issue #421 §5 / architect round-2 R2-2) is asserted rather than discovered: the
    /// three perimeter booleans plus the eight import/export megabyte caps, which already carried the
    /// security claim before this rule existed.
    /// </summary>
    [Fact]
    public void Audited_fields_are_exactly_the_security_claim_fields()
    {
        var audited = SystemSettingsRegistry.All.Where(d => d.AuditChanges).Select(d => d.Key).ToList();

        // 3 perimeter booleans + 8 import/export megabyte caps + 4 file-analysis disclosure strings
        // (Wave 1, legal disclosure shown at the point of consent) + 4 email settings (Wave 2: the
        // sender identity recipients see, and a throttle that is a security control on the anonymous
        // mail path). All carry system-settings.security.update, so all are audited by construction.
        // Unchanged by Wave 3: all nine per-request caps take the ORDINARY write claim (issue #421
        // §10.10), so none of them is audited. If this number moves when caps are added, the claim
        // split has drifted.
        //
        // +1 in Wave 4 for the upload cap, which does NOT follow the Wave 3 caps: it bounds a real
        // abuse surface (unauthenticated-adjacent bulk storage consumption), so it takes the security
        // claim and is audited (§10.10).
        //
        // +3 in issue #434, and each one is on the security claim BECAUSE AuditChanges is derived from
        // it — that is the deciding argument in all three cases, not an afterthought:
        //   * FileAnalysisMaxTokens — a direct third-party spend lever; an unaudited spend lever is
        //     worse than an over-classified one.
        //   * PhotoMetadataReadMegabytes — on the ordinary claim it would be the only unaudited
        //     megabyte cap in a ten-row set, and all nine existing ones are also resource bounds, so
        //     "resource bound, not abuse control" does not distinguish it from its neighbours.
        //   * EmailMaxTrackedRecipients — an authentication-adjacent abuse control, matching how the
        //     Wave 2 mail-throttle keys were classified.
        // The other twelve #434 keys take the ordinary write claim and are not audited.
        //
        // +3 in issue #439, and all three are on the security claim for the same derived-audit reason:
        //   * FileAnalysisEnabled — the switch that authorises transferring personal data to a third
        //     party. An unaudited change to it is an unaudited change to whether the deployment
        //     exports documents at all.
        //   * FileAnalysisModel — stamped on every job; the change is not retroactive, but which model
        //     future analyses run and record on is provenance.
        //   * FileAnalysisBaseUrl — where the document and the configured API key actually go. It is
        //     the highest-consequence value in the store, and the only descriptor carrying an
        //     AuditProjection, which reduces both the old and the new value to their host before the
        //     line is written.
        //
        // +4 in issue #8 — the whole mail transport, and the security claim is not a judgement call on
        // any of them:
        //   * EmailSmtpHost — the relay the credential is presented to, and every message body with
        //     it. It carries an AuditProjection for the same reason FileAnalysisBaseUrl does: the OLD
        //     value the line echoes was never seen by the write validator, so a row planted by a
        //     restore can carry `user:pass@host`.
        //   * EmailUseStartTls — whether that credential and every reset token travel encrypted. The
        //     one setting here an attacker can exploit with NO Odyssey privilege at all, given passive
        //     network position.
        //   * EmailClientBaseUrl — where every password-reset link points, so where a token lands. The
        //     audit line is one of the few controls on it, since clearing a credential protects nothing
        //     when no credential is involved. Host-projected for the same reason as the SMTP host.
        //   * EmailSmtpPort — audited by construction rather than on its own merits, and correctly so:
        //     splitting the port off onto the ordinary claim would let a caller move the transport to
        //     a port the relay serves in the clear without the change being recorded.
        Assert.Equal(30, audited.Count);
        Assert.All(SystemSettingsRegistry.All, descriptor =>
            Assert.Equal(
                descriptor.RequiredClaim == PermissionClaims.SystemSettingsSecurityUpdate,
                descriptor.AuditChanges));
    }

    // ── sentinel helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A legal, distinct <c>int</c> sentinel per <see cref="IntSetting"/>, allocated once over the whole
    /// registry and memoized (issue #437 AC 32).
    ///
    /// <para>
    /// <strong>An ordinal-derived sentinel does not work, and never really did.</strong> The old
    /// comment claimed the value was "inside every field's data-annotation range"; that was already
    /// false in both directions — three keys have a <c>[Range]</c> minimum above 1, and
    /// <c>PhotoMetadataReadMegabytes</c> is <c>[Range(1, 16)]</c> at ordinal 45. The test passed only
    /// because <c>Project</c> did not clamp. Now that the descriptors carry a bound pair and
    /// <c>Project</c> clamps into it, the round-trip assertion fails on exactly those keys.
    /// </para>
    ///
    /// <para>
    /// <c>Min + ordinal</c> does not fix it either: it leaves the pair (<c>1 + 45 = 46 &gt; 16</c>) and
    /// it collides. No per-descriptor offset can work — the pairs overlap heavily and <c>[1, 16]</c> has
    /// fewer legal values than there are descriptors, and greedy in <em>registry</em> order starves it
    /// because the many <c>Min = 1</c> keys ahead of it consume the low values.
    /// </para>
    ///
    /// <para>
    /// So it <strong>allocates</strong>: for each key, the first value in its own range not already
    /// taken. In-range by construction, distinct by construction, deterministic — which makes true the
    /// property the old comment only claimed. The order is <em>earliest-deadline-first</em> —
    /// <c>Max</c> ascending, then <c>Min</c>, then <c>Key</c> — which is provably optimal for this
    /// problem, unlike narrowest-pair-first, which is a heuristic that can starve on a feasible set
    /// (<c>[2,3], [1,2], [1,2]</c> are all width 1, and in that tie order it takes 2, then 1, then
    /// fails, while <c>1, 2, 3</c> exists). The <c>ThenBy(Key)</c> tiebreak stays explicit because LINQ
    /// <c>OrderBy</c> is stable but <c>List&lt;T&gt;.Sort</c>/<c>Array.Sort</c> are not.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, int> IntSentinels = AllocateIntSentinels();

    private static Dictionary<string, int> AllocateIntSentinels()
    {
        var allocated = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<int>();

        var ordered = SystemSettingsRegistry.All
            .OfType<IntSetting>()
            .OrderBy(setting => setting.Max)
            .ThenBy(setting => setting.Min)
            .ThenBy(setting => setting.Key, StringComparer.Ordinal);

        foreach (var setting in ordered)
        {
            var value = Enumerable.Range(setting.Min, setting.Max - setting.Min + 1)
                .FirstOrDefault(candidate => !taken.Contains(candidate), -1);

            Assert.True(value >= 0,
                $"{setting.Key}: no free value could be allocated in [{setting.Min}, {setting.Max}].");

            taken.Add(value);
            allocated[setting.Key] = value;
        }

        return allocated;
    }

    /// <summary>
    /// Sets a per-descriptor-distinct value on <paramref name="request"/> and returns its expected
    /// stored form. Values are chosen inside every field's data-annotation range so the sentinel is a
    /// legal value, not just a distinct one.
    /// </summary>
    private static string SetSentinel(SystemSettingDescriptor descriptor, SystemSettingsUpdate request)
    {
        var property = typeof(SystemSettingsUpdate)
            .GetProperty(descriptor.FieldName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);

        // Distinct per descriptor. Still ordinal-derived for the three kinds with no numeric [Range] to
        // satisfy — capacity caps carry none, a string sentinel is bounded by its own MaxLength, and the
        // one decimal key is [Range(0.0, 1.0)], which ordinal/100 stays inside. Only IntSetting needs
        // the allocator above.
        var ordinal = SystemSettingsRegistry.All.ToList().FindIndex(d => d.Key == descriptor.Key) + 1;

        switch (descriptor)
        {
            case BoolSetting:
                // Only two values exist, so a bool cannot carry a unique sentinel; the "also present"
                // and "lands on its own property" assertions still hold, and `true` differs from the
                // read DTO's default `false` for all three.
                property!.SetValue(request, true);
                return "true";

            case IntSetting:
                var allocated = IntSentinels[descriptor.Key];
                property!.SetValue(request, allocated);
                return allocated.ToString(CultureInfo.InvariantCulture);

            case CapacitySetting:
                property!.SetValue(request, new CapacityLimit { Value = ordinal });
                return ordinal.ToString(CultureInfo.InvariantCulture);

            case StringSetting text:
                // Distinct, within the descriptor's own MaxLength, and untrimmed-equal so Format's
                // trim does not change it. Validate() is not called here — this test is about
                // accessor wiring, and the semantic validators have their own tests.
                var sentinel = $"s{ordinal}";
                property!.SetValue(request, sentinel);
                return sentinel.Length <= text.MaxLength ? sentinel : sentinel[..text.MaxLength];

            case DecimalSetting:
                // ordinal/100 keeps it inside every decimal range in use AND canonical under the
                // "0.####" storage format, so the round-trip assertion is exact.
                var fraction = ordinal / 100m;
                property!.SetValue(request, fraction);
                return fraction.ToString(DecimalSetting.StorageFormat, CultureInfo.InvariantCulture);

            default:
                throw new InvalidOperationException(
                    $"Unhandled descriptor kind {descriptor.GetType().Name}; extend SetSentinel so the "
                    + "crossed-accessor guard keeps covering every kind.");
        }
    }

    private static string FormatProjected(object? value) => value switch
    {
        bool flag => flag ? "true" : "false",
        int number => number.ToString(CultureInfo.InvariantCulture),
        decimal fraction => fraction.ToString(DecimalSetting.StorageFormat, CultureInfo.InvariantCulture),
        string text => text,
        null => SystemSettingsDefaults.Unlimited,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
