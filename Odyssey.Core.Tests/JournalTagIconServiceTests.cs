using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #345 §8: the journal, task and photo tag services store the icon, project it through the
/// catalogue on read, and re-check it themselves for non-HTTP callers — where no <c>[TagIcon]</c>
/// attribute has run first.
/// </summary>
public class JournalTagIconServiceTests
{
    public enum Kind
    {
        Journal,
        Task,
        Photo,
    }

    public static TheoryData<Kind> Kinds => new() { Kind.Journal, Kind.Task, Kind.Photo };

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Create_stores_the_icon_and_update_changes_or_resets_it(Kind kind)
    {
        await using var context = TestContextFactory.Create();
        var tags = Tags.For(kind, context);

        var created = await tags.Create("Holidays", "beach_access");
        Assert.Equal("beach_access", created.Icon);
        Assert.Equal("beach_access", (await tags.Get(created.Id))!.Icon);

        Assert.Equal("flight", (await tags.Update(created.Id, "Holidays", "flight"))!.Icon);
        Assert.Null((await tags.Update(created.Id, "Holidays", null))!.Icon);
        Assert.Null(tags.StoredIcon());
    }

    [Theory]
    [MemberData(nameof(InvalidIcons))]
    public async Task The_service_refuses_an_unknown_icon_for_non_HTTP_callers(Kind kind, string icon)
    {
        await using var context = TestContextFactory.Create();
        var tags = Tags.For(kind, context);
        var existing = await tags.Create("Existing", "home");

        var onCreate = await Assert.ThrowsAsync<DomainValidationException>(() => tags.Create("New", icon));
        var onUpdate = await Assert.ThrowsAsync<DomainValidationException>(() => tags.Update(existing.Id, "Existing", icon));

        foreach (var exception in new[] { onCreate, onUpdate })
        {
            Assert.Equal(TagIcons.InvalidIconMessage, exception.Message);
            Assert.Equal([TagIcons.InvalidIconMessage], exception.Errors!["Icon"]);
        }

        Assert.Equal("home", tags.StoredIcon());
    }

    public static TheoryData<Kind, string> InvalidIcons()
    {
        var data = new TheoryData<Kind, string>();
        foreach (var kind in Enum.GetValues<Kind>())
        {
            foreach (var icon in new[] { "local_offer", "", "  ", "Beach_Access", "<svg>", "not_an_icon" })
            {
                data.Add(kind, icon);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_stored_unknown_key_reads_as_null_on_get_and_list(Kind kind)
    {
        await using var context = TestContextFactory.Create();
        var tags = Tags.For(kind, context);
        var id = tags.SeedRaw("Legacy", "retired_key");

        Assert.Null((await tags.Get(id))!.Icon);
        Assert.Null(Assert.Single(await tags.List()).Icon);
    }

    [Fact]
    public void The_guard_accepts_null_and_catalogue_keys_only()
    {
        TagIconGuard.EnsureKnown(null);
        TagIconGuard.EnsureKnown("beach_access");
        var refused = Assert.Throws<DomainValidationException>(() => TagIconGuard.EnsureKnown(TagIcons.Default));
        Assert.Equal([TagIcons.InvalidIconMessage], refused.Errors!["Icon"]);
    }

    private sealed record Row(Guid Id, string? Icon);

    /// <summary>One adapter per tag type, so every case runs against all three services.</summary>
    private sealed class Tags
    {
        public required Func<string, string?, Task<Row>> Create { get; init; }
        public required Func<Guid, string, string?, Task<Row?>> Update { get; init; }
        public required Func<Guid, Task<Row?>> Get { get; init; }
        public required Func<Task<IReadOnlyList<Row>>> List { get; init; }
        public required Func<string?> StoredIcon { get; init; }
        public required Func<string, string, Guid> SeedRaw { get; init; }

        public static Tags For(Kind kind, OdysseyContext context) => kind switch
        {
            Kind.Journal => Journal(new JournalTagService(context), context),
            Kind.Task => Task(new JournalTaskTagService(context), context),
            _ => Photo(new PhotoTagService(context), context),
        };

        private static Tags Journal(JournalTagService s, OdysseyContext c) => new()
        {
            Create = async (name, icon) => Of(await s.Create(new NewJournalTag { Name = name, Icon = icon })),
            Update = async (id, name, icon) => OfNullable(await s.Update(id, new UpdateJournalTag { Name = name, Icon = icon })),
            Get = async id => OfNullable(await s.Get(id)),
            List = async () => [.. (await s.ListAsync(new JournalTagsQueryParams())).Items.Select(Of)],
            StoredIcon = () => c.JournalTags.Single().Icon,
            SeedRaw = (name, icon) => Seed(c, new JournalTag { Name = name, Icon = icon }, t => t.JournalTagId),
        };

        private static Tags Task(JournalTaskTagService s, OdysseyContext c) => new()
        {
            Create = async (name, icon) => Of(await s.Create(new NewJournalTaskTag { Name = name, Icon = icon })),
            Update = async (id, name, icon) => OfNullable(await s.Update(id, new UpdateJournalTaskTag { Name = name, Icon = icon })),
            Get = async id => OfNullable(await s.Get(id)),
            List = async () => [.. (await s.ListAsync(new JournalTaskTagsQueryParams())).Items.Select(Of)],
            StoredIcon = () => c.JournalTaskTags.Single().Icon,
            SeedRaw = (name, icon) => Seed(c, new JournalTaskTag { Name = name, Icon = icon }, t => t.JournalTaskTagId),
        };

        private static Tags Photo(PhotoTagService s, OdysseyContext c) => new()
        {
            Create = async (name, icon) => Of(await s.Create(new NewPhotoTag { Name = name, Icon = icon })),
            Update = async (id, name, icon) => OfNullable(await s.Update(id, new UpdatePhotoTag { Name = name, Icon = icon })),
            Get = async id => OfNullable(await s.Get(id)),
            List = async () => [.. (await s.ListAsync(new PhotoTagsQueryParams())).Items.Select(Of)],
            StoredIcon = () => c.PhotoTags.Single().Icon,
            SeedRaw = (name, icon) => Seed(c, new PhotoTag { Name = name, Icon = icon }, t => t.PhotoTagId),
        };

        private static Guid Seed<T>(OdysseyContext c, T tag, Func<T, Guid> id) where T : class
        {
            c.Add(tag);
            c.SaveChanges();
            c.ChangeTracker.Clear();
            return id(tag);
        }

        private static Row Of(ExistingJournalTag t) => new(t.JournalTagId, t.Icon);
        private static Row Of(ExistingJournalTaskTag t) => new(t.JournalTaskTagId, t.Icon);
        private static Row Of(ExistingPhotoTag t) => new(t.PhotoTagId, t.Icon);
        private static Row? OfNullable(ExistingJournalTag? t) => t is null ? null : Of(t);
        private static Row? OfNullable(ExistingJournalTaskTag? t) => t is null ? null : Of(t);
        private static Row? OfNullable(ExistingPhotoTag? t) => t is null ? null : Of(t);
    }
}
