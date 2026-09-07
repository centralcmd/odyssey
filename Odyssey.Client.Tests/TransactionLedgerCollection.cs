using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Serialises every test class that moves <c>TransactionListView.InteractiveCheck</c>.
/// </summary>
/// <remarks>
/// The same hazard <see cref="SettingsPageCollection"/> exists for: the seam is
/// <c>internal static</c>, so it is process-wide, and each class that moves it restores it on
/// teardown. xUnit runs distinct classes in parallel, so one class's teardown could reset the seam
/// while another was still rendering — which surfaces as a different test failing on each run, the
/// shape that reads as flakiness rather than as the shared-state bug it is.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TransactionLedgerCollection
{
    public const string Name = "transaction-ledger";
}
