using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Keeps the checked-in documentation from drifting away from the tree it describes: every relative link resolves
/// (anchors included), every backticked C# name still exists somewhere in the source or in the framework and
/// libraries it builds on, and every package a shipped project references is named in <c>THIRD-PARTY-NOTICES.md</c>.
/// Prose is free to be wrong in other ways; these are the three kinds of claim a machine can check.
/// </summary>
public sealed partial class DocumentationLintTests
{
    /// <summary>This file names every allow-listed name, so it must not count as the source that defines them.</summary>
    private const string SelfFileName = "DocumentationLintTests.cs";

    private static readonly string[] RootDocuments = ["CLAUDE.md", "README.md", "CONTRIBUTING.md", "SECURITY.md", "SUPPORT.md"];

    /// <summary>
    /// Backticked names the identifier check accepts although no source file contains them. Each is mentioned on
    /// purpose; a name that is merely stale belongs fixed in the document, not here.
    /// </summary>
    private static readonly Dictionary<string, string> AbsentNameAllowList = new(StringComparer.Ordinal)
    {
        ["IApiClient"] = "the retired URL-string facade; the frontend reviewer is told to flag its reintroduction",
        ["OdsIbanField"] = "a hypothetical component the frontend spec template names for illustration, and says so",
        ["ShowMessageBox"] = "a MudBlazor API removed in v9; the gotchas table names its replacement",
        ["MinCharacters"] = "a MudBlazor parameter the gotchas table says to leave unset",
        ["ColGroup"] = "a MudBlazor MudTable slot the gotchas table explains",
        ["UserPreferencesConnection"] = "a removed connection-string key a stale local appsettings file may still carry",
        ["FinanceConnection"] = "a removed connection-string key a stale local appsettings file may still carry",
        ["JournalConnection"] = "a removed connection-string key a stale local appsettings file may still carry",
        ["ApplicationConnection"] = "a removed connection-string key a stale local appsettings file may still carry",
        ["TwoFactorController"] = "a spec item the 2FA implementation notes record as dropped",
        ["ApplicationUser.TwoFactorEnabledAt"] = "a spec item the 2FA implementation notes record as dropped",
        ["AddTwoFactorEnabledAt"] = "a spec migration the 2FA implementation notes record as dropped",
        ["IFileRenameObserver"] = "a retired seam the file-storage architecture note says went with the legacy photo shape",
        ["CreatedWithoutImage"] = "a spec state the images architecture note says is deliberately not implemented",
    };

    private static readonly Lazy<IReadOnlyList<string>> Documents = new(LoadDocuments);

    private static readonly Lazy<HashSet<string>> SourceWords = new(LoadSourceWords);

    private static readonly Lazy<HashSet<string>> ReferencedApiNames = new(LoadReferencedApiNames);

