using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Stands in for the <c>RouteGroupBuilder</c> that <c>MapIdentityApi</c> returns, so a convention can
/// be exercised without booting a host.
/// </summary>
/// <remarks>
/// <para>
/// <b>It applies every convention — <c>Finally</c> included — one endpoint at a time</b>, because that
/// is what <c>RouteEndpointDataSource</c> actually does: it walks the group's entries and, for each
/// one, applies the group conventions, then the entry's, then the entry's finally conventions, then
/// the group's finally conventions, and only then moves to the next endpoint.
/// </para>
/// <para>
/// This fake used to run all the <c>Add</c> conventions across all endpoints before any <c>Finally</c>
/// convention, which is the intuitive reading of the name and is wrong. Two startup guards were built
/// on that reading — they accumulated matches in an <c>Add</c> convention and reported the misses from
/// a <c>Finally</c> one — so both reported "route not found" against the first endpoint in the group
/// and logged an error on every single boot of every deployment, while their conventions were in fact
/// applying correctly. The tests passed the whole time. Keep this faithful; a fake that is kinder than
/// the framework is how that shipped.
/// </para>
/// </remarks>
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
            finallyConventions.ForEach(convention => convention(endpoint));
        }

        return endpoints;
    }

    /// <summary>
    /// <see cref="Apply"/>, then <c>Build()</c> — the endpoint set a startup validator sees. Note this
    /// carries metadata across but not filter factories, which the real data source folds into the
    /// request delegate; a validator therefore has to assert on metadata, not on a filter.
    /// </summary>
    public IReadOnlyList<Endpoint> ApplyAndBuild(params string[] routes) =>
        Apply(routes).Select(endpoint => endpoint.Build()).ToList();
}
