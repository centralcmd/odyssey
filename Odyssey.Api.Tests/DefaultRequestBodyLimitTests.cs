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
    public async Task ThePhotoUpload_IsHeldToTheAdminUploadCap_NotTheCeiling()
    {
        // A body over the admin cap must be refused at the transport, before it is spooled: under the 10 GB ceiling a
        // photos.create holder could otherwise force gigabytes of disk writes per request just to receive a 413.
        var feature = await InvokeAsync(new EndpointMetadataCollection(new UploadTransportLimitAttribute()));

        Assert.Equal(AdminUploadCap + Options.RequestEnvelopeHeadroomBytes, feature.MaxRequestBodySize);
    }

    [Fact]
    public async Task ThePhotoUpload_KeepsTheMultipartLimitAtTheCeiling_SoTheServiceNamesTheCap()
    {
        var context = await InvokeContextAsync(new EndpointMetadataCollection(new UploadTransportLimitAttribute()));

        Assert.Null(context.Features.Get<IFormFeature>()); // the configured (ceiling) form options are left in place
    }

    /// <summary>
    /// Every action that takes a file names how its body is bounded — an import cap, the upload cap, or the upload
    /// cap at the transport only. One that names none would be held to the 65 MB default and fail every larger file.
    /// </summary>
    [Fact]
    public void EveryFileAcceptingAction_DeclaresItsBodyLimit()
    {
        var actions = typeof(Program).Assembly.GetTypes()
            .Where(type => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(method => method.GetParameters().Any(p => typeof(IFormFile).IsAssignableFrom(p.ParameterType)))
            .ToList();

        Assert.NotEmpty(actions);
        var undeclared = actions
            .Where(method => !method.IsDefined(typeof(ImportSizeLimitAttribute), false)
                && !method.IsDefined(typeof(UploadSizeLimitAttribute), false)
                && !method.IsDefined(typeof(UploadTransportLimitAttribute), false))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToList();
        Assert.Empty(undeclared);
    }

    [Fact]
    public void ThePhotoUpload_CarriesTheTransportOnlyLimit()
    {
        var upload = typeof(Odyssey.Api.Controllers.PhotosController).GetMethods()
            .Single(method => method.GetParameters().Any(p => typeof(IFormFile).IsAssignableFrom(p.ParameterType)));

        Assert.True(upload.IsDefined(typeof(UploadTransportLimitAttribute), false));
    }

    [Fact]
    public async Task TheDefault_OnlyEverLowers()
    {
        var feature = await InvokeAsync(new EndpointMetadataCollection(), initial: 1024);

        Assert.Equal(1024, feature.MaxRequestBodySize);
    }

    private const long AdminUploadCap = 64L * 1024 * 1024;

    private static async Task<FakeBodySizeFeature> InvokeAsync(EndpointMetadataCollection? metadata, long? initial = null) =>
        (FakeBodySizeFeature)(await InvokeContextAsync(metadata, initial)).Features.Get<IHttpMaxRequestBodySizeFeature>()!;

    private static async Task<HttpContext> InvokeContextAsync(EndpointMetadataCollection? metadata, long? initial = null)
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
            context, limitsLookup: null!, uploadLimitsLookup: new FixedUploadLimits(AdminUploadCap),
            Microsoft.Extensions.Options.Options.Create(new FormOptions()), Microsoft.Extensions.Options.Options.Create(Options));
        return context;
    }

    private sealed class FakeBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; }
    }
}
