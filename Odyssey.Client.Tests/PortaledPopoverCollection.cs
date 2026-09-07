using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Serialises every test class that renders a MudBlazor <c>MudPopoverProvider</c> and then dispatches
/// events into the portaled popover.
///
/// <para>
/// <strong>Why it is needed.</strong> The popover's content is rendered outside the component that
/// owns it, and MudBlazor's popover plumbing carries state that outlives a single
/// <c>BunitContext</c> — a context that is never torn down leaves it behind. xUnit runs distinct
/// classes in PARALLEL, so one class's leftover provider could swallow another class's dispatched
/// <c>oninput</c>: the event ran against a stale tree, the search box stayed empty, and the create
/// rows the test was asserting on never appeared. A different test failed on each run, which is the
/// shape that reads as flakiness rather than as the shared-state bug it is.
/// </para>
///
/// <para>
/// A collection rather than a per-test workaround, for the same reason
/// <see cref="SettingsPageCollection"/> exists: the hazard is process-wide, the classes involved run
/// in about a second, and serialising them costs nothing. A class that opens a DS popover and then
/// clicks or types inside it must join this collection.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PortaledPopoverCollection
{
    public const string Name = "portaled-popover";
}
