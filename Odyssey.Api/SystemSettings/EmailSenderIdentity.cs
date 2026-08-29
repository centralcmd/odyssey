using MimeKit;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Validation for the admin-editable transactional-email sender identity (issue #421 Wave 2).
/// </summary>
internal static class EmailSenderIdentity
{
    /// <summary>
    /// Returns an error message, or null when <paramref name="value"/> is a single bare mailbox.
    ///
    /// <para>
    /// Parsed with MimeKit rather than a regex, because MimeKit is what actually consumes this value —
    /// validating with a different grammar than the consumer means a value can pass here and still
    /// throw at send time. Two shapes are rejected beyond "unparseable":
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// A value carrying a display name (<c>"Odyssey &lt;a@b.test&gt;"</c>). The display name is its own
    /// setting; accepting one here would give two sources for it and let this field silently override
    /// the other.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// A value carrying more than one address (<c>"a@b.test, c@d.test"</c>). This is the envelope
    /// sender, and a list is both meaningless and a header-shaped surprise.
    /// </description>
    /// </item>
    /// </list>
    /// </summary>
    internal static string? ValidateFromAddress(string value)
    {
        // TryParse accepts a display name and a group, so the shape checks below are not redundant.
        if (!MailboxAddress.TryParse(value, out var mailbox))
        {
            return "must be a valid email address.";
        }

        if (!string.IsNullOrEmpty(mailbox.Name))
        {
            return "must be a bare address with no display name — set the display name separately.";
        }

        if (!string.Equals(mailbox.Address, value.Trim(), StringComparison.Ordinal))
        {
            return "must be a single bare address.";
        }

        // MimeKit is lenient: it parses a bare token like "postmaster" as a valid local-part-only
        // address, which would pass every check above and then fail at send time. An envelope sender
        // needs a domain, so require one explicitly.
        //
        // Deliberately not requiring a dot in the domain: "odyssey@localhost" and other intranet hosts
        // are legitimate for an internal relay, and rejecting them would be stricter than the transport.
        var at = mailbox.Address.LastIndexOf('@');
        if (at <= 0 || at == mailbox.Address.Length - 1)
        {
            return "must include a domain, for example no-reply@example.com.";
        }

        return null;
    }
}
