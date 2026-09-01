using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// A source-lint asserting <c>IContactMutationLock</c> stays retired (issue #27 §5, AC #33).
/// </summary>
/// <remarks>
/// <para>
/// The lock existed to serialize the insurance write path against a contact delete, back when the
/// foreign key from a policy to its insurer had been removed. That key is back — as three of them, on
/// the new link tables — so the DATABASE arbitrates the race the lock was written for, and its
/// violation maps to a <c>409</c> rather than surfacing as a <c>500</c>. Removing the insurance call
/// sites left it a mutex with no counterparty, still taking a pinned connection and a blocking
/// ten-second <c>GET_LOCK</c> on every contact delete.
/// </para>
/// <para>
/// A lint rather than a behavioural test because the failure mode is <b>reintroduction by merge</b>: a
/// branch written against the old signature compiles the moment the type comes back, and nothing else
/// would notice. It lives in this project because this is where the repository's source-lints live —
/// the subject is the whole solution, not the client, so it reads through
/// <see cref="ClientSource.Sibling"/>.
/// </para>
/// </remarks>
public class ContactMutationLockRetiredTests
{
    private static readonly string[] ProjectsThatCouldHostIt =
    [
        "Odyssey.Core", "Odyssey.Api", "Odyssey.Context", "Odyssey.Client", "Odyssey.ApiClient",
    ];

    [Fact]
    public void The_contact_mutation_lock_is_gone_from_the_application_source()
    {
        var offenders = ApplicationSourceFiles()
            .Where(file => File.ReadAllText(file).Contains("ContactMutationLock", StringComparison.Ordinal))
            .Select(RelativeToRepo)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "IContactMutationLock, ContactMutationLock, its no-op default and its DI registration were "
            + "deleted in issue #27 §5: the Restrict foreign keys on the three contact link tables "
            + "arbitrate the race it was written for, and with the insurance call sites gone it was a "
            + "mutex with no counterparty. Reintroducing it costs a pinned connection and a blocking "
            + "10-second GET_LOCK per contact delete. Found in: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The other half, stated in terms of what the lock actually did: no advisory lock is taken on any
    /// path, whatever it might be called next time.
    /// </summary>
    [Fact]
    public void No_advisory_lock_is_acquired_anywhere_in_the_application_source()
    {
        var offenders = ApplicationSourceFiles()
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("GET_LOCK", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("RELEASE_LOCK", StringComparison.OrdinalIgnoreCase);
            })
            .Select(RelativeToRepo)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "No application path takes a MariaDB advisory lock. Found in: " + string.Join(", ", offenders));
    }

    private static IEnumerable<string> ApplicationSourceFiles() =>
        ProjectsThatCouldHostIt
            .Select(ClientSource.Sibling)
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file));

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string RelativeToRepo(string file) =>
        Path.GetRelativePath(Path.GetDirectoryName(ClientSource.Root)!, file);
}
