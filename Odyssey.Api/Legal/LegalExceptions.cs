namespace Odyssey.Api.Legal;

/// <summary>A request the caller can fix by correcting the body — surfaces as a 400.</summary>
public sealed class LegalValidationException(string message) : Exception(message);

/// <summary>
/// The echoed <c>tosVersionId</c> is not the current version — surfaces as a 409 so the client reloads
/// the current text and re-prompts rather than recording consent to a version the user never saw.
/// </summary>
public sealed class LegalVersionConflictException(string message) : Exception(message);
