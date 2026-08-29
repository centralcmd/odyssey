using Odyssey.Dtos;

namespace Odyssey.ApiClient;

/// <summary>
/// Fluent builder for the server-side list-query string (issue #277): <c>offset</c>/<c>limit</c> plus
/// <c>search</c>, per-resource filters, and <c>sortBy</c>/<c>sortDir</c>. All values are URL-encoded and
/// blank values are dropped. Flat-table pages call <see cref="Window"/> to request one page at a time
/// (the OdsPager contract); callers that omit it (reference-data / dropdown loads) get the whole
/// filtered/sorted set in a single large window (offset 0, limit <see cref="LimitAll"/>).
/// </summary>
public sealed class PagedQuery
{
    /// <summary>Limit large enough to return every matching record in one window (deferred pager UI).</summary>
    public const int LimitAll = ListDefaults.MaxLimit;

    /// <summary>
    /// Sentinel page size meaning "all matching rows". The single definition of the sentinel — the
    /// design system's <c>OdsPageSizes.All</c> defers to this so the pager UI and the query builder
    /// can never disagree.
    /// </summary>
    public const int SizeAll = -1;

    private readonly string path;
    private readonly List<string> parts = [];
    private int offset;
    private int limit = LimitAll;

    private PagedQuery(string path) => this.path = path;

    public static PagedQuery For(string path) => new(path);

    /// <summary>
    /// Set the request window from a 1-based page + page size (the OdsPager contract, issue #277
    /// follow-up). <see cref="SizeAll"/> requests the whole set (<see cref="LimitAll"/>).
    /// Without this the query defaults to the full window, as before.
    /// </summary>
    public PagedQuery Window(int page, int pageSize)
    {
        if (pageSize == SizeAll)
        {
            offset = 0;
            limit = LimitAll;
        }
        else
        {
            offset = Math.Max(0, (page - 1) * pageSize);
            limit = pageSize;
        }

        return this;
    }

    public PagedQuery Add(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{key}={Uri.EscapeDataString(value)}");
        }

        return this;
    }

    public PagedQuery AddMany(string key, IEnumerable<string>? values)
    {
        if (values is not null)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add($"{key}={Uri.EscapeDataString(value)}");
                }
            }
        }

        return this;
    }

    public PagedQuery Add(string key, DateTime? value) =>
        value is { } v ? Add(key, v.ToString("o")) : this;

    /// <summary>
    /// Append a single-value filter from a two-value multi-select (e.g. active/archived, income/expense):
    /// filters only when exactly one option is selected — none or both means "no filter".
    /// </summary>
    public PagedQuery AddSingle(string key, IReadOnlyCollection<string>? values) =>
        values is { Count: 1 } ? Add(key, values.First()) : this;

    public PagedQuery AddBool(string key, bool? value) =>
        value is { } v ? Add(key, v ? "true" : "false") : this;

    /// <summary>Append <c>sortBy</c>/<c>sortDir</c>. A blank <paramref name="key"/> is a no-op.</summary>
    public PagedQuery Sort(string? key, bool ascending)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            Add("sortBy", key);
            Add("sortDir", ascending ? "asc" : "desc");
        }

        return this;
    }

    /// <summary>
    /// Build the full URL. Requests the window set by <see cref="Window"/> (offset/limit), or the
    /// whole set (offset 0, <see cref="LimitAll"/>) when no window was set.
    /// </summary>
    public string Build()
    {
        var query = string.Join("&", parts);
        var head = $"offset={offset}&limit={limit}";
        return parts.Count == 0 ? $"{path}?{head}" : $"{path}?{head}&{query}";
    }
}
