using Microsoft.Extensions.Configuration;

namespace Odyssey.Api;

public static class CorsConfiguration
{
    public const string AllowedOriginsSection = "Cors:AllowedOrigins";

    /// <summary>
    /// Codespaces publishes each forwarded port on its own host under this domain, so the dev client's
    /// origin is not knowable ahead of time — the suffix is the only stable part.
    /// </summary>
    private const string CodespacesOriginSuffix = ".app.github.dev";

    public static string[] GetAllowedOrigins(IConfiguration configuration)
    {
        var origins = configuration.GetSection(AllowedOriginsSection).Get<string[]>();

        if (origins is null || origins.Length == 0 || origins.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"Configuration section '{AllowedOriginsSection}' must contain at least one non-empty origin.");
        }

        return origins;
    }

    /// <summary>
    /// Whether an origin may be reflected by the Development CORS policy, which pairs reflection with
    /// <c>AllowCredentials()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Development cannot use a fixed allow-list: <c>dotnet run</c>, Aspire (dynamic ports) and Compose
    /// all serve the client on different ports, and Codespaces on a generated host. But reflecting
    /// <em>every</em> origin, with credentials, means any page the developer visits can drive their
    /// local API as them. That is largely neutralised by <c>SameSite=Strict</c> on the auth cookie and by
    /// antiforgery — this closes the rest of it, and stops a scanner flagging the pattern the moment the
    /// repository is public.
    /// </para>
    /// <para>
    /// Host-only, deliberately: the port is what varies between the dev hosts, so matching on it would
    /// defeat the purpose. Anything that is not a loopback name or a Codespaces host is rejected, which
    /// includes the LAN address a phone would use — a device on the network testing against a dev
    /// machine needs the origin added to <c>Cors:AllowedOrigins</c> and a non-Development environment,
    /// same as any other deployment.
    /// </para>
    /// </remarks>
    public static bool IsDevelopmentOriginAllowed(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        // An origin is scheme://host[:port] and nothing else. Browsers never send more, so anything
        // carrying a path, query, fragment or userinfo is a hand-built string, not a real origin.
        if (uri.PathAndQuery != "/" || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            return false;
        }

        // Uri lowercases the host; DnsSafeHost is the form without the brackets an IPv6 literal carries.
        var host = uri.DnsSafeHost;

        return host is "localhost" or "127.0.0.1" or "::1"
            || host.EndsWith(CodespacesOriginSuffix, StringComparison.Ordinal);
    }
}
