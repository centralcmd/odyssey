using Odyssey.Core.Finance;
namespace Odyssey.Core.Tests;

/// <summary>
/// Test double for <see cref="IAccountLimitsLookup"/> (issue #434 key 15).
///
/// <para>
/// This is the reason that interface lives in <c>Odyssey.Core.Finance</c> rather than beside its
/// implementation: <c>Odyssey.Core.Tests</c> runs on EF InMemory and has no reference to
/// <c>Odyssey.Context</c>, so it fakes the lookup instead of seeding settings rows.
/// </para>
/// </summary>
internal sealed class FakeAccountLimitsLookup(int maxSmartTagsPerAccount = 20) : IAccountLimitsLookup
{
    public AccountLimits Limits { get; set; } = new(maxSmartTagsPerAccount, IsDegraded: false);

    public Task<AccountLimits> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Limits);
}
