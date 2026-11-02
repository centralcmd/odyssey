namespace Odyssey.Client.Components;

/// <summary>Where an <see cref="OdsAsyncSearch"/> stands — what its one polite status line says.</summary>
public enum OdsAsyncSearchStatus
{
    /// <summary>Nothing typed into an open field yet, or the field is closed.</summary>
    Idle,

    /// <summary>Fewer than the minimum characters: <i>Type at least 2 characters</i>.</summary>
    Short,

    /// <summary>A request is pending (debouncing or in flight): <i>Searching…</i>.</summary>
    Searching,

    /// <summary>The latest request answered: <i>N places found</i> / <i>No places match</i>.</summary>
    Done,

    /// <summary>The latest request failed: <i>Couldn't load places</i>.</summary>
    Failed,
}

/// <summary>
/// The server-side search behind <see cref="OdsCombobox"/>'s async mode (issue #383 §4, design system ·
/// Combobox <c>searchAsync</c>): a minimum length, a debounce, and <b>latest-wins</b> — every new query
/// cancels the one before it, in its pause or in flight, so a slow answer to an old query never
/// overwrites a newer one and at most one request leaves per pause.
/// </summary>
/// <remarks>
/// Kept apart from the component so the timing rules are tested without MudBlazor's popover, which bUnit
/// cannot open (the trade-off <c>ContactPickerCreateRowTests</c> records).
/// </remarks>
public sealed class OdsAsyncSearch(Func<string, CancellationToken, Task<IReadOnlyList<OdsOption>>> search) : IDisposable
{
    private CancellationTokenSource? current;

    /// <summary>Characters before a request is sent. Default 2.</summary>
    public int MinChars { get; init; } = 2;

    /// <summary>The pause after the last keystroke before a request is sent. Default 250 ms.</summary>
    public int DebounceMs { get; init; } = 250;

    public OdsAsyncSearchStatus Status { get; private set; }

    /// <summary>The rows of the latest answered request.</summary>
    public IReadOnlyList<OdsOption> Results { get; private set; } = [];

    /// <summary>The query the current <see cref="Results"/> answer, trimmed.</summary>
    public string Query { get; private set; } = string.Empty;

    /// <summary>Raised whenever <see cref="Status"/> changes, so the host re-renders its live region.</summary>
    public event Action? Changed;

    /// <summary>
    /// Runs one query. Returns the rows, or <see langword="null"/> when a newer query (or
    /// <paramref name="token"/>) superseded this one.
    /// </summary>
    public async Task<IReadOnlyList<OdsOption>?> RunAsync(string? text, CancellationToken token = default)
    {
        Cancel();
        var query = (text ?? string.Empty).Trim();
        Query = query;
        if (query.Length < MinChars)
        {
            Results = [];
            Set(OdsAsyncSearchStatus.Short);
            return [];
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        current = cts;
        Set(OdsAsyncSearchStatus.Searching);
        try
        {
            if (DebounceMs > 0)
                await Task.Delay(DebounceMs, cts.Token);
            var rows = await search(query, cts.Token);
            if (!ReferenceEquals(current, cts) || cts.IsCancellationRequested)
                return null;
            Results = rows;
            Set(OdsAsyncSearchStatus.Done);
            return rows;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception) when (ReferenceEquals(current, cts))
        {
            Results = [];
            Set(OdsAsyncSearchStatus.Failed);
            return [];
        }
    }

    /// <summary>Back to <see cref="OdsAsyncSearchStatus.Idle"/>, cancelling anything pending.</summary>
    public void Reset()
    {
        Cancel();
        Results = [];
        Query = string.Empty;
        Set(OdsAsyncSearchStatus.Idle);
    }

    private void Cancel()
    {
        var previous = current;
        current = null;
        if (previous is null)
            return;
        previous.Cancel();
        previous.Dispose();
    }

    private void Set(OdsAsyncSearchStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose() => Cancel();
}
