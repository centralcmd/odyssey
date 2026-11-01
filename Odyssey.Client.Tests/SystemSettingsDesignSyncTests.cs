using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The System settings page's alignment with the design system (ui_kits/web/SystemSettings.jsx and
/// system-settings-data.js): the section order, the explicit tighten-only rows, the per-row "last
/// changed" stamp, the header rollup's label and the persistent alert a 403 save raises.
/// </summary>
[Collection(SettingsPageCollection.Name)]
public class SystemSettingsDesignSyncTests : IDisposable
{
    /// <summary>Restores the page's browser-check seam, which is <c>internal static</c>.</summary>
    public void Dispose() => Settings.InteractiveCheck = static () => OperatingSystem.IsBrowser();

    private const string DesignData = "ui_kits/web/system-settings-data.js";

    [SkippableFact]
    public void Sections_follow_the_design_systems_order()
    {
        var design = DesignRows(File.ReadAllText(DesignSystemSource.File(DesignData)));

        Assert.Equal(
            design.Select(section => section.Group).ToArray(),
            Settings.Sections.Select(section => section.Group).ToArray());
    }

    /// <summary>
    /// "lower only" is the design's <c>ceiling</c>, not "the row has a published bound": the two
    /// aggregate calendar caps and the metadata read size publish theirs only so the control can bound
    /// itself, and marking them one-way would tell an administrator they cannot raise a value they can.
    /// </summary>
    [SkippableFact]
    public void Only_the_design_systems_ceiling_rows_are_lower_only()
    {
        var design = DesignRows(File.ReadAllText(DesignSystemSource.File(DesignData)));
        var ceilingKeys = design.SelectMany(section => section.Rows).Where(row => row.Ceiling).Select(row => row.Key);

        Assert.NotEmpty(ceilingKeys);
        Assert.Equal(
            ceilingKeys.Order(StringComparer.Ordinal),
            Settings.AllItems.Where(item => item.LowerOnly).Select(item => item.Key).Order(StringComparer.Ordinal));
    }

    /// <summary>Client-side invariant: a lower-only row quotes a published ceiling in its range sentence.</summary>
    [Fact]
    public void Every_lower_only_row_has_a_published_ceiling()
    {
        Assert.Contains(Settings.AllItems, item => item.LowerOnly);
        Assert.All(Settings.AllItems.Where(item => item.LowerOnly), item => Assert.NotNull(item.MaxFrom));
    }

