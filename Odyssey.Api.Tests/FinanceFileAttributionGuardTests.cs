using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using System.Text.RegularExpressions;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Structural guards for issue #106 AC 3: a future field, DTO or controller must not be able to
/// reintroduce a bare user id on a response without failing the build.
/// </summary>
/// <remarks>
/// Three layers, because each alone is escapable.
/// <list type="number">
/// <item>Every file DTO declaring an <c>AttachedByUserId</c> implements <see cref="IAttributedFile"/>,
/// so the family stays enumerable rather than remembered — a sixth file DTO cannot quietly opt out of
/// the one thing that gives it a resolved label.</item>
/// <item>Every controller whose success responses can contain an <see cref="IAttributedFile"/> depends
/// on <see cref="IUserDisplayNameResolver"/>. The set is COMPUTED from the response types, not listed,
/// so a new controller returning contract or renewal files is caught the day it is written.</item>
/// <item>Every <c>…ByUserId</c> property reachable from any controller's success responses has a
/// resolved companion, so a new attribution column on an existing DTO fails here.</item>
/// <item>Every individual ACTION returning such a response actually calls the enricher. The first
/// three layers all work at type granularity — a controller keeps its constructor dependency and its
/// DTOs keep their companions even if one action's call is dropped, so the full suite stays green
/// while that action ships bare ids again. This layer reads the controller SOURCE, because the call
/// is a statement and no reflection over the compiled assembly can see it.</item>
/// </list>
/// </remarks>
public sealed class FinanceFileAttributionGuardTests
{
    /// <summary>
    /// The one attribution id that is resolved under a shape the naming rule cannot see:
    /// <c>FileAnalysisController</c> projects it onto the nested <c>FileAnalysisAuditEntry.User</c>
    /// record (name + email), because that admin surface is <c>file-analysis.audit</c>-gated and shows
    /// the email alongside the name. It IS routed through the resolver. Listed rather than pattern-matched
    /// so the exemption is deliberate, and the test below fails if it stops naming a real property —
    /// an exemption must not outlive the thing it excuses.
    /// </summary>
    private static readonly (string Dto, string Property)[] ResolvedUnderAnotherShape =
    [
        ("FileAnalysisAuditEntry", "RequestedByUserId"),
    ];

    /// <summary>
    /// Accepted companion suffixes. <c>…ByName</c> is the file/journal convention; <c>…ByDisplayName</c>
    /// is the legal module's, and both are populated by the same resolver.
    /// </summary>
    private static readonly string[] CompanionSuffixes = ["Name", "DisplayName"];

    private static Assembly DtosAssembly => typeof(IAttributedFile).Assembly;

    private static IEnumerable<Type> Controllers =>
        typeof(Odyssey.Api.Controllers.AccountController).Assembly
            .GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

    [Fact]
    public void EveryFileDtoWithAnAttacherId_ImplementsIAttributedFile()
    {
        var offenders = DtosAssembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => type.GetProperty("AttachedByUserId", BindingFlags.Public | BindingFlags.Instance) is not null)
            .Where(type => !typeof(IAttributedFile).IsAssignableFrom(type))
            .Select(type => type.Name)
            .Order()
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These DTOs carry an attacher id but do not implement {nameof(IAttributedFile)}, so nothing "
            + "resolves it to a display label and nothing can enumerate them (issue #106): "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryControllerReturningAnAttributedFile_TakesTheDisplayNameResolver()
    {
        var offenders = Controllers
            .Where(ReturnsAttributedFile)
            .Where(controller => !controller
                .GetConstructors()
                .Any(constructor => constructor
                    .GetParameters()
                    .Any(parameter => parameter.ParameterType == typeof(IUserDisplayNameResolver))))
            .Select(controller => controller.Name)
            .Order()
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These controllers return an {nameof(IAttributedFile)} but do not depend on "
            + $"{nameof(IUserDisplayNameResolver)}, so their attribution fields would ship as bare user "
            + "ids (issue #106): " + string.Join(", ", offenders));

