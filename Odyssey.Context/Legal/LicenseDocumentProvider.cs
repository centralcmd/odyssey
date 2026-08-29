using System.Security.Cryptography;
using System.Text;
using Odyssey.Dtos.Application;

namespace Odyssey.Context.Legal;

/// <summary>
/// Serves the repository <c>LICENSE</c> text and the SHA-256 digest that License acceptance is recorded
/// against (issue #354 §5). Because the digest is derived from the file's content, a License text change
/// ships in an ordinary deploy and automatically invalidates every prior acceptance — there is no
/// version table and no migration step (§2 non-goal 4, AC 11).
/// </summary>
public interface ILicenseDocumentProvider
{
    /// <summary>The license text and its digest. Read from disk once and cached for the process lifetime.</summary>
    LicenseDocument Get();
}

/// <inheritdoc cref="ILicenseDocumentProvider"/>
/// <remarks>
/// <para>
/// It lives here, beside <see cref="LicenseAcceptance"/>, rather than in the API because the demo
/// seeder in <c>Odyssey.MigrationService</c> needs the identical digest to seed compliant acceptance
/// rows. A second copy of this computation would drift silently: a change to the normalisation below
/// would leave seeded rows hashing to something the API no longer recognises, and every seeded login
/// would land on the interstitial with nothing obviously wrong.
/// </para>
/// <para>
/// The file ships next to the assembly: this project includes <c>../LICENSE</c> as content copied to
/// the output directory, which flows to every referencing project — the published API image (§10.12,
/// AC 16), the migration runner, and the test projects. The content-root and repo-walk fallbacks below
/// cover a <c>dotnet run</c> from a source tree where the copy hasn't happened yet.
/// </para>
/// <para>
/// Line endings are normalised to <c>\n</c> before hashing <em>and</em> before serving, so a checkout
/// with CRLF endings computes the same digest as one with LF — otherwise a Windows clone would silently
/// force every user to re-accept — and the served <c>content</c> always hashes to the served
/// <c>sha256</c>.
/// </para>
/// </remarks>
public sealed class LicenseDocumentProvider : ILicenseDocumentProvider
{
    private const string FileName = "LICENSE";

    private readonly Lazy<LicenseDocument> document;

    public LicenseDocumentProvider(string contentRootPath)
    {
        document = new Lazy<LicenseDocument>(
            () => Load(contentRootPath),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public LicenseDocument Get() => document.Value;

    private static LicenseDocument Load(string contentRootPath)
    {
        var path = Resolve(contentRootPath)
            ?? throw new InvalidOperationException(
                $"The '{FileName}' file was not found. It must ship alongside the application (see "
                + "Odyssey.Context.csproj) — License acceptance cannot be computed without it.");

        var content = Normalize(File.ReadAllText(path));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

        return new LicenseDocument { Content = content, Sha256 = digest };
    }

    private static string? Resolve(string contentRootPath)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, FileName),
            Path.Combine(contentRootPath, FileName),
        };

        // Walk up from the content root for a source-tree run where the build copy is absent.
        for (var directory = new DirectoryInfo(contentRootPath); directory is not null; directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, FileName));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string Normalize(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
