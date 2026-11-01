using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Journal;

/// <summary>A photo thumbnail's size (issue #362): the long side, fitted, never upscaled.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThumbnailSize>))]
public enum ThumbnailSize : byte
{
    /// <summary>200 px: pickers, dialogs and the journal gallery.</summary>
    Small = 1,

    /// <summary>400 px: the library grid, the album grid and album covers.</summary>
    Medium = 2,
}

/// <summary>
/// The thumbnail worker's state as the API reports it (issue #362 §3.6): the most severe state among workers
/// heard from in the last 3 minutes, or <see cref="NotRunning"/> when none is. Ordered by severity, ascending.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThumbnailWorkerState>))]
public enum ThumbnailWorkerState : byte
{
    Running = 0,

    /// <summary>A decode hung; claiming waits for its thread to end.</summary>
    Paused = 1,

    /// <summary>The breaker: 3 crashes or hangs within an hour.</summary>
    Tripped = 2,

    /// <summary>The decoder's security self-test failed; nothing is decoded.</summary>
    PolicyFailed = 3,

    /// <summary>The thumbnail volume is unavailable.</summary>
    StorageUnavailable = 4,

    /// <summary>No worker has reported in the last 3 minutes. Never stored.</summary>
    NotRunning = 5,
}
