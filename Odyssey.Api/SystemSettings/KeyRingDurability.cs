using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Whether Data Protection is persisting its key ring somewhere that survives a restart (issue #444
/// §10). A secret written under an ephemeral key ring is unrecoverable at the next restart, so the
/// write is refused rather than accepted with a <c>204</c> and lost later.
/// </summary>
public interface IKeyRingDurability
{
    /// <summary>True when the resolved key repository is one of the durable types.</summary>
    bool IsDurable { get; }

    /// <summary>The resolved repository type name, or <c>"(none)"</c>. Logged at startup so an
    /// operator diagnosing an unexpected <c>503</c> can see what was classified.</summary>
    string RepositoryTypeName { get; }
}

/// <inheritdoc cref="IKeyRingDurability" />
/// <remarks>
/// <para>
/// <strong>The check is positive, not negative.</strong> "Inspect for an ephemeral implementation"
/// cannot be written: <c>EphemeralXmlRepository</c> is <c>internal</c> to
/// <c>Microsoft.AspNetCore.DataProtection</c>, so there is no type to test against — and depending on
/// version the fallback is chosen inside <c>XmlKeyManager</c> without being written back to the
/// options, so <c>XmlRepository</c> reads <see langword="null"/> and a negative check never fires at
/// all. This allow-lists the durable public repository types and treats anything else,
/// <see langword="null"/> included, as ephemeral.
/// </para>
/// <para>
/// Two consequences, stated rather than fixed. First, <strong>the allow-list is the extension
/// point</strong>: a deployment persisting keys elsewhere (Azure Blob, Redis, any custom
/// <see cref="IXmlRepository"/>) presents a type outside the list, is classified ephemeral and has
/// every secret write refused. It fails closed, which is the right direction, but on a correctly
/// configured durable deployment — so a future KMS or blob provider must extend this list, and the
/// startup log line names what was actually detected. Second, the check really means "explicitly
/// configured durable repository": a host where Data Protection auto-discovers a genuinely durable
/// <c>$HOME/.aspnet/DataProtection-Keys</c> ring reads <see langword="null"/> and is refused too. That
/// is defensible for a credential — a profile-local ring in a container is ephemeral in practice — but
/// it is why the refusal message says persistent key storage is not <em>explicitly configured</em>
/// rather than that the key ring is broken.
/// </para>
/// </remarks>
public sealed class KeyRingDurability(IOptions<KeyManagementOptions> options) : IKeyRingDurability
{
    /// <summary>
    /// The allow-list. Named by full type name rather than <c>typeof</c> because
    /// <c>RegistryXmlRepository</c> is Windows-only and referencing it directly drags a platform
    /// annotation into a cross-platform code path.
    /// </summary>
    private static readonly string[] DurableRepositoryTypes =
    [
        "Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository",
        "Microsoft.AspNetCore.DataProtection.Repositories.RegistryXmlRepository",
    ];

    public bool IsDurable => IsDurableRepository(options.Value.XmlRepository);

    public string RepositoryTypeName => options.Value.XmlRepository?.GetType().FullName ?? "(none)";

    /// <summary>
    /// The classification itself, as a pure function so it can be exercised with a
    /// <see langword="null"/> repository — the case that matters most and the one no integration test
    /// can construct, since the ephemeral type cannot be named.
    /// </summary>
    internal static bool IsDurableRepository(IXmlRepository? repository) =>
        repository is not null
        && DurableRepositoryTypes.Contains(repository.GetType().FullName, StringComparer.Ordinal);

    /// <summary>
    /// The message the <c>503</c> carries. It names the configuration to set, because nothing is
    /// "missing" or "broken" in a sense the operator would recognise otherwise.
    /// </summary>
    internal const string RefusalMessage =
        "Persistent key storage is not explicitly configured for this server, so a stored credential "
        + "would not survive a restart. Set DataProtection:KeysPath (DataProtection__KeysPath) to a "
        + "durable, writable directory and restart before saving a credential.";
}
