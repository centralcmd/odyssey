using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.TestShared;
using Xunit;
using CalendarEntity = Odyssey.Context.Calendar;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #392 over real HTTP: the colour on all four tag types' write and read paths, the record-level
/// <c>displayColor</c> that comes from the tag supplying the icon, and the shared palette on calendars.
/// </summary>
public class TagColorApiTests
{
    private const string ActorUserId = "tag-color-actor";

    public enum Kind
    {
        Transaction,
        Journal,
        Task,
        Photo,
    }

    public static TheoryData<Kind> Kinds => new() { Kind.Transaction, Kind.Journal, Kind.Task, Kind.Photo };

    public static TheoryData<Kind, string> KindsByInvalidColor()
    {
        var data = new TheoryData<Kind, string>();
        foreach (var kind in new[] { Kind.Transaction, Kind.Journal, Kind.Task, Kind.Photo })
        {
            foreach (var value in HexColorNegativeCorpus.All)
            {
                data.Add(kind, value);
            }
        }

        return data;
    }

    // ── Tag endpoints ──────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Post_with_a_lower_case_hex_stores_and_returns_it_upper_case(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(TagAdmin(kind), ActorUserId);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(TagPath(kind), new { name = "Holidays", archived = false, color = "#15803d" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // The transaction-tag POST answers 201 with an empty body (unchanged); the others echo the tag.
        if (kind != Kind.Transaction)
        {
            Assert.Equal("#15803D", await ColorOf(response));
        }
        Assert.Equal("#15803D", await StoredColorAsync(factory, kind));
        using var read = JsonDocument.Parse(await client.GetStringAsync(response.Headers.Location!));
        Assert.Equal("#15803D", read.RootElement.GetProperty("color").GetString());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Post_without_a_color_stores_null(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(TagAdmin(kind), ActorUserId);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(TagPath(kind), new { name = "Holidays", archived = false });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // The transaction-tag POST answers 201 with an empty body (unchanged); the others echo the tag.
        if (kind != Kind.Transaction)
        {
            Assert.Null(await ColorOf(response));
        }
        Assert.Null(await StoredColorAsync(factory, kind));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Put_changes_the_color_and_an_omitted_color_clears_it(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(TagAdmin(kind), ActorUserId);
        var id = await SeedTagAsync(factory, kind, "Holidays", "beach_access", "#0369A1");
        using var client = factory.CreateClient();

        var changed = await client.PutAsJsonAsync($"{TagPath(kind)}/{id}", new { name = "Holidays", archived = false, icon = "beach_access", color = "#c2185b" });
        Assert.True(changed.IsSuccessStatusCode, changed.StatusCode.ToString());
        Assert.Equal("#C2185B", await StoredColorAsync(factory, kind));

        var cleared = await client.PutAsJsonAsync($"{TagPath(kind)}/{id}", new { name = "Holidays", archived = false, icon = "beach_access" });
        Assert.True(cleared.IsSuccessStatusCode, cleared.StatusCode.ToString());
        Assert.Null(await StoredColorAsync(factory, kind));
    }

    [Theory]
    [MemberData(nameof(KindsByInvalidColor))]
    public async Task An_invalid_color_is_a_400_keyed_on_Color_that_echoes_nothing_and_persists_nothing(Kind kind, string color)
    {
        await using var factory = new OdysseyApiFactory(TagAdmin(kind), ActorUserId);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(TagPath(kind), new { name = "Holidays", archived = false, color });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        var errors = body.RootElement.GetProperty("errors").GetProperty("Color");
        Assert.Equal(HexColor.InvalidMessage, Assert.Single(errors.EnumerateArray().ToList()).GetString());
        // The submitted value never comes back as a JSON string anywhere in the problem body.
        Assert.DoesNotContain($"\"{JsonEncodedText.Encode(color)}\"", text, StringComparison.Ordinal);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await CountTagsAsync(context, kind));
    }

