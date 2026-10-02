using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Application;

/// <summary>
/// The persisted UI state for one page key. The length limit matches
/// <c>UserPreference.PreferencesJson</c>, so an oversized blob is a 400 rather than a write failure.
/// </summary>
public sealed record UserPreferenceRequest
{
    [StringLength(4096)]
    public required string PreferencesJson { get; set; }
}

public sealed record UserPreferenceResponse(string PageKey, string PreferencesJson, DateTime UpdatedAt);
