using Odyssey.Api.Email;
using Odyssey.Api.SystemSettings;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Pins the two shape rules issue #8 added — the SMTP relay host and the public link origin — and the
/// property that keeps the write path and the send path from drifting apart on them.
///
/// <para>
/// <strong>The rejected half is the security-relevant half in both cases.</strong> For the host,
/// everything refused below is silently DISCARDED by MailKit's <c>ConnectAsync</c>, which takes a bare
/// host: accepting a scheme, a path, a port or <c>userinfo</c> would store a value that reads as
/// configured and connects somewhere else. For the base URL, <c>Uri.TryCreate</c> accepts <c>file:</c>,
/// <c>ftp:</c> and <c>javascript:</c> as readily as <c>https:</c>, and this is the field that decides
/// where a password-reset token lands.
/// </para>
/// </summary>
public class EmailTransportRuleTests
{
    // ── EmailSmtpHostRule ────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> AcceptedHosts() => new(AcceptedHostValues);

    public static TheoryData<string> RejectedHosts() => new(RejectedHostValues);

    private static readonly string[] AcceptedHostValues =
    [
        "smtp.example.net",
        "smtp",
        "mail-relay.internal",
        "mail_relay.internal",
        "10.0.0.5",
        "127.0.0.1",
        "[::1]",
        // The fully-qualified spelling, canonicalised back to the relative one so the two are not two
        // distinct stored values — which here would additionally read as a host CHANGE and clear the
        // stored credential for a save that changed nothing.
        "smtp.example.net.",
        "SMTP.Example.NET",
        "  smtp.example.net  ",
        // Trailing whitespace INCLUDING a newline is trimmed, not refused. What the rule has to keep
        // out is a control character in the value it actually stores — a trailing one is gone by then,
        // and refusing a pasted line ending would be hostile for no gain. The interior cases below are
        // the ones that matter, and they are rejected by the label character set rather than by a
        // separate scan, so there is one place to get it wrong instead of two.
        "smtp.example.net\n",
    ];

    private static readonly string[] RejectedHostValues =
    [
        // Every one of these is accepted by a naive check and discarded by ConnectAsync.
        "smtp://smtp.example.net",
        "https://smtp.example.net",
        "smtp.example.net:587",
        "user:pass@smtp.example.net",
        "smtp.example.net/submit",
        "smtp.example.net\\submit",
        // Unbracketed IPv6 is refused because the host parameter cannot disambiguate it from
        // host:port either — accepting it would store a value that does not connect.
        "::1",
        // Log forging, and values that cannot be a host. NUL is not whitespace, so unlike a trailing
        // newline it survives the trim and has to be refused on its own merits.
        "smtp.example.net\r\nDATA",
        "smtp.example\rnet",
        "smtp.example.net\0",
        "smtp..example.net",
        "-leading.example.net",
        "trailing-.example.net",
        // Internationalised spellings reach MailKit as punycode or not at all, so accepting the
        // Unicode form would store a value that resolves differently from the one displayed — and put
        // a homograph in the audit line.
        "smtp.exämple.net",
        "smtp.example.net..",
        "not a host",
    ];

    [Theory]
    [MemberData(nameof(AcceptedHosts))]
    public void AnAcceptedHost_ValidatesAndCanonicalises(string value)
    {
        Assert.Null(EmailSmtpHostRule.Validate(value));
        Assert.NotNull(EmailSmtpHostRule.Canonicalize(value));
    }

