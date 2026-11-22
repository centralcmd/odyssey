namespace Odyssey.Api.ApiDocs;

/// <summary>
/// The two deploy-time switches for the API documentation routes (issue #569 §7.1): whether they exist, and
/// whether they need a signed-in user. Both are bound once, at startup, when the routes are mapped.
/// </summary>
public static class ApiDocsConfiguration
{
    public const string EnabledKey = "ApiDocs:Enabled";

    public const string RequireLoginKey = "ApiDocs:RequireLogin";

    /// <summary>
    /// The key <see cref="EnabledKey"/> replaced. It is never read for its value — a deployment still setting it
    /// gets docs OFF, fail-closed — only detected, so its presence can be reported.
    /// </summary>
    public const string RetiredEnabledKey = "Swagger:Enabled";

    /// <summary>
    /// Always in Development; elsewhere only when <see cref="EnabledKey"/> is true; NEVER in Production, whatever
    /// the flag (issue #451 §1.3). The environment half is the one an env file cannot forget.
    /// </summary>
    public static bool IsEnabled(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment()
        || (configuration.GetValue<bool>(EnabledKey) && !environment.IsProduction());

    /// <summary>
    /// Reads <see cref="RequireLoginKey"/> as a raw string, never through <c>GetValue&lt;bool&gt;</c>, which turns an
    /// empty value into <see langword="false"/> — i.e. anonymous docs. Absent, empty or whitespace is
    /// <see langword="true"/>; anything <see cref="bool.TryParse(string?, out bool)"/> rejects stops the host, naming
    /// the key and never echoing the value. Called in every environment, so a malformed value is reported where it
    /// was written rather than when docs are later switched on.
    /// </summary>
    public static bool ReadRequireLogin(IConfiguration configuration)
    {
        var raw = configuration[RequireLoginKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (bool.TryParse(raw.Trim(), out var requireLogin))
        {
            return requireLogin;
        }

        throw new InvalidOperationException(
            $"Configuration value {RequireLoginKey} must be 'true' or 'false'. Leave it unset to require a login.");
    }

    /// <summary>Logs once when the retired <see cref="RetiredEnabledKey"/> is still configured.</summary>
    public static void WarnOnRetiredKey(IConfiguration configuration, ILogger logger)
    {
        if (configuration[RetiredEnabledKey] is not null)
        {
            logger.LogWarning(
                "{RetiredKey} is no longer read; set {Key} instead. API documentation stays governed by {Key} alone.",
                RetiredEnabledKey, EnabledKey, EnabledKey);
        }
    }
}
