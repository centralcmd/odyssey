using System.Security.Cryptography;
using System.Text;

namespace Odyssey.Core.Finance;

/// <summary>
/// The extraction prompt a run is built with (issue #444 §3). The API stamps <see cref="Version"/> and
/// <see cref="Sha256"/> on a job when it is queued; the worker refuses to send unless its own template
/// hashes to the stamp — so the two hosts can never send a prompt the consent did not name.
/// </summary>
public interface IFileAnalysisPromptTemplate
{
    string Version { get; }

    string Text { get; }

    /// <summary>Lower-case hex SHA-256 of <see cref="Text"/> as UTF-8.</summary>
    string Sha256 { get; }
}

/// <summary>
/// The shipped template, compiled into <c>Odyssey.Core</c> as an embedded resource. Before issue #444 the
/// version and the file path were two configuration keys, so a deployment could pair a version with a
/// file it did not name, and the API and worker could each read their own copy. Compiling both into one
/// assembly is the strongest form of "a version string must not drift from the file it names".
/// </summary>
public sealed class EmbeddedFileAnalysisPromptTemplate : IFileAnalysisPromptTemplate
{
    /// <summary>Bump with every change to <c>Finance/Prompts/transaction-extraction.txt</c>.</summary>
    public const string CurrentVersion = "1.1";

    private const string ResourceName = "Odyssey.Core.Finance.Prompts.transaction-extraction.txt";

    private static readonly Lazy<string> Loaded = new(Load);
    private static readonly Lazy<string> Hash = new(() => HashOf(Loaded.Value));

    public string Version => CurrentVersion;

    public string Text => Loaded.Value;

    public string Sha256 => Hash.Value;

    public static string HashOf(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Load()
    {
        using var stream = typeof(EmbeddedFileAnalysisPromptTemplate).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded prompt template '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
