using System.Net;

namespace Odyssey.Core.Weather;

/// <summary>
/// A weather precondition that is not met (issue #348 §9): a <c>409</c> naming the precondition in
/// <see cref="Code"/>, so the client can tell a stale review from a running fetch without parsing prose.
/// Messages carry only opaque ids and fixed text — never coordinates, never provider text (§7.12).
/// </summary>
public sealed class WeatherConflictException : DomainException
{
    /// <summary>The source place changed between the Admin's review and the create (issue #387 §5 endpoint 4).</summary>
    public const string LocationChanged = "location-changed";

    /// <summary>A purge was asked for while a fetch holds the point's lease.</summary>
    public const string FetchInProgress = "fetch-in-progress";

    public const string PurgeInProgress = "purge-in-progress";
    public const string HouseholdDefault = "household-default";

    /// <summary>A purge or delete was asked for while a half it would delete is still on.</summary>
    public const string StillEnabled = "still-enabled";

    /// <summary>Another point has this name (keyed <c>name</c>).</summary>
    public const string DuplicateName = "duplicate-name";

    /// <summary>The <c>settingsVersion</c> sent is not the stored one: someone else changed the point.</summary>
    public const string Stale = "stale";

    private readonly string code;

    public WeatherConflictException(string code, string message, string? field = null) : base(message)
    {
        this.code = code;
        if (field is not null)
        {
            Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [field] = [message] };
        }
    }

    public override int StatusCode => (int)HttpStatusCode.Conflict;

    public override string? Code => code;
}

/// <summary>
/// No usable <c>WeatherUserAgentContact</c> is set, so nothing may be sent to MET Norway. A <c>503</c> on
/// the refresh endpoint; reads report <c>NotConfigured</c> instead.
/// </summary>
public sealed class WeatherNotConfiguredException : DomainException
{
    public const string ErrorCode = "weather-not-configured";

    public WeatherNotConfiguredException()
        : base("Weather isn't set up: add a contact address for MET Norway in System settings → Weather.")
    {
    }

    public override int StatusCode => (int)HttpStatusCode.ServiceUnavailable;

    public override string? Code => ErrorCode;
}
