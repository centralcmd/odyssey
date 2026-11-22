using System.Text.RegularExpressions;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The large-file route patterns in both proxies (issue #516 AC 9): the calendar <c>.ics</c> exports, their summaries
/// and the import take the 10 GB body size and the hour-long timeouts, and nothing else on those resources does.
/// Caddy's <c>@ordinary</c> must exclude exactly what <c>@largefiles</c> includes, or a route lands in both blocks.
/// </summary>
public class LargeFileProxyRouteTests
{
    private static readonly string Nginx =
        File.ReadAllText(Path.Combine(ClientSource.Root, "nginx", "templates", "default.conf.template"));

    private static readonly string Caddy =
        File.ReadAllText(SourceTree.File("Caddyfile"));

    public static TheoryData<string> CalendarIcsRoutes()
    {
        var id = Guid.NewGuid();
        return new TheoryData<string>
        {
            $"/api/api/calendars/{id}/ics", $"/api/api/calendars/{id}/ics/summary",
            $"/api/api/calendar-events/{id}/ics", $"/api/api/calendar-events/{id}/ics/summary",
            "/api/api/calendar-events/ics", "/api/api/calendar-events/ics/summary",
            $"/api/api/recurrence-patterns/{id}/ics", $"/api/api/recurrence-patterns/{id}/ics/summary",
        };
    }

    public static TheoryData<string> OrdinaryRoutes()
    {
        var id = Guid.NewGuid();
        return new TheoryData<string>
        {
            $"/api/api/calendars/{id}", "/api/api/calendars", $"/api/api/calendar-events/{id}", "/api/api/calendar-events",
            $"/api/api/recurrence-patterns/{id}", $"/api/api/recurrence-patterns/{id}/events", "/api/api/auth/login",
        };
    }

    [Theory]
    [MemberData(nameof(CalendarIcsRoutes))]
    public void Calendar_ics_routes_are_large_file_routes_in_both_proxies(string path)
    {
        Assert.Matches(NginxIcsPattern(), path);
        Assert.Matches(CaddyLargeFiles(), path);
        // @ordinary is "not <pattern>", so a path its pattern matches is one it excludes.
        Assert.Matches(CaddyOrdinaryNegated(), path);
    }

    [Theory]
    [MemberData(nameof(OrdinaryRoutes))]
    public void Other_calendar_routes_stay_ordinary_in_both_proxies(string path)
    {
        Assert.DoesNotMatch(NginxIcsPattern(), path);
        Assert.DoesNotMatch(CaddyLargeFiles(), path);
        Assert.DoesNotMatch(CaddyOrdinaryNegated(), path);
    }

    [Fact]
    public void Caddy_ordinary_negates_exactly_the_large_file_pattern()
    {
        Assert.Equal(CaddyLargeFiles().ToString(), CaddyOrdinaryNegated().ToString());
    }

    [Fact]
    public void The_ics_location_streams_responses_so_the_progress_guard_sees_the_client()
    {
        var at = Nginx.IndexOf(NginxIcsPattern().ToString(), StringComparison.Ordinal);
        var block = Nginx[at..Nginx.IndexOf('}', at)];

        Assert.Matches(@"(?m)^\s*proxy_buffering\s+off\s*;", block);
        Assert.Matches(@"(?m)^\s*client_max_body_size\s+10241m\s*;", block);
        Assert.Matches(@"(?m)^\s*proxy_read_timeout\s+3600s\s*;", block);
    }

    [Fact]
    public void Podcast_audio_downloads_stream_unbuffered_so_the_last_played_stamp_sees_the_client()
    {
        var pattern = Regex.Matches(Nginx, @"(?m)^\s*location ~ (\S+) \{").Select(m => m.Groups[1].Value).Single(p => p.Contains("podcast-episodes"));
        Assert.Matches(pattern, $"/api/api/podcast-episodes/{Guid.NewGuid()}/audio/content");
        Assert.DoesNotMatch(pattern, $"/api/api/podcast-episodes/{Guid.NewGuid()}/audio");
        Assert.DoesNotMatch(pattern, "/api/api/podcast-episodes");
        var at = Nginx.IndexOf(pattern, StringComparison.Ordinal);
        var block = Nginx[at..Nginx.IndexOf('}', at)];
        Assert.Matches(@"(?m)^\s*proxy_buffering\s+off\s*;", block);
        Assert.Matches(@"(?m)^\s*proxy_send_timeout\s+3600s\s*;", block);
    }

    private static Regex NginxIcsPattern() =>
        new(Regex.Matches(Nginx, @"(?m)^\s*location ~ (\S+) \{").Select(m => m.Groups[1].Value).Single(p => p.Contains("calendars")));

    private static Regex CaddyLargeFiles() => new(Regex.Match(Caddy, @"@largefiles path_regexp largefiles (\S+)").Groups[1].Value);

    // The pattern @ordinary negates.
    private static Regex CaddyOrdinaryNegated() => new(Regex.Match(Caddy, @"@ordinary not path_regexp (\S+)").Groups[1].Value);
}
