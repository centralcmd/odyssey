namespace Odyssey.Testing;

/// <summary>
/// The demo files and photos are written by the worker (its <c>DemoFileSeeder</c>) once the API has initialised the file
/// volumes, so on a freshly started stack they appear shortly AFTER the stack reports healthy — never assume them. A test
/// that needs a seeded file, photo, contact image or album polls for it here, bounded.
/// </summary>
public static class DemoFileWait
{
    /// <summary>How long a test waits for the worker's demo-file pass: the volume wait plus the pass, with room to spare.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Polls <paramref name="ready"/> until it answers true or the timeout passes; returns its last answer.</summary>
    public static async Task<bool> UntilAsync(Func<Task<bool>> ready, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (true)
        {
            if (await ready())
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(PollInterval);
        }
    }
}
