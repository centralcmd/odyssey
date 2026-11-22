using Odyssey.TestData;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// <see cref="DemoDataSet.Build"/> is the single source of truth for demo data, and its determinism
/// is what every seeded-count assertion in this project rests on — each one compares the database
/// against a <i>freshly built</i> set, so a build that varies makes those tests compare two different
/// datasets and fail with a count mismatch.
/// </summary>
/// <remarks>
/// <para>
/// Determinism used to hold only when nothing else was building at the same time. Three generators
/// seeded Bogus through <c>Randomizer.Seed</c> — a <b>static</b> <see cref="Random"/> shared
/// process-wide — so two concurrent builds reseeded each other mid-generation and interleaved their
/// draws, and the Bogus-driven filler rows came out different. Under xUnit's parallel collections
/// that is routine: a seeder run in one test races another test's own <c>Build()</c>.
/// </para>
/// <para>
/// It surfaced as <c>Seeds_journal_module_dataset</c> failing in CI ("Expected: 19, Actual: 21" on
/// <c>JournalEntryTags</c>) while passing on every developer machine — a scheduling-dependent flake
/// that is invisible to a serial run. Each generator now owns a local <c>Randomizer</c> seeded with
/// the same constant, so the values are unchanged and no build can disturb another.
/// </para>
/// <para>
/// This asserts the property directly rather than re-running the seeder tests and hoping, because
/// the race needs concurrency to appear at all.
/// </para>
/// </remarks>
public class DemoDataDeterminismTests
{
    [Fact]
    public void Concurrent_builds_all_produce_the_same_dataset()
    {
        var baseline = DemoDataSet.Build();

        var concurrent = new DemoDataSet[8];
        Parallel.For(0, concurrent.Length, i => concurrent[i] = DemoDataSet.Build());

        // The three collections fed by a Bogus faker — the only ones the shared randomizer could move.
        Assert.All(concurrent, set => Assert.Equal(baseline.JournalEntries.Count, set.JournalEntries.Count));
        Assert.All(concurrent, set => Assert.Equal(baseline.JournalEntryTags.Count, set.JournalEntryTags.Count));
        Assert.All(concurrent, set => Assert.Equal(baseline.JournalTasks.Count, set.JournalTasks.Count));
        Assert.All(concurrent, set => Assert.Equal(baseline.JournalTaskTagLinks.Count, set.JournalTaskTagLinks.Count));
        Assert.All(concurrent, set => Assert.Equal(baseline.Transactions.Count, set.Transactions.Count));
        Assert.All(concurrent, set => Assert.Equal(baseline.TransactionTagLinks.Count, set.TransactionTagLinks.Count));

        // Counts alone would not catch a build that produced the same number of different rows.
        Assert.All(concurrent, set => Assert.Equal(
            baseline.JournalEntries.Select(e => e.Title),
            set.JournalEntries.Select(e => e.Title)));
        Assert.All(concurrent, set => Assert.Equal(
            baseline.Transactions.Select(t => t.Description),
            set.Transactions.Select(t => t.Description)));
    }
}
