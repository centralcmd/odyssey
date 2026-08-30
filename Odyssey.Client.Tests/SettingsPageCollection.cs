using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Serialises every test class that renders the System settings page.
///
/// <para>
/// <strong>Why it is needed.</strong> <c>Settings.InteractiveCheck</c> is an <c>internal static</c>
/// seam — the page's <c>OnInitializedAsync</c> early-returns off-browser, which is false in a bUnit
/// host too, so a render without it stops at the loading skeleton with no rows to assert against.
/// Being static, it is process-wide, and each class that moves it restores it in <c>Dispose</c>.
/// xUnit runs distinct classes in PARALLEL, so one class's teardown could reset the seam while
/// another was still rendering — which showed up as a different test failing on each run, the shape
/// that reads as flakiness rather than as the shared-state bug it is.
/// </para>
///
/// <para>
/// A collection rather than making the seam thread-local or per-instance: the hazard is exactly the
/// process-wide-versus-per-instance one <c>CLAUDE.md</c> records for <c>RequestCapCeilings</c>, and
/// the two classes involved run in about two seconds, so serialising them costs nothing. A third
/// class that renders this page must join this collection.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SettingsPageCollection
{
    public const string Name = "settings-page";
}
