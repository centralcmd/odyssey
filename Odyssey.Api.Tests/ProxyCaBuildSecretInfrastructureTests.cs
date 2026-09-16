using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The optional build-time CA secret, asserted against the checked-in infrastructure files.
///
/// <para>
/// Where a session's egress is TLS-intercepted, a build container is intercepted too but holds no CA
/// for it, so <c>dotnet restore</c> fails with <c>NU1301 UntrustedRoot</c> and the alpine runtime
/// stage's <c>apk add</c> reports the same trust failure as <c>unable to select packages</c>. Each
/// Dockerfile therefore takes the CA on an optional BuildKit secret, and
/// <c>docker-compose.ca.yml</c> is the opt-in override that supplies it.
/// </para>
///
/// <para>
/// <strong>None of this is reachable from CI, which is exactly why it is pinned here.</strong>
/// <c>e2e.yml</c> builds with a plain <c>docker compose up -d --build</c> and passes no secret,
/// <c>ci.yml</c> builds no images at all, and <c>publish-images.yml</c> passes no secret either — so
/// the conditional branch never executes in automation, on this change or any later one. A
/// regression here would surface only to the next person in a TLS-intercepted session, as the
/// original four-minute <c>UntrustedRoot</c> failure. These assertions turn that into a fast-tier
/// test failure instead.
/// </para>
///
/// <para>
/// What they cannot cover is the runtime half — that BuildKit really does discard a secret and a
/// tmpfs mount rather than committing them to the layer. That is a property of the builder, verified
/// by building the images and searching their layers, and it is the reason the
/// <see cref="NoDockerfile_PromotesTheCaVariableToAnEnv"/> assertion below matters most: an
/// <c>ENV SSL_CERT_FILE</c> is the one edit that would defeat the guarantee while leaving every other
/// assertion here green.
/// </para>
/// </summary>
public class ProxyCaBuildSecretInfrastructureTests
{
    private const string SecretId = "proxy_ca";
    private const string SecretMount = "--mount=type=secret,id=proxy_ca";
    private const string TmpfsMount = "--mount=type=tmpfs,target=/tmp/ca";
    private const string Guard = "if [ -s /run/secrets/proxy_ca ]; then";

    // The COMMAND, not the phrase: every one of these files explains itself in a comment that
    // names `dotnet restore` well above the RUN, so a bare substring search finds the prose and
    // reports the guard as arriving too late.
    private const string RestoreCommand = "\n    dotnet restore";

    public static TheoryData<string> BuildDockerfiles =>
    [
        "Odyssey.Api/Dockerfile",
        "Odyssey.Client/Dockerfile",
        "Odyssey.MigrationService/Dockerfile",
    ];

    /// <summary>
    /// Every image whose build restores NuGet packages carries the guard, and carries it on the
    /// <c>RUN</c> that does the restoring — a secret mounted onto some other step would be inert.
    /// </summary>
    [Theory]
    [MemberData(nameof(BuildDockerfiles))]
    public void EveryBuildStage_GuardsItsRestoreWithTheOptionalCaSecret(string dockerfile)
    {
        var text = RepositoryRoot.ReadAllText(dockerfile);

        var mount = text.IndexOf(SecretMount, StringComparison.Ordinal);
        var tmpfs = text.IndexOf(TmpfsMount, StringComparison.Ordinal);
        var guard = text.IndexOf(Guard, StringComparison.Ordinal);
        var restore = text.IndexOf(RestoreCommand, StringComparison.Ordinal);

        Assert.True(mount >= 0, $"{dockerfile} does not mount the {SecretId} secret.");
        Assert.True(tmpfs >= 0, $"{dockerfile} does not assemble the bundle on a tmpfs.");
        Assert.True(guard >= 0, $"{dockerfile} does not guard on the secret being present.");
        Assert.True(restore >= 0, $"{dockerfile} no longer runs dotnet restore.");

        // Order is the assertion: the mounts open the RUN, the guard sits inside it, and the restore
        // is the command it wraps. A secret declared after the restore would never reach it.
        Assert.True(mount < guard, $"{dockerfile} mounts the secret after the guard.");
        Assert.True(tmpfs < guard, $"{dockerfile} mounts the tmpfs after the guard.");
        Assert.True(guard < restore, $"{dockerfile} restores before the guard runs.");
    }

