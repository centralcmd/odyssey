using System.Globalization;
using Odyssey.Dtos.Application;
using Odyssey.Core;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// What <see cref="SystemSettingDescriptor.Project"/> did with the stored value (issue #437 Goal 9).
///
/// <para>
/// <strong>Returned rather than logged</strong>, because <see cref="SystemSettingsRegistry.All"/> is
/// <c>static readonly</c> and built at type initialisation, so a descriptor can never hold a scoped
/// <c>ILogger</c> or <c>IMemoryCache</c>. A static logger on the registry would recreate exactly the
/// process-wide-versus-per-factory hazard <c>CLAUDE.md</c> records for <c>RequestCapCeilings</c>.
/// <c>SystemSettingsService.AssembleAsync</c> — a private instance method both <c>GetAsync</c> and
/// <c>UpdateAsync</c> return through — has both, so the outcome threads with no public signature change.
/// </para>
/// </summary>
internal enum ProjectionOutcome
{
    /// <summary>The stored value parsed and was within its bound pair.</summary>
    Ok,

    /// <summary>The stored value could not be parsed; <c>DefaultValue</c> was projected instead.</summary>
    Unparseable,

    /// <summary>The stored value parsed but fell outside its bound pair; the nearer bound was projected.</summary>
    Clamped,
}

/// <summary>
/// One admin-configurable setting, declared once instead of five times (issue #421 Wave 0).
///
/// <para>
/// Before this type, adding a key meant editing five parallel per-field blocks in
/// <see cref="SystemSettingsService"/> — the claim checks, the shape validation, the apply calls, the
/// read-DTO assembly and the default-value switch — and <em>nothing enforced that the key appeared in
/// all five</em>. A key present in <see cref="SystemSettingsUpdate"/> but missing from the claim-check
/// block was written with no authorization check at all, silently: no exception, no failing test. That
/// is the bug class this type exists to close, and the reason the registry landed before any new key.
/// </para>
///
/// <para>
/// Each concrete kind declares its own <em>typed</em> reader (see <see cref="CapacitySetting.Read"/>),
/// deliberately not a shared <c>object?</c> one. An <c>object?</c> reader still admits an accessor
/// written <c>r =&gt; r.SomeCap?.Value</c>, which is verbatim the shape of issue #343's finding F3 — a
/// caller lacking the claim setting every count cap to "unlimited" by sending only
/// <c>{ "unlimited": true }</c>, because <c>.Value</c> is null and the check is skipped. A typed
/// <c>Func&lt;SystemSettingsUpdate, CapacityLimit?&gt;</c> cannot return <c>.Value</c>, so the mistake is
/// unmakeable at the declaration site. <see cref="IsPresent"/> is a separate member for the same
/// reason: presence is decided by the object, once, in one place, for every kind.
/// </para>
/// </summary>
internal abstract class SystemSettingDescriptor
{
    /// <summary>The <see cref="Odyssey.Context.SystemSetting.Key"/> this descriptor owns.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// The <see cref="SystemSettingsUpdate"/> property name, as <c>nameof</c>. Used verbatim in the
    /// <c>403</c> and <c>400</c> messages, so it is a wire-visible contract, not a debugging aid.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>Which write claim a non-null value for this field requires.</summary>
    public required string RequiredClaim { get; init; }

    /// <summary>The string-serialized default, used when a row is missing (should not happen post-migration).</summary>
    public required string DefaultValue { get; init; }

    /// <summary>The memory-cache entry to evict when this setting is touched, if any.</summary>
    public string? CacheKeyToEvict { get; init; }

    /// <summary>
    /// Whether a write bumps <c>UpdatedAt</c>/<c>UpdatedBy</c> merely because the field was <em>present</em>
    /// on the request, rather than because the stored value actually changed.
    ///
    /// <para>
    /// <see langword="true"/> only for the five original issue #349 keys, which behave that way today —
    /// <c>ImportExportSettingsApiTests</c> depends on the sixteen newer keys behaving the other way, and
    /// its GET→PUT no-op assertion deliberately excludes the legacy five for exactly this reason. Every
    /// key added after #349 leaves this <see langword="false"/>. Unifying the two is a desirable
    /// follow-up with its own test consequences, deliberately not bundled into a behaviour-preserving
    /// refactor.
    /// </para>
    /// </summary>
    public bool TouchOnPresenceOnly { get; init; }

