using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Odyssey.TestShared;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// A calendar colour follows the tag rule (any <c>#RRGGBB</c>, stored upper-case, refused keyed on
/// <c>Color</c> without echoing the value) but is required. These run the service directly, as a non-HTTP
/// caller does, so no <c>[HexColor]</c> or <c>[Required]</c> attribute stands in front of it.
/// </summary>
public class CalendarColorServiceTests
{
    [Fact]
    public async Task Create_and_update_store_any_valid_hex_upper_case()
    {
        await using var context = TestContextFactory.Create();
        var service = new CalendarService(context);

        var created = await service.Create(new NewCalendar { Name = "Hobby", Color = "#c2185b" }, "user");
        Assert.Equal("#C2185B", created.Color);

        var updated = await service.Update(created.CalendarId, new NewCalendar { Name = "Hobby", Color = "#77aa33" }, "user");
        Assert.Equal("#77AA33", updated!.Color);
        Assert.Equal("#77AA33", (await context.Calendars.SingleAsync()).Color);
    }

    public static TheoryData<string?> RefusedColors()
    {
        var data = new TheoryData<string?> { (string?)null };
        foreach (var color in HexColorNegativeCorpus.All)
        {
            data.Add(color);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedColors))]
    public async Task The_service_refuses_an_invalid_or_missing_colour_keyed_on_Color(string? color)
    {
        await using var context = TestContextFactory.Create();
        var service = new CalendarService(context);
        var existing = await service.Create(new NewCalendar { Name = "Existing", Color = "#15803D" }, "user");

        var onCreate = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(new NewCalendar { Name = "New", Color = color! }, "user"));
        var onUpdate = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(existing.CalendarId, new NewCalendar { Name = "Existing", Color = color! }, "user"));

        foreach (var exception in new[] { onCreate, onUpdate })
        {
            Assert.Equal(HexColor.InvalidMessage, exception.Message);
            Assert.Equal([HexColor.InvalidMessage], exception.Errors!["Color"]);
        }

        Assert.Equal("#15803D", (await context.Calendars.SingleAsync()).Color);
    }
}
