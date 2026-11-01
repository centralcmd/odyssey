using Microsoft.Extensions.Options;

namespace Odyssey.Api.Map;

/// <summary>
/// The derived rules on <see cref="MapOptions"/> (issue #370 §8). A failure stops the API at start-up,
/// naming the key and the rule — never the value — so a misconfigured basemap is never a runtime
/// <c>500</c> nor a style the CSP silently blocks.
/// </summary>
public sealed class MapOptionsValidator : IValidateOptions<MapOptions>
{
    public ValidateOptionsResult Validate(string? name, MapOptions options)
    {
        var failures = new List<string>();

        void Check(string property, string? value, Func<string, string?> rule)
        {
            // Null and empty are the data annotations' to report ([Required]), so they are not repeated here.
            if (string.IsNullOrEmpty(value))
                return;

            var failure = Generic(value) ?? rule(value);
            if (failure is not null)
                failures.Add($"{MapOptions.SectionName}:{property} {failure}.");
        }

        var origins = (IReadOnlyList<Uri>)[];
        var allowedOrigins = options.AllowedOrigins ?? string.Empty;
        var originFailure = Generic(allowedOrigins) ?? MapOriginRule.TryParseAllowedOrigins(allowedOrigins, out origins);
        if (originFailure is not null)
            failures.Add($"{MapOptions.SectionName}:{nameof(MapOptions.AllowedOrigins)} {originFailure}.");

        Check(nameof(MapOptions.StyleUrlLight), options.StyleUrlLight, v => MapOriginRule.CheckStyleUrl(v, origins));
        Check(nameof(MapOptions.StyleUrlDark), options.StyleUrlDark, v => MapOriginRule.CheckStyleUrl(v, origins));
        Check(nameof(MapOptions.AttributionUrl), options.AttributionUrl, MapOriginRule.CheckAttributionUrl);

        // Attribution is the one [Required] value whose blank form ("   ") the attribute lets through.
        if (options.Attribution is not null
            && (Generic(options.Attribution) ?? MapOriginRule.CheckAttribution(options.Attribution)) is { } attributionFailure)
        {
            failures.Add($"{MapOptions.SectionName}:{nameof(MapOptions.Attribution)} {attributionFailure}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static string? Generic(string value)
    {
        if (MapOriginRule.HasControlCharacter(value))
            return "must not contain control characters (tab, carriage return, newline included)";
        if (MapOriginRule.HasPlaceholder(value))
            return "must not contain an unrendered '{{' or '}}' placeholder";

        return null;
    }
}
