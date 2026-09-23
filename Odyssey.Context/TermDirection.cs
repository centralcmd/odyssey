namespace Odyssey.Context;

/// <summary>
/// Which way the money a <see cref="Term"/> prices moves, stated from the HOUSEHOLD's perspective —
/// not from either named party's (issue #159). A rental contract where the household is the landlord
/// takes rent <see cref="Incoming"/>; the same contract read from the tenant's side would be
/// <see cref="Outgoing"/>, and nothing in the model says which party the household is, so the field
/// is recorded rather than derived (§3.2).
///
/// <para>
/// Direction is a property of the ENTRY, never of the series: it joins neither the series key
/// <c>(owner, LabelKey)</c>, the duplicate guard nor supersession. Correcting a
/// mis-directed term therefore supersedes it rather than forking a second series.
/// </para>
///
/// <para>
/// Magnitude stays positive whichever way the money moves — <see cref="Term.Value"/> keeps its
/// <c>&gt;= 0</c> rule for amounts. Expressing income as a negative amount is precisely the defect
/// this enum exists to prevent: the sign would mean two different things depending on who typed it.
/// </para>
/// </summary>
public enum TermDirection
{
    /// <summary>Money leaves the household — the default, and the backfill value for every pre-#159 row.</summary>
    Outgoing = 0,

    /// <summary>Money arrives.</summary>
    Incoming = 1,
}
