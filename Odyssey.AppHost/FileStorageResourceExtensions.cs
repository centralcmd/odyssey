namespace Odyssey.AppHost;

/// <summary>
/// The file-storage environment (issue #346 A3.2), applied from ONE dictionary to both the API and the worker, so
/// the two cannot be given different volumes. <c>Odyssey.Worker.Tests</c> checks both projects call this.
/// </summary>
internal static class FileStorageResourceExtensions
{
    public static IResourceBuilder<ProjectResource> WithFileStorage(
        this IResourceBuilder<ProjectResource> resource, IReadOnlyDictionary<string, string> settings)
    {
        foreach (var (name, value) in settings)
        {
            resource.WithEnvironment(name, value);
        }

        return resource;
    }
}
