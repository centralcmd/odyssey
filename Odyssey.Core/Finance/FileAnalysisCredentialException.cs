namespace Odyssey.Core.Finance;

/// <summary>
/// The analysis provider credential could not be resolved, so the request was never sent (issue #445
/// Wave 1).
///
/// <para>
/// A subtype of <see cref="FileAnalysisProviderException"/> rather than a sibling, so every existing
/// <c>catch</c> and every resilience predicate keeps treating it as the provider failure it is — and
/// so a caller that does not care about the distinction needs no change. <c>FileAnalysisService</c>
/// tests for it FIRST, because the two record different <c>FailureCode</c>s: an administrator looking
/// at a failed job needs to know the difference between "the provider answered badly" and "this
/// deployment has no usable key", since only the second is theirs to fix.
/// </para>
///
/// <para>
/// <strong>Nothing it carries is derived from the credential</strong> — not a length, not a prefix,
/// not whether a row existed. The message names the condition and the remedy, and that is all.
/// </para>
/// </summary>
public sealed class FileAnalysisCredentialException : FileAnalysisProviderException
{
    public FileAnalysisCredentialException(string message) : base(message) { }
}
