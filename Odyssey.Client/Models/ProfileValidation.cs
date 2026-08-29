using System.Text.RegularExpressions;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Models;

/// <summary>
/// Client-side profile validation and name resolution (issue #316 §9), mirroring the design system's
/// <c>profile-fields.jsx</c> and the server's <c>ProfileService</c> rules so the two surfaces (the
/// Account → Profile card and the first-sign-in onboarding gate) behave identically. The server
/// re-validates authoritatively; this drives inline errors and the completeness gate.
/// </summary>
public static partial class ProfileValidation
{
    /// <summary>Per-field max lengths (mirror the entity's <c>[MaxLength]</c>).</summary>
    public static readonly IReadOnlyDictionary<string, int> MaxLengths = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [nameof(ProfileDto.FirstName)] = 128,
        [nameof(ProfileDto.MiddleName)] = 128,
        [nameof(ProfileDto.LastName)] = 128,
        [nameof(ProfileDto.Title)] = 128,
        [nameof(ProfileDto.DisplayName)] = 256,
    };

    /// <summary>Date-of-birth lower bound (spec §9).</summary>
    public static readonly DateOnly MinBirthDate = new(1900, 1, 1);

    private static readonly string[] NameFields =
    [
        nameof(ProfileDto.FirstName), nameof(ProfileDto.MiddleName), nameof(ProfileDto.LastName),
        nameof(ProfileDto.DisplayName), nameof(ProfileDto.Title),
    ];

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    // Control characters and line breaks (C0 range + DEL), rejected on every string field (§9).
    [GeneratedRegex("[\\u0000-\\u001F\\u007F]")]
    private static partial Regex ControlCharRegex();

    /// <summary>
    /// Validate a profile. Returns per-field error messages (keyed by <see cref="ProfileDto"/> property
    /// name) and whether all four required fields (First/Last name, birth date, sex) are present &amp; valid.
    /// </summary>
    public static (Dictionary<string, string> Errors, bool IsComplete) Validate(ProfileDto p)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in NameFields)
        {
            var raw = ValueOf(p, field);
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (EmailRegex().IsMatch(trimmed))
            {
                errors[field] = "This can’t be an email address.";
            }
            else if (raw is not null && ControlCharRegex().IsMatch(raw))
            {
                errors[field] = "Remove line breaks and control characters.";
            }
            else if (trimmed.Length > MaxLengths[field])
            {
                errors[field] = $"Keep this under {MaxLengths[field]} characters.";
            }
        }

        if (string.IsNullOrWhiteSpace(p.FirstName))
        {
            errors.TryAdd(nameof(ProfileDto.FirstName), "First name is required.");
        }

        if (string.IsNullOrWhiteSpace(p.LastName))
        {
            errors.TryAdd(nameof(ProfileDto.LastName), "Last name is required.");
        }

        if (p.BirthDate is not { } dob)
        {
            errors[nameof(ProfileDto.BirthDate)] = "Date of birth is required.";
        }
        else if (dob < MinBirthDate)
        {
            errors[nameof(ProfileDto.BirthDate)] = "Enter a date on or after 1 Jan 1900.";
        }
        else if (dob > DateOnly.FromDateTime(DateTime.Now))
        {
            errors[nameof(ProfileDto.BirthDate)] = "Date of birth can’t be in the future.";
        }

        if (p.Sex is null)
        {
            errors[nameof(ProfileDto.Sex)] = "Select an option.";
        }

        var isComplete = !string.IsNullOrWhiteSpace(p.FirstName)
            && !string.IsNullOrWhiteSpace(p.LastName)
            && p.BirthDate is not null
            && p.Sex is not null;

        return (errors, isComplete);
    }

    /// <summary>Owner-side resolved label: <c>DisplayName ?? FirstName</c> (spec §9). The claim-aware
    /// email / "Unknown user" tail is server-only — a user always sees their own name.</summary>
    public static string ResolveName(ProfileDto? p) =>
        (p?.DisplayName?.Trim() is { Length: > 0 } display) ? display
        : (p?.FirstName?.Trim() is { Length: > 0 } first) ? first
        : string.Empty;

    /// <summary>Initials for the avatar: from the display name if set, else first + last initial.</summary>
    public static string Initials(ProfileDto? p)
    {
        var display = p?.DisplayName?.Trim();
        if (!string.IsNullOrEmpty(display))
        {
            var parts = display.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var initials = (parts[0][..1] + (parts.Length > 1 ? parts[^1][..1] : string.Empty)).ToUpperInvariant();
            return initials.Length > 0 ? initials : "?";
        }

        var f = p?.FirstName?.Trim();
        var l = p?.LastName?.Trim();
        var combined = ((f is { Length: > 0 } ? f[..1] : string.Empty) + (l is { Length: > 0 } ? l[..1] : string.Empty)).ToUpperInvariant();
        return combined.Length > 0 ? combined : "?";
    }

    private static string? ValueOf(ProfileDto p, string field) => field switch
    {
        nameof(ProfileDto.FirstName) => p.FirstName,
        nameof(ProfileDto.MiddleName) => p.MiddleName,
        nameof(ProfileDto.LastName) => p.LastName,
        nameof(ProfileDto.DisplayName) => p.DisplayName,
        nameof(ProfileDto.Title) => p.Title,
        _ => null,
    };
}
