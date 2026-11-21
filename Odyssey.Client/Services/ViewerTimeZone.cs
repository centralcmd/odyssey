using Odyssey.ApiClient;
using Odyssey.Client.Pages.Journal;

namespace Odyssey.Client.Services;

/// <summary>
/// The viewer's time zone for the calendar (issue #520 §2 "Times shown in the browser zone"): the zone the grid, the
/// agenda, the popover and the dialog's "your time" convert zoned events into, and the zone the page's windowed reads
/// name as <c>viewTimeZoneId</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BrowserZoneId"/> is the browser's own IANA zone, or <c>null</c> when it reports none in that shape. In that
/// case the view is <c>UTC</c> for the window AND for display (v5 N3), never <see cref="TimeZoneInfo.Local"/>, so the grid
/// shows exactly what the window asked for; a new event defaults to <c>UTC</c>, and the import sends no default zone.
/// </para>
/// <para>
/// The browser's tzdata can be newer than the server's, so a zone this browser resolves may still be refused by the
/// server (a <c>400</c> keyed on the zone field). The first such refusal switches this viewer to <c>UTC</c> for the rest of
/// the session (<see cref="UseUtc"/>): every later window, export and import then reads in UTC, rather than failing every
/// load (PR #528 architect review).
/// </para>
/// <para>
/// Not registered in DI: a component asks the service provider for one and falls back to <see cref="Browser"/>. Tests
/// register a fixed zone, which is why this is a class and not an interface the test harness would auto-mock.
/// </para>
/// </remarks>
public sealed class ViewerTimeZone
{
    private readonly TimeZoneInfo? browserZone;

    public ViewerTimeZone(string? browserZoneId)
    {
        browserZone = LocationVisuals.FindZone(browserZoneId);
        BrowserZoneId = browserZone is null ? null : browserZoneId;
    }

    /// <summary>The browser's zone, read once; shared by the session, so a fallback to UTC is remembered.</summary>
    public static ViewerTimeZone Browser { get; } = new(LocationVisuals.BrowserZone());

    /// <summary>The browser's IANA zone, or null when it reports none this browser can resolve.</summary>
    public string? BrowserZoneId { get; }

    /// <summary>The server refused the browser's zone once this session; the view is UTC from then on.</summary>
    public bool FellBackToUtc { get; private set; }

    /// <summary>The zone the view uses: the browser's, else <c>UTC</c>.</summary>
    public string ViewZoneId => FellBackToUtc ? "UTC" : BrowserZoneId ?? "UTC";

    /// <summary>The resolved <see cref="ViewZoneId"/>.</summary>
    public TimeZoneInfo Zone => FellBackToUtc ? TimeZoneInfo.Utc : browserZone ?? TimeZoneInfo.Utc;

    /// <summary>
    /// The import's <c>defaultTimeZoneId</c>: the browser's zone, <c>UTC</c> once the server has refused it, or null —
    /// the field omitted — when the browser reports none.
    /// </summary>
    public string? ImportZoneId => FellBackToUtc ? "UTC" : BrowserZoneId;

    /// <summary>Reads in UTC for the rest of the session: the server's tzdata does not know the browser's zone.</summary>
    public void UseUtc() => FellBackToUtc = true;

    /// <summary>
    /// Whether a failed call is the server refusing the zone it was sent: a <c>400</c> keyed on <paramref name="field"/>
    /// (matched whatever its case, so the bound property name and the query or form name both count). Only then does the
    /// caller fall back and retry; any other <c>400</c> is the call's own failure.
    /// </summary>
    public static bool RefusedZone(System.Net.HttpStatusCode status, ApiProblem? problem, string field) =>
        status == System.Net.HttpStatusCode.BadRequest
        && problem?.Errors.Keys.Any(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase)
                                         || k.EndsWith("." + field, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Runs a zoned call; when the server refuses the zone it sent (<paramref name="field"/>), switches to UTC and runs it
    /// once more. The call reads <see cref="ViewZoneId"/> or <see cref="ImportZoneId"/> itself, so the retry sends UTC.
    /// The retry depends on what THIS call sent, not on who switched: an overlapping call that switched to UTC while this
    /// one was in flight must not leave this one failing with a zone error (PR #528 frontend review).
    /// </summary>
    public async Task<ApiResult<T>> WithFallbackAsync<T>(string field, Func<Task<ApiResult<T>>> call)
    {
        var sentUtc = FellBackToUtc;
        var result = await call();
        if (sentUtc || result.IsSuccess || !RefusedZone(result.Status, result.Problem, field))
        {
            return result;
        }

        UseUtc();
        return await call();
    }

    /// <summary>The viewer's zone from DI when a host registered one, else the browser's.</summary>
    public static ViewerTimeZone From(IServiceProvider? services) =>
        services?.GetService(typeof(ViewerTimeZone)) as ViewerTimeZone ?? Browser;
}
