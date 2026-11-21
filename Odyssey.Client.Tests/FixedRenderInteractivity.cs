using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Client.Services;

namespace Odyssey.Client.Tests;

/// <summary>
/// A fixed answer to <see cref="IRenderInteractivity"/>. Registered per bUnit context, so a class that
/// renders non-interactively no longer shares state with one that renders interactively and neither
/// has to be serialised against the other.
/// </summary>
internal sealed class FixedRenderInteractivity(bool isInteractive) : IRenderInteractivity
{
    public bool IsInteractive { get; } = isInteractive;
}

internal static class RenderInteractivityTestExtensions
{
    /// <summary>Registers (or replaces) the context's interactivity answer.</summary>
    public static BunitContext UseInteractivity(this BunitContext ctx, bool isInteractive = true)
    {
        ctx.Services.RemoveAll<IRenderInteractivity>();
        ctx.Services.AddSingleton<IRenderInteractivity>(new FixedRenderInteractivity(isInteractive));
        return ctx;
    }
}