    /// <summary>
    /// Whether an actual value change is written to the audit log. <strong>Derived from the claim, not
    /// declared</strong> — a hand-picked list is what let the first draft of issue #421 miss the mail
    /// throttle and the upload cap.
    ///
    /// <para>
    /// Note this widens the audited set from three fields to eleven the moment Wave 0 lands: only the
    /// three perimeter booleans log today, but the eight import/export megabyte caps already carry
    /// <see cref="PermissionClaims.SystemSettingsSecurityUpdate"/>. That is a deliberate, documented
    /// behaviour change (issue #421 §5, §10.8) — Wave 0's "no behaviour change" claim is scoped to
    /// authorization, validation and stored values, not to log volume.
    /// </para>
    /// </summary>
    public bool AuditChanges => RequiredClaim == PermissionClaims.SystemSettingsSecurityUpdate;

    /// <summary>
    /// Optional non-blocking advisory for this field, evaluated against the freshly-projected read DTO
    /// (issue #434 §5 item 3 — this is the whole warnings mechanism on the server side). Returns
    /// <see langword="null"/> for "no advisory".
    ///
    /// <para>
    /// An advisory is not validation and must never behave like it: it cannot fail a save, cannot
    /// change a status code, and is computed <em>after</em> the write commits. A delegate that throws
    /// is swallowed and logged at Debug, and that advisory is simply omitted — see
    /// <see cref="SystemSettingsService"/>.
    /// </para>
    /// </summary>
    public Func<SystemSettingsDto, AdvisoryContext, string?>? Advise { get; init; }

    /// <summary>
    /// Projects a stored value before it reaches the audit line. <see langword="null"/> — every setting
    /// but one — logs the value verbatim, exactly as today.
    ///
    /// <para>
    /// It exists because the audit line echoes the value being <em>replaced</em>, and the write
    /// validator constrains only what an administrator can submit. A <c>FileAnalysisBaseUrl</c> row
    /// planted by a restore or a hand edit can carry <c>https://key:secret@host</c> — precisely the
    /// case the read path's re-validation exists to catch — so without this the first administrator to
    /// correct it through the UI would write that credential into the application log.
    /// </para>
    ///
    /// <para>
    /// An explicit typed delegate, never reflection, matching the rest of the registry: a projection
    /// resolved by property name would silently stop applying the moment a DTO property is renamed,
    /// which is the same failure class the registry exists to close.
    /// </para>
    /// </summary>
    public Func<string, string>? AuditProjection { get; init; }

    /// <summary>Whether the request carries a value for this field at all. <c>false</c> means "leave unchanged".</summary>
    public abstract bool IsPresent(SystemSettingsUpdate request);

    /// <summary>
    /// Per-field shape validation, run only for a present value and only after every claim check has
    /// passed. Throws <see cref="DomainValidationException"/>; range checks live on the DTO's data
    /// annotations instead, so <c>[ApiController]</c> model validation rejects them before this runs.
    /// </summary>
    public virtual void Validate(SystemSettingsUpdate request, RequestCapCeilings ceilings)
    {
    }

    /// <summary>Serializes the request's value for this field to its stored string form.</summary>
    public abstract string Format(SystemSettingsUpdate request);

    /// <summary>
    /// Parses a stored value onto the read DTO, and reports what it had to do to get there.
    ///
    /// <para>
    /// <strong>Never throws on row content</strong> (issue #437 Goal 9). Before that, four of the five
    /// kinds ended in a throwing parse inside an untry-caught loop over every descriptor, so one
    /// corrupt row returned <c>500</c> from <c>GET /api/system-settings</c> — the page an administrator
    /// would use to repair that row — and made a successful <c>PUT</c> <c>500</c> <em>after</em>
    /// committing.
    /// </para>
    ///
    /// <para>
    /// The fallback deliberately does <strong>not</strong> fail closed: for a display bound, refusing
    /// to serve the key would re-create the very fault this exists to remove — a condition that takes
    /// away the operator's ability to act. That reasoning does not extend to anything gating
    /// authorization, spend, or a control that fails open, which is why <c>IntSetting</c>'s bound is a
    /// <em>pair</em> rather than a ceiling with a hardcoded floor of 1.
    /// </para>
    /// </summary>
    public abstract ProjectionOutcome Project(string storedValue, SystemSettingsDto dto);

