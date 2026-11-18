namespace Odyssey.Client.Pages.Weather;

/// <summary>
/// The one cool-down every weather surface waits after a <c>429</c> (issue #363 §4): the dashboard card's and the
/// hours panel's Try again, the event dialog's Try again, and the journal and agenda lists' single retry.
/// <c>ApiResult</c> carries no <c>Retry-After</c>, so this is a pause for the person, not a copy of a server limit.
/// Settable so tests can shorten it.
/// </summary>
internal static class WeatherRetry
{
    internal static TimeSpan CoolDown = TimeSpan.FromSeconds(10);
}
