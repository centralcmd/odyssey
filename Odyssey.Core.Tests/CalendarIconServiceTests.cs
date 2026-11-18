using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Odyssey.TestShared;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// A calendar's icon follows the album rule: a <see cref="TagIcons"/> key or <c>null</c>, refused keyed on
/// <c>Icon</c> by the service itself (a non-HTTP caller has no <c>[TagIcon]</c> in front of it), and a
/// stored key the catalogue no longer has reads as <c>null</c> on every read path.
/// </summary>
public class CalendarIconServiceTests
{
    [Theory]
    [InlineData("<svg>")]
    [InlineData("not_a_known_key")]
    [InlineData(TagIcons.Default)]
    [InlineData(CalendarIcons.Default)]
    public async Task Create_and_update_refuse_an_unknown_or_default_icon_and_keep_the_stored_one(string icon)
    {
        await using var context = TestContextFactory.Create();
        var service = new CalendarService(context);
        var existing = await service.Create(new NewCalendar { Name = "Work", Color = "#0369A1", Icon = "work" }, "user");

        var onCreate = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(new NewCalendar { Name = "New", Color = "#0369A1", Icon = icon }, "user"));
        var onUpdate = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(existing.CalendarId, new NewCalendar { Name = "Work", Color = "#0369A1", Icon = icon }, "user"));

        foreach (var exception in new[] { onCreate, onUpdate })
        {
            Assert.True(exception.Errors!.ContainsKey("Icon"));
        }

        var stored = Assert.Single(context.Calendars);
        Assert.Equal("work", stored.Icon);
    }

    [Fact]
    public async Task A_retired_stored_key_reads_as_null_on_get_and_list()
    {
        await using var context = TestContextFactory.Create();
        var service = new CalendarService(context);
        var created = await service.Create(new NewCalendar { Name = "Work", Color = "#0369A1", Icon = "work" }, "user");
        Assert.Equal("work", created.Icon);

        var stored = await context.Calendars.SingleAsync();
        stored.Icon = "retired_key";
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.Null((await service.Get(created.CalendarId))!.Icon);
        Assert.Null(Assert.Single((await service.ListAsync(new CalendarsQueryParams())).Items).Icon);
    }
}
