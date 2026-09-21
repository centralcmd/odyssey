using Odyssey.Core.Finance;
namespace Odyssey.Core.Tests;

/// <summary>
/// Test double for <see cref="IContractLimitsLookup"/> (issue #166).
///
/// <para>
/// This is the reason that interface lives in <c>Odyssey.Core.Finance</c> rather than beside its
/// implementation: <c>Odyssey.Core.Tests</c> runs on EF InMemory and has no reference to
/// <c>Odyssey.Api</c>'s settings plumbing, so it fakes the lookup instead of seeding settings rows.
/// </para>
/// </summary>
internal sealed class FakeContractLimitsLookup(int maxSmartTagsPerContract = 20) : IContractLimitsLookup
{
    public ContractLimits Limits { get; set; } = new(maxSmartTagsPerContract, IsDegraded: false);

    public Task<ContractLimits> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Limits);
}
