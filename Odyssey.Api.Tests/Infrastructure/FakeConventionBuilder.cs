using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Stands in for the <c>RouteGroupBuilder</c> that <c>MapIdentityApi</c> returns, applying every
/// convention across every endpoint before any <c>Finally</c> convention runs — the ordering the
/// route-matching conventions in <c>IdentityRateLimiting</c> and <c>PasswordResetLogging</c> both
/// depend on for their missing-route report.
/// </summary>
public sealed class FakeConventionBuilder : IEndpointConventionBuilder
{
    private readonly List<Action<EndpointBuilder>> conventions = [];
    private readonly List<Action<EndpointBuilder>> finallyConventions = [];

    public void Add(Action<EndpointBuilder> convention) => conventions.Add(convention);

    public void Finally(Action<EndpointBuilder> finallyConvention) => finallyConventions.Add(finallyConvention);

    public IReadOnlyList<RouteEndpointBuilder> Apply(params string[] routes)
    {
        var endpoints = routes
            .Select(route => new RouteEndpointBuilder(
                _ => Task.CompletedTask, RoutePatternFactory.Parse(route), order: 0))
            .ToList();

        foreach (var endpoint in endpoints)
        {
            conventions.ForEach(convention => convention(endpoint));
        }

        foreach (var endpoint in endpoints)
        {
            finallyConventions.ForEach(convention => convention(endpoint));
        }

        return endpoints;
    }
}
