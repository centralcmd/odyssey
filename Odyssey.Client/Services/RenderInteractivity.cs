namespace Odyssey.Client.Services;

/// <summary>
/// Whether the component is rendering interactively, so a page can skip its data load on a
/// non-interactive (prerender) pass.
/// </summary>
/// <remarks>
/// An injected service rather than a direct <c>OperatingSystem.IsBrowser()</c> call because a bUnit host
/// is not a browser either: a hard check would stop every page at its loading skeleton under test. It
/// replaces the per-component <c>internal static Func&lt;bool&gt; InteractiveCheck</c> seams, which were
/// process-wide, so every test class that moved one had to be serialised against every other. A test
/// registers its own value per context instead.
/// </remarks>
public interface IRenderInteractivity
{
    bool IsInteractive { get; }
}

/// <inheritdoc cref="IRenderInteractivity" />
public sealed class BrowserRenderInteractivity : IRenderInteractivity
{
    public bool IsInteractive => OperatingSystem.IsBrowser();
}
