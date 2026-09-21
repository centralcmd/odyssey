using System.Globalization;
using System.Text;
using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// Which of a contract's four lifecycle stamps a transition concerns (issue #154 §8.1). Used by the
/// detector, by the title catalogue and by <c>ContractService.LogStampWrite</c>, so the three cannot
/// disagree about which stamps exist.
/// </summary>
public enum ContractStamp
{
    Paused,
    Archived,
    Ready,
    Signed,
}

/// <summary>Which of the three term verbs a <c>PriceChanged</c> event is describing (issue #154 §8.3).</summary>
public enum TermWriteAction
{
    Added,
    Changed,
    Removed,
}

/// <summary>
/// The fixed, per-transition mapping to an event's <c>Title</c> and <c>Description</c> (issue #154
/// §8.3). One table, so the wire text lives in one place and cannot drift between the call sites that
/// record it.
/// </summary>
/// <remarks>
/// <para>
/// <b>No contact or account name may appear here, ever</b> (§7.3). The reason is <em>permanence</em>,
/// not claim crossover — <c>IContactLookup</c> resolves contact names with no claim check at all, so a
/// <c>contracts.read</c> holder already sees them on the party projection. The difference is that the
/// party projection resolves a name at read time and goes blank when the contact is archived or
/// deleted, whereas a name written into an event title is frozen prose in a free-text column: it
/// survives archival, deletion and an erasure request, and stays reachable forever. That is a
/// revocation-proof copy of personal data created as a side effect of an unrelated write — a GDPR
/// Art. 17 problem rather than an access-control one. So <see cref="PartyAdded"/> reads
/// "Employer added as a party", never "Acme AS added as a party".
/// </para>
/// <para>
/// What a generated string may contain is therefore closed: the contract's own scalars, a closed
/// enum member's label, a term's amount/percentage/currency/cadence/effective date, and a term's
/// user-supplied <c>Label</c> — all of which <c>GET …/terms</c> already returns under the same claim.
/// </para>
/// <para>
/// <b>Every generated string is bounded here</b>, at the catalogue level, and truncated rather than
/// left to throw: a generated title must never be able to fail the entity's <c>[StringLength]</c> and
/// abort the write it is describing (§9, <c>GeneratedTitleTooLong</c>).
/// </para>
/// </remarks>
public static class ContractEventCatalogue
{
    /// <summary>Matches <c>ContractEvent.Title</c>'s <c>[StringLength(256)]</c>.</summary>
    public const int MaxTitleLength = 256;

    /// <summary>Matches <c>ContractEvent.Description</c>'s <c>[StringLength(1024)]</c>.</summary>
    public const int MaxDescriptionLength = 1024;

    /// <summary>
    /// The log's own prose date. Invariant culture, never a client-formatted value — the server writes
    /// the sentence, so the sentence does not vary by who reads it.
    /// </summary>
    private const string DateFormat = "d MMMM yyyy";

    /// <summary>
    /// One of the eight stamp transitions. <paramref name="set"/> distinguishes the
    /// <c>null</c> → non-null direction from its opposite; the detector never calls this for an
    /// unchanged stamp or for a non-null → <em>different</em> non-null re-date, both of which write
    /// nothing (§8.1).
    /// </summary>
    /// <param name="occurredAt">
    /// The transition moment already resolved by §8.4 — the server-generated stamp itself for
    /// <c>Paused</c>/<c>Archived</c>, <c>min(stamp, now)</c> for the two caller-supplied stamps, and
    /// the server clock for every clear. The description's date is read off this same value, so the
    /// prose and the timeline position can never disagree.
    /// </param>
    public static TransitionDescriptor Stamp(ContractStamp stamp, bool set, DateTime occurredAt)
    {
        var date = Date(occurredAt);

        var (type, title, description) = (stamp, set) switch
        {
            (ContractStamp.Paused, true) => (ContractEventType.Paused, "Contract paused", $"Suspended on {date}."),
            (ContractStamp.Paused, false) => (ContractEventType.Unpaused, "Contract resumed", $"Resumed on {date}."),
            (ContractStamp.Archived, true) => (ContractEventType.Archived, "Contract archived", $"Archived on {date}."),
            (ContractStamp.Archived, false) => (ContractEventType.Unarchived, "Contract restored from the archive", $"Restored on {date}."),
            (ContractStamp.Ready, true) => (ContractEventType.Ready, "Marked ready for signature", $"Ready for signature as of {date}."),
            (ContractStamp.Ready, false) => (ContractEventType.Unready, "Ready-for-signature mark withdrawn", $"Withdrawn on {date}."),
            // Ordinal 0 already IS the signing event, so there is deliberately no new Signed member.
            (ContractStamp.Signed, true) => (ContractEventType.Signed, "Contract signed", $"Signed by all parties on {date}."),
            _ => (ContractEventType.Unsigned, "Signed date cleared", $"Cleared on {date}."),
        };

        return new TransitionDescriptor(
            type, Bound(title, MaxTitleLength), Bound(description, MaxDescriptionLength), occurredAt);
    }

