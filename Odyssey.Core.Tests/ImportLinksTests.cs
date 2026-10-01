using Odyssey.Core.Journal.Interop;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The link rules the journal-entry and task calendar imports share (issue #287 M8), pinned directly
/// rather than only through the two imports that use them.
/// </summary>
public class ImportLinksTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    [Fact]
    public void Resolve_KeepsFileOrder_AndCountsUnresolvedAndCappedSeparately()
    {
        var (unresolved, capped) = (0, 0);

        var resolved = ImportLinks.Resolve(
            new Guid?[] { B, null, A, C }, id => id, maxLinksPerKind: 2,
            onUnresolved: () => unresolved++, onCapped: () => capped++);

        Assert.Equal([B, A], resolved);
        Assert.Equal(1, unresolved);
        Assert.Equal(1, capped);
    }

    [Fact]
    public void Resolve_DropsADuplicateSilently_EvenAtTheCap()
    {
        var (unresolved, capped) = (0, 0);

        var resolved = ImportLinks.Resolve(
            new Guid?[] { A, B, A, B }, id => id, maxLinksPerKind: 2,
            onUnresolved: () => unresolved++, onCapped: () => capped++);

        Assert.Equal([A, B], resolved);
        Assert.Equal(0, unresolved);
        Assert.Equal(0, capped);
    }

    [Fact]
    public void Replace_RemovesStaleLinks_AddsMissingOnes_AndKeepsTheRest()
    {
        var kept = new Link(A);
        var links = new List<Link> { kept, new(B) };

        ImportLinks.Replace(links, [A, C], link => link.Target, id => new Link(id));

        Assert.Equal([A, C], links.Select(link => link.Target));
        Assert.Same(kept, links[0]);
    }

    [Fact]
    public void Replace_WithNothingDesired_EmptiesTheCollection()
    {
        var links = new List<Link> { new(A), new(B) };

        ImportLinks.Replace(links, [], link => link.Target, id => new Link(id));

        Assert.Empty(links);
    }

    [Theory]
    [InlineData("entry", 5, "Links over the per-entry cap of 5 were not imported.")]
    [InlineData("task", 2, "Links over the per-task cap of 2 were not imported.")]
    public void LinksCappedReason_NamesTheRecordAndTheEffectiveCap(string noun, int cap, string expected) =>
        Assert.Equal(expected, ImportLinks.LinksCappedReason(noun, cap));

    private sealed record Link(Guid Target);
}
