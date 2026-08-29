using System.Net;

namespace Odyssey.ApiClient.Resources;

/// <summary>How a download attempt resolved — drives the user-facing message on the Settings page.</summary>
public enum DataExportOutcome
{
    Success,
    Forbidden,
    Failed,

    /// <summary>
    /// The response arrived without the transport complaining, but the document is missing its
    /// terminal completeness sentinel — the export failed part-way through and the body is a partial
    /// database. Distinct from <see cref="Failed"/> because only this case is worth retrying
    /// immediately, and because the user waited for a download that will not appear.
    /// </summary>
    Incomplete,
}

/// <summary>A download attempt: the file on success, otherwise the reason it failed.</summary>
public sealed record DataExportResult(DataExportOutcome Outcome, ApiFile? File);

/// <summary>
/// Typed client for the admin database-export endpoint (issue #160). <see cref="DownloadAsync"/>
/// fetches the JSON attachment, verifies the export's completeness sentinel and maps the HTTP status
/// to a <see cref="DataExportOutcome"/> so the page can message forbidden / truncated / generic
/// failures distinctly. Availability is gated purely by the caller's <c>data.export</c> permission.
/// </summary>
public interface IDataExportApiClient
{
    /// <summary>Downloads the database JSON export, or reports why it could not be produced.</summary>
    Task<DataExportResult> DownloadAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IDataExportApiClient" />
public sealed class DataExportApiClient(IOdysseyApi api) : IDataExportApiClient
{
    private const string BasePath = "api/admin/data-export";

    public async Task<DataExportResult> DownloadAsync(CancellationToken ct = default)
    {
        var result = await api.GetFileAsync(BasePath, "odyssey-database-export.json", ct: ct);
        if (result.IsSuccess && result.Value is { } file)
        {
            // The export is streamed (issue #395), so a failure past the first byte cannot come back
            // as a ProblemDetails — a 200 with a truncated body is a possible outcome. A mid-stream
            // abort usually surfaces as a transport exception, but that is an accident of the
            // plumbing rather than a guarantee; the sentinel is the actual contract, so check it.
            // No file is handed back when it is missing: a partial export sitting in the user's
            // downloads folder, indistinguishable from a whole one, is precisely what the sentinel
            // exists to prevent (issue #401).
            return HasCompletenessSentinel(file.Bytes)
                ? new DataExportResult(DataExportOutcome.Success, file)
                : new DataExportResult(DataExportOutcome.Incomplete, null);
        }

        var outcome = result.Status switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => DataExportOutcome.Forbidden,
            _ => DataExportOutcome.Failed,
        };
        return new DataExportResult(outcome, null);
    }

    /// <summary>
    /// True when <paramref name="payload"/> ends with the export document's terminal
    /// <c>"complete": true</c> property, which the server writes only after every table has gone out
    /// in full.
    ///
    /// This reads the tail rather than parsing: the payload is the whole finance database, already
    /// materialised as a <c>byte[]</c>, and building a <c>JsonDocument</c> from it on the WASM client
    /// to read one boolean would roughly double peak memory — undoing part of what the streaming
    /// change bought. The assumption that pays for it is structural: <c>complete</c> is the document's
    /// last property, written immediately before the closing brace. <c>DataExportDocument</c>'s doc
    /// comment tells editors to keep it last, and <c>DataExportStreamingTests</c> pins both this
    /// method's verdict on a real export and its refusal of a truncated one.
    ///
    /// Whitespace is tolerated between the tokens (the writer indents its output), but nothing else
    /// is: a document ending in <c>"complete": false</c>, or truncated anywhere before the sentinel,
    /// is partial.
    /// </summary>
    public static bool HasCompletenessSentinel(ReadOnlySpan<byte> payload)
    {
        // Matched back to front, so only the last few dozen bytes are ever touched.
        var end = payload.Length;
        return TrimToken(payload, "}"u8, ref end)
               && TrimToken(payload, "true"u8, ref end)
               && TrimToken(payload, ":"u8, ref end)
               && TrimToken(payload, "\"complete\""u8, ref end);
    }

    /// <summary>Strips whitespace then <paramref name="token"/> off the end of the first
    /// <paramref name="end"/> bytes, moving <paramref name="end"/> back past both. False (and
    /// <paramref name="end"/> left at the whitespace boundary) if the token is not there.</summary>
    private static bool TrimToken(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> token, ref int end)
    {
        while (end > 0 && IsJsonWhitespace(payload[end - 1]))
            end--;

        if (end < token.Length || !payload[(end - token.Length)..end].SequenceEqual(token))
            return false;

        end -= token.Length;
        return true;
    }

    private static bool IsJsonWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
