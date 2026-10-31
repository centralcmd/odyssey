using System.Net;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>The file-storage typed client (issue #346 §5): the route, and the string-serialised volume state.</summary>
public class FileStorageApiClientTests
{
    private sealed class RecordingHandler(string json) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task GetVolumes_reads_the_volume_list_with_its_states_as_strings()
    {
        var handler = new RecordingHandler("""
            [{"name":"primary","state":"Degraded","boundFeatures":["PhotoThumbnails"],
              "features":[{"featureKey":"PhotoThumbnails","objectCount":3,"committedBytes":900}],
              "pendingDeleteCount":1,"integrityFaultCount":0,"freeBytes":10,"totalBytes":20,
              "quarantinedCount":0,"lastPassOrphanCount":null,"reconciliationHalted":false,"orphanedFeatures":[],
              "maintenanceLastCompletedAtUtc":"2026-10-29T17:20:00Z","maintenanceStale":true}]
            """);
        var client = new FileStorageApiClient(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));

        var result = await client.GetVolumesAsync();

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/api/file-storage/volumes", handler.LastRequest.RequestUri!.AbsolutePath);
        var volume = Assert.Single(result.Value!);
        Assert.Equal(FileStorageVolumeState.Degraded, volume.State);
        Assert.Equal(900, Assert.Single(volume.Features).CommittedBytes);
        Assert.True(volume.MaintenanceStale);
    }
}
