using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Tests;

/// <summary>
/// A bUnit context for rendering a whole page: MudBlazor, a principal holding the given permissions, and
/// an auto-mock answering every other Odyssey service with an empty success.
/// </summary>
internal static class PageTestContext
{
    public static BunitContext Create(params string[] permissions)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(new Mock<IPageStateService> { DefaultValueProvider = new EmptySuccess() }.Object);
        ctx.Services.AddSingleton(new Mock<IReferenceDataCache> { DefaultValueProvider = new EmptySuccess() }.Object);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedIn(permissions));

        // Everything else a page (or a dialog it keeps mounted) injects is an auto-mock answering with
        // an empty success, so only the list under test is ever in flight.
        ctx.Services.AddFallbackServiceProvider(new AutoMocks());
        return ctx;
    }

    private sealed class AutoMocks : IServiceProvider
    {
        private readonly Dictionary<Type, object> made = [];

        public object? GetService(Type serviceType)
        {
            // Only the app's own services: the framework asks the provider for optional services of
            // its own (a component activator, say) and must keep getting its defaults for those.
            if (!serviceType.IsInterface || serviceType.Namespace?.StartsWith("Odyssey.", StringComparison.Ordinal) != true)
                return null;

            if (!made.TryGetValue(serviceType, out var instance))
            {
                var mock = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(serviceType))!;
                mock.DefaultValueProvider = new EmptySuccess();
                made[serviceType] = instance = mock.Object;
            }

            return instance;
        }
    }

    /// <summary>
    /// A Moq default that answers every <c>Task&lt;ApiResult&lt;T&gt;&gt;</c> with an empty success, rather
    /// than Moq's <c>null</c> result, which the pages' unwrapping would dereference.
    /// </summary>
    public sealed class EmptySuccess : DefaultValueProvider
    {
        protected override object GetDefaultValue(Type type, Mock mock)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [Value(inner)])!;
            }

            return type == typeof(Task) ? Task.CompletedTask : Value(type)!;
        }

        private static object? Value(Type type)
        {
            if (type == typeof(ApiResult))
                return ApiResult.Success(HttpStatusCode.OK);

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ApiResult<>))
            {
                var t = type.GetGenericArguments()[0];
                var value = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) ? Activator.CreateInstance(t) : null;
                return type.GetMethod(nameof(ApiResult<int>.Success))!.Invoke(null, [value, HttpStatusCode.OK, null]);
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    private sealed class SignedIn(IEnumerable<string> permissions) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                permissions.Select(p => new Claim(PermissionClaims.Type, p)), "test"))));
    }
}
