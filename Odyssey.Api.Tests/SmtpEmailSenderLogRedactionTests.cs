using System.Text.RegularExpressions;
using Odyssey.Api.Email;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The sender never writes a recipient address to a log, and writes an action link only where the
/// environment cannot serve real traffic (issue #248). An address in a log sits outside the erasure
/// path; a reset or confirmation link in a log is a live account-takeover credential.
/// </summary>
public class SmtpEmailSenderLogRedactionTests
{
    private const string Address = "Jane.Doe@Example.test";
    private const string Code = "CfDJ8-redaction-probe-token";

    private static readonly ApplicationUser User = new() { Id = "user-1", Email = Address, UserName = Address };

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Unconfigured_OutsideDevelopment_LogsNeitherTheAddressNorTheLink(string environment)
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(logger, environmentName: environment);

        await sender.SendPasswordResetCodeAsync(User, Address, Code);
        await sender.SendConfirmationLinkAsync(User, Address, "https://api.example.test/confirmEmail?userId=u&code=" + Code);

        Assert.Equal(2, logger.Messages.Count(m => m.Contains("no SMTP host configured", StringComparison.Ordinal)));
        AssertNoAddress(logger);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(Code, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Use this link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unconfigured_InDevelopment_LogsTheLink_ButStillNotTheAddress()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(logger, environmentName: "Development");

        await sender.SendPasswordResetCodeAsync(User, Address, Code);

        Assert.Contains(logger.Messages, m => m.Contains("Use this link", StringComparison.Ordinal)
            && m.Contains(Code, StringComparison.Ordinal));
        AssertNoAddress(logger);
    }

    [Fact]
    public async Task TheLoggedDigest_IsTheThrottlesKeyedDigest_OfTheNormalizedAddress()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(logger, environmentName: "Production");

        await sender.SendPasswordResetCodeAsync(User, Address, Code);

        // Same key, same normalization — so a skipped send correlates with a throttled one.
        var expected = EmailSendThrottle.HashRecipient(
            new StubEmailRecipientHashKey().Key.Span, EmailSendThrottle.Normalize(Address));
        Assert.Contains(logger.Messages, m => m.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedDelivery_DoesNotLogTheAddress()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(
            logger, smtpHost: SmtpEmailSenderTestHarness.UnreachableHost, environmentName: "Production");

        await sender.SendPasswordResetCodeAsync(User, Address, Code);

        Assert.Contains(logger.Messages, m => m.Contains("Failed to send email", StringComparison.Ordinal));
        AssertNoAddress(logger);
    }

    [Fact]
    public async Task UnusableTransport_DoesNotLogTheAddress()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(
            logger,
            smtpHost: SmtpEmailSenderTestHarness.UnreachableHost,
            environmentName: "Production",
            extraRows: new Dictionary<string, string> { [SystemSettingsKeys.EmailUseStartTls] = "yes" });

        await sender.SendPasswordResetCodeAsync(User, Address, Code);

