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
/// Not registered in DI: a component asks the service provider for one and falls back to <see cref="Browser"/>. Tests
/// register a fixed zone, which is why this is a class and not an interface the test harness would auto-mock.
/// </para>
/// </remarks>
public sealed class ViewerTimeZone
{
    public ViewerTimeZone(string? browserZoneId)
    {
        var zone = LocationVisuals.FindZone(browserZoneId);
        BrowserZoneId = zone is null ? null : browserZoneId;
        Zone = zone ?? TimeZoneInfo.Utc;
        ViewZoneId = BrowserZoneId ?? "UTC";
    }

    /// <summary>The browser's zone, read once.</summary>
    public static ViewerTimeZone Browser { get; } = new(LocationVisuals.BrowserZone());

    /// <summary>The browser's IANA zone, or null when it reports none this browser can resolve.</summary>
    public string? BrowserZoneId { get; }

    /// <summary>The zone the view uses: the browser's, else <c>UTC</c>.</summary>
    public string ViewZoneId { get; }

    /// <summary>The resolved <see cref="ViewZoneId"/>.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>The viewer's zone from DI when a host registered one, else the browser's.</summary>
    public static ViewerTimeZone From(IServiceProvider? services) =>
        services?.GetService(typeof(ViewerTimeZone)) as ViewerTimeZone ?? Browser;
}
