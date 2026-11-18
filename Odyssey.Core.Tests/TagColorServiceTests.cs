using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.TestShared;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #392: every tag service re-checks the colour itself, for non-HTTP callers that no
/// <c>[HexColor]</c> attribute has run in front of, and stores it normalised.
/// </summary>
public class TagColorServiceTests
{
    public enum Kind
    {
        Transaction,
        Journal,
        Task,
        Photo,
    }

    public static TheoryData<Kind> Kinds => new() { Kind.Transaction, Kind.Journal, Kind.Task, Kind.Photo };

    public static TheoryData<Kind, string> InvalidColors()
    {
        var data = new TheoryData<Kind, string>();
        foreach (var kind in Enum.GetValues<Kind>())
        {
            foreach (var color in HexColorNegativeCorpus.All)
            {
                data.Add(kind, color);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidColors))]
    public async Task The_service_refuses_an_invalid_colour_on_create_and_update(Kind kind, string color)
    {
        await using var context = TestContextFactory.Create();
        var tags = Tags.For(kind, context);
        var existing = await tags.Create("Existing", "#15803d");

        var onCreate = await Assert.ThrowsAsync<DomainValidationException>(() => tags.Create("New", color));
        var onUpdate = await Assert.ThrowsAsync<DomainValidationException>(() => tags.Update(existing, "Existing", color));

        foreach (var exception in new[] { onCreate, onUpdate })
        {
            Assert.Equal(HexColor.InvalidMessage, exception.Message);
            Assert.Equal([HexColor.InvalidMessage], exception.Errors!["Color"]);
        }

        Assert.Equal("#15803D", tags.StoredColor());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_colour_is_stored_upper_case_and_an_update_without_one_clears_it(Kind kind)
    {
        await using var context = TestContextFactory.Create();
        var tags = Tags.For(kind, context);

        var id = await tags.Create("Groceries", "#b23b3b");
        Assert.Equal("#B23B3B", tags.StoredColor());

        await tags.Update(id, "Groceries", "#abcdef");
        Assert.Equal("#ABCDEF", tags.StoredColor());

        await tags.Update(id, "Groceries", null);
        Assert.Null(tags.StoredColor());
    }

    /// <summary>One adapter per tag type, so every case runs against all four services.</summary>
    private sealed class Tags
    {
        public required Func<string, string?, Task<Guid>> Create { get; init; }
        public required Func<Guid, string, string?, Task> Update { get; init; }
        public required Func<string?> StoredColor { get; init; }

        public static Tags For(Kind kind, OdysseyContext c) => kind switch
        {
            Kind.Transaction => Transaction(new TransactionTagService(c), c),
            Kind.Journal => Journal(new JournalTagService(c), c),
            Kind.Task => Task(new JournalTaskTagService(c), c),
            _ => Photo(new PhotoTagService(c), c),
        };

        private static string? Stored(OdysseyContext c, Func<OdysseyContext, string?> read)
        {
            c.ChangeTracker.Clear();
            return read(c);
        }

        private static Tags Transaction(TransactionTagService s, OdysseyContext c) => new()
        {
            Create = async (name, color) => (await s.Create(new NewTransactionTag { Name = name, Archived = false, Color = color })).TransactionTagId,
            Update = (id, name, color) => s.Update(id, new NewTransactionTag { Name = name, Archived = false, Color = color }),
            StoredColor = () => Stored(c, x => x.TransactionTags.Single().Color),
        };

        private static Tags Journal(JournalTagService s, OdysseyContext c) => new()
        {
            Create = async (name, color) => (await s.Create(new NewJournalTag { Name = name, Color = color })).JournalTagId,
            Update = (id, name, color) => s.Update(id, new UpdateJournalTag { Name = name, Color = color }),
            StoredColor = () => Stored(c, x => x.JournalTags.Single().Color),
        };

        private static Tags Task(JournalTaskTagService s, OdysseyContext c) => new()
        {
            Create = async (name, color) => (await s.Create(new NewJournalTaskTag { Name = name, Color = color })).JournalTaskTagId,
            Update = (id, name, color) => s.Update(id, new UpdateJournalTaskTag { Name = name, Color = color }),
            StoredColor = () => Stored(c, x => x.JournalTaskTags.Single().Color),
        };

        private static Tags Photo(PhotoTagService s, OdysseyContext c) => new()
        {
            Create = async (name, color) => (await s.Create(new NewPhotoTag { Name = name, Color = color })).PhotoTagId,
            Update = (id, name, color) => s.Update(id, new UpdatePhotoTag { Name = name, Color = color }),
            StoredColor = () => Stored(c, x => x.PhotoTags.Single().Color),
        };
    }
}
