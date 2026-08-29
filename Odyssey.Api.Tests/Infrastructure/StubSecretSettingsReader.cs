using Odyssey.Context.Secrets;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// A hand-driven <see cref="ISecretSettingsReader"/> for the tests that construct a consumer directly
/// rather than through the host (issue #445).
///
/// <para>
/// It defaults every key to <see cref="SecretResult.NotSet"/>, which is the healthy "not configured"
/// state each consumer must behave as it always did in — so a test that says nothing about credentials
/// exercises exactly the pre-migration path.
/// </para>
/// </summary>
public sealed class StubSecretSettingsReader : ISecretSettingsReader
{
    private readonly Dictionary<string, SecretResult> results = new(StringComparer.Ordinal);

    /// <summary>Keys this reader was asked for, in order — so a test can assert nothing else is read.</summary>
    public List<string> Reads { get; } = [];

    public StubSecretSettingsReader Found(string key, string value)
    {
        results[key] = SecretResult.Found(value);
        return this;
    }

    public StubSecretSettingsReader Unreadable(string key)
    {
        results[key] = SecretResult.Unreadable;
        return this;
    }

    public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        Reads.Add(key);
        return Task.FromResult(results.TryGetValue(key, out var result) ? result : SecretResult.NotSet);
    }
}

/// <summary>
/// A fixed recipient hash key, so a test asserting on a throttle digest gets a stable one without
/// standing up the real resolver and its database read.
/// </summary>
public sealed class StubEmailRecipientHashKey(string key = "test-recipient-hash-key")
    : Odyssey.Api.Email.IEmailRecipientHashKey
{
    public ReadOnlyMemory<byte> Key { get; } = System.Text.Encoding.UTF8.GetBytes(key);

    public Task<ReadOnlyMemory<byte>> ResolveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Key);
}
