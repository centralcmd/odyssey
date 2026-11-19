using Odyssey.ApiClient;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Tests;

/// <summary>
/// Adapts a plain write outcome to the typed one the event dialog reads since issue #520 (the stored event, with its
/// detached report): a success carries no body, a failure keeps its status and problem.
/// </summary>
internal static class CalendarWriteResults
{
    public static ApiResult<ExistingCalendarEvent> Event(ApiResult result) =>
        result.IsSuccess
            ? ApiResult<ExistingCalendarEvent>.Success(null, result.Status)
            : ApiResult<ExistingCalendarEvent>.Failure(result.Status, result.Problem!);
}