        // The set is computed, so an empty one would pass vacuously if the walk ever stopped finding
        // file DTOs at all. Pin the surfaces the issue and its follow-up cover.
        Assert.Equal(
            [
                "AccountController",
                "BudgetController",
                "ContractController",
                "InsuranceController",
                "TaxStatementController",
                "TransactionController",
            ],
            Controllers.Where(ReturnsAttributedFile).Select(c => c.Name).Order().ToArray());
    }

    [Fact]
    public void EveryAttributionId_HasAResolvedNameCompanion()
    {
        var (found, offenders) = WalkAllControllers();

        Assert.True(
            offenders.Count == 0,
            "Every user-id property returned by a controller needs a resolved display-name companion "
            + "(issue #106). Missing: " + string.Join(", ", offenders.Order()));

        // An exemption that no longer names a real property is stale and would silently excuse nothing
        // — or, worse, a renamed field. Fail rather than let it rot.
        foreach (var (dto, property) in ResolvedUnderAnotherShape)
        {
            Assert.True(
                found.Contains($"{dto}.{property}"),
                $"{dto}.{property} is exempted as 'resolved under another shape', but the walk no longer "
                + "reaches it. Remove the exemption or fix the walk.");
        }
    }

    /// <summary>
    /// The enricher call is per ACTION, so it is checked per action — in the source, since a method
    /// body is invisible to reflection. Without this, dropping a single
    /// <c>EnrichFileAttributionAsync</c> line leaves every other guard and every behavioural test on
    /// the OTHER actions passing, which is exactly how a bare id would come back.
    /// </summary>
    [Fact]
    public void EveryActionReturningAnAttributedFile_CallsTheEnricher()
    {
        var offenders = new List<string>();

        foreach (var controller in Controllers.Where(ReturnsAttributedFile))
        {
            var source = ControllerSource(controller);

            foreach (var action in controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => SuccessResponseTypes(method).Any(type => ContainsAttributedFile(type, []))))
            {
                if (!CallsEnricher(source, action, out var reason))
                {
                    offenders.Add($"{controller.Name}.{action.Name} ({reason})");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These actions declare a success response that can carry an attributed file but never call "
            + $"{nameof(FileAttributionExtensions.EnrichFileAttributionAsync)}, so their attribution "
            + "fields ship as bare user ids (issue #106): " + string.Join(", ", offenders.Order()));

        // Same anti-vacuity pin as the layer above: a source scan that stopped matching action bodies
        // would report zero offenders forever.
        Assert.NotEmpty(EnricherCallSites());
    }

    /// <summary>
    /// Every <c>EnrichFileAttributionAsync</c> call site across the guarded controllers, so the scan
    /// above cannot pass by finding nothing at all.
    /// </summary>
    private static IReadOnlyList<string> EnricherCallSites() =>
        Controllers
            .Where(ReturnsAttributedFile)
            .SelectMany(controller => Regex
                .Matches(ControllerSource(controller), "EnrichFileAttributionAsync\\(")
                .Select(match => $"{controller.Name}@{match.Index}"))
            .ToList();

    private static string ControllerSource(Type controller) =>
        RepositoryRoot.ReadAllText(Path.Combine("Odyssey.Api", "Controllers", $"{controller.Name}.cs"));

    /// <summary>
    /// Whether the action's body calls the enricher.
    /// </summary>
    /// <remarks>
    /// Anchored on the action's ROUTE NAME (<c>[HttpGet("{id}", Name = "GetContract")]</c>), never on
    /// the method name: <c>ContractController</c> and four others carry two overloads called
    /// <c>Get</c>, and a name-anchored scan silently reads the list overload's body while reporting on
    /// the by-id one. The route name is unique per action and is what the framework itself keys on.
    /// An action with no route name, or one the scan cannot locate, is reported as an offender rather
    /// than skipped — a guard must never excuse itself.
    /// </remarks>
    private static bool CallsEnricher(string source, MethodInfo action, out string reason)
    {
        var routeName = action
            .GetCustomAttributes<HttpMethodAttribute>()
            .Select(attribute => attribute.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        if (routeName is null)
        {
            reason = "no route name to anchor on; add Name = \"…\" to its [Http…] attribute";
            return false;
        }

        var anchor = source.IndexOf($"Name = \"{routeName}\"", StringComparison.Ordinal);
        if (anchor < 0)
        {
            reason = $"route name {routeName} not found in the source";
            return false;
        }

        // From this action's attribute block to the start of the next action's, which is the whole of
        // its attributes plus its body.
        var rest = source[anchor..];
        var next = Regex.Match(rest, @"^    \[Http", RegexOptions.Multiline);
        var body = next.Success ? rest[..next.Index] : rest;

        reason = "no EnrichFileAttributionAsync call in its body";
        return body.Contains("EnrichFileAttributionAsync(", StringComparison.Ordinal);
    }

    private static bool ReturnsAttributedFile(Type controller)
    {
        var visited = new HashSet<Type>();
        return SuccessResponseTypes(controller).Any(type => ContainsAttributedFile(type, visited));
    }

    private static bool ContainsAttributedFile(Type type, HashSet<Type> visited) =>
        Unwrap(type)
            .Where(IsDtoType)
            .Where(visited.Add)
            .Any(element =>
                typeof(IAttributedFile).IsAssignableFrom(element)
                || element
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Any(property => ContainsAttributedFile(property.PropertyType, visited)));

    private static (HashSet<string> Found, List<string> Offenders) WalkAllControllers()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();
        var visited = new HashSet<Type>();
        var exempt = ResolvedUnderAnotherShape.Select(e => $"{e.Dto}.{e.Property}").ToHashSet(StringComparer.Ordinal);

        foreach (var responseType in Controllers.SelectMany(SuccessResponseTypes).Distinct())
        {
            Inspect(responseType, visited, found, offenders, exempt);
        }

        return (found, offenders);
    }

    // The payload of every 200/201 response each action declares.
    private static IEnumerable<Type> SuccessResponseTypes(Type controller) =>
        controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(SuccessResponseTypes);

    private static IEnumerable<Type> SuccessResponseTypes(MethodInfo action) =>
        action
            .GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Where(attribute =>
                attribute.StatusCode is StatusCodes.Status200OK or StatusCodes.Status201Created
                && attribute.Type is not null)
            .Select(attribute => attribute.Type);

    private static void Inspect(
        Type type,
        HashSet<Type> visited,
        HashSet<string> found,
        List<string> offenders,
        HashSet<string> exempt)
    {
        foreach (var element in Unwrap(type))
        {
            if (!IsDtoType(element) || !visited.Add(element))
            {
                continue;
            }

            var properties = element.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var names = properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var property in properties)
            {
                if (property.Name.EndsWith("ByUserId", StringComparison.Ordinal))
                {
                    var key = $"{element.Name}.{property.Name}";
                    found.Add(key);

                    var stem = property.Name[..^"UserId".Length];
                    var resolved = CompanionSuffixes.Any(suffix => names.Contains(stem + suffix));
                    if (!resolved && !exempt.Contains(key))
                    {
                        offenders.Add($"{key} (expected {stem}Name)");
                    }
                }

                Inspect(property.PropertyType, visited, found, offenders, exempt);
            }
        }
    }

    // Peels Nullable<>, arrays and generics (PagedResult<T>, List<T>, …) down to the payload types, so
    // a DTO nested inside an envelope is still inspected.
    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Unwrap(underlying);
        }

        if (type.IsArray)
        {
            return type.GetElementType() is { } element ? Unwrap(element) : [];
        }

        return type.IsGenericType
            ? [.. type.GetGenericArguments().SelectMany(Unwrap)]
            : [type];
    }

    private static bool IsDtoType(Type type) =>
        !type.IsPrimitive && !type.IsEnum && type.Assembly == DtosAssembly;
}
