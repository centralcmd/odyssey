using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Serialises every test class that moves <c>CreateTransactionDialog.InteractiveCheck</c> or
/// <c>FilesSectionBase&lt;ExistingTransactionFile&gt;.InteractiveCheck</c>, or renders a component
/// that reads the latter (<c>TransactionFilesSection</c>, and so <c>TransactionDetailPanel</c>).
/// </summary>
/// <remarks>
/// The same hazard <see cref="TransactionLedgerCollection"/> exists for: both seams are
/// <c>internal static</c>, so process-wide, and a class that moves one restores it on teardown. Run in
/// parallel, one class's teardown would flip the seam under another class's render — a section that
/// loads its files in one run and not the next, which reads as flakiness rather than shared state.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TransactionDialogCollection
{
    public const string Name = "transaction-dialog";
}
