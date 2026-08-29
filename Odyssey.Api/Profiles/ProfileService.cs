using Odyssey.Dtos.Application;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Api.Profiles;

/// <summary>
/// Reads and upserts the authenticated caller's own <see cref="UserProfile"/> (issue #316). Operates
/// strictly on the row keyed by the caller's user-id — there is no id parameter and no cross-user path.
/// Enforces the service-side validation rules from §9 (required-on-completion, email-format rejection,
/// control-char rejection, birth-date range); data-annotation length/enum limits are enforced earlier by
/// <c>[ApiController]</c> model validation.
/// </summary>
public sealed class ProfileService
{
    private static readonly DateOnly MinBirthDate = new(1900, 1, 1);

    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public ProfileService(OdysseyContext context, TimeProvider timeProvider)
    {
        this.context = context;
        this.timeProvider = timeProvider;
    }

    /// <summary>Read the caller's profile; an absent row returns an empty, incomplete DTO to prefill onboarding.</summary>
    public async Task<ProfileDto> GetAsync(string userId, CancellationToken cancellationToken)
    {
        var profile = await context.UserProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        var dto = profile is null ? new ProfileDto { IsComplete = false } : ToDto(profile);
        dto.MustChangePassword = await MustChangePasswordAsync(userId, cancellationToken);
        return dto;
    }

    /// <summary>
    /// Validate and upsert the caller's profile. All four required fields (First/Last name, birth date,
    /// sex) must be present and valid, so a persisted row is only ever complete (§6). Optional fields
    /// clear on blank. Throws <see cref="ProfileValidationException"/> (→ 400) on any rule violation.
    /// </summary>
    public async Task<ProfileDto> SaveAsync(string userId, ProfileDto request, CancellationToken cancellationToken)
    {
        var firstName = Normalize(request.FirstName, "First name", rejectEmail: true);
        var middleName = Normalize(request.MiddleName, "Middle name", rejectEmail: true);
        var lastName = Normalize(request.LastName, "Last name", rejectEmail: true);
        var displayName = Normalize(request.DisplayName, "Display name", rejectEmail: true);
        var title = Normalize(request.Title, "Title", rejectEmail: false);

        if (firstName is null)
        {
            throw new ProfileValidationException("First name is required.");
        }

        if (lastName is null)
        {
            throw new ProfileValidationException("Last name is required.");
        }

        if (request.BirthDate is not { } birthDate)
        {
            throw new ProfileValidationException("Date of birth is required.");
        }

        if (request.Sex is not { } sex)
        {
            throw new ProfileValidationException("Sex is required.");
        }

        if (!Enum.IsDefined(sex))
        {
            throw new ProfileValidationException("Sex is invalid.");
        }

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (birthDate < MinBirthDate || birthDate > today)
        {
            throw new ProfileValidationException(
                $"Date of birth must be on or after {MinBirthDate:yyyy-MM-dd} and not in the future.");
        }

        var profile = await context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (profile is null)
        {
            profile = new UserProfile { UserId = userId };
            context.UserProfiles.Add(profile);
        }

        profile.FirstName = firstName;
        profile.MiddleName = middleName;
        profile.LastName = lastName;
        profile.DisplayName = displayName;
        profile.Title = title;
        profile.BirthDate = birthDate;
        profile.Sex = sex;

        await context.SaveChangesAsync(cancellationToken);

        var dto = ToDto(profile);
        // Read back from the identity row, never from `request`: the field is response-only, so a client
        // that posts it can neither set nor clear it.
        dto.MustChangePassword = await MustChangePasswordAsync(userId, cancellationToken);
        return dto;
    }

    /// <summary>
    /// The admin-initiated-reset gate flag (issue #406 §5.5), read from the identity row the caller owns.
    /// <c>MainLayout</c> already fetches this profile for the onboarding gate, so surfacing it here costs
    /// the client no extra round trip.
    /// </summary>
    private async Task<bool> MustChangePasswordAsync(string userId, CancellationToken cancellationToken) =>
        await context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.MustChangePassword)
            .FirstOrDefaultAsync(cancellationToken);

    private static string? Normalize(string? value, string field, bool rejectEmail)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Any(char.IsControl))
        {
            throw new ProfileValidationException($"{field} must not contain control characters or line breaks.");
        }

        if (rejectEmail && new EmailAddressAttribute().IsValid(trimmed))
        {
            throw new ProfileValidationException($"{field} must not be an email address.");
        }

        return trimmed;
    }

    private static ProfileDto ToDto(UserProfile profile) => new()
    {
        FirstName = profile.FirstName,
        MiddleName = profile.MiddleName,
        LastName = profile.LastName,
        DisplayName = profile.DisplayName,
        Title = profile.Title,
        BirthDate = profile.BirthDate,
        Sex = profile.Sex,
        IsComplete = IsComplete(profile),
    };

    private static bool IsComplete(UserProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.FirstName)
        && !string.IsNullOrWhiteSpace(profile.LastName)
        && profile.BirthDate is not null
        && profile.Sex is not null;
}
