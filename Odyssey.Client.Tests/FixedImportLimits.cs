using Microsoft.Extensions.DependencyInjection;
using Moq;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Tests;

/// <summary>
/// An <see cref="IImportLimitsCache"/> answering with fixed limits — the shipped fallback unless a test says otherwise,
/// whose calendar link cap is null (issue #522: no pre-check). The calendar event dialog awaits it beside its detail.
/// </summary>
internal sealed class FixedImportLimits(ImportLimitsDto? limits = null) : IImportLimitsCache
{
    public ImportLimitsDto Limits { get; set; } = limits ?? ImportLimitsCache.Fallback;

    public int Invalidations { get; private set; }

    /// <summary>The fallback with a live calendar link cap.</summary>
    public static FixedImportLimits WithLinkCap(int? cap) => new(ImportLimitsCache.Fallback with { CalendarEventMaxLinksPerKind = cap });

    public Task<ImportLimitsDto> GetAsync(CancellationToken ct = default) => Task.FromResult(Limits);

    public void Invalidate() => Invalidations++;
}

/// <summary>
/// What the calendar event dialog's attachment fields inject (issue #522): the Files and Photos clients, the clipboard
/// the file rows' Copy ID uses, the import limits the cap comes from, and the clock the slow-load announcement reads. For a test context that registers its own.
/// </summary>
internal static class CalendarAttachmentServices
{
    public static void Add(IServiceCollection services, IImportLimitsCache? limits = null)
    {
        services.AddSingleton(Mock.Of<IFilesApiClient>());
        services.AddSingleton(Mock.Of<IPhotosApiClient>());
        services.AddSingleton(Mock.Of<IClipboardService>());
        services.AddSingleton(limits ?? new FixedImportLimits());
        services.AddSingleton(TimeProvider.System);
    }
}
