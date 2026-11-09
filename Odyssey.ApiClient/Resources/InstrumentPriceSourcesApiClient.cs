using Odyssey.Dtos.Finance;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for price-source administration (issue #446 §5 endpoints 16–18). The whole surface is
/// <c>system-settings.read</c>-gated; replacing an instrument's bindings needs <c>system-settings.update</c>. Run-now
/// and run history are the background-jobs endpoints (<see cref="IBackgroundJobsApiClient"/>).
/// </summary>
public interface IInstrumentPriceSourcesApiClient
{
    /// <summary>The registered sources, their state and the sync status. Empty sources while none is installed.</summary>
    Task<ApiResult<InstrumentPriceSourcesStatus>> GetStatusAsync(CancellationToken ct = default);

    /// <summary>The instrument's bindings in priority order; <c>404</c> for an unknown instrument.</summary>
    Task<ApiResult<List<ExistingInstrumentPriceBinding>>> GetBindingsAsync(Guid instrumentId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the whole binding list; priority is position. Field errors are keyed <c>bindings[i].*</c>.
    /// Returns the stored list.
    /// </summary>
    Task<ApiResult<List<ExistingInstrumentPriceBinding>>> ReplaceBindingsAsync(
        Guid instrumentId, InstrumentPriceBindingsUpdate update, CancellationToken ct = default);
}

/// <inheritdoc cref="IInstrumentPriceSourcesApiClient" />
public sealed class InstrumentPriceSourcesApiClient(IOdysseyApi api) : IInstrumentPriceSourcesApiClient
{
    private const string Base = "api/system-settings/instrument-price-sources";

    public Task<ApiResult<InstrumentPriceSourcesStatus>> GetStatusAsync(CancellationToken ct = default) =>
        api.GetAsync<InstrumentPriceSourcesStatus>(Base, ct);

    public Task<ApiResult<List<ExistingInstrumentPriceBinding>>> GetBindingsAsync(Guid instrumentId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingInstrumentPriceBinding>>($"{Base}/bindings/{instrumentId}", ct);

    public Task<ApiResult<List<ExistingInstrumentPriceBinding>>> ReplaceBindingsAsync(
        Guid instrumentId, InstrumentPriceBindingsUpdate update, CancellationToken ct = default) =>
        api.SendAsync<List<ExistingInstrumentPriceBinding>>(HttpMethod.Put, $"{Base}/bindings/{instrumentId}", update, ct);
}
