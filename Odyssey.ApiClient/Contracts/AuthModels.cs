namespace Odyssey.ApiClient.Contracts;

public sealed class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    // The built-in MapIdentityApi /login endpoint reads these to complete a 2FA
    // challenge in a second request; they stay null on the initial password attempt.
    public string? TwoFactorCode { get; set; }
    public string? TwoFactorRecoveryCode { get; set; }
}

public sealed class RegisterRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
