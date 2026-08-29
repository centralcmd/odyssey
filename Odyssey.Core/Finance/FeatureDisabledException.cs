using System.Net;
using Odyssey.Core;

namespace Odyssey.Core.Finance;

/// <summary>
/// Base type for the grandfathered "this capability is switched off by configuration" errors (file
/// analysis). Maps to <c>503 Service Unavailable</c> and carries a machine-readable <see cref="Code"/>
/// so clients can branch without parsing the message — <c>GlobalExceptionHandler</c> surfaces it as a
/// <c>code</c> problem extension. Lets these throws bubble to the central handler like every other
/// domain error instead of needing per-action <c>try/catch</c>.
/// </summary>
public abstract class FeatureDisabledException : DomainException
{
    public const string FeatureCode = "feature_disabled";

    public override int StatusCode => (int)HttpStatusCode.ServiceUnavailable;

    public override string? Code => FeatureCode;

    protected FeatureDisabledException(string message) : base(message)
    {
    }
}
