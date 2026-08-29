using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Services;

/// <summary>
/// Caches the processor disclosure the analyze-file consent gate renders (issue #421 Wave 1).
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="ImportLimitsCache"/> — one shared in-flight <see cref="Task"/> so concurrent
/// readers cost one request, failures never cached, a static <see cref="Fallback"/>, and an
/// <see cref="Invalidate"/> the settings page calls on a successful save.
/// </para>
/// <para>
/// <b>Stale-while-revalidate, not session-lifetime.</b> That is the one deliberate departure from
/// <see cref="ImportLimitsCache"/>: a Blazor WebAssembly session can last days, and a disclosure
/// changed by another admin must not stay invisible for that long — this is legal text under GDPR
/// Art. 13, not a volume cap. So a cached value is returned immediately AND a refresh is kicked off,
/// and the consent gate opens rarely enough that the extra request costs nothing worth optimising.
/// </para>
/// <para>
/// <b><see cref="Fallback"/> is where the old client constants went.</b> They were compile-time consts
/// in <c>Models/FileAnalysisConsent.cs</c>, duplicated from the server and already drifted — the panel
/// named a model the server did not use. They live here now, as the last resort for a failed fetch,
/// which is the only reason the client still needs a copy at all.
/// </para>
/// </remarks>
public interface IFileAnalysisDisclosureCache
{
    /// <summary>
    /// The live disclosure, or <see cref="FileAnalysisDisclosureCache.Fallback"/> when no successful
    /// fetch has completed yet. Never null, so the gate can never render a partial disclosure.
    /// </summary>
    Task<FileAnalysisDisclosureDto> GetAsync(CancellationToken ct = default);

    /// <summary>True once a fetch has succeeded — the gate keeps its affirmation disabled until then.</summary>
    bool IsResolved { get; }

    /// <summary>Drops the cached disclosure; the next reader re-fetches.</summary>
    void Invalidate();
}

/// <inheritdoc cref="IFileAnalysisDisclosureCache" />
public sealed class FileAnalysisDisclosureCache(IFileAnalysisDisclosureApiClient api) : IFileAnalysisDisclosureCache
{
    /// <summary>
    /// The shipped defaults, matching the migration seed. A last resort only: rendering these while
    /// presenting them as authoritative is exactly what the server's <c>503</c> exists to prevent, so
    /// the gate pairs them with a disabled affirmation.
    /// </summary>
    public static readonly FileAnalysisDisclosureDto Fallback = new()
    {
        Processor = "Anthropic",
        ProcessorRegion = "United States",
        LawfulBasis = "Consent · GDPR Art. 6(1)(a)",
        PrivacyNoticeUrl = "https://www.anthropic.com/legal/privacy",
        Model = string.Empty,
    };

    private FileAnalysisDisclosureDto? cached;
    private Task<FileAnalysisDisclosureDto>? pending;

    public bool IsResolved => cached is not null;

    public Task<FileAnalysisDisclosureDto> GetAsync(CancellationToken ct = default)
    {
        // Stale-while-revalidate: serve what we have, refresh behind it. The refresh is deliberately
        // not awaited — the caller must not block on it, and a failure must not surface as an error.
        if (cached is { } current)
        {
            _ = Refresh(ct);
            return Task.FromResult(current);
        }

        return pending ??= Refresh(ct);
    }

    public void Invalidate()
    {
        cached = null;
        pending = null;
    }

    private async Task<FileAnalysisDisclosureDto> Refresh(CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync(ct);
            if (result.IsSuccess && result.Value is { } dto)
            {
                cached = dto;
                return dto;
            }

            return cached ?? Fallback;
        }
        catch
        {
            return cached ?? Fallback;
        }
        finally
        {
            // Cleared unconditionally so a failed attempt is never cached and the next reader retries.
            pending = null;
        }
    }
}