        Assert.Contains(logger.Messages, m => m.Contains("cannot be used", StringComparison.Ordinal));
        AssertNoAddress(logger);
    }

    [Fact]
    public async Task IncompleteCredential_DoesNotLogTheAddress()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(
            logger,
            smtpHost: SmtpEmailSenderTestHarness.UnreachableHost,
            environmentName: "Production",
            secrets: new StubSecretSettingsReader().Found(SecretSettingKeys.EmailUsername, "relay-user"));

        await sender.SendPasswordResetCodeAsync(User, Address, Code);

        Assert.Contains(logger.Messages, m => m.Contains("SMTP credential is incomplete", StringComparison.Ordinal));
        AssertNoAddress(logger);
    }

    [Fact]
    public async Task ConfirmationDisabled_SkipLine_DoesNotLogTheAddress()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(
            logger,
            environmentName: "Production",
            extraRows: new Dictionary<string, string> { [SystemSettingsKeys.EmailRequireConfirmation] = "false" });

        await sender.SendConfirmationLinkAsync(User, Address, "https://api.example.test/confirmEmail?code=" + Code);

        Assert.Contains(logger.Messages, m => m.Contains("Email confirmation disabled", StringComparison.Ordinal));
        AssertNoAddress(logger);
    }

    /// <summary>
    /// The source-lint half: no log template in the API or in Core may carry a raw <c>{Recipient}</c>,
    /// and the one <c>{Link}</c> placeholder sits inside the environment gate. Behavioural tests only
    /// cover the branches they reach; this covers the next log line someone adds. Core is scanned too
    /// because the account services that log about users and mail (user administration, legal, profile,
    /// data export) moved there (issue #287 M10) — scanning the API alone would keep passing over them.
    /// </summary>
    [Fact]
    public void NoLogTemplate_CarriesARawRecipientOrAnUngatedLink()
    {
        var sources = new[] { "Odyssey.Api", "Odyssey.Core" }
            .Select(project => Path.Combine(RepositoryRoot.Path, project))
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(sources, path => path.EndsWith("UserAdministrationService.cs", StringComparison.Ordinal));

        // Any message-template placeholder naming an address — {Recipient}, {Email}, {To}, {ToAddress},
        // {RecipientAddress}, {UserEmail} … — in a regular, verbatim or raw string. The digest
        // ({RecipientHash}) does not end in an address noun, and a count that does — a cap
        // ({MaxTrackedRecipients}), a remap tally ({RemappedEmails}) or a usage count ({appliedTo}) —
        // is excluded by its prefix.
        var rawRecipient = new Regex(
            @"\{(?!(Max|Min|Remapped|Applied)\w*\})\w*(Recipients?|Emails?|Address(es)?|Mailbox|To)\}", RegexOptions.IgnoreCase);
        Assert.Matches(rawRecipient, "{Recipient}");
        Assert.Matches(rawRecipient, "{UserEmail}");
        Assert.Matches(rawRecipient, "{ToAddress}");
        Assert.Matches(rawRecipient, "{To}");
        Assert.DoesNotMatch(rawRecipient, "{RecipientHash}");
        Assert.DoesNotMatch(rawRecipient, "{MaxTrackedRecipients}");
        Assert.DoesNotMatch(rawRecipient, "{RemappedEmails}");
        Assert.DoesNotMatch(rawRecipient, "{appliedTo}");
        var offenders = sources
            .SelectMany(path => File.ReadLines(path)
                .Where(line => IsLogCall(line) || LooksLikeTemplate(line))
                .Where(line => rawRecipient.IsMatch(line))
                .Select(line => $"{Path.GetFileName(path)}: {line.Trim()}"))
            .ToList();
        Assert.True(offenders.Count == 0, "Raw recipient placeholder in: " + string.Join(", ", offenders));

        var linkPlaceholder = new Regex(@"""[^""\r\n]*\{Link\}");
        var linkFiles = sources.Where(path => linkPlaceholder.IsMatch(File.ReadAllText(path))).ToList();
        var sender = Assert.Single(linkFiles);
        Assert.EndsWith("SmtpEmailSender.cs", sender, StringComparison.Ordinal);

        var text = File.ReadAllText(sender);
        var gate = text.IndexOf("IsLinkLoggingEnvironment)", StringComparison.Ordinal);
        var link = linkPlaceholder.Match(text).Index;
        Assert.True(gate >= 0 && gate < link && text.IndexOf("else", gate, StringComparison.Ordinal) > link,
            "The {Link} log line must sit inside the IsLinkLoggingEnvironment branch.");
        Assert.Contains(
            """environment.IsDevelopment() || environment.IsEnvironment("Testing")""", text, StringComparison.Ordinal);
    }

    private static bool IsLogCall(string line) => line.Contains(".Log", StringComparison.Ordinal);

    // A continuation line of a multi-line template: a string literal, or a "+ \"…\"" concatenation.
    private static bool LooksLikeTemplate(string line) =>
        line.TrimStart().StartsWith('"') || line.TrimStart().StartsWith("+ \"", StringComparison.Ordinal)
        || line.TrimStart().StartsWith("$\"", StringComparison.Ordinal);

    private static void AssertNoAddress(CapturingLogger<SmtpEmailSender> logger)
    {
        Assert.DoesNotContain(logger.Messages, m => m.Contains(Address, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("example.test", StringComparison.OrdinalIgnoreCase)
            && m.Contains("jane", StringComparison.OrdinalIgnoreCase));
    }
}
