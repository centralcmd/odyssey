using System.ComponentModel.DataAnnotations;

namespace Odyssey.Api.Email;

/// <summary>
/// SMTP and link-generation settings for transactional email (account confirmation,
/// password reset). Bound from the <c>Email</c> configuration section. Credentials are
/// supplied per-environment via user-secrets / environment variables — never committed.
///
/// <c>RequireConfirmation</c> moved off this class and into the database-backed system-settings
/// store (issue #349, <c>SystemSettingsKeys.EmailRequireConfirmation</c>) — it is read live (never
/// cached) at both consulting sites: <see cref="SmtpEmailSender"/> and the
/// <c>IUserConfirmation&lt;ApplicationUser&gt;</c> sign-in seam.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>SMTP server host name. When empty, the sender logs and skips sending.</summary>
    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    /// <summary>Upgrade the connection with STARTTLS (port 587). Set false for implicit TLS (465).</summary>
    public bool UseStartTls { get; set; } = true;

    // Username and Password are GONE (issue #445 Wave 2). They moved to the encrypted secret store as
    // SecretSettingKeys.EmailUsername / EmailPassword and are resolved per send by SmtpEmailSender.
    //
    // Deleted rather than kept as documentation of record: a surviving property is a fallback waiting
    // to be written, and the one rule this migration exists to hold is that an UNREADABLE row never
    // resolves to the configured value. The transport fields around them — SmtpHost, SmtpPort,
    // UseStartTls — deliberately stay: the sender connects and THEN authenticates, so an admin-editable
    // host would harvest the relay credential and every reset token (issue #421 Non-Goal 2).

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "Odyssey";

    /// <summary>
    /// Public base URL of the Blazor client (e.g. <c>https://localhost:5199</c>). Confirmation
    /// and reset links are rewritten to land on the client's pages instead of the bare API
    /// endpoints, so the user sees a styled page.
    /// </summary>
    public string ClientBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Messages allowed to one recipient address per <see cref="PerRecipientWindowMinutes"/>
    /// (issue #393). Enforced by <see cref="EmailSendThrottle"/>, which is what stops a rotating-IP
    /// source from mailbombing a single mailbox through <c>/forgotPassword</c>.
    /// </summary>
    [Range(1, 1000)]
    public int PerRecipientLimit { get; set; } = 3;

    [Range(1, 1440)]
    public int PerRecipientWindowMinutes { get; set; } = 60;

    // RecipientHashKey is GONE too (issue #445 Wave 3) — same store, same reason. IEmailRecipientHashKey
    // resolves it per send and owns the per-process fallback that applies when no row exists, which is
    // still a supported configuration rather than a fault.
}
