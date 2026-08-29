namespace Odyssey.Client.Pages;

/// <summary>Which account state a <see cref="UserStatusBadge"/> reports.</summary>
public enum UserStatusKind
{
    /// <summary>Whether the account is enabled (as opposed to disabled/locked out).</summary>
    Account,

    /// <summary>Whether the user's email address has been confirmed.</summary>
    Email,
}
