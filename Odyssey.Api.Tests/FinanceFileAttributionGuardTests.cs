using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.Controllers;
using Odyssey.Api.Identity;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Structural guards for issue #106 AC 3: a future field must not be able to reintroduce a bare user
/// id on the two finance file surfaces without failing the build.
/// </summary>
/// <remarks>
/// Two halves, because either alone is escapable. The first pins that both controllers still take
/// <see cref="IUserDisplayNameResolver"/> — dropping it is what caused the defect in the first place.
/// The second walks the DTO graph each controller declares as its 200 response and requires every
/// <c>…ByUserId</c> property to have a <c>…ByName</c> companion, so adding a new attribution column to
/// (say) <c>ExistingAccountFile</c> fails here rather than shipping as another harvestable id.
/// </remarks>
public sealed class FinanceFileAttributionGuardTests
{
    private static readonly Type[] GuardedControllers = [typeof(TransactionController), typeof(AccountController)];

    [Theory]
    [InlineData(typeof(TransactionController))]
    [InlineData(typeof(AccountController))]
    public void Controller_TakesTheDisplayNameResolver(Type controller)
    {
        var takesResolver = controller
            .GetConstructors()
            .Any(constructor => constructor
                .GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(IUserDisplayNameResolver)));

        Assert.True(
            takesResolver,
            $"{controller.Name} must depend on {nameof(IUserDisplayNameResolver)} so its attribution "
            + "fields are resolved to a claim-conditional label instead of returning a bare user id (issue #106).");
    }

    [Fact]
    public void EveryAttributionIdOnTheseSurfaces_HasAResolvedNameCompanion()
    {
        var (found, offenders) = WalkGuardedSurfaces();

        Assert.True(
            offenders.Count == 0,
            "Every user-id property returned by the transaction and account surfaces needs a resolved "
            + "display-name companion (issue #106). Missing: " + string.Join(", ", offenders.Order()));

        // A structural guard that stops reaching the fields it protects keeps passing forever, so pin
        // the three the issue names. If one is renamed or moved off these surfaces, update this list
        // deliberately rather than letting the walk go quietly empty.
        Assert.Equal(
            [
                "ExistingAccountFile.AttachedByUserId",
                "ExistingFileMetadata.UploadedByUserId",
                "ExistingTransactionFile.AttachedByUserId",
            ],
            found.Order().ToArray());
    }

    private static (List<string> Found, List<string> Offenders) WalkGuardedSurfaces()
    {
        var found = new List<string>();
        var offenders = new List<string>();
        var visited = new HashSet<Type>();

        foreach (var responseType in GuardedControllers.SelectMany(ResponseTypes).Distinct())
        {
            Inspect(responseType, visited, found, offenders);
        }

        return (found, offenders);
    }

    // The 200-response payload of every action on the controller, as its ProducesResponseType declares it.
    private static IEnumerable<Type> ResponseTypes(Type controller) =>
        controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetCustomAttributes<ProducesResponseTypeAttribute>())
            .Where(attribute => attribute.StatusCode == StatusCodes.Status200OK && attribute.Type is not null)
            .Select(attribute => attribute.Type);

    private static void Inspect(Type type, HashSet<Type> visited, List<string> found, List<string> offenders)
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
                    found.Add($"{element.Name}.{property.Name}");
                    var companion = string.Concat(property.Name.AsSpan(0, property.Name.Length - "UserId".Length), "Name");
                    if (!names.Contains(companion))
                    {
                        offenders.Add($"{element.Name}.{property.Name} (expected {companion})");
                    }
                }

                Inspect(property.PropertyType, visited, found, offenders);
            }
        }
    }

    // Peels Nullable<>, arrays and single-argument generics (PagedResult<T>, List<T>, …) down to the
    // payload types, so a DTO nested inside an envelope is still inspected.
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
        !type.IsPrimitive
        && !type.IsEnum
        && type.Assembly == typeof(Odyssey.Dtos.Finance.ExistingTransactionFile).Assembly;
}
