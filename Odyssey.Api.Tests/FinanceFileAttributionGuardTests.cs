using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.Identity;
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
            .SelectMany(method => method.GetCustomAttributes<ProducesResponseTypeAttribute>())
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
