using Odyssey.Core.Finance;
namespace Odyssey.Core.Tests;

/// <summary>Test double for <see cref="IPropertyLimitsLookup"/> (issue #167), mirroring <see cref="FakeContractLimitsLookup"/>.</summary>
internal sealed class FakePropertyLimitsLookup(int maxSmartTagsPerProperty = 20) : IPropertyLimitsLookup
{
    public PropertyLimits Limits { get; set; } = new(maxSmartTagsPerProperty, IsDegraded: false);

    public Task<PropertyLimits> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Limits);
}
