namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// Where an import writes its temporary files, and the sweep of ones a crashed process left behind. One resolver for
/// every import that spools (the task and journal attachment spool, the calendar grouper's runs and upload copy), so
/// all of them land on the disk ASP.NET Core buffers the upload on.
/// </summary>
/// <remarks>
/// <c>ASPNETCORE_TEMP</c> when it is set and exists — the image points it at the <c>import_spool</c> volume, because the
/// OS temp directory can be a RAM-backed tmpfs and the API's root filesystem is read-only — else the OS temp directory.
/// </remarks>
internal static class ImportSpool
{
    /// <summary>A leftover older than this is a crashed import's, never a live one's.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    /// <summary>The spool directory, resolved per call so a test can point the environment elsewhere.</summary>
    public static string Directory() =>
        Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") is { Length: > 0 } configured && System.IO.Directory.Exists(configured)
            ? configured
            : Path.GetTempPath();

    /// <summary>
    /// Removes files matching <paramref name="pattern"/> older than <see cref="StaleAfter"/>. Best effort: a file another
    /// import still holds, or one already gone, is simply skipped.
    /// </summary>
    public static void SweepStale(string directory, string pattern)
    {
        try
        {
            var cutoff = DateTime.UtcNow - StaleAfter;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles(pattern))
            {
                try
                {
                    if (file.LastWriteTimeUtc < cutoff)
                    {
                        file.Delete();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
