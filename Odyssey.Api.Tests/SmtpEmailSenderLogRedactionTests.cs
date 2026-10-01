using System.Text.RegularExpressions;
using Odyssey.Api.Email;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
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

    /// <summary>
    /// The source-lint half: no log template in the API may carry a raw <c>{Recipient}</c>, and the one
    /// <c>{Link}</c> placeholder sits inside the environment gate. Behavioural tests only cover the
    /// branches they reach; this covers the next log line someone adds.
    /// </summary>
    [Fact]
    public void NoLogTemplate_CarriesARawRecipientOrAnUngatedLink()
    {
        var apiRoot = Path.Combine(RepositoryRoot.Path, "Odyssey.Api");
        var sources = Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sources);

        var rawRecipient = new Regex(@"""[^""\r\n]*\{(Recipient|Email|EmailAddress|ToEmail)\}", RegexOptions.IgnoreCase);
        var offenders = sources.Where(path => rawRecipient.IsMatch(File.ReadAllText(path))).ToList();
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

    private static void AssertNoAddress(CapturingLogger<SmtpEmailSender> logger)
    {
        Assert.DoesNotContain(logger.Messages, m => m.Contains(Address, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("example.test", StringComparison.OrdinalIgnoreCase)
            && m.Contains("jane", StringComparison.OrdinalIgnoreCase));
    }
}
