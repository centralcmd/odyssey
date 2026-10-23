using System.Net;

namespace Odyssey.Core.Finance.ExchangeRateSync;

/// <summary>A manual run was asked for within the cooldown (issue #349 §5.5). Maps to <c>429</c>.</summary>
public sealed class ExchangeRateSyncCooldownException(DateTime availableAtUtc)
    : DomainException("A manual run was started less than a minute ago. Try again shortly.")
{
    public const string ErrorCode = "exchange-rate-sync-cooldown";

    public DateTime AvailableAtUtc { get; } = availableAtUtc;

    public override int StatusCode => (int)HttpStatusCode.TooManyRequests;

    public override string? Code => ErrorCode;
}

/// <summary>
/// Enabling a currency needs the provider's list and it could not be fetched (issue #349 §5.2). Maps to
/// <c>503</c>. Disabling never needs it.
/// </summary>
public sealed class ExchangeRateSupportedListUnavailableException()
    : DomainException("Norges Bank's currency list can't be fetched right now, so the currency can't be enabled. Try again in a few minutes.")
{
    public const string ErrorCode = "exchange-rate-provider-unavailable";

    public override int StatusCode => (int)HttpStatusCode.ServiceUnavailable;

    public override string? Code => ErrorCode;
}
