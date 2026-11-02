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

        // A shipped same-origin style whose remote resources the CSP would not admit (the off-switch with
        // the shipped dark default left in place) fails here rather than as a silently blank map.
        void CheckShippedStyle(string property, string? value)
        {
            // An unparseable origin list is already reported; naming the shipped origin then could echo the
            // configured value back, which this validator never does.
            if (originFailure is not null || value is null
                || !MapOptions.ShippedStyleRemoteOrigins.TryGetValue(value, out var needed))
                return;

            var missing = needed.Where(n => !origins.Any(o => MapOriginRule.SameOrigin(o, n))).ToList();
            if (missing.Count > 0)
            {
                failures.Add($"{MapOptions.SectionName}:{property} names a style the client ships that loads its tiles, "
                    + $"glyphs and sprite from {string.Join(" ", missing.Select(m => m.GetLeftPart(UriPartial.Authority)))}; "
                    + $"list that origin in {MapOptions.SectionName}:{nameof(MapOptions.AllowedOrigins)} or point the style elsewhere.");
            }
        }

        Check(nameof(MapOptions.StyleUrlLight), options.StyleUrlLight, v => MapOriginRule.CheckStyleUrl(v, origins));
        Check(nameof(MapOptions.StyleUrlDark), options.StyleUrlDark, v => MapOriginRule.CheckStyleUrl(v, origins));
        CheckShippedStyle(nameof(MapOptions.StyleUrlLight), options.StyleUrlLight);
        CheckShippedStyle(nameof(MapOptions.StyleUrlDark), options.StyleUrlDark);
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
