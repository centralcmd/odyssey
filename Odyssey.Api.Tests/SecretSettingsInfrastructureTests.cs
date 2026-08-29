using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The key-ring plumbing, asserted against the checked-in infrastructure files (issue #444 §14, and
/// the static half of ACs 24–26).
///
/// <para>
/// <strong>The dev stack had no Data Protection at all before this issue.</strong> <c>dataprotection_keys</c>
/// and <c>DataProtection__KeysPath</c> appeared only in <c>docker-compose.prod.yml</c>, and only on the
/// <c>api</c> service; <c>docker-compose.yml</c> and <c>AppHost.cs</c> mentioned it nowhere. So every
/// developer machine ran an ephemeral ring — which is exactly why the write-path refusal matters more
/// than a startup warning, and why an acceptance criterion about a dev-stack restart would have had no
/// environment to run in.
/// </para>
///
/// <para>
/// The runtime halves of those criteria (a secret surviving <c>docker compose restart</c>, a
/// cross-host unprotect) need a live stack and a real engine; they live in
/// <c>Odyssey.IntegrationTests</c> and in manual verification. What is pinned here is the wiring those
/// halves depend on, which is text in files no assembly compiles.
/// </para>
/// </summary>
public class SecretSettingsInfrastructureTests
{
    private const string KeysPath = "/var/odyssey/dataprotection-keys";
    private const string EnvironmentVariable = "DataProtection__KeysPath";

    /// <summary>
    /// Both services in the DEV stack carry the key ring, and they share one volume — the migrations
    /// job must derive the same keys as the API, or a future adoption step would write rows the API
    /// can never decrypt.
    /// </summary>
    [Fact]
    public void TheDevComposeStack_GivesBothServicesTheSharedKeyRing()
    {
        var compose = RepositoryRoot.ReadAllText("docker-compose.yml");

        // The volume is declared once, at the bottom, and mounted by both services.
        Assert.Contains("\n  dataprotection_keys:", compose, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(compose, $"- dataprotection_keys:{KeysPath}"));
        Assert.Equal(2, Occurrences(compose, $"{EnvironmentVariable}: {KeysPath}"));
    }

    /// <summary>
    /// The production overlay adds the path to the migrations job. The <c>api</c> service already had
    /// it, and the volume itself now comes from the base file rather than being declared twice.
    /// </summary>
    [Fact]
    public void TheProductionOverlay_GivesTheMigrationsJobTheSameKeyRing()
    {
        var overlay = RepositoryRoot.ReadAllText("docker-compose.prod.yml");

        Assert.Equal(2, Occurrences(overlay, $"{EnvironmentVariable}: {KeysPath}"));

        // Declared in the base file only — a second declaration here would be a different volume.
        Assert.DoesNotContain("\n  dataprotection_keys:", overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// Aspire gets the same treatment, and for the same reason: a developer running
    /// <c>dotnet run --project Odyssey.AppHost</c> would otherwise be refused every credential write
    /// with a <c>503</c> and no obvious place to point.
    /// </summary>
    [Fact]
    public void TheAspireHost_GivesBothResourcesTheSameKeysDirectory()
    {
        var appHost = RepositoryRoot.ReadAllText(System.IO.Path.Combine("Odyssey.AppHost", "AppHost.cs"));

        Assert.Equal(2, Occurrences(appHost, $"WithEnvironment(\"{EnvironmentVariable}\", dataProtectionKeysPath)"));
    }

    /// <summary>
    /// Both images create the directory with the right owner at BUILD time. A named volume mounted at
    /// a path absent from the image is created <c>root:root 0755</c> by the daemon, and <c>app</c>
    /// could then not write it — which Data Protection meets by silently falling back to an in-memory
    /// ring.
    /// </summary>
    [Theory]
    [InlineData("Odyssey.Api")]
    [InlineData("Odyssey.MigrationService")]
    public void BothImages_CreateTheKeysDirectoryOwnedByTheRuntimeUser(string project)
    {
        var dockerfile = RepositoryRoot.ReadAllText(System.IO.Path.Combine(project, "Dockerfile"));

        Assert.Contains($"mkdir -p {KeysPath}", dockerfile, StringComparison.Ordinal);
        Assert.Contains($"chown -R app:app {KeysPath}", dockerfile, StringComparison.Ordinal);

        // …and the chown happens while still root, i.e. BEFORE the USER switch.
        Assert.True(
            dockerfile.IndexOf("chown -R app:app", StringComparison.Ordinal)
                < dockerfile.IndexOf("USER app", StringComparison.Ordinal),
            "The keys directory is chowned after the USER switch, so the chown itself would fail.");
    }

    /// <summary>
    /// The upgrade hazard has a documented one-line remediation. Docker copies image directory
    /// ownership into a named volume ONLY when the volume is empty, so an installation whose
    /// <c>dataprotection_keys</c> volume predates this release is unaffected by the build-time
    /// <c>chown</c> — it has been silently ephemeral, and the new startup assertion turns that into a
    /// refusal to start. That is the right posture, but only if the fix is written down.
    /// </summary>
    [Fact]
    public void TheDeploymentGuide_CarriesTheChownRemediationAndTheBackupGuidance()
    {
        var guide = RepositoryRoot.ReadAllText(System.IO.Path.Combine("docs", "deployment.md"));

        Assert.Contains($"chown -R app:app {KeysPath}", guide, StringComparison.Ordinal);

        // Both directions of the backup rule: back it up, and back it up SEPARATELY.
        Assert.Contains("separately", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("down -v", guide, StringComparison.Ordinal);
        Assert.Contains("Art. 33", guide, StringComparison.Ordinal);
    }

    // ── Issue #445: the retired configuration plumbing ──────────────────────────────────────────

    /// <summary>
    /// AC 17. The five retired keys are gone from every place that could supply them.
    ///
    /// <para>
    /// This is not tidiness. A surviving <c>Email__Password</c> in <c>docker-compose.yml</c> would keep
    /// the plaintext in the process environment — readable by anything that can run
    /// <c>docker inspect</c> or read <c>/proc</c> — which is exactly the exposure the migration exists
    /// to close, and it would do so while the UI showed the credential as stored and rotatable.
    /// </para>
    ///
    /// <para>
    /// Matched with the trailing <c>:</c> so this asserts over the ENV-VAR ASSIGNMENT and not over the
    /// prose explaining why it was removed — those comments necessarily name the key.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("FileAnalysis__ApiKey")]
    [InlineData("Email__Username")]
    [InlineData("Email__Password")]
    [InlineData("Email__RecipientHashKey")]
    [InlineData("Legal__PseudonymizationSecret")]
    public void TheRetiredKeys_AreGoneFromEveryConfigurationSurface(string key)
    {
        foreach (var file in new[] { "docker-compose.yml", "docker-compose.prod.yml" })
        {
            Assert.DoesNotContain($"{key}:", RepositoryRoot.ReadAllText(file), StringComparison.Ordinal);
        }

        var appHost = RepositoryRoot.ReadAllText(System.IO.Path.Combine("Odyssey.AppHost", "AppHost.cs"));
        Assert.DoesNotContain($"\"{key}\"", appHost, StringComparison.Ordinal);

        // The .env templates carry the shell-style names rather than the double-underscore ones.
        var shellName = key switch
        {
            "FileAnalysis__ApiKey" => "FILE_ANALYSIS_API_KEY",
            "Email__Username" => "EMAIL_USERNAME",
            "Email__Password" => "EMAIL_PASSWORD",
            "Email__RecipientHashKey" => "EMAIL_RECIPIENT_HASH_KEY",
            _ => "LEGAL_PSEUDONYMIZATION_SECRET",
        };

        foreach (var file in new[] { ".env.example", ".env.prod.example" })
        {
            Assert.DoesNotContain($"{shellName}=", RepositoryRoot.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The other half, and the reason the test above cannot simply grep for the word "Email": the
    /// transport around the credential deliberately STAYS in configuration (issue #421 Non-Goal 2, and
    /// #445 Non-Goal 2). A change that removed these too would be a security regression dressed as
    /// consistency — the sender connects to the host and THEN authenticates, so an admin-editable host
    /// harvests the relay credential and every password-reset token.
    /// </summary>
    [Theory]
    [InlineData("Email__SmtpHost")]
    [InlineData("Email__SmtpPort")]
    [InlineData("Email__UseStartTls")]
    [InlineData("Email__ClientBaseUrl")]
    [InlineData("FileAnalysis__BaseUrl")]
    public void TheRetainedKeys_AreStillPlumbedThrough(string key)
    {
        Assert.Contains($"{key}:", RepositoryRoot.ReadAllText("docker-compose.yml"), StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 18. The gap between deploying and entering each credential is a designed state, not an edge
    /// case — and for the relay password it means transactional mail stops. An operator has to be able
    /// to read that before they upgrade, not deduce it from a support ticket.
    /// </summary>
    [Fact]
    public void TheDeploymentGuide_CarriesTheCredentialEntryReleaseNote()
    {
        var guide = RepositoryRoot.ReadAllText(System.IO.Path.Combine("docs", "deployment.md"));

        Assert.Contains("Credentials", guide, StringComparison.Ordinal);
        Assert.Contains("not adopted from configuration", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Transactional mail is not sent", guide, StringComparison.OrdinalIgnoreCase);

        // The derivation-key warning, which is the one that cannot be undone by re-entering a value.
        Assert.Contains("permanently un-re-derivable", guide, StringComparison.Ordinal);
        Assert.Contains("Art. 7(1)", guide, StringComparison.Ordinal);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
