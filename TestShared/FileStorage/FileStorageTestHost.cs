using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;

namespace Odyssey.Testing.FileStorage;

/// <summary>
/// A process of the file store for tests: the production registration (<c>AddFileStorage</c>) over a database the
/// caller chooses, temp volume roots, the test features, a manual clock, a settable free-space probe and captured
/// logs. Two hosts over the same database and folders are two processes — a restart, or a second replica.
/// </summary>
public sealed class FileStorageTestHost : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private FileStorageTestHost(ServiceProvider provider, TestClock clock, TestSpaceProbe space, CapturedLogs logs)
    {
        this.provider = provider;
        Clock = clock;
        Space = space;
        Logs = logs;
    }

    public IServiceProvider Services => provider;

    public TestClock Clock { get; }

    public TestSpaceProbe Space { get; }

    public CapturedLogs Logs { get; }

    /// <param name="useDatabase">Configures <see cref="OdysseyContext"/>'s provider: InMemory, or a MariaDB connection.</param>
    /// <param name="volumes">Volume name → root path.</param>
    /// <param name="bindings">Feature key → volume name.</param>
    /// <param name="features">The catalogue; the three test features unless given.</param>
    public static FileStorageTestHost Create(
        Action<DbContextOptionsBuilder<OdysseyContext>> useDatabase,
        IReadOnlyDictionary<string, string> volumes,
        IReadOnlyDictionary<string, string>? bindings = null,
        IEnumerable<StorageFeature>? features = null,
        TestClock? clock = null,
        FileStorageForbiddenRoots? forbiddenRoots = null,
        long maxFileSizeBytes = 64L * 1024 * 1024)
    {
        clock ??= new TestClock();
        var space = new TestSpaceProbe();
        var logs = new CapturedLogs();
        var settings = new Dictionary<string, string?> { ["FileStorage:MaxFileSizeBytes"] = maxFileSizeBytes.ToString() };
        foreach (var (name, root) in volumes)
        {
            settings[$"FileStorage:Volumes:{name}:RootPath"] = root;
        }

        foreach (var (feature, volume) in bindings ?? new Dictionary<string, string>())
        {
            settings[$"FileStorage:Bindings:{feature}"] = volume;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddLogging(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
        services.AddMemoryCache();
        services.AddSingleton<TimeProvider>(clock);
        var options = new DbContextOptionsBuilder<OdysseyContext>();
        useDatabase(options);
        services.AddSingleton(options.Options);
        services.AddSingleton<DbContextOptions>(sp => sp.GetRequiredService<DbContextOptions<OdysseyContext>>());
        services.AddScoped<OdysseyContext>(sp => new TestStorageContext(sp.GetRequiredService<DbContextOptions<OdysseyContext>>()));
        foreach (var feature in features ?? TestStorageFeatures.All)
        {
            services.AddSingleton(feature);
        }

        services.AddFileStorage();
        services.Replace(ServiceDescriptor.Singleton(forbiddenRoots ?? new FileStorageForbiddenRoots([])));
        services.Replace(ServiceDescriptor.Singleton<IFileStorageSpaceProbe>(space));
        services.AddSingleton<FileStorageMaintenanceJob>();

        return new FileStorageTestHost(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), clock, space, logs);
    }

    public Task<FileStorageVerification> VerifyAsync(FileStorageVerifyMode mode = FileStorageVerifyMode.Initialize) =>
        provider.VerifyFileStorageAsync(mode);

    public AsyncServiceScope Scope() => provider.CreateAsyncScope();

    public FileStorageVolumeRegistry Registry => provider.GetRequiredService<FileStorageVolumeRegistry>();

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}

/// <summary>A clock that moves only when told to.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Free space as the test says; null measures the real disk.</summary>
public sealed class TestSpaceProbe : IFileStorageSpaceProbe
{
    public (long Free, long Total)? Override { get; set; } = (500L * 1024 * 1024 * 1024, 1024L * 1024 * 1024 * 1024);

    public (long Free, long Total)? Measure(string rootPath) => Override;
}

/// <summary>Every log line, with its level and rendered message.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> entries = new();

    public IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries => [.. entries];

    public IEnumerable<string> At(LogLevel level) => entries.Where(e => e.Level == level).Select(e => e.Message);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturedLogs sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.entries.Enqueue((logLevel, category, formatter(state, exception) + (exception is null ? string.Empty : " :: " + exception.GetType().Name + ": " + exception.Message)));
    }
}
