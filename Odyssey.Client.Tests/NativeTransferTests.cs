using System.Net;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The browser-native transfer the task export and import use for files too large for the WASM heap. Its result must
/// read exactly like the typed client's, so the dialog handles both paths alike — and the service worker must let its
/// download navigation reach the API.
/// </summary>
public class NativeTransferTests
{
    [Fact]
    public async Task A2xxBody_DeserializesIntoTheValue()
    {
        var result = await NativeTransfer.ToResultAsync<TaskIcsImportResult>(
            new NativeTransfer.NativeResponse(200, """{"importedCount":2,"updatedCount":1,"storedFileCount":3,"reusedFileCount":4}"""));

        Assert.True(result.IsSuccess);
        Assert.Equal((2, 1, 3, 4), (result.Value!.ImportedCount, result.Value.UpdatedCount, result.Value.StoredFileCount, result.Value.ReusedFileCount));
    }

    [Fact]
    public async Task AProblemBody_BecomesTheProblem_WithItsStatus()
    {
        var result = await NativeTransfer.ToResultAsync<TaskIcsImportResult>(
            new NativeTransfer.NativeResponse(403, """{"status":403,"title":"Forbidden","detail":"needs permission to read and upload files."}"""));

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Contains("read and upload files", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonJsonErrorBody_StillFails_WithItsStatus()
    {
        // A proxy's own 413 page is HTML, not problem+json.
        var result = await NativeTransfer.ToResultAsync<TaskIcsImportResult>(new NativeTransfer.NativeResponse(413, "<html>413</html>"));

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, result.Status);
    }

    [Fact]
    public async Task ANetworkFailure_IsAFailure_NotASuccess()
    {
        var result = await NativeTransfer.ToResultAsync<TaskIcsImportResult>(new NativeTransfer.NativeResponse(0, "Failed to fetch"));

        Assert.False(result.IsSuccess);
        Assert.Contains("Failed to fetch", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServiceWorker_NeverAnswersAnApiNavigationWithTheAppShell()
    {
        var worker = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "service-worker.published.js"));

        Assert.Contains("!new URL(event.request.url).pathname.startsWith('/api/')", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJsInterop_IsCalledOnlyThroughNativeTransfer()
    {
        var offenders = Directory.EnumerateFiles(ClientSource.Root, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(ClientSource.Root, "*.razor", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.EndsWith("NativeTransfer.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("odysseyNativeTransfer", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }
}
