namespace Odyssey.Api;

/// <summary>
/// The startup assertion behind the Data Protection keys volume (issue #444 §10).
///
/// <para>
/// <strong>Why an assertion rather than restrictive permissions.</strong> <c>0700</c> cannot be
/// asserted from the compose file: both images run <c>USER app</c>, and a named volume mounted at a
/// path absent from the image is created <c>root:root 0755</c> by the daemon. The images therefore
/// create the directory with the right owner at build time — but Docker copies image directory
/// ownership into a named volume <em>only when the volume is empty</em>, so an installation whose
/// <c>dataprotection_keys</c> volume already exists is unaffected by the build-time <c>chown</c> and
/// may be silently running an ephemeral ring today.
/// </para>
///
/// <para>
/// That is exactly the case this turns loud: such a deployment now refuses to start on upgrade, with
/// the one-line remediation in <c>docs/deployment.md</c> and the release notes
/// (<c>docker compose run --rm --user root api chown -R app:app /var/odyssey/dataprotection-keys</c>).
/// A loud stop is the right trade against a silently-ephemeral key ring holding credentials.
/// </para>
/// </summary>
internal static class DataProtectionKeyDirectory
{
    /// <summary>
    /// Creates the directory if it is missing and proves it is writable, throwing otherwise. Only ever
    /// called for an explicitly configured path — an unconfigured one is a legitimate dev default and
    /// is covered by the write-path refusal instead.
    /// </summary>
    public static void EnsureWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            // A probe write, not a permission-bit inspection: the effective answer depends on
            // ownership, mode, ACLs, SELinux labels and the mount's own read-only flag, and only an
            // actual write covers all five. A read-only mount is the failure this most needs to catch
            // — Data Protection meets it by falling back to an in-memory ring with a log line.
            var probe = Path.Combine(path, $".odyssey-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The configured Data Protection keys directory '{path}' is not writable, so the key "
                + "ring would silently fall back to an in-memory one — logging every user out on each "
                + "restart and making any stored credential unrecoverable. Fix the directory's "
                + "ownership and mount, then restart. On Docker Compose, an existing keys volume does "
                + "not inherit the image's build-time ownership; run: docker compose run --rm "
                + "--user root api chown -R app:app " + path,
                exception);
        }
    }
}
