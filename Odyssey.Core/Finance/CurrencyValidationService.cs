using Odyssey.Core;
using Odyssey.Context;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Odyssey.Core.Finance;

public static partial class CurrencyValidationService
{
    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex IsoCodeRegex();

    public static string Normalize(string currencyCode)
    {
        return currencyCode.Trim().ToUpperInvariant();
    }

    public static bool IsIsoFormat(string currencyCode)
    {
        return IsoCodeRegex().IsMatch(currencyCode);
    }

    public static async Task EnsureSupportedAndActive(OdysseyContext context, string currencyCode, string fieldName, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(currencyCode);
        if (!IsIsoFormat(normalized))
        {
            throw new DomainValidationException($"{fieldName} must be a 3-letter ISO-4217 code.");
        }

        var isSupported = await context.Currencies.AnyAsync(c => c.CurrencyCode == normalized && c.Archived == null, cancellationToken);
        if (!isSupported)
        {
            throw new DomainValidationException($"{fieldName} '{normalized}' is not supported.");
        }
    }
}
