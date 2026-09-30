namespace Odyssey.Client.Services;

/// <summary>
/// Latest-request-wins guard for a list page's fetch (issue #249). Search (debounced), filters,
/// sort, page and page size all call one fetch, and a slower earlier response that lands last used
/// to overwrite the newer one: rows for a search the user had already replaced, a total and pager
/// that disagreed with them, and the refetch bar cleared while the newest request was still running.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="RunAsync{T}"/> supersedes every earlier one: it cancels the previous request's
/// token and, when its own response arrives, reports whether it is still the newest. The sequence
/// number is the correctness mechanism; cancellation only saves the wasted round-trip. That split
/// matters because the transport (<c>OdysseyApi</c>) turns a cancelled request into a failed
/// <c>ApiResult</c> rather than throwing — so a caller must check <see cref="Latest{T}.IsSuperseded"/>
/// <b>before</b> unwrapping with <c>PagedOrToast</c> / <c>ItemsOrToast</c>, or every superseded
/// request would toast a failure the user never caused.
/// </para>
/// <para>
/// A superseded response must touch nothing: not the rows, the total, the pager, the live
/// announcement, the error flag or the loading/refetching flags — those belong to the newest request,
/// which clears them itself. The owning component disposes the loader so a response arriving after
/// the component is gone is dropped the same way.
/// </para>
/// </remarks>
public sealed class ListLoader : IDisposable
{
    private CancellationTokenSource? current;
    private int sequence;
    private bool disposed;

    /// <summary>
    /// Starts a fetch that supersedes any in flight. <paramref name="fetch"/> receives a token that is
    /// cancelled when a newer fetch starts or the loader is disposed; pass it through to the API call.
    /// </summary>
    public async Task<Latest<T>> RunAsync<T>(Func<CancellationToken, Task<T>> fetch)
    {
        if (disposed)
            return Latest<T>.Superseded;

        current?.Cancel();
        var mine = new CancellationTokenSource();
        current = mine;
        var ticket = ++sequence;

        try
        {
            var value = await fetch(mine.Token);
            return IsCurrent(ticket) ? Latest<T>.Current(value) : Latest<T>.Superseded;
        }
        catch (OperationCanceledException) when (!IsCurrent(ticket))
        {
            return Latest<T>.Superseded;
        }
        finally
        {
            // Each request disposes its own source once it has finished with the token, so a newer
            // request never disposes a token an older one may still be registering against.
            if (ReferenceEquals(current, mine))
                current = null;
            mine.Dispose();
        }
    }

    private bool IsCurrent(int ticket) => !disposed && ticket == sequence;

    /// <summary>Cancels the request in flight and drops every response still to arrive.</summary>
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        current?.Cancel();
        current = null;
    }
}

/// <summary>
/// The outcome of a <see cref="ListLoader.RunAsync{T}"/>: either the newest request's value, or a
/// marker that a newer request (or disposal) superseded it and the caller must return untouched.
/// </summary>
public readonly record struct Latest<T>
{
    private Latest(bool isSuperseded, T value)
    {
        IsSuperseded = isSuperseded;
        Value = value;
    }

    /// <summary>True when a newer request started, or the owner was disposed, before this one finished.</summary>
    public bool IsSuperseded { get; }

    /// <summary>The fetched value; <c>default</c> when <see cref="IsSuperseded"/>.</summary>
    public T Value { get; }

    public static Latest<T> Superseded => new(true, default!);

    public static Latest<T> Current(T value) => new(false, value);
}
