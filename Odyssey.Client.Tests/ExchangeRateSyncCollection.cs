using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Serialises the test classes that move <c>CurrenciesCard.InteractiveCheck</c> or
/// <c>ExchangeRatesCard.InteractiveCheck</c> — both are process-wide statics.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExchangeRateSyncCollection
{
    public const string Name = "Exchange-rate sync surfaces";
}
