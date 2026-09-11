namespace Odyssey.Dtos.Journal;

/// <summary>
/// The contact projection embedded in a finance read DTO — <c>ExistingTransaction.Contact</c>
/// (issue #48 §10.2). <b>Exactly two members</b>, the only two anything on that path reads: the
/// transaction detail panel and table cell render <see cref="ResolvedDisplayName"/>, and the create
/// dialog and row menu recover <see cref="ContactId"/>.
///
/// <para>
/// It exists for the same reason <c>ContactRef</c> does: those endpoints are gated by
/// <c>transactions.read</c> / <c>accounts.read</c> / <c>budgets.read</c>, <b>not</b> by
/// <c>contacts.read</c>. Embedding the full <see cref="ExistingContact"/> there — which is what the
/// lookup used to return — sent every contact field to a caller holding none of the
/// <c>contacts.*</c> claims, and would have carried the alias list, the middle name and the three
/// lifecycle dates across with zero further code change.
/// </para>
///
/// <para>
/// <b>Do not add members.</b> Not <c>Type</c>, not <c>Archived</c>, and above all not
/// <c>OrganizationNumber</c> — an organisasjonsnummer is a real-world join key into the
/// Enhetsregisteret, and shipping one to a <c>transactions.read</c>-only caller fails GDPR
/// Art. 5(1)(c) data minimisation one notch down from the fields this type was created to withhold.
/// A guard test asserts the member count.
/// </para>
/// </summary>
public sealed record ContactEmbed
{
    public required Guid ContactId { get; set; }

    public required string ResolvedDisplayName { get; set; }
}
