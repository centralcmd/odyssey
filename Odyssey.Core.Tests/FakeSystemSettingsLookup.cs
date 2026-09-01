using Odyssey.Core.Finance;
namespace Odyssey.Core.Tests;

/// <summary>
/// Test double for <see cref="ISystemSettingsLookup"/>, covering all three of its methods.
///
/// <para>
/// This is the reason that interface lives in <c>Odyssey.Core.Finance</c> rather than beside its
/// implementation: <c>Odyssey.Core.Tests</c> runs on EF InMemory and has no reference to
/// <c>Odyssey.Context</c>, so it fakes the lookup instead of seeding settings rows. The
/// literals below are inline for the same reason — this project cannot name the key catalogue — and
/// every one mirrors the shipped default, so a test that does not care about a value reads production
/// behaviour.
/// </para>
/// </summary>
internal sealed class FakeSystemSettingsLookup : ISystemSettingsLookup
{
    public InsurancePolicySettings InsurancePolicy { get; set; } = new(30, 1000);

    public FinanceRequestCaps Caps { get; set; } = new(25, 50, 1000, 100, 50, 50);

    /// <summary>The issue #437 defaults: a 45-day window, six renewal rows, a 1000-row summary fetch.</summary>
    public SubscriptionSettings Subscriptions { get; set; } = new(45, 6, 1000);

    public Task<InsurancePolicySettings> GetInsurancePolicySettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(InsurancePolicy);

    public Task<FinanceRequestCaps> GetRequestCapsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Caps);

    public Task<SubscriptionSettings> GetSubscriptionSettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Subscriptions);
}
