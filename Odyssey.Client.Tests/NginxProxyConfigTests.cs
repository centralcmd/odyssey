using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Two directives in the <c>/api/</c> proxy block that only the Compose browser tier would otherwise catch
/// regressing. Each was found by stopping or probing the live stack, and neither leaves a trace in a unit
/// test: removing one only changes what a running nginx does.
/// </summary>
public class NginxProxyConfigTests
{
    private static readonly Lazy<string> Config = new(() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "nginx.conf")));

    /// <summary>
    /// The server-level <c>add_header</c> already sends <c>X-Content-Type-Options</c> on every response,
    /// and the API sets it again on its download surfaces; without the hide the browser receives
    /// <c>nosniff, nosniff</c> (ContactAvatarRenderTests under Compose). The server-level header must stay:
    /// it is the copy that covers every response, error pages included.
    /// </summary>
    [Fact]
    public void The_api_proxy_drops_the_upstream_nosniff_and_keeps_the_server_level_one()
    {
        var api = ApiLocation();

        Assert.Matches(@"(?m)^\s*proxy_hide_header\s+X-Content-Type-Options\s*;", api);
        // An add_header inside the location would stop it inheriting the server-level security headers.
        Assert.DoesNotMatch(@"(?m)^\s*add_header\b", api);
        Assert.Matches(@"(?m)^\s*add_header\s+X-Content-Type-Options\s+""nosniff""\s+always\s*;", ServerLevel());
    }

    /// <summary>
    /// nginx's 60 s default connect wait is what left a blank page for over a minute while the API was
    /// down, before the session probe could fail over to the retry panel. Bounded, and kept short.
    /// </summary>
    [Fact]
    public void The_api_proxy_bounds_the_connect_to_a_few_seconds()
    {
        var match = Regex.Match(ApiLocation(), @"(?m)^\s*proxy_connect_timeout\s+(\d+)s\s*;");

        Assert.True(match.Success, "location /api/ must set proxy_connect_timeout.");
        Assert.InRange(int.Parse(match.Groups[1].Value), 1, 10);
    }

    private static string ApiLocation()
    {
        var text = Uncommented();
        var start = text.IndexOf("location /api/", StringComparison.Ordinal);
        Assert.True(start >= 0, "nginx.conf has no location /api/ block.");
        return Block(text, text.IndexOf('{', start));
    }

    // The server block with every nested location removed, so only server-level directives remain.
    private static string ServerLevel()
    {
        var text = Uncommented();
        var server = Block(text, text.IndexOf('{', text.IndexOf("server", StringComparison.Ordinal)));
        return Regex.Replace(server, @"location[^{]*\{[^{}]*\}", string.Empty);
    }

    private static string Uncommented() => Regex.Replace(Config.Value, @"(?m)#.*$", string.Empty);

    private static string Block(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
                return text[(open + 1)..i];
        }

        throw new InvalidOperationException("Unbalanced braces in nginx.conf.");
    }
}
