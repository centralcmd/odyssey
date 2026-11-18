using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Context;
using Odyssey.Core.Jobs;

namespace Odyssey.Core.Tests;

/// <summary>
/// The run log's recording services for a test's own service collection (issue #409): every job resolves the
/// <see cref="JobRunRecorder"/> singleton, whose writes need a scoped <see cref="OdysseyContext"/> over the test's
/// store, a clock and a logger.
/// </summary>
internal static class JobRunTestServices
{
    /// <summary>Registers the recorder with a fresh context per scope over <paramref name="store"/>'s InMemory database.</summary>
    public static IServiceCollection AddJobRunRecordingOver(this IServiceCollection services, OdysseyContext store, TimeProvider time)
    {
        var options = (DbContextOptions<OdysseyContext>)store.GetService<IDbContextOptions>();
        services.TryAddScoped(_ => new OdysseyContext(options));
        return services.AddJobRunRecordingWith(time);
    }

    /// <summary>Registers the recorder over whatever scoped <see cref="OdysseyContext"/> the collection already has.</summary>
    public static IServiceCollection AddJobRunRecordingWith(this IServiceCollection services, TimeProvider time)
    {
        services.TryAddSingleton<TimeProvider>(time);
        services.AddLogging();
        services.AddMemoryCache();
        return services.AddJobRunRecording();
    }
}