    [Theory]
    [MemberData(nameof(RejectedHosts))]
    public void ARejectedHost_IsRefusedWithoutEchoingIt(string value)
    {
        var message = EmailSmtpHostRule.Validate(value);

        Assert.NotNull(message);
        Assert.Null(EmailSmtpHostRule.Canonicalize(value));
        Assert.DoesNotContain(value.Trim(), message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SMTP.Example.NET", "smtp.example.net")]
    [InlineData("smtp.example.net.", "smtp.example.net")]
    [InlineData("  smtp.example.net  ", "smtp.example.net")]
    [InlineData("[::1]", "[::1]")]
    public void HostCanonicalisation_CollapsesEquivalentSpellings(string value, string expected) =>
        Assert.Equal(expected, EmailSmtpHostRule.Canonicalize(value));

    /// <summary>
    /// A row planted by a restore never passed the write validator, and this projection is what the
    /// audit line echoes for the OLD value. It must never carry credential material.
    /// </summary>
    [Fact]
    public void TheHostProjection_NeverEchoesAnUnparseableValue()
    {
        Assert.Equal(FileAnalysisBaseUrlRule.Unparseable, EmailSmtpHostRule.Host("user:pass@evil.test"));
        Assert.Equal(EmailSmtpHostRule.NotConfigured, EmailSmtpHostRule.Host(string.Empty));
        Assert.Equal("smtp.example.net", EmailSmtpHostRule.Host("SMTP.Example.NET"));
    }

    // ── EmailClientBaseUrlRule ───────────────────────────────────────────────────────────────────

    public static TheoryData<string> AcceptedUrls() => new(AcceptedUrlValues);

    public static TheoryData<string> RejectedUrls() => new(RejectedUrlValues);

    private static readonly string[] AcceptedUrlValues =
    [
        "https://odyssey.example.net",
        "https://odyssey.example.net/",
        "https://odyssey.example.net:8443",
        // A deployment may live under a subpath — links are composed as {base}/{clientPath}{query}.
        "https://odyssey.example.net/app",
        // The loopback http exemption, which is what keeps the dev and Aspire stacks working with no
        // environment variable. Uri.IsLoopback matches the LITERAL host, so it is not DNS-rebindable.
        "http://localhost:5199",
        "http://127.0.0.1:5199",
        "http://[::1]:5199",
    ];

    private static readonly string[] RejectedUrlValues =
    [
        // The case that matters: an http public origin planted by a restore or a hand edit.
        "http://odyssey.example.net",
        "http://10.0.0.5",
        "ftp://odyssey.example.net",
        "file:///etc/passwd",
        "javascript:alert(1)",
        "odyssey.example.net",
        "https://token@odyssey.example.net",
        "https://odyssey.example.net?code=leaky",
        "https://odyssey.example.net#fragment",
        "://broken",
        "not a url",
    ];

    [Theory]
    [MemberData(nameof(AcceptedUrls))]
    public void AnAcceptedUrl_ValidatesAndCanonicalises(string value)
    {
        Assert.Null(EmailClientBaseUrlRule.Validate(value));
        Assert.NotNull(EmailClientBaseUrlRule.Canonicalize(value));
    }

    [Theory]
    [MemberData(nameof(RejectedUrls))]
    public void ARejectedUrl_IsRefusedWithoutEchoingIt(string value)
    {
        var message = EmailClientBaseUrlRule.Validate(value);

        Assert.NotNull(message);
        Assert.Null(EmailClientBaseUrlRule.Canonicalize(value));
        Assert.DoesNotContain(value.Trim(), message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://odyssey.example.net/", "https://odyssey.example.net")]
    [InlineData("https://odyssey.example.net/app/", "https://odyssey.example.net/app")]
    public void UrlCanonicalisation_DropsTheTrailingSlash(string value, string expected) =>
        Assert.Equal(expected, EmailClientBaseUrlRule.Canonicalize(value));

    [Theory]
    [InlineData("https://admin.internal", "https://admin.internal")]
    [InlineData("https://admin.internal:8443/app", "https://admin.internal:8443")]
    [InlineData("", null)]
    [InlineData("not a url", null)]
    public void TheOriginProjection_IsSchemeHostAndPort(string value, string? expected) =>
        Assert.Equal(expected, EmailClientBaseUrlRule.Origin(value));

    // ── The write path and the send path agree ───────────────────────────────────────────────────

    /// <summary>
    /// The guard issue #8 §5.9 asks for: the send-path reader accepts and rejects exactly what the
    /// registry descriptor does.
    ///
    /// <para>
    /// It matters because the two paths differ deliberately in what they DO with a bad value — the
    /// descriptor writes the row through and reports a fault so an administrator can repair it, while
    /// the reader refuses the send — and a difference in what they consider bad would be invisible from
    /// either side. The field at stake decides where a credential and a reset token travel, so "the
    /// page says the row is fine and the sender refuses it" is precisely the state to rule out.
    /// </para>
    ///
    /// <para>
    /// Asserted through the descriptors' own <c>ReadValidator</c>, which is the delegate the
    /// <c>GET</c> path runs, rather than by re-invoking the rule directly — that would prove the rule
    /// agrees with itself.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptedHosts))]
    [MemberData(nameof(RejectedHosts))]
    public void TheHostReadValidator_IsTheSamePredicateTheWritePathApplies(string value)
    {
        var descriptor = Assert.IsType<StringSetting>(
            SystemSettingsRegistry.ByKey[SystemSettingsKeys.EmailSmtpHost]);

        Assert.NotNull(descriptor.ReadValidator);
        Assert.Equal(EmailSmtpHostRule.Validate(value) is null, descriptor.ReadValidator!(value) is null);
    }

    [Theory]
    [MemberData(nameof(AcceptedUrls))]
    [MemberData(nameof(RejectedUrls))]
    public void TheBaseUrlReadValidator_IsTheSamePredicateTheWritePathApplies(string value)
    {
        var descriptor = Assert.IsType<StringSetting>(
            SystemSettingsRegistry.ByKey[SystemSettingsKeys.EmailClientBaseUrl]);

        Assert.NotNull(descriptor.ReadValidator);
        Assert.Equal(EmailClientBaseUrlRule.Validate(value) is null, descriptor.ReadValidator!(value) is null);
    }

    /// <summary>
    /// Empty is legal on both paths, and it is the one value where "the two agree" is not enough — it
    /// has to be legal, because it is the only spelling of "mail is not configured" and the only route
    /// back to it. Without <c>AllowEmpty</c> the write path would reject it, and configuring mail
    /// would be a one-way door.
    /// </summary>
    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.EmailSmtpHost))]
    [InlineData(nameof(SystemSettingsUpdate.EmailClientBaseUrl))]
    public void TheTwoMailStringSettings_AcceptTheEmptyValueOnBothPaths(string fieldName)
    {
        var descriptor = Assert.IsType<StringSetting>(
            SystemSettingsRegistry.All.Single(d => d.FieldName == fieldName));

        Assert.True(descriptor.AllowEmpty);

        // The read path writes the empty row through as healthy rather than faulting it.
        var dto = new SystemSettingsDto();
        Assert.Equal(ProjectionOutcome.Ok, descriptor.Project(string.Empty, dto));
    }

    /// <summary>
    /// The read-path validator's whole reason for existing: a StringSetting used to report
    /// <c>Ok</c> unconditionally — "there is nothing to parse, so no stored value can fault here",
    /// which is true of parsing and false of semantics. An http:// public host planted by a restore
    /// would then fail closed on send while the settings page rendered the row as healthy.
    /// </summary>
    [Fact]
    public void AStoredValueTheRuleRejects_IsReportedAsAFault_AndWrittenThroughAsStored()
    {
        var descriptor = Assert.IsType<StringSetting>(
            SystemSettingsRegistry.ByKey[SystemSettingsKeys.EmailClientBaseUrl]);
        var dto = new SystemSettingsDto();

        Assert.Equal(ProjectionOutcome.Unparseable, descriptor.Project("http://attacker.test", dto));

        // As STORED, not as the compiled default: an administrator repairing the row needs to see what
        // is actually in the database, and there is no default here more truthful than the row.
        Assert.Equal("http://attacker.test", dto.EmailClientBaseUrl);
    }
}
