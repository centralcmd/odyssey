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
/// <strong>AC 25 was narrowed, and these tests now assert the narrowing.</strong> The ring was
/// originally given to the migrations job as well, so a config-adoption step could protect a secret
/// under keys the API could read. That step has been removed, the job protects nothing, and a second
/// holder of the ring is pure exposure — any container that mounts it can decrypt every stored
/// credential. The assertions below therefore count holders, not occurrences of a string: the base
/// file declares and mounts the ring exactly once, and the production overlay — which is always
/// layered on top of it — adds nothing at all. A re-added mount has to come back with a step that
/// needs it, and changing these numbers is how that gets noticed.
/// </para>
///
/// <para>
/// <strong>The numbers changed once, for issue #348.</strong> The background-job host,
/// <c>Odyssey.Worker</c>, reads the encrypted secret store, so it holds the ring too — <em>read-only</em>,
/// with automatic key generation off (a product decision recorded in docs/deployment.md, "Two containers
/// hold the key ring"). So the counts are now one read-write holder (the API), one read-only holder (the
/// worker), and none anywhere else, the migrations job included.
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
    /// The DEV stack gives the key ring read-write to the <c>api</c> service, read-only to the
    /// <c>worker</c>, and to nothing else: the volume is declared once, and each of the two services
    /// carries the path beside its own mount.
    /// </summary>
    [Fact]
    public void TheDevComposeStack_GivesTheKeyRingToTheApiReadWriteAndTheWorkerReadOnly()
    {
        var compose = RepositoryRoot.ReadAllText("docker-compose.yml");
        var readWrite = $"- dataprotection_keys:{KeysPath}\n";
        var readOnly = $"- dataprotection_keys:{KeysPath}:ro\n";

        Assert.Contains("\n  dataprotection_keys:", compose, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(compose, $"- dataprotection_keys:{KeysPath}"));
        Assert.Equal(2, Occurrences(compose, $"{EnvironmentVariable}: {KeysPath}"));

        var api = ComposeService(compose, "api");
        Assert.Contains(readWrite, api, StringComparison.Ordinal);
        Assert.Contains($"{EnvironmentVariable}: {KeysPath}", api, StringComparison.Ordinal);

        var worker = ComposeService(compose, "worker");
        Assert.Contains(readOnly, worker, StringComparison.Ordinal);
        Assert.DoesNotContain(readWrite, worker, StringComparison.Ordinal);
        Assert.Contains($"{EnvironmentVariable}: {KeysPath}", worker, StringComparison.Ordinal);

        Assert.DoesNotContain("dataprotection_keys:/", ComposeService(compose, "migrations"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The production overlay adds nothing to the key-ring wiring. The base file is the single source
    /// of both the path and the mount, and the overlay is always layered on top of it
    /// (<c>-f docker-compose.yml -f docker-compose.prod.yml</c>, per docs/deployment.md), so the
    /// merged <c>api</c> service inherits both.
    ///
    /// <para>
    /// This assertion is the inverse of what it once was, and the inversion is the point. The overlay
    /// used to repeat <c>DataProtection__KeysPath</c>, which put the path in two files that had to
    /// agree while the mount it has to agree with lived in only one — so an edit to one copy would
    /// have pointed the ring at an unmounted directory and made production silently ephemeral, with
    /// Data Protection meeting that by falling back to an in-memory ring. Re-adding it here restores
    /// that hazard; adding it to a SECOND service is the worse form, since any container that mounts
    /// the ring can decrypt every stored credential.
    /// </para>
    ///
    /// <para>
    /// The mount is guarded in the other direction too: an overlay that <c>!reset</c>s a service's
    /// volumes would drop the ring without ever naming it.
    /// </para>
    /// </summary>
    [Fact]
    public void TheProductionOverlay_AddsNothingToTheKeyRingWiring()
    {
        var overlay = RepositoryRoot.ReadAllText("docker-compose.prod.yml");

        Assert.Equal(0, Occurrences(overlay, $"{EnvironmentVariable}: {KeysPath}"));
        Assert.Equal(0, Occurrences(overlay, $"- dataprotection_keys:{KeysPath}"));
        Assert.DoesNotContain("\n  dataprotection_keys:", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("volumes: !reset", overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// Aspire gets the ring for the same reason — a developer running
    /// <c>dotnet run --project Odyssey.AppHost</c> would otherwise be refused every credential write
    /// with a <c>503</c> and no obvious place to point — on the API resource and the worker only. A host
    /// directory cannot be mounted read-only here; the worker's own <c>DisableAutomaticKeyGeneration</c>
    /// is what keeps it from writing (pinned by <c>Odyssey.Worker.Tests</c>).
    /// </summary>
    [Fact]
    public void TheAspireHost_GivesTheKeysDirectoryToTheApiAndTheWorkerOnly()
    {
        var appHost = RepositoryRoot.ReadAllText(System.IO.Path.Combine("Odyssey.AppHost", "AppHost.cs"));
        var grant = $"WithEnvironment(\"{EnvironmentVariable}\", dataProtectionKeysPath)";

        Assert.Equal(2, Occurrences(appHost, grant));
        Assert.Contains(grant, AspireResource(appHost, "Odyssey_Api"), StringComparison.Ordinal);
        Assert.Contains(grant, AspireResource(appHost, "Odyssey_Worker"), StringComparison.Ordinal);
        Assert.DoesNotContain(grant, AspireResource(appHost, "Odyssey_MigrationService"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Both images create the directory with the right owner at BUILD time. A named volume mounted at
    /// a path absent from the image is created <c>root:root 0755</c> by the daemon, and <c>app</c>
    /// could then not write it — which Data Protection meets by silently falling back to an in-memory
    /// ring.
    /// </summary>
    [Theory]
    [InlineData("Odyssey.Api")]
    // The worker only reads the ring, but if it is the first to mount a fresh volume Docker initialises
    // the volume from its image — so its copy of the directory must be app-owned too.
    [InlineData("Odyssey.Worker")]
    public void TheImage_CreatesTheKeysDirectoryOwnedByTheRuntimeUser(string project)
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
    /// The inverse of the test above, and it now asserts the opposite of what it once did.
    ///
    /// <para>
    /// It used to require that the transport around the credential <em>stayed</em> in configuration
    /// (issue #421 Non-Goal 2, #445 Non-Goal 2), on the argument that the sender connects to the host
    /// and THEN authenticates — so an admin-editable host harvests the relay credential and every
    /// password-reset token. That argument was right, and issue #8 answered it a different way:
    /// changing the host, or turning STARTTLS off, CLEARS the stored credential in the same
    /// transaction. There is nothing left to hand to the new host, so the value became safe to move.
    /// </para>
    ///
    /// <para>
    /// So the assertion is inverted rather than deleted. Re-adding any of these to compose would be a
    /// silent fallback path into a store whose whole point is that configuration cannot reach it —
    /// which is the same defect the retired-keys test above guards, one layer up.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Email__SmtpHost")]
    [InlineData("Email__SmtpPort")]
    [InlineData("Email__UseStartTls")]
    [InlineData("Email__ClientBaseUrl")]
    // FileAnalysis__BaseUrl belongs to the same class and is absent from compose for the same reason:
    // issue #439 made it a setting. It is not listed here only because compose never carried it except
    // as an input to the config-adoption step, so there is no removal to guard. Its protection is not
    // "unreachable from the UI" but the mitigations on the setting itself: the security claim,
    // https-only validation with no path, query, fragment or credentials, the host-only projection
    // every echo goes through, AllowAutoRedirect = false on the outbound client, and the refusal to
    // substitute the compiled default for an unusable stored value.
    public void TheMovedTransportKeys_AreNotPlumbedThroughAnyMore(string key)
    {
        Assert.DoesNotContain($"{key}:", RepositoryRoot.ReadAllText("docker-compose.yml"), StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"{key}:", RepositoryRoot.ReadAllText("docker-compose.prod.yml"), StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 15's other half: the shell-shaped names go too. A compose file that no longer reads
    /// <c>EMAIL_SMTP_HOST</c> while <c>.env.prod.example</c> still asks an operator to set it is worse
    /// than either problem alone — it invites someone to configure a relay that is silently ignored,
    /// and then to believe mail is configured when it is not.
    /// </summary>
    [Theory]
    [InlineData("EMAIL_SMTP_HOST")]
    [InlineData("EMAIL_SMTP_PORT")]
    [InlineData("EMAIL_USE_STARTTLS")]
    [InlineData("EMAIL_CLIENT_BASE_URL")]
    public void TheMovedTransportVariables_AreGoneFromBothEnvTemplates(string shellName)
    {
        foreach (var file in new[] { ".env.example", ".env.prod.example" })
        {
            Assert.DoesNotContain($"{shellName}=", RepositoryRoot.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The <c>Email</c> configuration SECTION is gone from <c>appsettings.json</c> too, so there is no
    /// deploy-time value left for anything to bind or fall back to. Asserted as its own case because
    /// the section is what a reader looking for mail configuration would find first, and an empty
    /// leftover would read as "configure it here".
    /// </summary>
    [Fact]
    public void TheEmailConfigurationSection_IsGoneFromAppsettings()
    {
        Assert.DoesNotContain(
            "\"Email\"", RepositoryRoot.ReadAllText("Odyssey.Api/appsettings.json"), StringComparison.Ordinal);
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

    /// <summary>One top-level service's block in a compose file: from <c>"  name:"</c> to the next service.</summary>
    private static string ComposeService(string compose, string name)
    {
        var start = compose.IndexOf($"\n  {name}:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {name} service in the compose file");
        var lines = compose[(start + 1)..].Split('\n');
        var block = lines.Skip(1).TakeWhile(line => line.Length == 0 || line.StartsWith("   ", StringComparison.Ordinal)
            || line.StartsWith("  #", StringComparison.Ordinal));
        return string.Join('\n', block) + '\n';
    }

    /// <summary>One <c>AddProject&lt;Projects.X&gt;</c> chain in AppHost.cs, up to its terminating semicolon.</summary>
    private static string AspireResource(string appHost, string project)
    {
        var start = appHost.IndexOf($"AddProject<Projects.{project}>", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {project} resource in AppHost.cs");
        return appHost[start..appHost.IndexOf(';', start)];
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