    /// <summary>Kebab-cases <see cref="FieldName"/> for the <c>system-settings.invalid.*</c> error codes.</summary>
    protected string ErrorCode => $"system-settings.invalid.{ToKebabCase(FieldName)}";

    private static string ToKebabCase(string pascalCase)
    {
        var builder = new System.Text.StringBuilder(pascalCase.Length + 8);
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}

/// <summary>A <see cref="bool"/>-valued setting, stored as the literal <c>true</c>/<c>false</c>.</summary>
internal sealed class BoolSetting : SystemSettingDescriptor
{
    public required Func<SystemSettingsUpdate, bool?> Read { get; init; }

    public required Action<SystemSettingsDto, bool> Write { get; init; }

    public override bool IsPresent(SystemSettingsUpdate request) => Read(request) is not null;

    public override string Format(SystemSettingsUpdate request) => Read(request)!.Value ? "true" : "false";

    public override ProjectionOutcome Project(string storedValue, SystemSettingsDto dto)
    {
        if (bool.TryParse(storedValue, out var parsed))
        {
            Write(dto, parsed);
            return ProjectionOutcome.Ok;
        }

        Write(dto, bool.Parse(DefaultValue));
        return ProjectionOutcome.Unparseable;
    }
}

/// <summary>
/// An <see cref="int"/>-valued setting stored as an invariant-culture decimal integer. Covers both the
/// two Insurance knobs (which set <see cref="SystemSettingDescriptor.TouchOnPresenceOnly"/>) and the
/// eight import/export megabyte caps (which do not).
/// </summary>
internal sealed class IntSetting : SystemSettingDescriptor
{
    public required Func<SystemSettingsUpdate, int?> Read { get; init; }

    public required Action<SystemSettingsDto, int> Write { get; init; }

    /// <summary>
    /// Optional semantic check beyond the DTO's <c>[Range]</c>, for a bound that cannot be expressed in
    /// an attribute — a ceiling held in another project's <c>const</c>, or one only known at runtime.
    /// Returns an error message, or null when the value is acceptable.
    /// </summary>
    public Func<int, RequestCapCeilings, string?>? Validator { get; init; }

    /// <summary>
    /// The lower half of this key's <c>SystemSettingsBounds</c> pair — the same number its
    /// <c>[Range]</c> minimum, the read-path clamp and the client catalogue's <c>Min</c> all name
    /// (issue #437 Goal 4).
    ///
    /// <para>
    /// <c>required</c> so a new int key cannot silently get no read-path bound at all: that is the
    /// omission AC 22(b) exists to catch, made into a compile error as well as a test.
    /// </para>
    /// </summary>
    public required int Min { get; init; }

    /// <summary>The upper half of the same pair.</summary>
    public required int Max { get; init; }

    public override bool IsPresent(SystemSettingsUpdate request) => Read(request) is not null;

    public override void Validate(SystemSettingsUpdate request, RequestCapCeilings ceilings)
    {
        if (Validator?.Invoke(Read(request)!.Value, ceilings) is { } problem)
        {
            throw new DomainValidationException($"Setting '{FieldName}' {problem}", ErrorCode, FieldName);
        }
    }

    public override string Format(SystemSettingsUpdate request) =>
        Read(request)!.Value.ToString(CultureInfo.InvariantCulture);

