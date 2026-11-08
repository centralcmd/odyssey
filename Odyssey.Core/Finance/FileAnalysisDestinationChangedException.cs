using System.Net;
using Odyssey.Core;

namespace Odyssey.Core.Finance;

/// <summary>
/// A match was refused because the destination or the disclosure changed since the job's consent was
/// given (issue #433 §3 step 2). Nothing was sent and the match status is unchanged; the remedy is a
/// new analysis, under the disclosure now in force.
///
/// <para>
/// The message names no host, processor or region: those are administrator configuration, and a
/// <c>409</c> body is not the place to report what they changed to.
/// </para>
/// </summary>
public sealed class FileAnalysisDestinationChangedException : DomainException
{
    /// <summary>The <c>code</c> extension the typed client keys on.</summary>
    public const string DestinationChangedCode = "file_analysis_destination_changed";

    public override int StatusCode => (int)HttpStatusCode.Conflict;

    public override string? Code => DestinationChangedCode;

    public FileAnalysisDestinationChangedException()
        : base("The analysis provider settings changed after this statement was analysed, so nothing was sent. "
               + "Start a new analysis to match it.")
    {
    }
}
