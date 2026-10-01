using Odyssey.Core;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Every exception type <c>Odyssey.Api</c> and <c>Odyssey.Core</c> declare derives from
/// <see cref="DomainException"/>, so <c>GlobalExceptionHandler</c> maps it for every caller (issue #287
/// M11). A parallel <c>: Exception</c> type needs a controller try/catch to become anything but a
/// <c>500</c>, and a caller that does not know about that catch gets the <c>500</c>.
/// </summary>
public class DomainExceptionHierarchyGuardTests
{
    /// <summary>
    /// Deliberately outside the hierarchy: none of these is a client-correctable condition, so the
    /// <c>500</c> the global handler gives a non-domain exception is the intended response.
    /// </summary>
    private static readonly HashSet<string> AllowedNonDomain =
    [
        // An export that cannot be produced as a complete snapshot; logged and surfaced as a generic 500.
        "Odyssey.Core.FileExport.FileExportException",
        // A third-party provider failure, recorded on the analysis job rather than shaped for a client.
        "Odyssey.Core.Finance.FileAnalysisProviderException",
        "Odyssey.Core.Finance.FileAnalysisCredentialException",
    ];

    [Fact]
    public void Every_declared_exception_type_is_a_DomainException_or_explicitly_allowed()
    {
        var offenders = new[] { typeof(Program).Assembly, typeof(DomainException).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(Exception).IsAssignableFrom(type))
            .Where(type => !typeof(DomainException).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .Where(name => !AllowedNonDomain.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Derive these from DomainException (or allow-list them with a reason): " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_allow_listed_type_still_exists_outside_the_hierarchy()
    {
        var declared = new[] { typeof(Program).Assembly, typeof(DomainException).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(Exception).IsAssignableFrom(type) && !typeof(DomainException).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(AllowedNonDomain, name => Assert.Contains(name, declared));
    }
}
