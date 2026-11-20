using System.Globalization;
using Bunit;
using Odyssey.Client.Pages;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Xunit;
using static Odyssey.Client.Tests.SettingsPageHarness;

namespace Odyssey.Client.Tests;

/// <summary>
/// System settings → the two calendar <c>.ics</c> size rows (issue #517 §3 "Settings", AC 17): the shared
/// <see cref="SystemSettingsBounds"/> pair, which stops at 10240 MB, the spec's descriptions, and the server's advisory
/// above 1024 MB in the row's Advisory slot.
/// </summary>
public class CalendarIcsSettingsRowsTests
{
    private const string ExportKey = "calendarIcsMaxExportMegabytes";
    private const string ImportKey = "calendarIcsMaxImportMegabytes";

    private static Settings.SettingItem Row(string key) => Settings.AllItems.Single(i => i.Key == key);

    [Fact] // AC 17
    public void The_size_rows_take_the_shared_bounds_which_stop_at_10240()
    {
        var export = Row(ExportKey);
        var import = Row(ImportKey);

        Assert.Equal((SystemSettingsBounds.CalendarIcsMaxExportMegabytesMin, SystemSettingsBounds.CalendarIcsMaxExportMegabytesMax), (export.Min, export.Max));
        Assert.Equal((SystemSettingsBounds.CalendarIcsMaxImportMegabytesMin, SystemSettingsBounds.CalendarIcsMaxImportMegabytesMax), (import.Min, import.Max));
        Assert.Equal(10240, export.Max);
        Assert.Equal(10240, import.Max);
        Assert.Equal(Settings.SettingControl.Size, export.Control);
        Assert.Equal(Settings.SettingControl.Size, import.Control);
    }

    [Fact] // §3 Settings: the spec's descriptions
    public void The_size_rows_say_what_happens_at_the_limit()
    {
        Assert.EndsWith("refused before the download starts, or stopped mid-download — never truncated.", Row(ExportKey).Description, StringComparison.Ordinal);
        Assert.EndsWith("It cannot usefully exceed the reverse proxy's body-size limit; see the deployment guide.", Row(ImportKey).Description, StringComparison.Ordinal);
    }

    [Fact] // AC 17: the input stops at 10240, and the server's advisory above 1024 shows in the Advisory slot
    public async Task Above_1024_the_rows_show_the_server_advisory()
    {
        var dto = Valid();
        dto.CalendarIcsMaxImportMegabytes = 4096;
        dto.CalendarIcsMaxExportMegabytes = 2048;
        dto.Warnings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(SystemSettingsUpdate.CalendarIcsMaxImportMegabytes)] = "Above 1 GB the whole upload is buffered to the API's temporary disk.",
            [nameof(SystemSettingsUpdate.CalendarIcsMaxExportMegabytes)] = "Above 1 GB a large export streams for a long time.",
        };
        await using var ctx = NewPageContext(SettingsClient(dto).Object);

        var page = ctx.Render<Settings>();

        Assert.Equal(10240.ToString(CultureInfo.InvariantCulture), page.Find($"#ss-in-{ImportKey}").GetAttribute("max"));
        Assert.Equal(10240.ToString(CultureInfo.InvariantCulture), page.Find($"#ss-in-{ExportKey}").GetAttribute("max"));
        Assert.Contains("temporary disk", page.Find($"#ss-advisory-{ImportKey}").TextContent, StringComparison.Ordinal);
        Assert.Contains("streams for a long time", page.Find($"#ss-advisory-{ExportKey}").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC 17: at or below 1024 the server sends no advisory, and the row shows none
    public async Task At_or_below_1024_the_rows_show_no_advisory()
    {
        var dto = Valid();
        dto.CalendarIcsMaxImportMegabytes = 1024;
        await using var ctx = NewPageContext(SettingsClient(dto).Object);

        var page = ctx.Render<Settings>();

        Assert.Empty(page.FindAll($"#ss-advisory-{ImportKey}"));
    }

    /// <summary>A DTO every row accepts as loaded (the EmailTransportSurfaceTests helper's shape).</summary>
    private static SystemSettingsDto Valid()
    {
        var dto = new SystemSettingsDto();
        var properties = typeof(SystemSettingsDto).GetProperties().ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var property in properties.Values.Where(p => p.PropertyType == typeof(int)))
        {
            if (property.Name.EndsWith("Ceiling", StringComparison.Ordinal))
                property.SetValue(dto, 1_000_000);
            else if (property.Name.EndsWith("Floor", StringComparison.Ordinal))
                property.SetValue(dto, 1);
        }

        foreach (var item in Settings.AllItems)
        {
            if (item.Field is not { } field || !properties.TryGetValue(field, out var property))
                continue;
            if (property.PropertyType == typeof(int))
                property.SetValue(dto, Math.Max(item.Min, 1));
            else if (property.PropertyType == typeof(decimal))
                property.SetValue(dto, item.DecimalMin);
            else if (property.PropertyType == typeof(string) && !item.AllowEmpty)
                property.SetValue(dto, "x");
        }

        dto.EmailSmtpHost = "smtp.old.test";
        dto.EmailSmtpPort = 587;
        dto.EmailUseStartTls = true;
        return dto;
    }
}