    [Theory]
    [MemberData(nameof(KindsByInvalidColor))]
    public async Task An_invalid_color_on_PUT_is_a_400_keyed_on_Color_and_leaves_the_stored_color(Kind kind, string color)
    {
        await using var factory = new OdysseyApiFactory(TagAdmin(kind), ActorUserId);
        var id = await SeedTagAsync(factory, kind, "Holidays", null, "#15803D");
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"{TagPath(kind)}/{id}", new { name = "Holidays", archived = false, color });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        var errors = body.RootElement.GetProperty("errors").GetProperty("Color");
        Assert.Equal(HexColor.InvalidMessage, Assert.Single(errors.EnumerateArray().ToList()).GetString());
        Assert.DoesNotContain($"\"{JsonEncodedText.Encode(color)}\"", text, StringComparison.Ordinal);
        Assert.Equal("#15803D", await StoredColorAsync(factory, kind));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_stored_corrupt_color_reads_as_null_and_never_500s(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(TagAdmin(kind), ActorUserId);
        var id = await SeedTagAsync(factory, kind, "Legacy", null, "red;x:y");
        using var client = factory.CreateClient();

        var single = await client.GetAsync($"{TagPath(kind)}/{id}");
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        Assert.Null(await ColorOf(single));

        var listText = await client.GetStringAsync(TagPath(kind));
        Assert.DoesNotContain("red;x:y", listText, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Put_without_the_update_claim_is_forbidden_and_the_read_claim_sees_the_color(Kind kind)
    {
        await using var factory = new OdysseyApiFactory([ReadTagClaim(kind)], ActorUserId);
        var id = await SeedTagAsync(factory, kind, "Holidays", null, "#15803D");
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"{TagPath(kind)}/{id}", new { name = "Holidays", archived = false, color = "#0369A1" });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        Assert.Equal("#15803D", await ColorOf(await client.GetAsync($"{TagPath(kind)}/{id}")));
    }

    // ── Record display colour ─────────────────────────────────────────────────

    [Fact]
    public async Task A_transaction_takes_the_color_of_the_tag_that_wins_its_icon()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TransactionsRead]);
        var accountId = await SeedAccountAsync(factory);
        var transactionId = await SeedTransactionAsync(factory, accountId,
            ("Groceries", "shopping_cart", "#b23b3b"), ("Business", null, "#15803D"), ("Travel", "flight", "#0369A1"));
        using var client = factory.CreateClient();

        var detail = await client.GetFromJsonAsync<ExistingTransaction>($"/api/transactions/{transactionId}");
        var listed = Assert.Single(await client.GetPagedItemsAsync<ExistingTransaction>("/api/transactions"));

        foreach (var transaction in new[] { detail!, listed })
        {
            Assert.Equal("shopping_cart", transaction.DisplayIcon);
            Assert.Equal("#B23B3B", transaction.DisplayColor);
            Assert.Equal(["#15803D", "#B23B3B", "#0369A1"], transaction.TransactionTags.Select(tag => tag.Color));
        }
    }

    [Fact]
    public async Task A_transaction_whose_icon_tag_has_no_color_shows_none_even_when_another_tag_has_one()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TransactionsRead]);
        var accountId = await SeedAccountAsync(factory);
        var transactionId = await SeedTransactionAsync(factory, accountId,
            ("Alpha", "home", null), ("Beta", null, "#15803D"));
        using var client = factory.CreateClient();

        var detail = await client.GetFromJsonAsync<ExistingTransaction>($"/api/transactions/{transactionId}");

        Assert.Equal("home", detail!.DisplayIcon);
        Assert.Null(detail.DisplayColor);
    }

    [Fact]
    public async Task A_transaction_whose_winning_tag_color_is_corrupt_shows_none_on_the_record_and_the_embed()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TransactionsRead]);
        var accountId = await SeedAccountAsync(factory);
        var transactionId = await SeedTransactionAsync(factory, accountId, ("Alpha", "home", "#fff;a"));
        using var client = factory.CreateClient();

        var text = await client.GetStringAsync($"/api/transactions/{transactionId}");
        var detail = JsonSerializer.Deserialize<ExistingTransaction>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("home", detail!.DisplayIcon);
        Assert.Null(detail.DisplayColor);
        Assert.Null(Assert.Single(detail.TransactionTags).Color);
        Assert.DoesNotContain("#fff;a", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Kind.Journal)]
    [InlineData(Kind.Task)]
    [InlineData(Kind.Photo)]
    public async Task Every_journal_task_and_photo_response_carries_the_winning_tags_color(Kind kind)
    {
        await using var factory = new OdysseyApiFactory(RecordAdmin(kind), ActorUserId);
        var trip = await SeedTagAsync(factory, kind, "Trip", "beach_access", "#0369a1");
        var art = await SeedTagAsync(factory, kind, "art", null, "#15803D");
        var corrupt = await SeedTagAsync(factory, kind, "Auto", "directions_car", "red;x:y");
        var fileId = kind == Kind.Photo ? await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId) : Guid.Empty;
        using var client = factory.CreateClient();

        // art sorts first but has no icon, so Trip wins — and its colour comes along, upper-cased.
        var post = await client.PostAsJsonAsync(RecordPath(kind), CreateBody(kind, fileId, [trip, art]));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = await DisplayOf(post);
        Assert.Equal(("beach_access", "#0369A1"), (created.Icon, created.Color));

        // Auto joins and wins the icon; its stored colour is corrupt, so the record shows none.
        var put = await client.PutAsJsonAsync($"{RecordPath(kind)}/{created.Id}", UpdateBody(kind, [trip, art, corrupt]));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(("directions_car", (string?)null), await DisplayPairOf(put));

        Assert.Equal(("directions_car", (string?)null), await DisplayPairOf(await client.GetAsync($"{RecordPath(kind)}/{created.Id}")));
        using var list = JsonDocument.Parse(await client.GetStringAsync(RecordPath(kind)));
        var row = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray().ToList());
        Assert.Null(row.GetProperty("displayColor").GetString());
        Assert.Equal("directions_car", row.GetProperty("displayIcon").GetString());
    }

    // ── Calendars ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_calendar_colour_follows_the_tag_rule_but_is_required()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.CalendarRead, PermissionClaims.CalendarUpdate, PermissionClaims.CalendarCreate]);
        var lower = await SeedCalendarAsync(factory, "Lower", "#b23b3b");
        var corrupt = await SeedCalendarAsync(factory, "Corrupt", "red;x:y");
        using var client = factory.CreateClient();

        Assert.Equal("#B23B3B", await ColorOf(await client.GetAsync($"/api/calendars/{lower}")));
        Assert.Equal(ColorPalette.CalendarDefault, await ColorOf(await client.GetAsync($"/api/calendars/{corrupt}")));
        var listText = await client.GetStringAsync("/api/calendars");
        Assert.DoesNotContain("red;x:y", listText, StringComparison.Ordinal);

        // A PUT keeping the lower-case palette member is accepted and stored upper-case.
        var put = await client.PutAsJsonAsync($"/api/calendars/{lower}", new { name = "Lower", color = "#b23b3b" });
        Assert.True(put.IsSuccessStatusCode, put.StatusCode.ToString());
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal("#B23B3B", (await context.Calendars.SingleAsync(c => c.CalendarId == lower)).Color);

        // Any hex is accepted, as on a tag, and stored upper-case.
        var custom = await client.PostAsJsonAsync("/api/calendars", new { name = "Custom", color = "#c2185b" });
        Assert.Equal(HttpStatusCode.Created, custom.StatusCode);
        Assert.Equal("#C2185B", await ColorOf(custom));

        // An invalid colour is refused keyed on color, never echoed; unlike a tag, null is refused too.
        foreach (var bad in new[] { "red;x:y", "#12345", "", null })
        {
            var refused = await client.PostAsJsonAsync("/api/calendars", new { name = "Odd", color = bad });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var body = await refused.Content.ReadAsStringAsync();
            Assert.Contains("\"Color\"", body, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(bad))
                Assert.DoesNotContain(bad, body, StringComparison.Ordinal);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string TagPath(Kind kind) => kind switch
    {
        Kind.Transaction => "/api/transaction-tags",
        Kind.Journal => "/api/journal-tags",
        Kind.Task => "/api/task-tags",
        _ => "/api/photo-tags",
    };

    private static string ReadTagClaim(Kind kind) => kind switch
    {
        Kind.Transaction => PermissionClaims.TransactionTagsRead,
        Kind.Journal => PermissionClaims.JournalTagsRead,
        Kind.Task => PermissionClaims.TaskTagsRead,
        _ => PermissionClaims.PhotoTagsRead,
    };

    private static string[] TagAdmin(Kind kind) => kind switch
    {
        Kind.Transaction => [PermissionClaims.TransactionTagsCreate, PermissionClaims.TransactionTagsRead, PermissionClaims.TransactionTagsUpdate],
        Kind.Journal => [PermissionClaims.JournalTagsCreate, PermissionClaims.JournalTagsRead, PermissionClaims.JournalTagsUpdate],
        Kind.Task => [PermissionClaims.TaskTagsCreate, PermissionClaims.TaskTagsRead, PermissionClaims.TaskTagsUpdate],
        _ => [PermissionClaims.PhotoTagsCreate, PermissionClaims.PhotoTagsRead, PermissionClaims.PhotoTagsUpdate],
    };

    private static string RecordPath(Kind kind) => kind switch
    {
        Kind.Journal => "/api/journal-entries",
        Kind.Task => "/api/tasks",
        _ => "/api/photos",
    };

    private static string[] RecordAdmin(Kind kind) => kind switch
    {
        Kind.Journal => [PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate],
        Kind.Task => [PermissionClaims.TasksRead, PermissionClaims.TasksCreate, PermissionClaims.TasksUpdate],
        _ => [PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate, PermissionClaims.PhotosUpdate, PermissionClaims.FilesRead],
    };

    private static object CreateBody(Kind kind, Guid fileId, Guid[] tagIds) => kind switch
    {
        Kind.Journal => new { title = "A day", content = "Dear journal.", entryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), tagIds },
        Kind.Task => new { title = "A task", tagIds },
        _ => new { fileId, tagIds },
    };

    private static object UpdateBody(Kind kind, Guid[] tagIds) => kind switch
    {
        Kind.Journal => new { title = "A day", content = "Dear journal.", entryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), archived = false, tagIds },
        Kind.Task => new { title = "A task", tagIds },
        _ => new { tagIds },
    };

    private static async Task<string?> ColorOf(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("color").GetString();
    }

    private static async Task<(Guid Id, string Icon, string? Color)> DisplayOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        var id = root.EnumerateObject()
            .First(property => property.Name is "journalEntryId" or "journalTaskId" or "photoId")
            .Value.GetGuid();
        return (id, root.GetProperty("displayIcon").GetString()!, root.GetProperty("displayColor").GetString());
    }

    private static async Task<(string Icon, string? Color)> DisplayPairOf(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        var (_, icon, color) = await DisplayOf(response);
        return (icon, color);
    }

    private static async Task<Guid> SeedTagAsync(WebApplicationFactory<Program> factory, Kind kind, string name, string? icon, string? color)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        switch (kind)
        {
            case Kind.Transaction:
                context.TransactionTags.Add(new TransactionTag { TransactionTagId = id, Name = name, Icon = icon, Color = color });
                break;
            case Kind.Journal:
                context.JournalTags.Add(new JournalTag { JournalTagId = id, Name = name, Icon = icon, Color = color });
                break;
            case Kind.Task:
                context.JournalTaskTags.Add(new JournalTaskTag { JournalTaskTagId = id, Name = name, Icon = icon, Color = color });
                break;
            default:
                context.PhotoTags.Add(new PhotoTag { PhotoTagId = id, Name = name, Icon = icon, Color = color });
                break;
        }

        await context.SaveChangesAsync();
        return id;
    }

    private static async Task<string?> StoredColorAsync(WebApplicationFactory<Program> factory, Kind kind)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return kind switch
        {
            Kind.Transaction => (await context.TransactionTags.SingleAsync()).Color,
            Kind.Journal => (await context.JournalTags.SingleAsync()).Color,
            Kind.Task => (await context.JournalTaskTags.SingleAsync()).Color,
            _ => (await context.PhotoTags.SingleAsync()).Color,
        };
    }

    private static async Task<int> CountTagsAsync(OdysseyContext context, Kind kind)
    {
        await context.Database.EnsureCreatedAsync();
        return kind switch
        {
            Kind.Transaction => await context.TransactionTags.CountAsync(),
            Kind.Journal => await context.JournalTags.CountAsync(),
            Kind.Task => await context.JournalTaskTags.CountAsync(),
            _ => await context.PhotoTags.CountAsync(),
        };
    }

    private static async Task<Guid> SeedAccountAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var account = new Account
        {
            AccountId = Guid.NewGuid(),
            Name = "Checking",
            Description = "Test account",
            Opened = DateTime.UtcNow,
            AccountType = Odyssey.Context.AccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }

    private static async Task<Guid> SeedTransactionAsync(
        WebApplicationFactory<Program> factory, Guid accountId, params (string Name, string? Icon, string? Color)[] tags)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var transaction = new Transaction
        {
            TransactionId = Guid.NewGuid(),
            Description = "Lunch",
            Amount = -12,
            TimeStamp = DateTime.UtcNow,
            AccountId = accountId,
            CurrencyCode = "USD",
            Status = TransactionStatus.New,
            StatusChangedAt = DateTime.UtcNow,
        };
        foreach (var (name, icon, color) in tags)
        {
            transaction.TransactionTags.Add(new TransactionTag { Name = name, Icon = icon, Color = color });
        }

        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();
        return transaction.TransactionId;
    }

    private static async Task<Guid> SeedCalendarAsync(WebApplicationFactory<Program> factory, string name, string color)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var calendar = new CalendarEntity { CalendarId = Guid.NewGuid(), Name = name, Color = color, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        return calendar.CalendarId;
    }
}
