using System.Net;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Xunit;
using DialogHost = Odyssey.Client.Tests.ContractSignatureSurfaceTests.DialogHost;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client half of issue #181 — the contract reference number: the shared rules, the two atoms
/// (<c>OdsReferenceNumberField</c>, <c>OdsReferenceNumber</c>), the dialog's create and edit writes,
/// and the <c>ContractsCard</c> surfaces the design system places it on.
/// </summary>
/// <remarks>
/// The atoms and the dialog RENDER. The card's branches are source lints, for the reason
/// <see cref="ContractPauseSurfaceTests"/> records: its rows arrive through <c>OdsInfiniteList</c>,
/// which materialises nothing under bUnit.
/// </remarks>
public class ContractReferenceNumberSurfaceTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();

    public Task InitializeAsync()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    // ── Rules ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("AGR-2026/114-B.2")]
    [InlineData("Ω-2026.№114 (rev/2)")]
    [InlineData("REF-\U00020BB7-1")]
    [InlineData("")]
    public void Printable_values_break_no_rule(string value) =>
        Assert.Null(OdsReferenceNumberRules.Validate(value));

    /// <summary>
    /// The client check is the server's pattern, not an approximation of it: every value the DTO's
    /// attribute refuses, the client refuses, and vice versa.
    /// </summary>
    [Theory]
    [InlineData("REF\n1")]
    [InlineData("REF\t1")]
    [InlineData("REF\u00001")]
    [InlineData("REF\u202E1")]
    [InlineData("REF\u200B1")]
    [InlineData("REF-\U00020BB7-1")]
    [InlineData("😀")]
    public void The_character_check_agrees_with_the_dto_pattern(string value)
    {
        var serverAccepts = Regex.IsMatch(value, ContractReferenceNumber.Pattern);
        var clientAccepts = OdsReferenceNumberRules.FindHidden(value) is null;
        Assert.Equal(serverAccepts, clientAccepts);
    }

    [Fact]
    public void A_hidden_character_is_named_by_code_point_without_echoing_the_value()
    {
        var violation = OdsReferenceNumberRules.Validate("SECRET\u202E77\u200B");

        Assert.NotNull(violation);
        Assert.Equal("reference_number_invalid_characters", violation!.Code);
        Assert.Contains("right-to-left override (U+202E) and 1 more", violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", violation.Message, StringComparison.Ordinal);
        Assert.Equal("SECRET77", OdsReferenceNumberRules.StripHidden("SECRET\u202E77\u200B"));
    }

    [Fact]
    public void The_length_is_judged_on_the_trimmed_value()
    {
        var atLimit = new string('A', ContractReferenceNumber.MaxLength);
        Assert.Null(OdsReferenceNumberRules.Validate($"  {atLimit}  "));

        var violation = OdsReferenceNumberRules.Validate(atLimit + "B");
        Assert.Equal("reference_number_too_long", violation!.Code);
        Assert.Contains("64 characters or fewer — this one is 65", violation.Message, StringComparison.Ordinal);
    }

    // ── OdsReferenceNumberField ──────────────────────────────────────────────

    [Fact]
    public void The_field_takes_a_whole_over_long_paste_and_says_so()
    {
        var value = new string('7', 70);
        var cut = _ctx.Render<OdsReferenceNumberField>(p => p.Add(f => f.Value, value));

        var input = cut.Find("input");
        // No native maxlength: a paste cut to 64 would be saved as a DIFFERENT number.
        Assert.False(input.HasAttribute("maxlength"));
        Assert.Equal("true", input.GetAttribute("aria-invalid"));
        Assert.Equal("70/64", cut.Find(".odc-field-count.over").TextContent.Trim());
        Assert.Contains("64 characters or fewer", cut.Find(".odc-field-help.error").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_it_strips_the_hidden_characters_and_keeps_the_rest()
    {
        string? bound = "AB\u200BC";
        var cut = _ctx.Render<OdsReferenceNumberField>(p => p
            .Add(f => f.Value, bound)
            .Add(f => f.ValueChanged, v => bound = v));

        cut.Find(".odc-refnum-strip").Click();

        Assert.Equal("ABC", bound);
    }

    [Fact]
    public void Blur_trims_so_what_is_seen_is_what_is_stored()
    {
        string? bound = "  REF 1 / 2  ";
        var cut = _ctx.Render<OdsReferenceNumberField>(p => p
            .Add(f => f.Value, bound)
            .Add(f => f.ValueChanged, v => bound = v));

        cut.Find("input").Blur();

        Assert.Equal("REF 1 / 2", bound);
    }

    // ── OdsReferenceNumber ───────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_number_renders_nothing(string? value)
    {
        _ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        var cut = _ctx.Render<OdsReferenceNumber>(p => p.Add(r => r.Value, value));

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void The_search_match_is_marked_case_insensitively()
    {
        _ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        var cut = _ctx.Render<OdsReferenceNumber>(p => p
            .Add(r => r.Value, "AGR-2026/114-B.2")
            .Add(r => r.Size, OdsReferenceNumberSize.Sm)
            .Add(r => r.Highlight, "114-b"));

        Assert.Equal("114-B", cut.Find("mark.odc-refnum-mark").TextContent);
        Assert.Equal("Reference number AGR-2026/114-B.2", cut.Find(".odc-refnum-text").TextContent);
        // Sm ellipsizes, so the full value travels in the title; its glyph labels a bare meta value.
        Assert.Equal("AGR-2026/114-B.2", cut.Find(".odc-refnum").GetAttribute("title"));
        Assert.NotNull(cut.Find(".odc-refnum-icon"));
    }

    [Fact]
    public void Copy_writes_the_whole_value()
    {
        var clipboard = new Mock<IClipboardService>();
        clipboard.Setup(c => c.CopyAsync(It.IsAny<string>(), It.IsAny<string?>())).ReturnsAsync(true);
        _ctx.Services.AddSingleton(clipboard.Object);
        var cut = _ctx.Render<OdsReferenceNumber>(p => p
            .Add(r => r.Value, "PHI-BC-2026-0098812")
            .Add(r => r.Copyable, true));

        Assert.Empty(cut.FindAll(".odc-refnum-icon"));
        cut.Find(".odc-refnum-copy").Click();

        // With a success message, so the Snackbar announces the copy (WCAG 4.1.3).
        clipboard.Verify(c => c.CopyAsync("PHI-BC-2026-0098812", "Reference number copied."), Times.Once);
    }

    // ── The dialog ───────────────────────────────────────────────────────────

    /// <summary>
    /// PUT is a full replacement, so an unrelated edit must send the stored number back — the
    /// behavioural half of the carry-forward lint, for the one site that owns a field.
    /// </summary>
    [Fact]
    public void An_unrelated_edit_carries_the_reference_number_forward()
    {
        var contract = Contract("AGR-2026/114-B.2");
        var (dialog, client) = RenderDialog(contract);

        Assert.Equal("AGR-2026/114-B.2", RefInput(dialog).GetAttribute("value"));
        dialog.FindAll("input[type=text]")[0].Input("Fibre broadband — renewed");
        ClickFooter(dialog, "Save changes");

        client.Verify(c => c.UpdateAsync(contract.ContractId,
            It.Is<UpdateContract>(u => u.ReferenceNumber == "AGR-2026/114-B.2"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Emptying_the_field_clears_it()
    {
        var contract = Contract("AGR-2026/114-B.2");
        var (dialog, client) = RenderDialog(contract);

        RefInput(dialog).Input("   ");
        ClickFooter(dialog, "Save changes");

        client.Verify(c => c.UpdateAsync(contract.ContractId,
            It.Is<UpdateContract>(u => u.ReferenceNumber == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The create write path carries the field too, normalised — the parallel branch to the edit
    /// tests above, so dropping the assignment from the NewContract block fails here.
    /// </summary>
    [Fact]
    public void Create_sends_the_normalised_reference_number()
    {
        var (dialog, client) = RenderDialog(null);

        dialog.FindAll("input[type=text]")[0].Input("Storage unit B12");
        RefInput(dialog).Input("  B12-1001  ");
        // The type is required; pick it through the select's own trigger and option, the path a
        // user takes. The popover portals into the host's provider, so re-render before querying.
        dialog.Find("button.odc-select-trigger").Click();
        dialog.Render();
        dialog.FindAll("[role='menuitemradio']")
            .Single(o => o.TextContent.Contains("Rental", StringComparison.Ordinal))
            .Click();
        ClickFooter(dialog, "Create contract");

        // Choosing an option closes a popover first, so the save lands a tick later; wait for it.
        dialog.WaitForAssertion(() => client.Verify(c => c.CreateAsync(
            It.Is<NewContract>(n => n.ReferenceNumber == "B12-1001" && n.Name == "Storage unit B12"),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void A_broken_rule_blocks_the_save()
    {
        var contract = Contract(null);
        var (dialog, client) = RenderDialog(contract);

        RefInput(dialog).Input("REF\u202E1");
        ClickFooter(dialog, "Save changes");

        client.Verify(c => c.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateContract>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("U+202E", dialog.Find(".odc-refnum-shell .odc-field-help.error").TextContent, StringComparison.Ordinal);
    }

    // ── The card (source lints) ──────────────────────────────────────────────

    [Fact]
    public void The_card_places_the_number_where_the_design_system_does()
    {
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "ContractsCard.razor"));
        var code = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "ContractsCard.razor.cs"));

        // Search covers it, and the placeholder says so.
        Assert.Contains("Placeholder=\"Search name, reference, party…\"", markup, StringComparison.Ordinal);
        // The meta line, marked with the live search term.
        Assert.Matches(new Regex(@"ReferenceMeta\(c\.ReferenceNumber\)"), markup);
        Assert.Matches(new Regex(@"<OdsReferenceNumber [^>]*Size=""OdsReferenceNumberSize\.Sm"" Highlight=""@_searchString"""), markup);
        // The detail tile, only when a value is on file.
        Assert.Matches(new Regex(@"@if \(!string\.IsNullOrWhiteSpace\(detail\.ReferenceNumber\)\)\s*\{\s*<OdsInfoTile Icon=""tag"" Label=""Reference number"""), markup);
        // The sort key, last, under the server's enum name.
        Assert.Matches(new Regex(@"Key = ""referenceNumber"", Label = ""Reference number""[^\n]*\n\s*\];"), code);
        // Copy reference number: present only with a value, and just before Copy ID.
        Assert.Matches(new Regex(@"if \(!string\.IsNullOrWhiteSpace\(c\.ReferenceNumber\)\)[\s\S]{0,300}?Label = ""Copy reference number""[\s\S]{0,400}?Label = ""Copy ID"""), code);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AngleSharp.Dom.IElement RefInput(IRenderedComponent<DialogHost> cut) => cut.Find(".odc-refnum-input");

    private static void ClickFooter(IRenderedComponent<DialogHost> cut, string label) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(label, StringComparison.Ordinal)).Click();

    private static ExistingContract Contract(string? referenceNumber) => new()
    {
        ContractId = Guid.NewGuid(),
        Name = "Fibre broadband",
        Type = ContractType.Service,
        Status = ContractStatus.Active,
        ReferenceNumber = referenceNumber,
        StartDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedAtUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private (IRenderedComponent<DialogHost> Cut, Mock<IContractsApiClient> Client) RenderDialog(ExistingContract? contract)
    {
        var client = new Mock<IContractsApiClient>();
        client
            .Setup(c => c.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateContract>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));
        client
            .Setup(c => c.CreateAsync(It.IsAny<NewContract>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
        _ctx.Services.AddSingleton(client.Object);

        return (_ctx.Render<DialogHost>(p => p.Add(h => h.Contract, contract)), client);
    }
}
