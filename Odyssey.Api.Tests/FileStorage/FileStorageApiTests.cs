using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Api.Tests.FileStorage;

/// <summary>
/// The store over HTTP (issue #346): the status endpoint and its claim, the serving rules through a consumer
/// endpoint, the problem mapping, the reservation context factory and the no-op rule (AC 7, 21, 24, 26, 27, 29, A3-5).
/// </summary>
public class FileStorageApiTests
{
    private const string VolumesPath = "/api/file-storage/volumes";

    [Fact]
    public async Task An_admin_gets_every_configured_volume_with_no_path_file_name_or_object_id()
    {
        await using var factory = FileStorageApiFactory.Create([PermissionClaims.FileStorageRead]);
        using var client = factory.CreateClient();
        var id = await PutAsync(client, "Passport_Ola_Nordmann.jpg", 3000);

        var response = await client.GetAsync(VolumesPath);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var volumes = JsonSerializer.Deserialize<List<FileStorageVolumeStatus>>(body, JsonSerializerOptions.Web)!;
        Assert.Equal(["archive", "primary"], volumes.Select(v => v.Name));
        var primary = volumes.Single(v => v.Name == "primary");
        Assert.Equal(FileStorageVolumeState.Available, primary.State);
        Assert.Equal(["TestFiles", "TestSecrets"], primary.BoundFeatures);
        var usage = Assert.Single(primary.Features);
        Assert.Equal("TestFiles", usage.FeatureKey);
        Assert.Equal(1, usage.ObjectCount);
        Assert.Equal(3000, usage.CommittedBytes);
        Assert.NotNull(primary.FreeBytes);
        Assert.Contains("\"state\":\"Available\"", body, StringComparison.Ordinal);

        Assert.DoesNotContain("/", body.Replace("\\u002F", "/"), StringComparison.Ordinal);
        Assert.DoesNotContain("Passport", body, StringComparison.Ordinal);
        Assert.DoesNotContain(id.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(id.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.VolumeRoot("primary"), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_entries_carry_the_same_instance_wide_maintenance_fields()
    {
        await using var factory = FileStorageApiFactory.Create([PermissionClaims.FileStorageRead]);
        using var client = factory.CreateClient();
        var completed = new DateTime(2026, 10, 29, 17, 20, 0, DateTimeKind.Utc);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.FileStorageMaintenanceLeases.Add(new FileStorageMaintenanceLease { Id = 1, Version = Guid.NewGuid(), LastCompletedAtUtc = completed });
            await context.SaveChangesAsync();
        }

        var volumes = await client.GetFromJsonAsync<List<FileStorageVolumeStatus>>(VolumesPath);

        Assert.Equal(2, volumes!.Count);
        Assert.All(volumes, v => Assert.Equal(completed, v.MaintenanceLastCompletedAtUtc));
        Assert.Single(volumes.Select(v => v.MaintenanceStale).Distinct());
    }

    [Fact]
    public async Task Without_the_claim_it_is_a_403_and_unauthenticated_a_401()
    {
        await using var forbidden = FileStorageApiFactory.Create([PermissionClaims.SystemSettingsRead, PermissionClaims.FilesRead]);
        using (var client = forbidden.CreateClient())
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(VolumesPath)).StatusCode);
        }