    /// <summary>
    /// The API's runtime stage installs ICU over the network too, so it needs the same guard — and it
    /// is the one <c>RUN</c> in the set whose layers actually ship.
    /// </summary>
    [Fact]
    public void TheApiRuntimeStage_GuardsItsPackageInstallTheSameWay()
    {
        var text = RepositoryRoot.ReadAllText("Odyssey.Api/Dockerfile");

        var apk = text.IndexOf("apk add --no-cache icu-libs", StringComparison.Ordinal);
        Assert.True(apk >= 0, "The API image no longer installs ICU.");

        // The LAST guard before the apk line is the runtime stage's own; the build stage's guard sits
        // far above it, so a plain Contains() would pass even if this stage had none.
        var guard = text.LastIndexOf(Guard, apk, StringComparison.Ordinal);
        var mount = text.LastIndexOf(SecretMount, apk, StringComparison.Ordinal);

        Assert.True(guard >= 0 && mount >= 0, "The apk install is not guarded by the CA secret.");

        // And it belongs to the FINAL stage rather than an earlier one. Anchoring on the last FROM is
        // what makes that precise: there is no FROM after the runtime stage to search forward to.
        var finalStage = text.LastIndexOf("\nFROM ", StringComparison.Ordinal);
        Assert.True(finalStage >= 0, "The API Dockerfile is no longer multi-stage.");
        Assert.True(
            guard > finalStage,
            "The guard nearest the apk install belongs to an earlier stage, not the runtime stage.");
    }

    /// <summary>
    /// The CA never becomes an image-level variable. <c>SSL_CERT_FILE</c> is exported inside a
    /// <c>RUN</c>'s own shell so it dies with the step; promoting it to an <c>ENV</c> would publish a
    /// session-local trust path in every container started from the image, and would be the single
    /// edit that defeats the no-persistence guarantee without tripping anything else here.
    /// </summary>
    [Theory]
    [MemberData(nameof(BuildDockerfiles))]
    public void NoDockerfile_PromotesTheCaVariableToAnEnv(string dockerfile)
    {
        var text = RepositoryRoot.ReadAllText(dockerfile);

        Assert.DoesNotContain("ENV SSL_CERT_FILE", text, StringComparison.Ordinal);
        Assert.Contains("export SSL_CERT_FILE=", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>RUN --mount</c> is a BuildKit frontend feature, and an unsupported mount flag is a
    /// parse-time error rather than a graceful no-op. Pinning the frontend is what keeps the
    /// ephemeral-mount behaviour these files depend on from varying with whatever version happens to
    /// ship with the local engine.
    /// </summary>
    [Theory]
    [MemberData(nameof(BuildDockerfiles))]
    public void EveryDockerfileUsingMounts_PinsTheBuildKitFrontend(string dockerfile)
    {
        var text = RepositoryRoot.ReadAllText(dockerfile);

        // The directive is only honoured as the very first line of the file.
        Assert.StartsWith("# syntax=docker/dockerfile:1", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The override supplies the secret to exactly the services the base stack builds. A service
    /// named here but not built there is dead wiring; one built there but missing here fails the
    /// build it was meant to fix, several minutes in.
    /// </summary>
    [Fact]
    public void TheCaOverride_SuppliesTheSecretToEveryServiceTheBaseStackBuilds()
    {
        var overrideFile = RepositoryRoot.ReadAllText("docker-compose.ca.yml");
        var baseFile = RepositoryRoot.ReadAllText("docker-compose.yml");

        foreach (var service in new[] { "migrations", "api", "client" })
        {
            Assert.Contains($"\n  {service}:", baseFile, StringComparison.Ordinal);
            Assert.Contains($"\n  {service}:", overrideFile, StringComparison.Ordinal);
        }

        // Declared as a BUILD secret, not a runtime one: a runtime `secrets:` on the service would
        // mount the CA into the running container and never reach the build at all.
        Assert.Equal(3, Occurrences(overrideFile, $"      secrets:\n        - {SecretId}"));
        Assert.Contains($"\n  {SecretId}:\n    file: ", overrideFile, StringComparison.Ordinal);
    }

    /// <summary>
    /// The override stays opt-in. Compose implicitly loads <c>docker-compose.override.yml</c> and
    /// nothing else, so the filename is what keeps an ordinary <c>docker compose up</c> — CI's
    /// included — off a path whose secret file does not exist on that machine.
    /// </summary>
    [Fact]
    public void TheCaOverride_IsNotAFilenameComposeLoadsImplicitly()
    {
        Assert.False(
            File.Exists(Path.Combine(RepositoryRoot.Path, "docker-compose.override.yml")),
            "An implicitly-loaded override now exists; check it does not pull in the CA secret.");

        var workflow = RepositoryRoot.ReadAllText(".github/workflows/e2e.yml");
        Assert.DoesNotContain("docker-compose.ca.yml", workflow, StringComparison.Ordinal);
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