    public override ProjectionOutcome Project(string storedValue, SystemSettingsDto dto)
    {
        if (!int.TryParse(storedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            Write(dto, int.Parse(DefaultValue, CultureInfo.InvariantCulture));
            return ProjectionOutcome.Unparseable;
        }

        // A parseable value is CLAMPED into its pair, not replaced by the default: a raise inside the
        // bound is honoured — the whole point of the setting — while a hand-edited row outside it is
        // clamped rather than obeyed or silently reverted. "0" is this case, not the unparseable one.
        var clamped = Math.Clamp(parsed, Min, Max);
        Write(dto, clamped);
        return clamped == parsed ? ProjectionOutcome.Ok : ProjectionOutcome.Clamped;
    }
}

/// <summary>
/// A count cap with a three-state write shape: absent (leave unchanged), <c>{ unlimited: true }</c>, or
/// <c>{ value: n }</c>. Stored as either an invariant decimal integer or the
/// <see cref="Odyssey.Dtos.SystemSettingsDefaults.Unlimited"/> sentinel.
/// </summary>
internal sealed class CapacitySetting : SystemSettingDescriptor
{
    /// <summary>
    /// Typed on purpose — see the remarks on <see cref="SystemSettingDescriptor"/>. A reader returning
    /// <c>object?</c> would let this be written <c>r =&gt; r.Field?.Value</c>, reintroducing issue #343's
    /// finding F3: a caller without the claim sends <c>{ "unlimited": true }</c>, <c>.Value</c> is null,
    /// the claim check is skipped, and every count cap becomes unlimited.
    /// </summary>
    public required Func<SystemSettingsUpdate, CapacityLimit?> Read { get; init; }

    public required Action<SystemSettingsDto, int?> Write { get; init; }

    public override bool IsPresent(SystemSettingsUpdate request) => Read(request) is not null;

    public override void Validate(SystemSettingsUpdate request, RequestCapCeilings ceilings)
    {
        var value = Read(request)!;

        // Exactly one of Unlimited / Value must be set. The violation is precisely the case where the
        // two booleans agree (issue #343 §9).
        if (value.Unlimited == (value.Value is not null))
        {
            throw new DomainValidationException(
                $"Setting '{FieldName}' must set exactly one of 'unlimited' or 'value'.", ErrorCode, FieldName);
        }
    }

    public override string Format(SystemSettingsUpdate request)
    {
        var value = Read(request)!;
        return Odyssey.Context.SystemSettingsKeys.FormatCount(value.Unlimited ? null : value.Value);
    }

    public override ProjectionOutcome Project(string storedValue, SystemSettingsDto dto)
    {
        // TryParseCount already existed directly below ParseCount, documented for exactly this case,
        // and had simply never been adopted here (issue #437 §1).
        if (Odyssey.Context.SystemSettingsKeys.TryParseCount(storedValue, out var parsed))
        {
            Write(dto, parsed);
            return ProjectionOutcome.Ok;
        }

        Odyssey.Context.SystemSettingsKeys.TryParseCount(DefaultValue, out var fallback);
        Write(dto, fallback);
        return ProjectionOutcome.Unparseable;
    }
}

/// <summary>
/// A string-valued setting (issue #421 Wave 0b). No setting uses this kind yet — the first four land
/// in Wave 1 (the AI-analysis processor disclosure) — so it ships with the shared pipeline and no
/// per-field validator.
///
/// <para>
/// The shared pipeline, in order: trim → reject empty/whitespace → reject control characters → length
/// bound → the descriptor's own <see cref="Validator"/>. Two of those steps carry reasoning worth not
/// re-deriving:
/// </para>
///
/// <para>
/// <strong>Trimming happens in <see cref="Format"/>, not at the edge.</strong> Without it <c>" x"</c>
/// and <c>"x"</c> are distinct stored values, and a <c>GET</c>-then-<c>PUT</c> of unchanged data stops
/// being a no-op — a property <c>ImportExportSettingsApiTests</c> asserts.
/// </para>
///
/// <para>
/// <strong>Empty is a <c>400</c>, not a clear.</strong> On <see cref="SystemSettingsUpdate"/> null
/// already means "leave unchanged", so <c>""</c> is the only remaining spelling of "clear" — and no
/// string setting in this feature has a meaningful empty value. <c>[Required]</c> must NOT be used on
/// these DTO properties: it would reject the legitimate unchanged-null.
/// </para>
/// </summary>
internal sealed class StringSetting : SystemSettingDescriptor
{
    public required Func<SystemSettingsUpdate, string?> Read { get; init; }

    public required Action<SystemSettingsDto, string> Write { get; init; }

    /// <summary>Maximum stored length. Mirrors the DTO's <c>[StringLength]</c> as defence in depth for direct callers.</summary>
    public required int MaxLength { get; init; }

    /// <summary>
    /// Optional semantic check, run last and only on an already-trimmed, non-empty, length-checked
    /// value. Returns an error message, or null when the value is acceptable.
    /// </summary>
    public Func<string, string?>? Validator { get; init; }

