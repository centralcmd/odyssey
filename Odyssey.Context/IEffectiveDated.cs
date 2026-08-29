namespace Odyssey.Context;

/// <summary>
/// A time-versioned entry whose value-in-force is resolved by implicit supersession: there is no
/// explicit end date, so the entry in force on a date is the one with the greatest
/// <see cref="EffectiveFrom"/> on or before it, ties broken by the most recently created row
/// (<see cref="CreatedAtUtc"/>). Implemented by <see cref="AccountTerm"/> and
/// <see cref="AccountEstimate"/>; the tie-break rule itself lives in
/// <c>EffectiveDatedExtensions</c> so it has a single home.
/// </summary>
public interface IEffectiveDated
{
    DateTime EffectiveFrom { get; }

    DateTime CreatedAtUtc { get; }
}
