using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Testing.FileStorage;

namespace Odyssey.Api.Tests.FileStorage;

/// <summary>
/// The store's own API host (issue #346 §11): temp volumes, the test features bound to them, the test reference
/// tables, and a test-only controller that serves a stored object the way a consumer must. Every other API test
/// host stays a no-op, because it configures no volume (A2.1).
/// </summary>
internal sealed class FileStorageApiFactory : OdysseyApiFactory
{
    private readonly DirectoryInfo volumes;

    private FileStorageApiFactory(IReadOnlyCollection<string>? permissions, DirectoryInfo volumes, CapturingLoggerProvider logs, TestSpaceProbe space)
        : base(
            permissions,
            configuration: new Dictionary<string, string?>
            {
                ["FileStorage:Volumes:primary:RootPath"] = Path.Combine(volumes.FullName, "primary"),
                ["FileStorage:Volumes:archive:RootPath"] = Path.Combine(volumes.FullName, "archive"),
                ["FileStorage:Bindings:TestFiles"] = "primary",
                ["FileStorage:Bindings:TestSecrets"] = "primary",
                ["FileStorage:Bindings:TestDated"] = "archive",
            },
            configureServices: services =>
            {
                services.AddScoped<OdysseyContext>(sp => new TestStorageContext(sp.GetRequiredService<Microsoft.EntityFrameworkCore.DbContextOptions<OdysseyContext>>()));
                foreach (var feature in TestStorageFeatures.All)
                {
                    services.AddSingleton(feature);
                }

                // The test volumes live under the temp directory, which C4 forbids in a deployment.
                services.Replace(ServiceDescriptor.Singleton(new FileStorageForbiddenRoots([])));
                services.Replace(ServiceDescriptor.Singleton<IFileStorageSpaceProbe>(space));
                services.AddSingleton<ILoggerProvider>(logs);
                services.AddControllers().AddApplicationPart(typeof(TestFilesController).Assembly);
            })
    {
        this.volumes = volumes;
        Logs = logs;
        Space = space;
    }

    public CapturingLoggerProvider Logs { get; }

    public TestSpaceProbe Space { get; }

    public string VolumeRoot(string name) => Path.Combine(volumes.FullName, name);

    public static FileStorageApiFactory Create(IReadOnlyCollection<string>? permissions)
    {
        var volumes = Directory.CreateTempSubdirectory("odyssey-api-fs-");
        Directory.CreateDirectory(Path.Combine(volumes.FullName, "primary"));
        Directory.CreateDirectory(Path.Combine(volumes.FullName, "archive"));
        return new FileStorageApiFactory(permissions, volumes, new CapturingLoggerProvider(), new TestSpaceProbe());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && volumes.Exists)
        {
            volumes.Delete(recursive: true);
        }
    }
}

/// <summary>
/// A consumer endpoint in miniature, test-only: what a real feature's controller does with the store. It serves
/// with range processing, a strong ETag from the hash, the database-held content type and <c>nosniff</c> (§7.7).
/// </summary>
[ApiController]
[Route("api/test-files")]
[Authorize]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class TestFilesController(IObjectStore store, OdysseyContext context) : ControllerBase
{
    [HttpPost("{owner:guid}")]
    public async Task<IActionResult> Put(Guid owner, [FromQuery] string name, CancellationToken cancellationToken)
    {
        var row = await store.PutAsync("TestFiles", owner, name, null, "image/jpeg", Request.Body, 10 * 1024 * 1024, cancellationToken);
        ((TestStorageContext)context).TestFileAttachments.Add(new TestFileAttachment { Id = Guid.NewGuid(), StoredObjectId = row.Id });
        await context.SaveChangesAsync(cancellationToken);
        return Ok(row.Id);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var content = await store.OpenReadAsync("TestFiles", id, cancellationToken);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(content.Content, "image/jpeg", lastModified: null, entityTag: new EntityTagHeaderValue(content.ETag), enableRangeProcessing: true);
    }
}