    /// <summary>
    /// The sections of the design's <c>SS_GROUPS</c>, in order, each with its rows' keys and whether
    /// the row carries a <c>ceiling</c>. A row runs from its <c>key:</c> to the next row or section;
    /// comments are stripped first, so prose mentioning a ceiling is not read as one.
    /// </summary>
    private static List<(string Group, List<(string Key, bool Ceiling)> Rows)> DesignRows(string source)
    {
        var start = source.IndexOf("const SS_GROUPS", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{DesignData} no longer declares SS_GROUPS.");
        var end = source.IndexOf("\nconst ", start + 1, StringComparison.Ordinal);
        var groups = source[start..(end < 0 ? source.Length : end)];
        groups = System.Text.RegularExpressions.Regex.Replace(groups, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        groups = System.Text.RegularExpressions.Regex.Replace(groups, @"^\s*//.*$", string.Empty, System.Text.RegularExpressions.RegexOptions.Multiline);

        var marks = System.Text.RegularExpressions.Regex.Matches(groups, @"\b(?<kind>group|key):\s*'(?<name>[^']+)'");
        var sections = new List<(string Group, List<(string Key, bool Ceiling)> Rows)>();
        for (var index = 0; index < marks.Count; index++)
        {
            var mark = marks[index];
            var spanEnd = index + 1 < marks.Count ? marks[index + 1].Index : groups.Length;
            if (mark.Groups["kind"].Value == "group")
            {
                sections.Add((mark.Groups["name"].Value, []));
                continue;
            }

            Assert.NotEmpty(sections);
            var span = groups[mark.Index..spanEnd];
            sections[^1].Rows.Add((mark.Groups["name"].Value,
                System.Text.RegularExpressions.Regex.IsMatch(span, @"\bceiling:\s*\d")));
        }

        Assert.NotEmpty(sections);
        return sections;
    }

    [Theory]
    [InlineData(true, true, "Credentials")]
    [InlineData(true, false, "Credentials")]
    [InlineData(false, true, "Storage")]
    [InlineData(false, false, "Email")]
    public void The_rollup_label_names_its_most_serious_kind(bool unreadable, bool storage, string expected) =>
        Assert.Equal(expected, Settings.SetupProblemsLabelFor(unreadable, storage));

    [Fact]
    public void The_credential_clear_advisory_points_at_a_card_that_exists() =>
        Assert.Contains(Settings.Sections, section => section.Group == Settings.RelayCredentialGroup);

    [Fact]
    public async Task Each_row_carries_its_own_last_changed_stamp()
    {
        var dto = Valid();
        dto.Provenance = new Dictionary<string, SettingProvenance>
        {
            [nameof(SystemSettingsUpdate.EmailFromName)] = new()
            {
                UpdatedAt = new DateTime(2026, 8, 2, 9, 10, 0, DateTimeKind.Utc),
                UpdatedByDisplayName = "Marcus Reyes",
            },
        };

        await using var ctx = SettingsPageHarness.NewPageContext(SettingsPageHarness.SettingsClient(dto).Object);
        var page = ctx.Render<Settings>();

        var changed = page.Find("[data-ss-row=emailFromName] .odc-sfield-stamp").TextContent;
        Assert.StartsWith("Last changed by Marcus Reyes, ", changed, StringComparison.Ordinal);

        var untouched = page.Find("[data-ss-row=emailFromAddress] .odc-sfield-stamp").TextContent;
        Assert.Equal("Never changed — default value.", untouched);

        // The page-level summary line is gone: the stamp lives on the row it describes.
        Assert.Empty(page.FindAll(".ss-lastchanged"));
    }

    [Fact]
    public async Task A_save_refused_with_403_raises_the_persistent_sign_out_alert()
    {
        var settings = SettingsPageHarness.SettingsClient(Valid());
        settings.Setup(client => client.UpdateAsync(It.IsAny<SystemSettingsUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<SystemSettingsDto>.Failure(
                HttpStatusCode.Forbidden, new ApiProblem { Title = "Forbidden" }));

        await using var ctx = SettingsPageHarness.NewPageContext(settings.Object);
        var page = ctx.Render<Settings>();

        page.Find("#ss-in-emailFromName").Input("Odyssey Mail");
        page.FindAll("button").Single(button =>
            button.TextContent.Contains("Save changes", StringComparison.Ordinal)).Click();

        var alert = page.WaitForElement(".ss-authalert");
        // Announced once, by the focus move onto a named group, not again by role="alert" (WCAG 4.1.3).
        Assert.NotEqual("alert", alert.GetAttribute("role"));
        Assert.Equal("-1", alert.GetAttribute("tabindex"));
        Assert.Equal("ss-authalert-ttl", alert.GetAttribute("aria-labelledby"));
        Assert.Equal("ss-authalert-desc", alert.GetAttribute("aria-describedby"));
        Assert.Contains("withdrawn", alert.TextContent, StringComparison.Ordinal);
        Assert.Contains(alert.QuerySelectorAll("button"), button => button.TextContent.Contains("Sign out", StringComparison.Ordinal));

        // The next edit dismisses it, as the design's page does.
        page.Find("#ss-in-emailFromName").Input("Odyssey Mail 2");
        Assert.Empty(page.FindAll(".ss-authalert"));
    }

    [Fact]
    public void Section_headings_are_h2()
    {
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages/Settings.razor"));
        Assert.Contains("<h2 class=\"ss-sect-t\"", markup, StringComparison.Ordinal);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A DTO every row is valid against, with mail configured so the rollup stays empty.</summary>
    private static SystemSettingsDto Valid()
    {
        var dto = new SystemSettingsDto();
        var properties = typeof(SystemSettingsDto).GetProperties()
            .ToDictionary(property => property.Name, StringComparer.Ordinal);

        foreach (var property in properties.Values.Where(p => p.PropertyType == typeof(int)))
        {
            if (property.Name.EndsWith("Ceiling", StringComparison.Ordinal))
            {
                property.SetValue(dto, 1_000_000);
            }
            else if (property.Name.EndsWith("Floor", StringComparison.Ordinal))
            {
                property.SetValue(dto, 1);
            }
        }

        foreach (var item in Settings.AllItems)
        {
            if (item.Field is not { } field || !properties.TryGetValue(field, out var property))
            {
                continue;
            }

            if (property.PropertyType == typeof(int))
            {
                property.SetValue(dto, Math.Max(item.Min, 1));
            }
            else if (property.PropertyType == typeof(decimal))
            {
                property.SetValue(dto, item.DecimalMin);
            }
            else if (property.PropertyType == typeof(string) && !item.AllowEmpty)
            {
                property.SetValue(dto, "x");
            }
        }

        dto.EmailSmtpHost = "smtp.example.test";
        dto.EmailSmtpPort = 587;
        dto.EmailUseStartTls = true;
        return dto;
    }
}
