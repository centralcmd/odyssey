namespace Odyssey.Dtos.Finance;

/// <summary>
/// The bound on a term's <c>IntervalCount</c>. One pair, two consumers: the <c>[Range]</c> attribute
/// on <see cref="NewTerm.IntervalCount"/> and the re-check in <c>TermService.ApplyAndValidate</c>,
/// which runs for direct (non-HTTP) callers that never reach model validation. Naming the same
/// constants is what keeps the two layers from drifting.
/// </summary>
public static class TermIntervalCount
{
    public const int Min = 1;

    public const int Max = 1000;
}
