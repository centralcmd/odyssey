using System.Net;
using System.Text;

namespace Odyssey.Client.Services;

/// <summary>Why a batch of uploads stopped, from the server's answer (issue #449 §3.2).</summary>
public enum UploadStop
{
    /// <summary>The answer applies to this file only; the batch goes on.</summary>
    None,

    /// <summary><c>507</c>: the file store is below its reserve.</summary>
    StorageFull,

    /// <summary><c>503</c>: the store is degraded or busy.</summary>
    StorageUnavailable,

    /// <summary><c>429</c>: the per-user upload limit.</summary>
    TooManyUploads,

    /// <summary>No answer at all (status 0): the connection failed, so every later file would fail the same way.</summary>
    ConnectionFailed,
}

/// <summary>
/// Every upload-failure sentence, for every surface that writes to the file store — the attach dialogs, the file
/// sections, the photo upload and the contact image (issue #449 §7) — so the texts cannot drift. Plain text only: a file
/// name or a server detail has control and bidi-override characters removed and is cut to 120 characters, and no status
/// code, volume, area or free-space figure ever appears.
/// <para>
/// Batch rule: a <c>507</c>, <c>503</c> or <c>429</c> stops the batch, since the cause applies to every later file too;
/// any other failure is the file's own and the batch goes on.
/// </para>
/// </summary>
public static class UploadFailureText
{
    /// <summary>The longest file name or server detail a message carries.</summary>
    public const int MaxEchoLength = 120;

    public static UploadStop StopOf(HttpStatusCode status) => status switch
    {
        HttpStatusCode.InsufficientStorage => UploadStop.StorageFull,
        HttpStatusCode.ServiceUnavailable => UploadStop.StorageUnavailable,
        HttpStatusCode.TooManyRequests => UploadStop.TooManyUploads,
        0 => UploadStop.ConnectionFailed,
        _ => UploadStop.None,
    };

    /// <summary>
    /// The summary of a stopped batch: <paramref name="notUploaded"/> is the failed file plus every untried one.
    /// <paramref name="pickAgain"/> is for a surface with no list to keep them in (the file sections).
    /// </summary>
    public static string Stopped(UploadStop stop, int notUploaded, TimeSpan? retryAfter, bool pickAgain = false)
    {
        var files = $"{Plural(notUploaded, "file", "files")} {(notUploaded == 1 ? "wasn’t" : "weren’t")} uploaded";
        var text = stop switch
        {
            UploadStop.StorageFull => $"File storage is full, so {files}. Ask an administrator.",
            UploadStop.StorageUnavailable => $"File storage isn’t available right now, so {files}. Try again in a moment.",
            UploadStop.TooManyUploads => $"You’ve uploaded a lot of files in a short time, so {files}. Try again {UploadRetryWording.When(retryAfter)}.",
            UploadStop.ConnectionFailed => $"Couldn’t reach the server, so {files}. Check your connection and try again.",
            _ => throw new ArgumentOutOfRangeException(nameof(stop)),
        };
        return pickAgain ? $"{text} Pick the {Plural(notUploaded, "remaining file", "remaining files")} again." : text;
    }

    /// <summary>One file's own failure: too large, not accepted, or anything else.</summary>
    public static string PerFile(HttpStatusCode status, string? detail, string fileName, int capMegabytes) => status switch
    {
        HttpStatusCode.RequestEntityTooLarge => $"“{Clean(fileName)}” is larger than the {capMegabytes} MB upload limit.",
        HttpStatusCode.BadRequest when !string.IsNullOrWhiteSpace(detail) => $"“{Clean(fileName)}” wasn’t accepted: {Clean(detail)}",
        _ => $"Couldn’t upload “{Clean(fileName)}”.",
    };

    /// <summary>
    /// The one summary of a batch whose files failed on their own: one file's line as is; several as a count, followed —
    /// where <paramref name="itemised"/> — by at most three of the lines, then "and {k} more".
    /// </summary>
    public static string Several(IReadOnlyList<string> perFile, bool itemised, string countLead = "couldn’t be uploaded")
    {
        if (perFile.Count == 1)
        {
            return perFile[0];
        }

        var text = new StringBuilder($"{perFile.Count} files {countLead}.");
        if (itemised)
        {
            foreach (var line in perFile.Take(3))
            {
                text.Append(' ').Append(line);
            }

            if (perFile.Count > 3)
            {
                text.Append($" And {perFile.Count - 3} more.");
            }
        }

        return text.ToString();
    }

    /// <summary>Removes control and format (Cf) characters and cuts to <see cref="MaxEchoLength"/>.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var kept = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            // Controls, and every format character (Cf): the bidi embeddings, overrides and isolates, LRM/RLM/ALM and the
            // zero-width ones — none of them belongs in an echoed name.
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
            {
                continue;
            }

            kept.Append(c);
        }

        var text = kept.ToString().Trim();
        return text.Length > MaxEchoLength ? text[..(MaxEchoLength - 1)].TrimEnd() + "…" : text;
    }

    private static string Plural(int n, string one, string many) =>
        $"{n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-GB"))} {(n == 1 ? one : many)}";
}

/// <summary>
/// <c>Retry-After</c> in words for an upload (issue #449 §7) — its own rule, not the file-analysis wording: 1 s to 60 min
/// rounds <b>up</b> to whole minutes; more than an hour says so rather than understating; zero, negative, missing (the client
/// reads both header forms into one <see cref="TimeSpan"/>) or more than a year — a garbage or overflowed value, not a real
/// wait — is "shortly" (Odyssey Design System · AttachDocumentsModal <c>odsUploadRetryWhen</c>).
/// </summary>
public static class UploadRetryWording
{
    private static readonly TimeSpan MaxPlausibleWait = TimeSpan.FromDays(365);

    public static string When(TimeSpan? wait)
    {
        if (wait is not { } span || span <= TimeSpan.Zero || span > MaxPlausibleWait)
        {
            return "shortly";
        }

        if (span > TimeSpan.FromHours(1))
        {
            return "in over an hour";
        }

        var minutes = (int)Math.Ceiling(span.TotalMinutes);
        return minutes == 1 ? "in 1 minute" : $"in {minutes} minutes";
    }
}
