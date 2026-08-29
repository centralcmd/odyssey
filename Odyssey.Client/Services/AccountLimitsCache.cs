using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Application;
using Odyssey.Dtos;

namespace Odyssey.Client.Services;

/// <summary>
/// A per-session cache for the effective per-account limits (issue #434 key 15), the same shape as
/// <see cref="IUploadLimitsCache"/>/<see cref="IImportLimitsCache"/> rather than a bespoke one.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the smart-tag cap used to be a <c>private const int MaxTags = 20</c> in
/// <c>AccountSmartTagsSection</c>. That made the setting useless in both directions the moment it
/// became editable: lowering it would let a user add tags the server then refused, and raising it would
/// be unusable because the local pre-check still stopped at 20.
/// </para>
/// <para>
/// <b>Failures are never cached, and never surfaced as null.</b> A failed load — including the degraded
/// <c>503</c> — leaves the slot empty so the next reader retries, and this attempt resolves to
/// <see cref="Fallback"/>. There is deliberately <b>no disable branch</b> in the consuming section:
/// this method cannot fail, the upload surfaces it mirrors do not disable either, and the server
/// remains the control — <c>AccountSmartTagService</c> rejects an over-cap add whatever the client
/// believes. A local pre-check is a convenience, never the gate.
/// </para>
/// <para>
/// <b>Mutations must invalidate.</b> A successful save on <c>/settings</c> calls
/// <see cref="Invalidate"/>; otherwise an admin who lowers the cap and then expands an account in the
/// same session would pre-validate against the old value for the rest of that session.
/// </para>
/// </remarks>
public interface IAccountLimitsCache
{
    /// <summary>The effective account limits — the live server value, or the shipped fallback.</summary>
    Task<AccountLimitsDto> GetAsync(CancellationToken ct = default);

    /// <summary>Drops the cached limits; the next reader re-fetches.</summary>
    void Invalidate();
}

/// <inheritdoc cref="IAccountLimitsCache" />
public sealed class AccountLimitsCache(IAccountLimitsApiClient api) : IAccountLimitsCache
{
    /// <summary>
    /// The shipped default, named by reference rather than restated as a literal: the same
    /// <see cref="SystemSettingsDefaults"/> constant the migration seeds and the server's <c>[Range]</c>
    /// bound names, reachable here because <c>Odyssey.Dtos</c> has zero project references and the
    /// client already gets it transitively.
    ///
    /// <para>
    /// This is the ONE place in the client a smart-tag cap number legitimately appears, and the
    /// source-lint that forbids the number in <c>Pages/</c> deliberately does not flag it.
    /// </para>
    /// </summary>
    public static readonly AccountLimitsDto Fallback = new()
    {
        MaxSmartTagsPerAccount = SystemSettingsDefaults.AccountMaxSmartTagsPerAccount,
    };

    private Task<AccountLimitsDto?>? pending;

    public async Task<AccountLimitsDto> GetAsync(CancellationToken ct = default)
    {
        var task = pending ??= LoadAsync(ct);
        var result = await task;

        if (result is null && ReferenceEquals(pending, task))
        {
            pending = null;
        }

        return result ?? Fallback;
    }

    public void Invalidate() => pending = null;

    private async Task<AccountLimitsDto?> LoadAsync(CancellationToken ct)
    {
        var result = await api.GetAsync(ct);
        return result.IsSuccess ? result.Value : null;
    }
}