        await using var anonymous = FileStorageApiFactory.Create(permissions: null);
        using var anonymousClient = anonymous.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousClient.GetAsync(VolumesPath)).StatusCode);
    }

    [Fact]
    public async Task With_no_volumes_configured_the_store_is_a_no_op_and_the_endpoint_returns_an_empty_list()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.FileStorageRead]);
        using var client = factory.CreateClient();

        var volumes = await client.GetFromJsonAsync<List<FileStorageVolumeStatus>>(VolumesPath);

        Assert.Empty(volumes!);
        Assert.False(factory.Services.GetRequiredService<FileStorageVolumeRegistry>().IsActive);
    }

    [Fact]
    public async Task A_range_request_through_a_consumer_endpoint_returns_206_with_exactly_those_bytes()
    {
        await using var factory = FileStorageApiFactory.Create([PermissionClaims.FileStorageRead]);
        using var client = factory.CreateClient();
        var payload = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7)).ToArray();
        var id = await PutAsync(client, "range.jpg", payload);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/test-files/{id}");
        request.Headers.Range = new RangeHeaderValue(99_000, 99_999);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(payload[99_000..100_000], await response.Content.ReadAsByteArrayAsync());
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

        var full = await client.GetAsync($"/api/test-files/{id}");
        Assert.Equal(payload, await full.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_degraded_volume_is_a_503_with_retry_after_and_shows_as_degraded()
    {
        await using var factory = FileStorageApiFactory.Create([PermissionClaims.FileStorageRead]);
        using var client = factory.CreateClient();
        await PutAsync(client, "first.jpg", 10);
        File.Delete(Path.Combine(factory.VolumeRoot("primary"), StorageNames.MarkerFileName));

        var response = await client.PostAsync($"/api/test-files/{Guid.NewGuid()}?name=x.jpg", new ByteArrayContent(new byte[10]));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("60", response.Headers.GetValues("Retry-After").Single());
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("file-storage-unavailable", problem.GetProperty("code").GetString());
        Assert.DoesNotContain(factory.VolumeRoot("primary"), problem.ToString(), StringComparison.Ordinal);

        var volumes = await client.GetFromJsonAsync<List<FileStorageVolumeStatus>>(VolumesPath);
        Assert.Equal(FileStorageVolumeState.Degraded, volumes!.Single(v => v.Name == "primary").State);
        Assert.Equal(FileStorageVolumeState.Available, volumes!.Single(v => v.Name == "archive").State);
    }

    [Fact]
    public async Task A_full_volume_is_a_507_and_an_oversized_body_a_413()
    {
        await using var factory = FileStorageApiFactory.Create([PermissionClaims.FileStorageRead]);
        using var client = factory.CreateClient();

        factory.Space.Override = (1024, 1L << 40);
        var full = await client.PostAsync($"/api/test-files/{Guid.NewGuid()}?name=x.jpg", new ByteArrayContent(new byte[10]));
        Assert.Equal(HttpStatusCode.InsufficientStorage, full.StatusCode);
        Assert.Equal("file-storage-full", (await full.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        factory.Space.Override = (1L << 40, 1L << 41);
        var large = await client.PostAsync($"/api/test-files/{Guid.NewGuid()}?name=x.jpg", new ByteArrayContent(new byte[10 * 1024 * 1024 + 1]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Equal("file-storage-too-large", (await large.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_reservation_made_through_the_factory_is_visible_from_the_requests_own_context()
    {
        await using var factory = FileStorageApiFactory.Create([PermissionClaims.FileStorageRead]);
        _ = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();

        var row = await store.PutAsync("TestFiles", Guid.NewGuid(), "x.jpg", null, "image/jpeg", new MemoryStream(new byte[10]), 100, default);

        // Not saved by the caller: what the scoped context sees was committed by the factory's own context.
        await using var other = factory.Services.CreateAsyncScope();
        var seen = await other.ServiceProvider.GetRequiredService<OdysseyContext>().StoredObjects.AsNoTracking().SingleAsync(o => o.Id == row.Id);
        Assert.Equal(StoredObjectState.Pending, seen.State);
    }

    [Fact]
    public async Task The_api_registers_no_hosted_service_for_the_store()
    {
        await using var factory = FileStorageApiFactory.Create([PermissionClaims.FileStorageRead]);
        _ = factory.CreateClient();

        var hosted = factory.Services.GetServices<IHostedService>().Select(s => s.GetType().FullName!).ToList();

        Assert.DoesNotContain(hosted, name => name.Contains("FileStorage", StringComparison.Ordinal) || name.Contains("PeriodicJob", StringComparison.Ordinal));
    }

    /// <summary>The source half of A3-5: no future edit to Program.cs may schedule the store's jobs in the API.</summary>
    [Fact]
    public void The_api_source_names_no_file_storage_job()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "Odyssey.Api", "Program.cs"));

        Assert.DoesNotContain("FileStorageMaintenanceJob", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddPeriodicJob", program, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddFileStorage();", program, StringComparison.Ordinal);
    }

    private static Task<Guid> PutAsync(HttpClient client, string name, int length) =>
        PutAsync(client, name, new byte[length]);

    private static async Task<Guid> PutAsync(HttpClient client, string name, byte[] payload)
    {
        var response = await client.PostAsync($"/api/test-files/{Guid.NewGuid()}?name={Uri.EscapeDataString(name)}", new ByteArrayContent(payload));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>();
    }
}

/// <summary>The deletion grace through the settings endpoints (issue #346 §3.9, §4.3; AC 12, 31).</summary>
public class FileStorageGraceSettingApiTests
{
    private const string Path = "/api/system-settings";

    private static readonly string[] AdminClaims =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate, PermissionClaims.SystemSettingsSecurityUpdate];

    [Theory]
    [InlineData(0)]
    [InlineData(169)]
    public async Task Out_of_range_values_are_a_400(int hours)
    {
        await using var factory = new LoggingFactory(AdminClaims);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { FileStorageDeletionGraceHours = hours });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(168, true)]
    [InlineData(24, false)]
    [InlineData(72, false)]
    public async Task The_bounds_are_accepted_audited_and_advised_outside_24_to_72(int hours, bool advised)
    {
        await using var factory = new LoggingFactory(AdminClaims);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { FileStorageDeletionGraceHours = hours });
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(hours, dto!.FileStorageDeletionGraceHours);
        Assert.Equal(advised, dto.Warnings.ContainsKey(nameof(SystemSettingsUpdate.FileStorageDeletionGraceHours)));
        if (hours != 24)
        {
            Assert.Contains(factory.Logs.Entries, e => e.Message.Contains("security-claim change", StringComparison.Ordinal)
                && e.Message.Contains(nameof(SystemSettingsUpdate.FileStorageDeletionGraceHours), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Changing_it_needs_the_security_claim()
    {
        await using var factory = new LoggingFactory([PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { FileStorageDeletionGraceHours = 48 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    public async Task A_stored_value_the_sweeper_would_not_use_shows_as_a_projection_fault(string stored)
    {
        await using var factory = new LoggingFactory(AdminClaims);
        using var client = factory.CreateClient();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingsKeys.FileStorageDeletionGraceHours, Value = stored, UpdatedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.Contains(nameof(SystemSettingsUpdate.FileStorageDeletionGraceHours), dto!.ProjectionFaults.Keys);
        Assert.Contains(nameof(SystemSettingsUpdate.FileStorageDeletionGraceHours), dto.Warnings.Keys);
    }

    private sealed class LoggingFactory : OdysseyApiFactory
    {
        public LoggingFactory(IReadOnlyCollection<string> permissions)
            : this(permissions, new CapturingLoggerProvider())
        {
        }

        private LoggingFactory(IReadOnlyCollection<string> permissions, CapturingLoggerProvider logs)
            : base(permissions, configureServices: services => services.AddSingleton<ILoggerProvider>(logs)) =>
            Logs = logs;

        public CapturingLoggerProvider Logs { get; }
    }
}
