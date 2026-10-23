using System.Net;

namespace Odyssey.Core.Journal.Geocoding;

/// <summary>
/// The geocoding provider for the requested region could not answer (issue #350 §9): a 3 s timeout, a
/// provider 4xx/5xx, a redirect, an oversized or malformed body. Maps to <c>503</c> with the
/// <c>geocoding-unavailable</c> code. Deliberately carries no inner exception and no provider text: a
/// provider error body is never relayed, and a transport exception could name the outbound URL, which
/// carries the query.
/// </summary>
public sealed class GeocodingUnavailableException : DomainException
{
    public const string ErrorCode = "geocoding-unavailable";

    public GeocodingUnavailableException(string provider)
        : base($"{provider} isn't answering right now. Try again in a moment, or enter the place by hand.")
    {
    }

    public override int StatusCode => (int)HttpStatusCode.ServiceUnavailable;

    public override string? Code => ErrorCode;
}

/// <summary>
/// A provider call failed for any reason. Internal and message-free on purpose — see
/// <see cref="GeocodingUnavailableException"/> for why nothing from the failure is kept.
/// </summary>
internal sealed class GeocodingProviderException : Exception
{
}
