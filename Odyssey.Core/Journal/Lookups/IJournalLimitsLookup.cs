namespace Odyssey.Core.Journal;

/// <summary>
/// The per-request caps for the photo, journal and calendar modules, migrated out of POCO defaults and
/// <c>private const</c>s into the settings store (issue #421 Wave 3, extended by issue #434).
/// </summary>
/// <param name="PhotoMaxLinksPerKind">
/// Max distinct tag/person/album links on one photo. <strong>Tighten-only</strong>: the compile-time
/// <c>PhotoLimits.MaxLinksPerKind</c> also feeds <c>[MaxLength]</c> on the photo request DTOs, so a
/// value above it could never take effect and is rejected on write.
/// </param>
/// <param name="PhotoMaxAlbumMembers">Max photos in one album. Tighten-only, same reason.</param>
/// <param name="JournalEntryMaxLinksPerKind">
/// Max links of one kind on a journal entry. Enforced on the create/update path <em>and</em> — since
/// issue #434 §9-A — on the ICS import path, which used to ignore it in favour of a hardcoded 50.
/// </param>
/// <param name="JournalTaskMaxLinksPerKind">Max links of one kind on a task. Same, both paths.</param>
/// <param name="PhotoMetadataReadBytes">
/// Upper bound on the blob prefix read for metadata extraction, pre-converted to bytes here at the
/// lookup boundary so no consumer repeats the megabyte arithmetic. Extraction materialises a full
/// array of this size per photo, so it is a per-upload memory multiplier.
/// </param>
/// <param name="PhotoMetadataExtractionTimeoutSeconds">Wall-clock timeout for one extraction.</param>
/// <param name="CalendarMaxWindowDays">Widest From/To window a calendar-event list query may span.</param>
/// <param name="CalendarMaxEventDurationDays">Longest a single calendar event may span.</param>
/// <param name="RecurrenceMaxGeneratedOccurrences">
/// Occurrences one recurrence pattern may generate. <strong>Tighten-only</strong>, and the read path
/// clamps to the shipped default as well as the write path: <c>[Range]</c> runs on the HTTP path
/// alone, so a row written by config adoption, by a hand edit or by a restore would otherwise carry an
/// unbounded value straight into the generator.
///
/// <para>
/// It lives on this record rather than on <see cref="ImportExportLimits"/> — where issue #434 §5 first
/// placed it — because its two consumers, <c>RecurrencePatternService</c> and
/// <c>CalendarIcsService</c>, both also need <see cref="CalendarMaxEventDurationDays"/> through the
/// shared <c>CalendarEventService.ValidateTimes</c> helper. Putting both on one record is what keeps
/// each of them to exactly ONE new lookup instead of two.
/// </para>
/// </param>
/// <param name="IsDegraded">
/// True when one or more values fell back rather than being read cleanly — the query failed, or a row
/// was present carrying a value this setting cannot use. An <em>absent</em> row is healthy: it
/// resolves to the compiled default, the same posture the settings service takes on reads.
///
/// <para>
/// This flag and the last-known-good watermark behind it were added in issue #434 (V3-S2) so this
/// record degrades by the <em>same</em> algorithm as <see cref="ImportExportLimits"/>. Without them,
/// the two link caps would have resolved by one rule when read on the create/update path and another
/// when read on the ICS import path — the divergence §9-A exists to remove.
/// </para>
/// </param>
public sealed record JournalLimits(
    int PhotoMaxLinksPerKind,
    int PhotoMaxAlbumMembers,
    int JournalEntryMaxLinksPerKind,
    int JournalTaskMaxLinksPerKind,
    long PhotoMetadataReadBytes,
    int PhotoMetadataExtractionTimeoutSeconds,
    int CalendarMaxWindowDays,
    int CalendarMaxEventDurationDays,
    int RecurrenceMaxGeneratedOccurrences,
    bool IsDegraded);

/// <summary>
/// Narrow cross-domain lookup for the journal-side request caps (issue #421 Wave 3).
///
/// <para>
/// Separate from <c>Odyssey.Core.Finance</c>'s <c>ISystemSettingsLookup</c> for the reason that decides all
/// of these: a lookup interface lives in the domain project that <em>consumes</em> it, so that
/// project's tests can fake it without referencing <c>Odyssey.Context</c>. Wave 3's nine
/// caps span two domain projects, so one interface could not have served both.
/// </para>
/// </summary>
public interface IJournalLimitsLookup
{
    Task<JournalLimits> GetAsync(CancellationToken cancellationToken = default);
}
