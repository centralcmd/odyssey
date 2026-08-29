using System.Net;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Covers <see cref="ApiInteropExtensions"/> and <see cref="PagedLoad{T}"/> — the seam every one of
/// the client's list and write surfaces goes through to turn an <see cref="ApiResult{T}"/> into
/// rendered state plus, on failure, a toast.
/// </summary>
/// <remarks>
/// The distinction being pinned is Empty versus Error. A failed fetch that falls back to an empty
/// list renders the onboarding empty state — "No contracts yet — Add a contract…" — after a 500,
/// telling the user their data is gone. That regression shipped on three Photos surfaces during the
/// typed-client migration, which is why the toast and the state are produced by one helper instead
/// of by each page. <see cref="ListPageContractTests"/> lints that the pages hold a failure flag;
/// these tests pin that the helpers actually set one and say something.
/// </remarks>
public class ApiInteropTests
{
    private sealed record Row(string Name);

    private static ApiResult<T> Failed<T>(string detail = "boom") =>
        ApiResult<T>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = detail });

    private static ApiResult FailedWrite(string detail = "boom") =>
        ApiResult.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = detail });

    private static PagedResult<Row> Page(params Row[] rows) =>
        new() { Items = rows, Offset = 0, Limit = 25, TotalCount = rows.Length };

    // ── OrToast ──────────────────────────────────────────────────────────────

    [Fact]
    public void OrToast_returns_the_value_and_says_nothing_on_success()
    {
        var snackbar = new RecordingSnackbar();

        var value = ApiResult<Row>.Success(new Row("a"), HttpStatusCode.OK).OrToast(snackbar, "Unable to save");

        Assert.Equal(new Row("a"), value);
        Assert.Empty(snackbar.Toasts);
    }

    [Fact]
    public void OrToast_returns_null_and_toasts_the_lead_with_the_detail_on_failure()
    {
        var snackbar = new RecordingSnackbar();

        var value = Failed<Row>("Name is required").OrToast(snackbar, "Unable to save account");

        Assert.Null(value);
        Assert.Equal(("Unable to save account: Name is required", Severity.Error), Assert.Single(snackbar.Toasts));
    }

    // ── ValueOrToast / ItemsOrToast ──────────────────────────────────────────

    [Fact]
    public void ValueOrToast_uses_the_fallback_and_the_app_wide_load_wording_on_failure()
    {
        var snackbar = new RecordingSnackbar();

        var value = Failed<int>("timeout").ValueOrToast(snackbar, "the balance", -1);

        Assert.Equal(-1, value);
        Assert.Equal(("Unable to load the balance: timeout", Severity.Error), Assert.Single(snackbar.Toasts));
    }

    /// <summary>A 204 (or a body that deserialized to null) is a success, not a failure — it must
    /// take the fallback without toasting.</summary>
    [Fact]
    public void ValueOrToast_falls_back_on_a_null_body_without_toasting()
    {
        var snackbar = new RecordingSnackbar();

        var value = ApiResult<string>.Success(null, HttpStatusCode.NoContent).ValueOrToast(snackbar, "the note", "—");

        Assert.Equal("—", value);
        Assert.Empty(snackbar.Toasts);
    }

    [Fact]
    public void ItemsOrToast_returns_an_empty_list_and_toasts_on_failure()
    {
        var snackbar = new RecordingSnackbar();

        var items = Failed<List<Row>>().ItemsOrToast(snackbar, "accounts");

        Assert.Empty(items);
        Assert.Equal("Unable to load accounts: boom", Assert.Single(snackbar.Toasts).Message);
    }

    // ── PagedOrToast: the Empty-vs-Error pairing ─────────────────────────────

    /// <summary>Zero rows from a healthy endpoint is the Empty state: successful, and silent.</summary>
    [Fact]
    public void PagedOrToast_reports_success_for_a_page_with_no_rows()
    {
        var snackbar = new RecordingSnackbar();

        var load = ApiResult<PagedResult<Row>>.Success(Page(), HttpStatusCode.OK).PagedOrToast(snackbar, "contracts");

        Assert.True(load.IsSuccess);
        Assert.Empty(load.Items);
        Assert.Empty(snackbar.Toasts);
    }

    /// <summary>The regression this helper exists for: a failure must be distinguishable from
    /// "you have none yet", and must say so.</summary>
    [Fact]
    public void PagedOrToast_reports_failure_and_toasts_rather_than_looking_empty()
    {
        var snackbar = new RecordingSnackbar();

        var load = Failed<PagedResult<Row>>().PagedOrToast(snackbar, "contracts");

        Assert.False(load.IsSuccess);
        Assert.Empty(load.Items);
        Assert.Equal("Unable to load contracts: boom", Assert.Single(snackbar.Toasts).Message);
    }

    [Fact]
    public void PagedOrToast_carries_the_window_and_total_through_on_success()
    {
        var snackbar = new RecordingSnackbar();
        var page = new PagedResult<Row> { Items = [new("a")], Offset = 50, Limit = 25, TotalCount = 300 };

        var load = ApiResult<PagedResult<Row>>.Success(page, HttpStatusCode.OK).PagedOrToast(snackbar, "contracts");

        Assert.Equal(50, load.Offset);
        Assert.Equal(25, load.Limit);
        Assert.Equal(300, load.TotalCount);
    }

    [Fact]
    public void PagedItemsOrToast_returns_the_rows_and_still_toasts_on_failure()
    {
        var snackbar = new RecordingSnackbar();

        Assert.Equal(
            [new Row("a")],
            ApiResult<PagedResult<Row>>.Success(Page(new Row("a")), HttpStatusCode.OK)
                .PagedItemsOrToast(snackbar, "photos"));
        Assert.Empty(snackbar.Toasts);

        Assert.Empty(Failed<PagedResult<Row>>().PagedItemsOrToast(snackbar, "photos"));
        Assert.Single(snackbar.Toasts);
    }

    // ── Toast (writes) ───────────────────────────────────────────────────────

    [Fact]
    public void Toast_reports_success_and_shows_the_success_message_when_one_is_given()
    {
        var snackbar = new RecordingSnackbar();

        Assert.True(ApiResult.Success(HttpStatusCode.NoContent).Toast(snackbar, "Unable to save", "Saved"));
        Assert.Equal(("Saved", Severity.Success), Assert.Single(snackbar.Toasts));
    }

    /// <summary>Most writes are confirmed by the row updating on screen, so a success with no message
    /// must stay silent rather than toasting an empty string.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Toast_stays_silent_on_success_without_a_message(string? successMessage)
    {
        var snackbar = new RecordingSnackbar();

        Assert.True(ApiResult.Success(HttpStatusCode.NoContent).Toast(snackbar, "Unable to save", successMessage));
        Assert.Empty(snackbar.Toasts);
    }

    [Fact]
    public void Toast_reports_failure_and_never_shows_the_success_message()
    {
        var snackbar = new RecordingSnackbar();

        Assert.False(FailedWrite("Name is required").Toast(snackbar, "Unable to save account", "Saved"));
        Assert.Equal(("Unable to save account: Name is required", Severity.Error), Assert.Single(snackbar.Toasts));
    }

    /// <summary>
    /// A request that never reached the server has no problem body. The toast must still carry a
    /// human-readable reason rather than a bare lead with a dangling colon.
    /// </summary>
    [Fact]
    public void A_transport_failure_still_produces_a_readable_message()
    {
        var snackbar = new RecordingSnackbar();

        Assert.False(ApiResult.Failure(new HttpRequestException("Connection refused"))
            .Toast(snackbar, "Unable to save account"));
        Assert.Equal("Unable to save account: Connection refused", Assert.Single(snackbar.Toasts).Message);
    }

    // ── PagedLoad ────────────────────────────────────────────────────────────

    [Fact]
    public void A_failed_PagedLoad_reads_as_an_empty_window_without_claiming_success()
    {
        var load = PagedLoad<Row>.Failure();

        Assert.False(load.IsSuccess);
        Assert.Empty(load.Items);
        Assert.Equal(0, load.TotalCount);
        Assert.Equal(0, load.Offset);
        Assert.Equal(0, load.Limit);
    }

    /// <summary>
    /// <c>From</c> keys off the value, not the status: a 200 whose body deserialized to null is not a
    /// usable page, and treating it as one would render an empty list as a successful load.
    /// </summary>
    [Fact]
    public void From_treats_a_success_with_no_body_as_a_failure()
    {
        Assert.False(PagedLoad<Row>.From(ApiResult<PagedResult<Row>>.Success(null, HttpStatusCode.OK)).IsSuccess);
        Assert.False(PagedLoad<Row>.From(Failed<PagedResult<Row>>()).IsSuccess);
        Assert.True(PagedLoad<Row>.From(ApiResult<PagedResult<Row>>.Success(Page(), HttpStatusCode.OK)).IsSuccess);
    }
}
