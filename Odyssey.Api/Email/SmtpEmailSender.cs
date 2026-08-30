using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;
using Odyssey.Dtos;

namespace Odyssey.Api.Email;

/// <summary>
/// MailKit-backed <see cref="IEmailSender{TUser}"/> that <c>MapIdentityApi</c> resolves to
/// deliver the confirmation and password-reset messages produced by the built-in Identity
/// endpoints. Either way the user must land on a styled Blazor client page rather than on a bare
/// API endpoint, but the framework hands the two callbacks different raw material:
/// <list type="bullet">
/// <item>confirmation gets a fully-formed link at the API, which is rewritten onto the client page
/// with its <c>userId</c>/<c>code</c> query preserved (<see cref="RewriteToClient"/>);</item>
/// <item>password reset gets a bare token and no link at all, so the client URL is composed here
/// (<see cref="SendPasswordResetCodeAsync"/>).</item>
/// </list>
/// <para>
/// It also implements <see cref="IPasswordResetLinkSender"/> (issue #406) so the admin-initiated reset
/// mails the byte-identical link through this same composition and send code rather than a parallel
/// sender of its own. The two entry points differ only in what they do about the per-recipient send
/// permit and about a failed relay — see <see cref="SendAsync"/> and <see cref="SendResetLinkAsync"/>.
/// </para>
/// </summary>
/// <remarks>
/// Takes <see cref="IServiceScopeFactory"/>, not <see cref="OdysseyContext"/>, directly:
/// <c>MapIdentityApi</c> resolves <c>IEmailSender&lt;TUser&gt;</c> exactly once, from the app's ROOT
/// service provider, and caches that single instance for the app's lifetime — it is never resolved
/// per-request. A constructor dependency on the scoped <see cref="OdysseyContext"/> would make
/// that root-provider resolution itself fail at startup ("requires scoped service"), before any
/// request ever reaches it. The live <c>EmailRequireConfirmation</c> read instead opens its own
/// short-lived scope on each call.
/// </remarks>
public sealed class SmtpEmailSender(
    IServiceScopeFactory scopeFactory,
    IEmailSendThrottle throttle,
    IEmailRecipientHashKey recipientHashKey,
    IHostEnvironment environment,
    ILogger<SmtpEmailSender> logger)
    : IEmailSender<ApplicationUser>, IPasswordResetLinkSender
{
    public async Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
    {
        // Live read (issue #349) — never cached, so a toggle takes effect on the very next
        // registration attempt with no staleness window.
        var requireConfirmation = await GetRequireConfirmationAsync();
        if (!requireConfirmation)
        {
            // Confirmation is disabled: MapIdentityApi still calls this on register, but the user
            // can already sign in, so sending a "confirm your account" email would be misleading.
            logger.LogDebug("Email confirmation disabled; skipping confirmation link for {Recipient}.", email);
            return;
        }

        // One snapshot, threaded through composition AND delivery. Composing against a base URL that
        // a concurrent admin write then changed before the send would mail a link to the old origin
        // while the audit line named the new one.
        var settings = await ReadSettingsAsync();

        var link = RewriteToClient(confirmationLink, "confirm-email", settings.Transport);
        var body = $"""
            <p>Welcome to Odyssey. Confirm your email address to activate your account:</p>
            <p><a href="{link}">Confirm my email</a></p>
            <p>If you didn't create this account, you can ignore this message.</p>
            """;
        await SendAsync(email, "Confirm your Odyssey account", body, link, settings);
    }

    /// <summary>
    /// Deliberately does nothing. <c>MapIdentityApi</c>'s <c>/forgotPassword</c> handler calls
    /// <see cref="SendPasswordResetCodeAsync"/> unconditionally and never this overload (verified
    /// empirically against Identity 10.0.5, issue #405), so composing a message here would be dead
    /// code that reads as if it were the live reset path. The whole reset mail lives in
    /// <see cref="SendPasswordResetCodeAsync"/>.
    /// </summary>
    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        Task.CompletedTask;

    /// <summary>
    /// The live password-reset mail. Identity hands over a bare, HTML-encoded Base64Url token rather
    /// than a link, so — unlike confirmation, which gets a framework link to rewrite — the client URL
    /// is composed here.
    /// </summary>
    public async Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
    {
        var settings = await ReadSettingsAsync();
        var message = PasswordResetMail.Compose(resetCode, settings.Transport.ClientBaseUrl);
        await SendAsync(email, PasswordResetMail.Subject, message.Body, message.Link, settings);
    }

    /// <summary>
    /// The admin-initiated reset's send (issue #406 §5.1). Same composition, same delivery, one
    /// difference in each direction: it acquires <b>no</b> send permit — its caller already did, before
    /// mutating anything — and it reports the real outcome instead of returning indistinguishably from
    /// success.
    /// </summary>
    public async Task<PasswordResetLinkDelivery> SendResetLinkAsync(
        string email, string base64UrlCode, CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsAsync(cancellationToken);
        var message = PasswordResetMail.Compose(base64UrlCode, settings.Transport.ClientBaseUrl);
        return await DeliverAsync(
            email, PasswordResetMail.Subject, message.Body, message.Link, cancellationToken, settings);
    }

    /// <summary>
    /// The database-backed email settings, read once per send (issue #421 Wave 2).
    /// </summary>
    /// <remarks>
    /// One snapshot, not a read per field. <c>SendAsync</c> tests the host before the permit check and
    /// <c>DeliverAsync</c> tests it again, so per-field live reads would let a single send observe two
    /// different values across a concurrent admin write — consuming a recipient's permit and then
    /// dropping the message it was consumed for. Since issue #8 the SMTP transport fields are read in
    /// the same scope and the same method: <c>EmailOptions</c> is gone, and there is no configuration
    /// left for any of them to come from.
    ///
    /// <para>
    /// <strong>No credential is a member of this record.</strong> A record prints its members, and a
    /// record's <c>ToString()</c> is what surfaces in a logged exception context — so the SMTP username
    /// and password are resolved into locals at the point of use (<see cref="ResolveCredentialsAsync"/>)
    /// and never travel in a printable object. The recipient hash key is bytes, not a printable
    /// credential, and it is what the throttle needs at exactly this moment.
    /// </para>
    /// </remarks>
    private sealed record EmailSettings(
        string FromAddress,
        string FromName,
        int PerRecipientLimit,
        int WindowMinutes,
        int MaxTrackedRecipients,
        ReadOnlyMemory<byte> RecipientHashKey,
        EmailTransportSettings Transport);

    private async Task<EmailSettings> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        // The transport four go through their OWN reader, not SystemSettingsReader's defaulting
        // overloads (issue #8 §5.9, §11.1). That reader resolves an unparseable value to the compiled
        // default so a display bound "degrades instead of disappearing"; here that would substitute a
        // TLS mode or a port the administrator never chose onto the path a reset token travels.
        var transport = await EmailTransportSettingsReader.ReadAsync(context, cancellationToken);

        return new EmailSettings(
            await SystemSettingsReader.GetStringAsync(
                context, SystemSettingsKeys.EmailFromAddress, SystemSettingsDefaults.EmailFromAddress),
            await SystemSettingsReader.GetStringAsync(
                context, SystemSettingsKeys.EmailFromName, SystemSettingsDefaults.EmailFromName),
            await SystemSettingsReader.GetIntAsync(
                context, SystemSettingsKeys.EmailPerRecipientLimit, SystemSettingsDefaults.EmailPerRecipientLimit),
            await SystemSettingsReader.GetIntAsync(
                context, SystemSettingsKeys.EmailPerRecipientWindowMinutes,
                SystemSettingsDefaults.EmailPerRecipientWindowMinutes),
            // Raise-only, so the read clamps UPWARD to the shipped floor (issue #434 key 14). The
            // throttle fails open at capacity, which makes max — not min — the conservative direction
            // here, and the clamp holds no matter how the row was written.
            Math.Max(
                await SystemSettingsReader.GetIntAsync(
                    context, SystemSettingsKeys.EmailMaxTrackedRecipients,
                    SystemSettingsDefaults.EmailMaxTrackedRecipients),
                SystemSettingsDefaults.EmailMaxTrackedRecipients),
            // Read on the same cadence as the limits and for the same reason: the throttle's
            // compare-and-increment runs in a lock and cannot await one of its own (issue #445 Wave 3).
            await recipientHashKey.ResolveAsync(),
            transport);
    }

    // A short-lived scope per call, rather than a constructor dependency — see the class remarks.
    private async Task<bool> GetRequireConfirmationAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await SystemSettingsReader.GetBoolAsync(
            context, SystemSettingsKeys.EmailRequireConfirmation, SystemSettingsDefaults.EmailRequireConfirmation);
    }

    /// <summary>
    /// The relay credential, resolved live from the encrypted store on each send (issue #445 Wave 2).
    ///
    /// <para>
    /// <strong>The pair moves together.</strong> A username from the store beside a password that is
    /// absent — or the reverse — is a half-configured credential, and there is no useful thing to do
    /// with it: <c>AuthenticateAsync</c> would fail, and skipping authentication would send with an
    /// identity the relay is not expecting. So only "both stored" authenticates and only "neither
    /// stored" sends unauthenticated; everything between is <see cref="SmtpCredentialState.Unusable"/>.
    /// </para>
    ///
    /// <para>
    /// <strong>Unreadable fails closed.</strong> This service's instinct elsewhere is to fail open — a
    /// dropped password reset is a lockout — and it does not apply here: an unreadable password cannot
    /// produce a successful <c>AuthenticateAsync</c>, so "failing open" would mean an unauthenticated
    /// attempt the relay rejects. The mail is lost either way, and the one remaining alternative —
    /// falling back to a configured value — would send with the credential the administrator believed
    /// they had replaced. There is nothing to fall back to in any case: <c>Email:Username</c> and
    /// <c>Email:Password</c> were removed from the bound options class in the same change, and issue
    /// #8 deleted what was left of that class along with the whole <c>Email</c> configuration section.
    /// </para>
    /// </summary>
    private async Task<SmtpCredentials> ResolveCredentialsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISecretSettingsReader>();

        var username = await reader.GetAsync(SecretSettingKeys.EmailUsername, cancellationToken);
        var password = await reader.GetAsync(SecretSettingKeys.EmailPassword, cancellationToken);

        if (username.State == SecretReadState.Unreadable || password.State == SecretReadState.Unreadable)
        {
            return SmtpCredentials.Unusable;
        }

        if (username.State == SecretReadState.NotSet && password.State == SecretReadState.NotSet)
        {
            // Today's behaviour with neither configured: an unauthenticated relay is a legitimate
            // configuration on a trusted network, so this is healthy, not degraded.
            return SmtpCredentials.Unauthenticated;
        }

        return username.TryGetValue(out var user) && password.TryGetValue(out var secret)
            ? SmtpCredentials.Ready(user, secret)
            : SmtpCredentials.Unusable;
    }

    private enum SmtpCredentialState
    {
        /// <summary>Neither half is stored — connect and send without authenticating.</summary>
        Unauthenticated = 1,

        /// <summary>Both halves resolved.</summary>
        Ready = 2,

        /// <summary>Half-configured, or a row that cannot be decrypted. The send is skipped.</summary>
        Unusable = 3,
    }

    /// <summary>
    /// A deliberately plain <c>sealed class</c>, not a <c>record</c>: a record generates a
    /// member-printing <c>ToString()</c>, and that is exactly what a logged exception context prints.
    /// The override below states the same guarantee explicitly, at the place a reader looks.
    /// </summary>
    private sealed class SmtpCredentials
    {
        private readonly string? username;
        private readonly string? password;

        private SmtpCredentials(SmtpCredentialState state, string? username, string? password)
        {
            State = state;
            this.username = username;
            this.password = password;
        }

        public SmtpCredentialState State { get; }

        public static readonly SmtpCredentials Unauthenticated =
            new(SmtpCredentialState.Unauthenticated, null, null);

        public static readonly SmtpCredentials Unusable = new(SmtpCredentialState.Unusable, null, null);

        public static SmtpCredentials Ready(string username, string password) =>
            new(SmtpCredentialState.Ready, username, password);

        /// <summary>The only route to the pair, so a caller cannot reach it without branching first.</summary>
        public bool TryGetPair(out string user, out string secret)
        {
            user = username ?? string.Empty;
            secret = password ?? string.Empty;
            return State == SmtpCredentialState.Ready;
        }

        public override string ToString() => $"{nameof(SmtpCredentials)} {{ State = {State} }}";
    }

    /// <summary>
    /// Replaces the host/path of the framework-generated link with the configured client base
    /// URL and <paramref name="clientPath"/>, keeping the original query string (<c>userId</c>,
    /// <c>code</c>, optional <c>changedEmail</c>) verbatim so the values still decode server-side.
    /// </summary>
    private static string RewriteToClient(
        string frameworkLink, string clientPath, EmailTransportSettings transport)
    {
        // Empty covers BOTH the absent case (no client URL configured — degrade to the framework
        // link, as before) and the unusable one (a stored value the rule rejects, which the reader
        // resolves to empty rather than to the compiled default). The unusable case never reaches a
        // recipient regardless: DeliverAsync fails the send closed before anything is transmitted.
        if (string.IsNullOrWhiteSpace(transport.ClientBaseUrl))
        {
            return frameworkLink;
        }

        var query = new Uri(frameworkLink).Query;
        return $"{transport.ClientBaseUrl.TrimEnd('/')}/{clientPath}{query}";
    }

    /// <summary>
    /// Fails open. A throttle that throws is an availability problem; a silently dropped password
    /// reset is a lockout, so an unexpected failure sends the mail and tells operators about it.
    /// </summary>
    private bool TryAcquireSendPermit(string toEmail, string subject, EmailSettings settings)
    {
        try
        {
            return throttle.TryAcquire(
                toEmail,
                settings.PerRecipientLimit,
                settings.WindowMinutes,
                settings.MaxTrackedRecipients,
                settings.RecipientHashKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Per-recipient email throttle failed; sending anyway (subject: {Subject}).", subject);
            return true;
        }
    }

    private bool IsLinkLoggingEnvironment =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    /// <summary>
    /// The <c>IEmailSender&lt;ApplicationUser&gt;</c> entry point: acquire a per-recipient permit, and on
    /// rejection drop the message while returning exactly as a successful send would. Deliberate — a 429
    /// on <c>/forgotPassword</c> would reveal that this address recently received mail, i.e. that it
    /// exists, which is the enumeration leak that flow is designed to avoid.
    /// </summary>
    /// <remarks>
    /// The acquisition lives here rather than in <see cref="DeliverAsync"/> so the admin path
    /// (<see cref="SendResetLinkAsync"/>) can acquire once, up front, and still learn what happened to its
    /// message — sharing one permit-acquiring body would both double-consume the recipient's budget and
    /// silently drop the mail after the caller had already committed its writes (issue #406 §5.1).
    /// </remarks>
    private async Task SendAsync(
        string toEmail, string subject, string htmlBody, string? actionLink, EmailSettings? snapshot = null)
    {
        // Before the permit check, so an unconfigured dev stack still logs every action link — and so a
        // throttled call in that environment doesn't consume a permit for a message there was no relay to
        // send. DeliverAsync repeats the host check; it is the one that logs.
        var settings = snapshot ?? await ReadSettingsAsync();

        // IsConfigured, not "the host string is non-empty": an unusable host is not a configured one,
        // and a permit consumed for a message that is about to fail closed would count against a
        // recipient who never received anything.
        if (settings.Transport.IsConfigured && !TryAcquireSendPermit(toEmail, subject, settings))
        {
            return;
        }

        await DeliverAsync(toEmail, subject, htmlBody, actionLink, CancellationToken.None, settings);
    }

    /// <summary>
    /// Composes and delivers, reporting the outcome and acquiring no send permit. Every send goes through
    /// here; the entry points differ only in whether they throttle and whether they surface the result.
    /// </summary>
    private async Task<PasswordResetLinkDelivery> DeliverAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string? actionLink,
        CancellationToken cancellationToken,
        EmailSettings? snapshot = null)
    {
        // Reuse the caller's snapshot when there is one, so a throttled-then-delivered send sees a
        // single consistent view; read a fresh one for the entry points that do not throttle.
        var settings = snapshot ?? await ReadSettingsAsync(cancellationToken);
        var transport = settings.Transport;

        // FAIL CLOSED, and before the unconfigured branch below (issue #8 §11.1). A stored value that
        // is present and unusable is degraded, not absent, and the difference is the whole reason this
        // read does not go through SystemSettingsReader: substituting a compiled default here would
        // connect to a relay, or compose a reset link, on terms the administrator did not choose.
        //
        // It refuses on ANY of the four, not just the host: the port and the TLS flag decide how the
        // credential travels, and the base URL decides where the token lands. Naming the keys and
        // never the values — a stored value can carry `user:pass@host` planted by a restore.
        if (transport.UnusableKeys.Count > 0)
        {
            logger.LogError(
                "Email not sent to {Recipient} (subject: {Subject}): the stored mail transport settings "
                + "{Keys} cannot be used. Correct them in System settings; no default is substituted.",
                toEmail, subject, string.Join(", ", transport.UnusableKeys));
            return PasswordResetLinkDelivery.NotConfigured;
        }

        if (!transport.IsConfigured)
        {
            // No SMTP configured: log loudly rather than failing the surrounding Identity request.
            // Surface the action link itself so confirmation still works in local/dev setups with
            // no mail server — the link is HTML-encoded for the email body, so decode it to a
            // copy-pasteable URL. Without this the account is created but unconfirmable.
            //
            // Development/Testing only (issue #405): a password-reset link is a direct
            // account-takeover primitive, so anywhere else the log records that the mail could not be
            // sent and stops there.
            //
            // Production DOES reach this branch now (issue #8 §11.3). The startup ValidateOnStart gate
            // on Email:SmtpHost could not survive the move: a value entered through the UI cannot be a
            // precondition for the UI coming up. The failure moved from startup to the first send —
            // the identical trade issue #445 made for Legal:PseudonymizationSecret. The consequence
            // for a fresh deployment (no self-service recovery until mail is configured) is in
            // docs/deployment.md, and the settings page says so in its own header.
            if (actionLink is not null && IsLinkLoggingEnvironment)
            {
                logger.LogWarning(
                    "Email not sent to {Recipient} (subject: {Subject}): no SMTP host configured. Use this link: {Link}",
                    toEmail, subject, WebUtility.HtmlDecode(actionLink));
            }
            else
            {
                logger.LogWarning(
                    "Email not sent to {Recipient} (subject: {Subject}): no SMTP host configured.",
                    toEmail, subject);
            }

            return PasswordResetLinkDelivery.NotConfigured;
        }

        // BEFORE the connection, deliberately: a send that cannot authenticate must not open a socket to
        // the relay at all, and skipping here is the same posture the unset-host branch above takes.
        var credentials = await ResolveCredentialsAsync(cancellationToken);
        if (credentials.State == SmtpCredentialState.Unusable)
        {
            // Names neither half and echoes nothing — an operator needs to know the credential is the
            // problem, not which byte of it. "incomplete or unreadable" covers both without an oracle.
            logger.LogError(
                "Email not sent to {Recipient} (subject: {Subject}): the SMTP credential is incomplete or "
                + "cannot be decrypted on this server. Set the SMTP username and password in System settings.",
                toEmail, subject);
            return PasswordResetLinkDelivery.NotConfigured;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        try
        {
            using var client = new SmtpClient();
            var secureSocket = transport.UseStartTls
                ? SecureSocketOptions.StartTls
                : SecureSocketOptions.SslOnConnect;
            await client.ConnectAsync(transport.Host, transport.Port, secureSocket, cancellationToken);

            if (credentials.TryGetPair(out var username, out var password))
            {
                await client.AuthenticateAsync(username, password, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception ex)
        {
            // Swallow so a transient SMTP failure doesn't turn registration into a 500 (the user
            // exists and can resend). Surfaced as an error for operators — and, for the callers that
            // asked for it, as a Failed outcome they can report to the person who is waiting.
            logger.LogError(ex, "Failed to send email to {Recipient} (subject: {Subject}).", toEmail, subject);
            return PasswordResetLinkDelivery.Failed;
        }

        return PasswordResetLinkDelivery.Delivered;
    }
}
