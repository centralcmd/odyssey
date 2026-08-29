extern alias migrations;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Dtos;
using Xunit;
using ConfigAdoption = migrations::Odyssey.MigrationService.SystemSettingsConfigAdoption;

namespace Odyssey.Api.Tests;

/// <summary>
/// Proves the upgrade path really applies the shared base-URL rule (issue #439).
///
/// <para>
/// <strong>What this used to be, and why it changed.</strong> The rule was once hand-duplicated —
/// <c>Odyssey.Api</c> for the <c>PUT</c> path, a private copy inside
/// <c>SystemSettingsConfigAdoption</c> for the migrations job — and this test existed to catch the two
/// drifting apart. The duplication is gone: the rule moved to
/// <see cref="FileAnalysisBaseUrlRule"/> in <c>Odyssey.Dtos</c>, which both halves reach, so
/// drift is now impossible rather than merely detected.
/// </para>
///
/// <para>
/// <strong>The test is kept anyway, because a single implementation does not imply a single
/// behaviour.</strong> Adoption still has to <em>call</em> the rule, in the right place, with the
/// canonicalising convert step wired ahead of the validator — and none of that is guaranteed by there
/// being one copy of the predicate. What this pins is the end-to-end outcome: for one candidate list,
/// the value <c>PUT</c> would accept and the value an upgrade would adopt are the same value. A
/// dropped <c>Convert</c>, a reordered check, or a validator that stopped being invoked all show up
/// here; none of them would show up in a comparison of two predicates.
/// </para>
///
/// <para>
/// The adoption side is driven through the real <c>ExecuteAsync</c> rather than by calling anything
/// directly, which is what makes it an integration check rather than a restatement of the rule.
/// </para>
/// </summary>
public class FileAnalysisBaseUrlParityTests
{
    /// <summary>
    /// The single candidate list both paths are judged against. Anything added here is automatically
    /// asked of both, which is the point — a new case cannot be added to one side only.
    /// </summary>
    public static TheoryData<string> Candidates() => new(CandidateValues);

    private static readonly string[] CandidateValues =
    [
        // Accepted
        "https://api.anthropic.com",
        "https://gateway.internal",
        "https://gateway.internal/",
        "https://127.0.0.1:8443",
        "https://10.0.0.5",
        "https://localhost",
        "  https://gateway.internal  ",
        // Rejected
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
    [MemberData(nameof(Candidates))]
    public async Task BothRules_AgreeOnEveryCandidate(string candidate)
    {
        var apiAccepts = FileAnalysisBaseUrlRule.Validate(candidate) is null;
        var (adoptionAccepts, adoptedValue) = await RunAdoptionAsync(candidate);

        Assert.True(apiAccepts == adoptionAccepts,
            $"The write path and the upgrade path disagree on '{candidate}': the API "
            + $"{(apiAccepts ? "accepts" : "rejects")} it while config adoption "
            + $"{(adoptionAccepts ? "accepts" : "rejects")} it. They share one rule, so this means "
            + "adoption is not applying it — a dropped convert step, a reordered check, or a validator "
            + "that stopped being called. This setting decides which host receives the document and "
            + "the API key.");

        // Agreeing on accept/reject is not enough: they must also agree on what gets STORED, or the
        // same input yields two different rows depending on which path wrote it.
        if (apiAccepts)
        {
            Assert.Equal(FileAnalysisBaseUrlRule.Canonicalize(candidate), adoptedValue);
        }
    }

    /// <summary>
    /// Runs the real adoption step over an in-memory <see cref="OdysseyContext"/> and reports
    /// whether the row moved, and to what.
    ///
    /// <para>
    /// Seeded with a sentinel rather than the shipped default: adoption skips a value that already
    /// equals what is stored, so seeding the default would make a candidate canonicalising <em>to</em>
    /// the default look like a rejection. No candidate in the list canonicalises to the sentinel.
    /// </para>
    /// </summary>
    private static async Task<(bool Accepted, string? Value)> RunAdoptionAsync(string candidate)
    {
        const string sentinel = "https://seeded.sentinel";

        await using var context = new OdysseyContext(
            new DbContextOptionsBuilder<OdysseyContext>()
                .UseInMemoryDatabase($"baseurl-parity-{Guid.NewGuid()}")
                .Options);

        context.SystemSettings.Add(new SystemSetting
        {
            Key = SystemSettingsKeys.FileAnalysisBaseUrl,
            Value = sentinel,
            UpdatedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("FileAnalysis:BaseUrl", candidate)])
            .Build());
        services.AddSingleton<ILogger<ConfigAdoption>>(NullLogger<ConfigAdoption>.Instance);
        services.AddTransient<ConfigAdoption>();

        await services.BuildServiceProvider().GetRequiredService<ConfigAdoption>()
            .ExecuteAsync(CancellationToken.None);

        var stored = (await context.SystemSettings
            .SingleAsync(row => row.Key == SystemSettingsKeys.FileAnalysisBaseUrl)).Value;

        return stored == sentinel ? (false, null) : (true, stored);
    }

    /// <summary>
    /// Guards the guard. If the candidate list ever became all-accept or all-reject, the theory above
    /// would still pass while testing nothing about disagreement.
    /// </summary>
    [Fact]
    public void TheCandidateListCoversBothOutcomes()
    {
        var verdicts = CandidateValues
            .Select(candidate => FileAnalysisBaseUrlRule.Validate(candidate) is null)
            .ToList();

        Assert.Contains(true, verdicts);
        Assert.Contains(false, verdicts);
    }
}
