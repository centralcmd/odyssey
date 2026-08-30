using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Pins the shape rule for the admin-editable file-analysis base URL (issue #439) — the setting that
/// decides which host receives an uploaded document and the configured API key.
///
/// <para>
/// <strong>What this replaced.</strong> The rule once had two callers, the API's <c>PUT</c> path and
/// the migrations job's configuration adoption, each with its own copy of the predicate; this file
/// was a parity test holding the two together, and later held the single shared rule to its two call
/// sites. Adoption is gone — there was never a deployment old enough to need it — so the API is the
/// only caller and there is no parity left to check. The candidate list survived the deletion because
/// it, not the parity, is what carries the value: it is the only place the rejections below are
/// asserted at all.
/// </para>
///
/// <para>
/// The rejected half is the security-relevant half. <c>Uri.TryCreate</c> accepts <c>file:</c>,
/// <c>ftp:</c> and <c>javascript:</c> as readily as <c>https:</c>; userinfo is a credential and a
/// query or fragment can carry a token; and a path is silently discarded by the root-absolute request
/// builder, so accepting one would save a host that looks right while requests went elsewhere.
/// </para>
/// </summary>
public class FileAnalysisBaseUrlRuleTests
{
    public static TheoryData<string> Accepted() => new(AcceptedValues);

    public static TheoryData<string> Rejected() => new(RejectedValues);

    private static readonly string[] AcceptedValues =
    [
        "https://api.anthropic.com",
        "https://gateway.internal",
        "https://gateway.internal/",
        "https://127.0.0.1:8443",
        "https://10.0.0.5",
        "https://localhost",
        "  https://gateway.internal  ",
    ];

    private static readonly string[] RejectedValues =
    [
        "http://api.anthropic.com",
        "ftp://api.anthropic.com",
        "file:///etc/passwd",
        "javascript:alert(1)",
        "api.anthropic.com",
        "https:///v1",
        "https://key:secret@gateway.internal",
        "https://host?token=leaky",
        "https://host#fragment",
        "https://host/v1/messages",
        "https://host/proxy",
        "://broken",
        "not a url",
        "",
        "   ",
    ];

    [Theory]
    [MemberData(nameof(Accepted))]
    public void AnAcceptedValue_ValidatesAndCanonicalises(string candidate)
    {
        Assert.Null(FileAnalysisBaseUrlRule.Validate(candidate));

        var canonical = FileAnalysisBaseUrlRule.Canonicalize(candidate);

        Assert.NotNull(canonical);
        // Scheme + authority, no trailing slash — so https://host and https://host/ store identically
        // and one does not read as a change against the other, producing a spurious audit line.
        Assert.Equal(canonical, FileAnalysisBaseUrlRule.Canonicalize(canonical!));
        Assert.Contains(FileAnalysisBaseUrlRule.Host(candidate), canonical!, StringComparison.Ordinal);
        Assert.DoesNotContain('/', canonical!["https://".Length..]);
    }

    [Theory]
    [MemberData(nameof(Rejected))]
    public void ARejectedValue_FailsValidationAndCanonicalisesToNull(string candidate)
    {
        Assert.NotNull(FileAnalysisBaseUrlRule.Validate(candidate));
        Assert.Null(FileAnalysisBaseUrlRule.Canonicalize(candidate));
    }

    /// <summary>
    /// The projection every echo of the value goes through — the audit line, the advisories, the job
    /// stamp. It is applied to the OLD value as well as the new one, which is why it has to hold for a
    /// value the validator would have rejected: a row planted by a restore can carry credentials, and
    /// without this the first administrator to correct it through the UI writes them to the log.
    /// </summary>
    [Fact]
    public void Host_NeverEchoesCredentialsQueryOrPath()
    {
        Assert.Equal("gateway.internal", FileAnalysisBaseUrlRule.Host("https://key:secret@gateway.internal"));
        Assert.Equal("host", FileAnalysisBaseUrlRule.Host("https://host?token=leaky"));
        Assert.Equal("host", FileAnalysisBaseUrlRule.Host("https://host/v1/messages"));
        Assert.Equal(FileAnalysisBaseUrlRule.Unparseable, FileAnalysisBaseUrlRule.Host("not a url"));
    }
}
