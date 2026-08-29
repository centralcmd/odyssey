using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The four tag resources share one client closed over their row type and bound to their route at
/// registration. These pin that the binding actually reaches the right route — a
/// <c>ITagsApiClient&lt;ExistingPhotoTag&gt;</c> wired to <c>api/journal-tags</c> would compile,
/// deserialize, and quietly write to the wrong resource.
/// </summary>
public class TagsApiClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public HttpResponseMessage Response { get; set; } =
            new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[],"offset":0,"limit":50,"totalCount":0}""",
                                            System.Text.Encoding.UTF8, "application/json"),
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    private static (ITagsApiClient<T> Client, RecordingHandler Handler) Create<T>(string basePath)
    {
        var handler = new RecordingHandler();
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new TagsApiClient<T>(api, basePath), handler);
    }

    [Theory]
    [InlineData("api/journal-tags")]
    [InlineData("api/task-tags")]
    [InlineData("api/photo-tags")]
    public async Task Every_operation_stays_on_the_route_the_client_was_bound_to(string basePath)
    {
        var id = Guid.NewGuid();
        var tag = new TagWrite("Rent", "Monthly", Archived: false);

        var (client, handler) = Create<ExistingJournalTag>(basePath);

        await client.ListAllAsync();
        Assert.Equal($"/{basePath}", handler.LastRequest!.RequestUri!.AbsolutePath);

        await client.ListAsync(1, 25);
        Assert.Equal($"/{basePath}", handler.LastRequest!.RequestUri!.AbsolutePath);

        handler.Response = new HttpResponseMessage(HttpStatusCode.Created);
        await client.CreateAsync(tag);
        Assert.Equal($"/{basePath}", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);

        handler.Response = new HttpResponseMessage(HttpStatusCode.NoContent);
        await client.UpdateAsync(id, tag);
        Assert.Equal($"/{basePath}/{id}", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);

        await client.DeleteAsync(id);
        Assert.Equal($"/{basePath}/{id}", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
    }

    [Fact]
    public async Task ListAllAsync_requests_the_full_window()
    {
        var (client, handler) = Create<ExistingJournalTag>("api/journal-tags");

        await client.ListAllAsync();

        Assert.Contains($"offset=0&limit={PagedQuery.LimitAll}", handler.LastRequest!.RequestUri!.Query);
    }

    // Status is the Active/Archived two-value multiselect: filter only when exactly one is chosen.
    [Theory]
    [InlineData("", false)]
    [InlineData("Active,Archived", false)]
    [InlineData("Archived", true)]
    public async Task ListAsync_filters_status_only_when_one_is_selected(string selected, bool expectFilter)
    {
        var (client, handler) = Create<ExistingJournalTag>("api/journal-tags");

        await client.ListAsync(1, 25, status: selected.Split(',', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(expectFilter, handler.LastRequest!.RequestUri!.Query.Contains("status="));
    }

    /// <summary>
    /// The real guard. The four tag clients are distinguished only by the base path handed to them at
    /// registration, so the failure mode is a wiring mistake in <c>AddOdysseyApiClient()</c> — a client
    /// closed over one tag type pointed at another's route. That compiles, deserializes, and silently
    /// writes to the wrong resource.
    /// </summary>
    /// <remarks>
    /// This resolves through the real container rather than constructing the client directly. An
    /// earlier version of this file hand-built <c>TagsApiClient&lt;T&gt;</c> and therefore proved
    /// nothing about the registrations: swapping the task-tag and photo-tag bindings left the whole
    /// suite green.
    /// </remarks>
    [Theory]
    [InlineData(typeof(ExistingTransactionTag), "/api/transaction-tags")]
    [InlineData(typeof(ExistingJournalTag), "/api/journal-tags")]
    [InlineData(typeof(ExistingJournalTaskTag), "/api/task-tags")]
    [InlineData(typeof(ExistingPhotoTag), "/api/photo-tags")]
    public async Task The_registered_client_for_each_tag_type_targets_that_type_s_route(Type tagType, string expectedPath)
    {
        var handler = new RecordingHandler();
        var services = new ServiceCollection();
        services.AddOdysseyApiClient();
        services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService(typeof(ITagsApiClient<>).MakeGenericType(tagType));
        var listAll = client.GetType().GetMethod(nameof(ITagsApiClient<object>.ListAllAsync))!;
        await (Task)listAll.Invoke(client, [null, CancellationToken.None])!;

        Assert.Equal(expectedPath, handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// <see cref="TagWrite"/> stands in for seven per-resource DTOs. It is only a valid substitute while
    /// they all agree on their limits — so this asserts against the real DTOs rather than restating
    /// the numbers. If one resource ever diverges, this fails and the generic client should be split.
    /// </summary>
    [Theory]
    [InlineData(typeof(NewTransactionTag))]
    [InlineData(typeof(NewJournalTag))]
    [InlineData(typeof(NewJournalTaskTag))]
    [InlineData(typeof(NewPhotoTag))]
    [InlineData(typeof(UpdateJournalTag))]
    [InlineData(typeof(UpdateJournalTaskTag))]
    [InlineData(typeof(UpdatePhotoTag))]
    public void TagWrite_limits_match_the_DTOs_it_substitutes_for(Type dto)
    {
        Assert.Equal(TagWrite.MaxNameLength, MaxLengthOf(dto, "Name"));
        Assert.Equal(TagWrite.MaxDescriptionLength, MaxLengthOf(dto, "Description"));

        static int MaxLengthOf(Type type, string property) =>
            type.GetProperty(property)!.GetCustomAttribute<StringLengthAttribute>()!.MaximumLength;
    }

    [Fact]
    public void TagWrite_requires_a_name()
    {
        var required = typeof(TagWrite).GetProperty(nameof(TagWrite.Name))!
            .GetCustomAttribute<RequiredAttribute>();

        Assert.NotNull(required);
    }
}
