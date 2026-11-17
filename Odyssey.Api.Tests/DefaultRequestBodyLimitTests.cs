using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odyssey.Core.Finance;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The transport ceiling (<c>FileStorage:MaxFileSizeBytes</c>) is 10 GB so a task export can carry large attached
/// files. That must not make every endpoint accept a 10 GB body: <see cref="ImportSizeLimitMiddleware"/> holds each
/// endpoint without a file cap of its own to <see cref="FileStorageOptions.DefaultMaxRequestBodyBytes"/>. TestServer
/// exposes no <see cref="IHttpMaxRequestBodySizeFeature"/>, so this drives the middleware directly.
/// </summary>
public class DefaultRequestBodyLimitTests
{
    private static readonly FileStorageOptions Options = new();

    [Fact]
    public async Task AnEndpointWithoutAFileCap_IsHeldToTheDefault()
    {
        var feature = await InvokeAsync(new EndpointMetadataCollection());

        Assert.Equal(Options.DefaultMaxRequestBodyBytes, feature.MaxRequestBodySize);
    }

    [Fact]
    public async Task ARequestWithNoEndpoint_IsHeldToTheDefault()
    {
        var feature = await InvokeAsync(metadata: null);

        Assert.Equal(Options.DefaultMaxRequestBodyBytes, feature.MaxRequestBodySize);
    }

    [Fact]
    public async Task ThePhotoUpload_KeepsTheTransportCeiling()
    {
        var feature = await InvokeAsync(new EndpointMetadataCollection(new TransportCeilingBodyAttribute()));

        Assert.Equal(Options.MaxRequestBodyBytes, feature.MaxRequestBodySize);
    }

    [Fact]
    public async Task TheDefault_OnlyEverLowers()
    {
        var feature = await InvokeAsync(new EndpointMetadataCollection(), initial: 1024);

        Assert.Equal(1024, feature.MaxRequestBodySize);
    }

    [Fact]
    public void TheDefault_IsFarBelowTheCeiling()
    {
        Assert.True(Options.DefaultMaxRequestBodyBytes * 100 < Options.MaxRequestBodyBytes);
    }

    private static async Task<FakeBodySizeFeature> InvokeAsync(EndpointMetadataCollection? metadata, long? initial = null)
    {
        var context = new DefaultHttpContext();
        var feature = new FakeBodySizeFeature { MaxRequestBodySize = initial ?? Options.MaxRequestBodyBytes };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        if (metadata is not null)
        {
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));
        }

        var middleware = new ImportSizeLimitMiddleware(_ => Task.CompletedTask, NullLogger<ImportSizeLimitMiddleware>.Instance);
        await middleware.InvokeAsync(
            context, limitsLookup: null!, uploadLimitsLookup: null!,
            Microsoft.Extensions.Options.Options.Create(new FormOptions()), Microsoft.Extensions.Options.Options.Create(Options));
        return feature;
    }

    private sealed class FakeBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; }
    }
}
