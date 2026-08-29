namespace Odyssey.Api.Profiles;

/// <summary>Thrown when a profile save fails a service-side validation rule (issue #316 §9); surfaced as a 400.</summary>
public sealed class ProfileValidationException : Exception
{
    public ProfileValidationException(string message)
        : base(message)
    {
    }
}
