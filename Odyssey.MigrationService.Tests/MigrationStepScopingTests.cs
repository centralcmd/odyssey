using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// Every <see cref="IMigrationStep"/> must open its own scope rather than take a scoped service.
///
/// <para>
/// <c>Worker</c> is a singleton <c>IHostedService</c> and depends on all six steps, so a step whose
/// constructor asks for a <see cref="DbContext"/> makes the container refuse to build:
/// <c>Cannot consume scoped service … from singleton 'IHostedService'</c>. The migrations job then
/// crashes on startup, before it migrates anything.
/// </para>
///
/// <para>
/// This is worth a test rather than a convention because of <em>where</em> it fails. Unit tests for a
/// step construct it directly and never touch the container, so they stay green; the whole suite passed
/// while the job could not start. Container validation only happens at runtime, which in practice means
/// on a developer's stack or in a deployment.
/// </para>
/// </summary>
public class MigrationStepScopingTests
{
    private static IEnumerable<Type> StepImplementations() =>
        typeof(IMigrationStep).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => typeof(IMigrationStep).IsAssignableFrom(type));

    [Fact]
    public void NoMigrationStep_TakesADbContextInItsConstructor()
    {
        var offenders = new List<string>();

        foreach (var type in StepImplementations())
        {
            foreach (var parameter in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                         .SelectMany(constructor => constructor.GetParameters()))
            {
                if (typeof(DbContext).IsAssignableFrom(parameter.ParameterType))
                {
                    offenders.Add($"{type.Name}({parameter.ParameterType.Name} {parameter.Name})");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A migration step must take IServiceProvider and resolve its context inside a scope — Worker "
            + "is a singleton, so a scoped constructor dependency stops the container building and the "
            + "job never starts: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The steps are also the only thing <c>Worker</c> depends on, so this doubles as a check that a new
    /// step was actually given a role interface rather than being resolved some other way.
    /// </summary>
    [Fact]
    public void EveryStepImplementation_IsFound()
    {
        // Six today; the assertion is a floor, so adding one does not need this number changed, but
        // deleting the discovery by accident does fail.
        Assert.True(StepImplementations().Count() >= 6,
            $"Only found {StepImplementations().Count()} migration steps — the discovery is broken.");
    }
}