    /// <summary>
    /// Optional canonicalisation applied on the way to storage, after <see cref="Validator"/> has
    /// accepted the value. Returning null leaves the trimmed value as-is.
    ///
    /// <para>
    /// Same purpose as the unconditional trim below, one level up: without it
    /// <c>https://host</c> and <c>https://host/</c> are two distinct stored values, so a
    /// <c>GET</c>→<c>PUT</c> round trip of unchanged data stops being a no-op and emits a spurious
    /// audit line for a change nobody made.
    /// </para>
    /// </summary>
    public Func<string, string?>? Canonicalize { get; init; }

    public override bool IsPresent(SystemSettingsUpdate request) => Read(request) is not null;

    public override void Validate(SystemSettingsUpdate request, RequestCapCeilings ceilings)
    {
        var raw = Read(request)!;
        var value = raw.Trim();

        if (value.Length == 0)
        {
            throw Invalid("must not be empty.");
        }

        // Control characters are rejected for every string setting, not just the ones where they are
        // dangerous: CR/LF in a value that reaches a mail header is injection, and the rest have no
        // legitimate use in a single-line setting.
        if (value.Any(c => c < 0x20 || c == 0x7F))
        {
            throw Invalid("must not contain control characters.");
        }

        if (value.Length > MaxLength)
        {
            throw Invalid($"must be {MaxLength} characters or fewer.");
        }

        if (Validator?.Invoke(value) is { } problem)
        {
            throw Invalid(problem);
        }
    }

    public override string Format(SystemSettingsUpdate request)
    {
        var trimmed = Read(request)!.Trim();
        return Canonicalize?.Invoke(trimmed) ?? trimmed;
    }

    public override ProjectionOutcome Project(string storedValue, SystemSettingsDto dto)
    {
        // Always safe: there is nothing to parse, so no stored value can fault here.
        Write(dto, storedValue);
        return ProjectionOutcome.Ok;
    }

    private DomainValidationException Invalid(string problem) =>
        new($"Setting '{FieldName}' {problem}", ErrorCode, FieldName);
}

/// <summary>
/// A <see cref="decimal"/>-valued setting (issue #421 Wave 0b). No setting uses this kind yet — the
/// first is the AI auto-link confidence threshold in Wave 1.
///
/// <para>
/// <strong>Culture is the whole story here.</strong> Both directions pin
/// <see cref="CultureInfo.InvariantCulture"/>, matching every existing serialization in
/// <see cref="Odyssey.Context.SystemSettingsKeys"/>. A bare <c>ToString()</c> under a
/// comma-decimal culture writes <c>0,6</c> and then throws on read — so this is a correctness
/// requirement, not a style preference, and there is a round-trip test under <c>de-DE</c> for it.
/// </para>
///
/// <para>
/// <see cref="decimal"/> rather than <see cref="double"/> even where the consuming options property is
/// a double: the consuming comparison already casts to decimal, and a double string round-trip is not
/// exactly stable. The conversion happens at the lookup boundary instead.
/// </para>
/// </summary>
internal sealed class DecimalSetting : SystemSettingDescriptor
{
    public required Func<SystemSettingsUpdate, decimal?> Read { get; init; }

    public required Action<SystemSettingsDto, decimal> Write { get; init; }

    public override bool IsPresent(SystemSettingsUpdate request) => Read(request) is not null;

    public override string Format(SystemSettingsUpdate request) =>
        Read(request)!.Value.ToString(StorageFormat, CultureInfo.InvariantCulture);

    public override ProjectionOutcome Project(string storedValue, SystemSettingsDto dto)
    {
        if (decimal.TryParse(storedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            Write(dto, parsed);
            return ProjectionOutcome.Ok;
        }

        Write(dto, decimal.Parse(DefaultValue, NumberStyles.Float, CultureInfo.InvariantCulture));
        return ProjectionOutcome.Unparseable;
    }

    /// <summary>
    /// Four decimal places is ample for every threshold-shaped setting and keeps the stored form
    /// canonical, so <c>0.60</c> and <c>0.6</c> do not become two distinct stored values that break
    /// the <c>GET</c>→<c>PUT</c> no-op.
    /// </summary>
    internal const string StorageFormat = "0.####";
}