    /// <summary>A party joining the agreement. The role, never the target's name (§7.3).</summary>
    public static TransitionDescriptor PartyAdded(ContractPartyRole role, DateTime occurredAt) =>
        new(ContractEventType.PartyAdded, Bound($"{RoleLabel(role)} added as a party", MaxTitleLength), null, occurredAt);

    /// <summary>A party leaving the agreement. The role, never the target's name (§7.3).</summary>
    public static TransitionDescriptor PartyRemoved(ContractPartyRole role, DateTime occurredAt) =>
        new(ContractEventType.PartyRemoved, Bound($"{RoleLabel(role)} removed as a party", MaxTitleLength), null, occurredAt);

    /// <summary>
    /// A term write, reusing the existing <see cref="ContractEventType.PriceChanged"/> rather than
    /// adding term-specific members (§2 goal 5).
    /// </summary>
    /// <remarks>
    /// The term's user-supplied <c>Label</c> is interpolated into the title. <b>Note the asymmetry
    /// with §8.8:</b> the label may appear in the <em>event</em>, which is contract data behind
    /// <c>contracts.read</c>, and may <b>not</b> appear in the <em>log line</em>, which is
    /// operator-facing and takes no free text. It is also the only interpolated value here that is not
    /// a bounded enum label, which is why <see cref="Bound"/> exists.
    /// </remarks>
    public static TransitionDescriptor Term(TermWriteAction action, Term term, DateTime occurredAt)
    {
        ArgumentNullException.ThrowIfNull(term);

        var verb = action switch
        {
            TermWriteAction.Added => "added",
            TermWriteAction.Changed => "changed",
            _ => "removed",
        };

        var label = term.Label;
        var title = string.IsNullOrWhiteSpace(label)
            ? $"{KindLabel(term.TermKind)} term {verb}"
            : $"{KindLabel(term.TermKind)} term {verb} ({label})";

        var effective = Date(term.EffectiveFrom);
        var description = action == TermWriteAction.Removed
            ? $"Effective {effective} — removed."
            : $"{TermValue(term)} effective {effective}.";

        return new TransitionDescriptor(
            ContractEventType.PriceChanged,
            Bound(title, MaxTitleLength),
            Bound(description, MaxDescriptionLength),
            occurredAt);
    }

    /// <summary>
    /// The term's priced value as the term API already returns it: an amount with its currency code or
    /// a percentage, plus its fee cadence where it has one.
    /// </summary>
    private static string TermValue(Term term)
    {
        var number = term.Value.ToString("0.######", CultureInfo.InvariantCulture);
        var value = term.ValueUnit == TermValueUnit.Amount
            ? string.IsNullOrWhiteSpace(term.CurrencyCode) ? number : $"{term.CurrencyCode} {number}"
            : $"{number}%";

        var cadence = Cadence(term.Interval, term.IntervalCount);
        return cadence is null ? value : $"{value} {cadence}";
    }

    /// <summary>
    /// A fee's cadence in prose. <c>IntervalCount</c> is a <b>divisor of the period</b>, not a
    /// multiplier of the price: 3 with <c>Monthly</c> is quarterly, so it reads "every 3 months".
    /// The two rate kinds and a cadence-less fee return <see langword="null"/> and the value stands
    /// alone.
    /// </summary>
    private static string? Cadence(Interval? interval, int? count) => interval switch
    {
        null => null,
        Interval.OneTime => "one-time",
        Interval.PerOccurrence => "per occurrence",
        Interval.PerUnit => "per unit",
        Interval.Daily => Periodic(count, "day", "days"),
        Interval.Weekly => Periodic(count, "week", "weeks"),
        Interval.Monthly => Periodic(count, "month", "months"),
        Interval.Annually => Periodic(count, "year", "years"),
        _ => null,
    };

    private static string Periodic(int? count, string singular, string plural) =>
        count is null or 1
            ? $"per {singular}"
            : $"every {count.Value.ToString(CultureInfo.InvariantCulture)} {plural}";

    /// <summary>
    /// A closed enum member as prose: PascalCase split into words and sentence-cased, so
    /// <c>ExpectedReturn</c> reads "Expected return".
    /// </summary>
    private static string KindLabel(TermKind kind) => SentenceCase(kind.ToString());

    /// <summary>
    /// A contract party role as prose. Every member is a single word today, so the split is a
    /// safeguard for later additions rather than something the current set exercises.
    /// </summary>
    /// <remarks>
    /// Issue #154 §8.3 carves out <c>Unspecified</c>, whose title would have read "Party added as a
    /// party"; that ordinal is <b>retired</b> (issue #157) and cannot be written, so the carve-out has
    /// nothing to apply to and no special case is implemented for it.
    /// </remarks>
    private static string RoleLabel(ContractPartyRole role) => SentenceCase(role.ToString());

    private static string SentenceCase(string pascal)
    {
        var builder = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var ch = pascal[i];
            if (i > 0 && char.IsUpper(ch))
            {
                builder.Append(' ');
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string Date(DateTime value) =>
        value.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Truncates rather than throwing. A generated title that failed validation would abort the write
    /// it is describing, which is the one outcome this feature must never produce (§9). The ellipsis is
    /// a single character, so a truncated string is <em>exactly</em> <paramref name="max"/> long.
    /// </summary>
    public static string Bound(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");
}