    [Fact]
    public void Every_relative_link_and_import_resolves()
    {
        var violations = new List<string>();
        var headingCache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var document in Documents.Value)
        {
            var directory = Path.GetDirectoryName(document)!;
            var isClaudeFile = Path.GetFileName(document) == "CLAUDE.md";
            foreach (var (lineNumber, line) in ProseLines(document))
            {
                var text = InlineCode().Replace(line, match => new string(' ', match.Length));
                foreach (var target in LinkTargets(text))
                {
                    var problem = CheckLink(document, directory, target, headingCache);
                    if (problem is not null)
                    {
                        violations.Add($"{SourceTree.Relative(document)}:{lineNumber} → {problem}");
                    }
                }

                if (isClaudeFile)
                {
                    var import = ImportLine().Match(line);
                    if (import.Success && !PathExists(Path.Combine(directory, import.Groups["path"].Value)))
                    {
                        violations.Add($"{SourceTree.Relative(document)}:{lineNumber} → import @{import.Groups["path"].Value} does not exist");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, Report("broken relative link(s) or import(s)", violations));
    }

    [Fact]
    public void Every_backticked_code_name_exists_in_the_source()
    {
        var words = new HashSet<string>(SourceWords.Value, StringComparer.Ordinal);
        words.UnionWith(ReferencedApiNames.Value);
        var violations = new List<string>();
        var allowListHits = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in Documents.Value.Where(IsCurrentDocumentation))
        {
            foreach (var (lineNumber, line) in ProseLines(document))
            {
                foreach (Match span in InlineCode().Matches(line))
                {
                    var token = span.Groups["code"].Value.Trim();
                    if (!CodeName().IsMatch(token))
                    {
                        continue;
                    }

                    var name = token.EndsWith("()", StringComparison.Ordinal) ? token[..^2] : token;
                    var missing = name.Split('.').Where(part => !words.Contains(part)).ToList();
                    if (missing.Count == 0)
                    {
                        continue;
                    }

                    if (AbsentNameAllowList.ContainsKey(name))
                    {
                        allowListHits.Add(name);
                        continue;
                    }

                    violations.Add($"{SourceTree.Relative(document)}:{lineNumber} → `{token}`: {string.Join(", ", missing)} not found in any source file");
                }
            }
        }

        violations.AddRange(AbsentNameAllowList.Keys
            .Where(name => !allowListHits.Contains(name))
            .Select(name => $"allow-list entry `{name}` is no longer mentioned, or now exists in the source — remove it"));

        Assert.True(violations.Count == 0, Report("backticked name(s) with no match in the source", violations));
    }

    [Fact]
    public void Every_shipped_package_is_named_in_the_third_party_notices()
    {
        var central = XDocument.Load(SourceTree.File("Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var shipped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in SourceTree.Files(SourceTree.Root, "*.csproj"))
        {
            var references = XDocument.Load(project)
                .Descendants("PackageReference")
                .Select(element => (string?)element.Attribute("Include"))
                .OfType<string>()
                .ToList();
            var isTestProject = Path.GetFileNameWithoutExtension(project).EndsWith("Tests", StringComparison.Ordinal)
                || references.Any(reference => reference.StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
            if (!isTestProject)
            {
                shipped.UnionWith(references.Where(central.Contains));
            }
        }

        var named = PackageName().Matches(SourceTree.ReadAllText("THIRD-PARTY-NOTICES.md"))
            .Select(match => match.Value.TrimEnd('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var violations = shipped
            .Where(package => !IsNamed(package, named))
            .Select(package => $"THIRD-PARTY-NOTICES.md → package {package} is referenced by a shipped project but not named")
            .ToList();

        Assert.NotEmpty(shipped);
        Assert.True(violations.Count == 0, Report("package(s) missing from the notices", violations));
    }

    /// <summary>
    /// A package is named when the notices carry its id, a dotted family id it belongs to (<c>Swashbuckle.AspNetCore</c>
    /// names <c>Swashbuckle.AspNetCore.SwaggerGen</c>) or a <c>Prefix.*</c> wildcard over it.
    /// </summary>
    private static bool IsNamed(string package, HashSet<string> named) =>
        named.Any(name =>
            string.Equals(name, package, StringComparison.OrdinalIgnoreCase)
            || (name.EndsWith(".*", StringComparison.Ordinal) && package.StartsWith(name[..^1], StringComparison.OrdinalIgnoreCase))
            || (name.Contains('.', StringComparison.Ordinal) && package.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)));

    private static string? CheckLink(
        string document, string directory, string target, Dictionary<string, HashSet<string>> headingCache)
    {
        if (UriScheme().IsMatch(target))
        {
            return null;
        }

        var hashIndex = target.IndexOf('#', StringComparison.Ordinal);
        var pathPart = hashIndex < 0 ? target : target[..hashIndex];
        var anchor = hashIndex < 0 ? null : target[(hashIndex + 1)..];
        var queryIndex = pathPart.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            pathPart = pathPart[..queryIndex];
        }

        pathPart = Uri.UnescapeDataString(pathPart);
        string resolved;
        if (pathPart.Length == 0)
        {
            resolved = document;
        }
        else if (pathPart.StartsWith('/'))
        {
            resolved = Path.GetFullPath(Path.Combine(SourceTree.Root, pathPart.TrimStart('/')));
        }
        else
        {
            resolved = Path.GetFullPath(Path.Combine(directory, pathPart));
        }

        if (!PathExists(resolved))
        {
            return $"link target {target} does not exist";
        }

        if (string.IsNullOrEmpty(anchor) || !resolved.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
        {
            return null;
        }

        if (!headingCache.TryGetValue(resolved, out var anchors))
        {
            anchors = Anchors(resolved);
            headingCache[resolved] = anchors;
        }

        return anchors.Contains(Uri.UnescapeDataString(anchor).ToLowerInvariant())
            ? null
            : $"link target {target}: no heading with anchor #{anchor} in {SourceTree.Relative(resolved)}";
    }

    private static HashSet<string> Anchors(string document)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, line) in ProseLines(document))
        {
            foreach (Match explicitAnchor in HtmlAnchor().Matches(line))
            {
                anchors.Add(explicitAnchor.Groups["id"].Value.ToLowerInvariant());
            }

            var heading = Heading().Match(line);
            if (!heading.Success)
            {
                continue;
            }

            var slug = Slug(heading.Groups["text"].Value);
            if (seen.TryGetValue(slug, out var count))
            {
                seen[slug] = count + 1;
                anchors.Add($"{slug}-{count}");
            }
            else
            {
                seen[slug] = 1;
                anchors.Add(slug);
            }
        }

        return anchors;
    }

    /// <summary>GitHub's heading slug: rendered text, lower-cased, punctuation but <c>-</c> and <c>_</c> removed, spaces to <c>-</c>.</summary>
    private static string Slug(string heading)
    {
        var text = MarkdownLink().Replace(heading.Trim(), "${text}");
        text = HtmlTag().Replace(text, string.Empty);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                builder.Append(c);
            }
            else if (c == ' ')
            {
                builder.Append('-');
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> LinkTargets(string text)
    {
        foreach (Match match in InlineLink().Matches(text))
        {
            yield return match.Groups["target"].Value.Trim('<', '>');
        }

        var definition = LinkDefinition().Match(text);
        if (definition.Success)
        {
            yield return definition.Groups["target"].Value;
        }
    }

    /// <summary>Lines outside fenced code blocks, 1-based.</summary>
    private static IEnumerable<(int LineNumber, string Line)> ProseLines(string document)
    {
        string? fence = null;
        var lines = File.ReadAllLines(document);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            var marker = trimmed.StartsWith("```", StringComparison.Ordinal) ? "```"
                : trimmed.StartsWith("~~~", StringComparison.Ordinal) ? "~~~"
                : null;
            if (marker is not null && (fence is null || fence == marker))
            {
                fence = fence is null ? marker : null;
                continue;
            }

            if (fence is null)
            {
                yield return (i + 1, lines[i]);
            }
        }
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Documents that describe the tree as it is. The upgrade notes, the specs and the test-environment spec (a dated
    /// record, as its own banner says) record how it got there, so they legitimately name things that are gone; only
    /// the link check applies to them.
    /// </summary>
    private static bool IsCurrentDocumentation(string document)
    {
        var relative = SourceTree.Relative(document).Replace('\\', '/');
        return relative is not ("docs/upgrade-notes.md" or "docs/test-environment-and-e2e-spec.md")
            && !relative.StartsWith("docs/specs/", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> LoadDocuments() =>
        SourceTree.Files(SourceTree.Root, "*.md")
            .Where(path => !IsVendored(path))
            .Where(path =>
            {
                var relative = SourceTree.Relative(path).Replace('\\', '/');
                var name = Path.GetFileName(path);
                return RootDocuments.Contains(relative)
                    || name is "CLAUDE.md" or "README.md"
                    || relative.StartsWith("docs/", StringComparison.Ordinal)
                    || relative.StartsWith(".claude/", StringComparison.Ordinal);
            })
            .Where(path => Path.GetFileName(path) != "CHANGELOG.md")
            .ToList();

    private static HashSet<string> LoadSourceWords()
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceTree.Files(SourceTree.Root, "*.cs", "*.razor", "*.csproj", "*.props", "*.json", "*.yml", "*.js")
            .Where(path => !IsVendored(path) && Path.GetFileName(path) != SelfFileName))
        {
            foreach (Match word in Word().Matches(File.ReadAllText(file)))
            {
                words.Add(word.Value);
            }
        }

        return words;
    }

    /// <summary>
    /// Type and member names of the shared frameworks the tests run on and of every assembly beside the test binary
    /// (EF Core, Identity and the rest of the dependency graph), read from metadata without loading anything. A
    /// document may name a framework API this repository never spells out (<c>ShouldRender</c>,
    /// <c>NumberFormatInfo.CurrencySymbol</c>); that is not drift.
    /// </summary>
    private static HashSet<string> LoadReferencedApiNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in ReferenceDirectories().SelectMany(directory => Directory.EnumerateFiles(directory, "*.dll")))
        {
            try
            {
                using var stream = File.OpenRead(assembly);
                using var reader = new PEReader(stream);
                if (!reader.HasMetadata)
                {
                    continue;
                }

                var metadata = reader.GetMetadataReader();
                foreach (var handle in metadata.TypeDefinitions)
                {
                    var type = metadata.GetTypeDefinition(handle);
                    names.Add(metadata.GetString(type.Name).Split('`')[0]);
                    names.UnionWith(type.GetMethods().Select(method => metadata.GetString(metadata.GetMethodDefinition(method).Name)));
                    names.UnionWith(type.GetProperties().Select(property => metadata.GetString(metadata.GetPropertyDefinition(property).Name)));
                    names.UnionWith(type.GetFields().Select(field => metadata.GetString(metadata.GetFieldDefinition(field).Name)));
                    names.UnionWith(type.GetEvents().Select(@event => metadata.GetString(metadata.GetEventDefinition(@event).Name)));
                }
            }
            catch (BadImageFormatException)
            {
                // A native library beside the managed ones; it names nothing a document could mean.
            }
        }

        return names;
    }

    private static IEnumerable<string> ReferenceDirectories()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        yield return runtime;
        yield return AppContext.BaseDirectory;

        var aspNetCore = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(runtime)!)!, "Microsoft.AspNetCore.App");
        if (Directory.Exists(aspNetCore))
        {
            var latest = Directory.EnumerateDirectories(aspNetCore)
                .Select(directory => (Directory: directory, Version: Version.TryParse(Path.GetFileName(directory).Split('-')[0], out var version) ? version : null))
                .Where(candidate => candidate.Version is not null)
                .MaxBy(candidate => candidate.Version);
            if (latest.Directory is not null)
            {
                yield return latest.Directory;
            }
        }
    }

    private static bool IsVendored(string path) =>
        SourceTree.Relative(path).Replace('\\', '/').Contains("wwwroot/lib/", StringComparison.Ordinal);

    private static string Report(string what, IReadOnlyCollection<string> violations) =>
        $"{violations.Count} {what}:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}";

    [GeneratedRegex(@"`(?<code>[^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"!?\[(?:[^\[\]]|\[[^\]]*\])*\]\((?<target><[^>]+>|[^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex InlineLink();

    [GeneratedRegex(@"^\s{0,3}\[[^\]]+\]:\s*(?<target>\S+)")]
    private static partial Regex LinkDefinition();

    [GeneratedRegex(@"^\s*@(?<path>[\w./-]+\.md)\s*$")]
    private static partial Regex ImportLine();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex UriScheme();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(?<text>.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"<a\s+(?:name|id)=""(?<id>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAnchor();

    [GeneratedRegex(@"!?\[(?<text>[^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    /// <summary>A PascalCase name of two or more humps (an <c>I</c> interface prefix allowed), optionally <c>Type.Member</c> or a call.</summary>
    [GeneratedRegex(@"^I?[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+(?:\.[A-Z][A-Za-z0-9]+)?(?:\(\))?$")]
    private static partial Regex CodeName();

    [GeneratedRegex(@"[A-Za-z][\w-]*(?:\.[\w-]+)*(?:\.\*)?")]
    private static partial Regex PackageName();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Word();
}
