using System.Net;
using Odyssey.Client.Pages.JobRuns;
using Odyssey.Client.Services;
using Xunit;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Tests;

/// <summary>
/// The upload-failure sentences (issue #449 §7), the retry wording and the file move's card states (§3.2/§4.2).
/// Counts are kept below 1,000 so the assertions do not depend on the runner's culture.
/// </summary>
public sealed class UploadFailureTextTests
{
    [Theory]
    [InlineData(HttpStatusCode.InsufficientStorage, UploadStop.StorageFull)]
    [InlineData(HttpStatusCode.ServiceUnavailable, UploadStop.StorageUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, UploadStop.TooManyUploads)]
    [InlineData((HttpStatusCode)0, UploadStop.ConnectionFailed)]
    [InlineData(HttpStatusCode.BadRequest, UploadStop.None)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, UploadStop.None)]
    [InlineData(HttpStatusCode.Conflict, UploadStop.None)]
    [InlineData(HttpStatusCode.InternalServerError, UploadStop.None)]
    public void Only_a_store_wide_cause_stops_the_batch(HttpStatusCode status, UploadStop expected) =>
        Assert.Equal(expected, UploadFailureText.StopOf(status));

    [Fact]
    public void A_stopped_batch_names_how_many_files_were_not_uploaded_and_the_way_out()
    {
        Assert.Equal("File storage is full, so 1 file wasn’t uploaded. Ask an administrator.",
            UploadFailureText.Stopped(UploadStop.StorageFull, 1, null));
        Assert.Equal("File storage isn’t available right now, so 3 files weren’t uploaded. Try again in a moment.",
            UploadFailureText.Stopped(UploadStop.StorageUnavailable, 3, null));
        Assert.Equal("You’ve uploaded a lot of files in a short time, so 2 files weren’t uploaded. Try again in 4 minutes.",
            UploadFailureText.Stopped(UploadStop.TooManyUploads, 2, TimeSpan.FromSeconds(181)));
        Assert.Equal("Couldn’t reach the server, so 1 file wasn’t uploaded. Check your connection and try again.",
            UploadFailureText.Stopped(UploadStop.ConnectionFailed, 1, null));
        Assert.Equal("File storage is full, so 2 files weren’t uploaded. Ask an administrator. Pick the 2 remaining files again.",
            UploadFailureText.Stopped(UploadStop.StorageFull, 2, null, pickAgain: true));
    }

    [Fact]
    public void A_per_file_failure_never_shows_a_status_code()
    {
        Assert.Equal("“a.pdf” is larger than the 25 MB upload limit.",
            UploadFailureText.PerFile(HttpStatusCode.RequestEntityTooLarge, "ignored", "a.pdf", 25));
        Assert.Equal("“a.pdf” wasn’t accepted: Unsupported type.",
            UploadFailureText.PerFile(HttpStatusCode.BadRequest, "Unsupported type.", "a.pdf", 25));
        Assert.Equal("Couldn’t upload “a.pdf”.", UploadFailureText.PerFile(HttpStatusCode.BadRequest, " ", "a.pdf", 25));
        Assert.Equal("Couldn’t upload “a.pdf”.", UploadFailureText.PerFile(HttpStatusCode.InternalServerError, "trace", "a.pdf", 25));
    }

    [Fact]
    public void Several_failures_are_one_summary_itemising_at_most_three()
    {
        Assert.Equal("only", UploadFailureText.Several(["only"], itemised: true));
        Assert.Equal("5 files couldn’t be uploaded.", UploadFailureText.Several(["a", "b", "c", "d", "e"], itemised: false));
        Assert.Equal("5 files couldn’t be uploaded. a b c And 2 more.", UploadFailureText.Several(["a", "b", "c", "d", "e"], itemised: true));
        Assert.Equal("2 files can’t be added. a b", UploadFailureText.Several(["a", "b"], itemised: true, "can’t be added"));
    }

    [Fact]
    public void An_echoed_name_loses_its_control_and_bidi_characters_and_is_cut_to_120()
    {
        var hostile = "<b>x</b>\r\n‮fdp.exe" + new string('a', 300);
        var clean = UploadFailureText.Clean(hostile);

        Assert.DoesNotContain('\r', clean);
        Assert.DoesNotContain('\n', clean);
        Assert.DoesNotContain('‮', clean);
        Assert.StartsWith("<b>x</b>fdp.exe", clean, StringComparison.Ordinal);
        Assert.Equal(UploadFailureText.MaxEchoLength, clean.Length);
        Assert.EndsWith("…", clean, StringComparison.Ordinal);
        Assert.Equal(string.Empty, UploadFailureText.Clean(null));
        Assert.Equal("ab.pdf", UploadFailureText.Clean("a\u200Eb\u200F\u061C\u200B\u2067.pdf"));
    }

    [Theory]
    [InlineData(null, "shortly")]
    [InlineData(0, "shortly")]
    [InlineData(-5, "shortly")]
    [InlineData(1, "in 1 minute")]
    [InlineData(60, "in 1 minute")]
    [InlineData(61, "in 2 minutes")]
    [InlineData(3600, "in 60 minutes")]
    [InlineData(3601, "in over an hour")]
    [InlineData(31536000, "in over an hour")]
    [InlineData(31536001, "shortly")]
    [InlineData(int.MaxValue, "shortly")]
    public void Retry_after_rounds_up_to_whole_minutes(int? seconds, string expected) =>
        Assert.Equal(expected, UploadRetryWording.When(seconds is { } s ? TimeSpan.FromSeconds(s) : null));

    private static FileBackfillStatus Status(int toMove = 0, int waiting = 0, int failed = 0) => new()
    {
        ToMove = toMove, WaitingOnPhotos = waiting, Failed = failed, Remaining = toMove + waiting + failed,
        OnDisk = new FileBackfillOnDisk { Finance = 1, Journal = 2, ContactAvatars = 3 },
    };

    [Fact]
    public void The_file_move_card_reads_its_states_in_precedence_order()
    {
        Assert.Equal("Every file is on disk.", BackgroundJobPresentation.FileMoveView(Status()).Lead);
        Assert.Equal("4 files still to move from the database to disk.",
            BackgroundJobPresentation.FileMoveView(Status(toMove: 4, waiting: 2, failed: 1)).Lead);
        Assert.Equal("2 files are waiting for the photo move to finish.",
            BackgroundJobPresentation.FileMoveView(Status(waiting: 2, failed: 1)).Lead);
        Assert.Equal("1 file is waiting for the photo move to finish.",
            BackgroundJobPresentation.FileMoveView(Status(waiting: 1)).Lead);
        Assert.StartsWith("Only parked files are left.", BackgroundJobPresentation.FileMoveView(Status(failed: 1)).Lead, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_move_card_takes_its_counts_as_sent_and_names_every_area()
    {
        var view = BackgroundJobPresentation.FileMoveView(Status(toMove: 4, waiting: 2, failed: 1) with
        {
            FailedByCode = new() { ["type"] = 1, ["hash"] = 0 },
        });

        Assert.Equal([("To move", 4), ("Parked", 1), ("Waiting on photos", 2)], view.Tiles);
        Assert.Equal("On disk: 1 finance file · 2 journal files · 3 contact avatars", view.DetailLine);
        Assert.Equal(["Unsupported type: 1"], view.ParkedLines);
        Assert.Equal("Parked files by reason", view.ParkedListCaption);
    }
}
