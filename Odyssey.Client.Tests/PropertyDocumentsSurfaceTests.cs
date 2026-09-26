using System.Reflection;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Moq;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The smaller pieces of the property Documents feature (issue #210): the name → type guess, the two
/// type pickers, the files table's renamed danger item, the delete dialog's document line and the
/// Properties row menu's "Attach documents" gate.
/// </summary>
public class PropertyDocumentsSurfaceTests
{
    static PropertyDocumentsSurfaceTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    // ── PropertyFileTypeGuess ───────────────────────────────────────────────────

    [Theory]
    [InlineData("skjøte-storgata-14.pdf", PropertyFileType.Deed)]
    [InlineData("Grunnbok utskrift.pdf", PropertyFileType.Deed)]
    [InlineData("DEED.PDF", PropertyFileType.Deed)]
    [InlineData("Kjøpekontrakt signert.pdf", PropertyFileType.PurchaseAgreement)]
    [InlineData("purchase-agreement.pdf", PropertyFileType.PurchaseAgreement)]
    [InlineData("takst-2025.pdf", PropertyFileType.Valuation)]
    [InlineData("Appraisal.pdf", PropertyFileType.Valuation)]
    [InlineData("EU-kontroll 2025.pdf", PropertyFileType.Inspection)]
    [InlineData("tilstandsrapport.pdf", PropertyFileType.Inspection)]
    [InlineData("vognkort.jpg", PropertyFileType.Registration)]
    [InlineData("forsikringsbevis.pdf", PropertyFileType.Insurance)]
    [InlineData("garantibevis.pdf", PropertyFileType.Warranty)]
    [InlineData("faktura-rørlegger.pdf", PropertyFileType.Receipt)]
    [InlineData("repair-log.pdf", PropertyFileType.Maintenance)]
    [InlineData("skattemelding.pdf", PropertyFileType.Tax)]
    [InlineData("tegning-1-etasje.png", PropertyFileType.Drawing)]
    public void A_recognisable_name_guesses_its_type(string fileName, PropertyFileType expected) =>
        Assert.Equal(expected, PropertyFileTypeGuess.Guess(fileName));

    /// <summary>
    /// An unmatched name falls to Other — never to Deed, the first member in reading order. A default
    /// of Deed would quietly assert that an arbitrary scan is the title document.
    /// </summary>
    [Theory]
    [InlineData("scan001.pdf")]
    [InlineData("IMG_4411.jpeg")]
    [InlineData("")]
    public void An_unmatched_name_guesses_Other_never_Deed(string fileName)
    {
        Assert.Equal(PropertyFileType.Other, PropertyFileTypeGuess.Guess(fileName));
        Assert.Equal("Other", PropertyFileTypeGuess.GuessKey(fileName));
    }

    /// <summary>The first matching rule wins, in the design system's order.</summary>
    [Fact]
    public void The_first_matching_rule_wins() =>
        Assert.Equal(PropertyFileType.Deed, PropertyFileTypeGuess.Guess("title-insurance-invoice.pdf"));

    [Fact]
    public void Every_guessed_key_is_a_registry_key()
    {
        var keys = OdsTypeRegistries.PropertyFileTypes.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var name in new[] { "deed", "sale", "takst", "survey", "regist", "policy", "garanti", "invoice", "maint", "tax", "plan", "x" })
            Assert.Contains(PropertyFileTypeGuess.GuessKey(name), keys);
    }

    /// <summary>
    /// The guess mirrors the design system's <c>propGuessFileType</c> rule for rule. The DS patterns
    /// are read from <c>properties-data.js</c> and evaluated the way the DS evaluates them (first match
    /// over the lower-cased name); every alternative word in every rule, plus a mixed corpus, must
    /// guess the same on both sides — so a rule added, dropped or reordered on one side fails here.
    /// </summary>
    [Fact]
    public void The_guess_agrees_with_the_design_systems_rule()
    {
        var path = ClientSource.Sibling(Path.Combine("Odyssey Design System", "ui_kits", "web", "properties-data.js"));
        Assert.True(File.Exists(path), $"The design system's guess rule is missing at {path}.");

        var source = File.ReadAllText(path);
        var body = source[source.IndexOf("propGuessFileType", StringComparison.Ordinal)..];
        var rules = Regex.Matches(body[..body.IndexOf("rules.find", StringComparison.Ordinal)],
                @"\[/(?<re>[^/]+)/,\s*'(?<key>\w+)'\]")
            .Select(m => (Pattern: new Regex(m.Groups["re"].Value), Key: m.Groups["key"].Value))
            .ToList();

        Assert.Equal(11, rules.Count);

        string DsGuess(string name) =>
            rules.FirstOrDefault(r => r.Pattern.IsMatch(name.ToLowerInvariant())).Key ?? "Other";

        var corpus = rules
            .SelectMany(r => r.Pattern.ToString().Split('|'))
            .SelectMany(word => new[] { word, word.ToUpperInvariant(), $"2025-{word}-scan.pdf" })
            .Concat(["scan001.pdf", "IMG_4411.jpeg", "title-insurance-invoice.pdf", "Kjøpekontrakt.PDF"]);

        foreach (var name in corpus)
            Assert.True(DsGuess(name) == PropertyFileTypeGuess.GuessKey(name),
                $"'{name}': design system guesses {DsGuess(name)}, the client {PropertyFileTypeGuess.GuessKey(name)}");
    }

    // ── The two type pickers ────────────────────────────────────────────────────

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Render<MudPopoverProvider>();
        return ctx;
    }

    [Fact]
    public async Task The_single_select_is_wired_to_the_property_vocabulary()
    {
        await using var ctx = NewContext();
        string? chosen = null;
        var cut = ctx.Render<OdsPropertyFileTypeSelect>(p => p
            .Add(s => s.Value, "Deed")
            .Add(s => s.ValueChanged, (string v) => chosen = v));

        var select = cut.FindComponent<OdsSelect>().Instance;
        Assert.Same(OdsTypeRegistries.PropertyFileOptions, select.Options);
        Assert.Equal("Type", select.Label);
        Assert.Equal("Deed", select.Value);

        await cut.InvokeAsync(() => select.ValueChanged.InvokeAsync("Warranty"));
        Assert.Equal("Warranty", chosen);
    }

    [Fact]
    public async Task The_multi_select_filter_is_wired_to_the_property_vocabulary()
    {
        await using var ctx = NewContext();
        IReadOnlyCollection<string>? chosen = null;
        var cut = ctx.Render<OdsPropertyFileTypeMultiSelect>(p => p
            .Add(s => s.ValuesChanged, (IReadOnlyCollection<string> v) => chosen = v));

        var multi = cut.FindComponent<OdsMultiSelect>().Instance;
        Assert.Same(OdsTypeRegistries.PropertyFileOptions, multi.Options);
        Assert.Equal("Any type", multi.Label);
        Assert.Equal("home_work", multi.Icon);

        await cut.InvokeAsync(() => multi.ValuesChanged.InvokeAsync(["Deed", "Tax"]));
        Assert.Equal(["Deed", "Tax"], chosen);
    }

    // ── OdsFilesTable's danger item ─────────────────────────────────────────────

    private static readonly OdsFilesRow[] OneFile =
    [
        new()
        {
            Id = "f-1",
            Name = "deed.pdf",
            Kind = "Deed",
            SizeBytes = 1_000,
            UploadedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        },
    ];

    private static async Task<(IReadOnlyList<string> Labels, string Markup)> DangerMenu(Action<ComponentParameterCollectionBuilder<OdsFilesTable>>? extra)
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var popover = ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<OdsFilesTable>(p =>
        {
            p.Add(t => t.Files, OneFile)
             .Add(t => t.OnDelete, (OdsFilesRow _) => { });
            extra?.Invoke(p);
        });

        cut.Find("button[aria-label='Row actions']").Click();
        popover.WaitForElement("div.mud-menu-item");
        var danger = popover.FindAll(".odc-menu-item").Last();
        return ([.. popover.FindAll(".odc-menu-item-body > span:first-child").Select(e => e.TextContent.Trim())], danger.OuterHtml);
    }

    /// <summary>The Type chip prints the registry label the host supplied, not the raw enum key.</summary>
    [Fact]
    public async Task The_type_chip_reads_the_kind_label()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var cut = ctx.Render<OdsFilesTable>(p => p
            .Add(t => t.Files, [OneFile[0] with { Kind = "PurchaseAgreement" }])
            .Add(t => t.Kinds, OdsTypeRegistries.PropertyFileOptions));

        Assert.Equal("Purchase agreement", cut.Find(".odc-ft-type").TextContent.Trim());
    }

    /// <summary>Every existing host keeps its "Delete" · delete item — the new parameters default to it.</summary>
    [Fact]
    public async Task The_danger_item_defaults_to_delete()
    {
        var (labels, danger) = await DangerMenu(null);

        Assert.Equal("Delete", labels[^1]);
        Assert.Contains(">delete<", danger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_surface_renames_the_danger_item_to_detach()
    {
        var (labels, danger) = await DangerMenu(p => p.Add(t => t.DeleteLabel, "Detach").Add(t => t.DeleteIcon, "link_off"));

        Assert.Equal("Detach", labels[^1]);
        Assert.DoesNotContain("Delete", labels);
        Assert.Contains(">link_off<", danger, StringComparison.Ordinal);
    }

    // ── DeletePropertyDialog's document line ────────────────────────────────────

    /// <summary>
    /// There is no count on ExistingProperty (Non-Goal 4), so a property never opened says its links go
    /// without a number — never "No documents", which would be a claim the client cannot back.
    /// </summary>
    [Theory]
    [InlineData(null, "Its document links, if it has any — the files stay in Files")]
    [InlineData(0, "No documents")]
    [InlineData(1, "1 document link — the file stays in Files")]
    [InlineData(3, "3 document links — the files stay in Files")]
    public async Task The_delete_dialog_states_what_happens_to_the_documents(int? count, string expected)
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        // The dialog reads the property's contract links (issue #208); it never calls here without
        // contracts.read, which this host does not grant.
        ctx.Services.AddSingleton(new Mock<IPropertiesApiClient>().Object);

        var cut = ctx.Render<DeleteHost>(p => p.Add(h => h.DocumentCount, count));

        var line = cut.FindAll(".prop-del-list li")
            .Single(li => li.TextContent.Contains("attach_file", StringComparison.Ordinal));
        Assert.Equal(expected, line.TextContent.Replace("attach_file", string.Empty, StringComparison.Ordinal).Trim());
    }

    public sealed class DeleteHost : ComponentBase
    {
        [Parameter] public int? DocumentCount { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<DeletePropertyDialog>(1);
            builder.AddComponentParameter(2, nameof(DeletePropertyDialog.Property), new ExistingProperty
            {
                PropertyId = Guid.NewGuid(),
                Name = "Storgata 14",
                Description = "Primary residence",
                Type = PropertyType.RealEstate,
                CurrencyCode = "NOK",
            });
            builder.AddComponentParameter(3, nameof(DeletePropertyDialog.Open), true);
            builder.AddComponentParameter(4, nameof(DeletePropertyDialog.DocumentCount), DocumentCount);
            builder.CloseComponent();
        }
    }

    // ── PropertiesCard's row menu ───────────────────────────────────────────────

    // PropertiesCard is an @page whose permissions are read only in the browser, so the menu builder is
    // driven directly over a constructed instance with its claim flags set — the branch itself, not a
    // lint of its text.
    private static PropertiesCard CardWith(bool update, bool filesRead)
    {
        var card = new PropertiesCard();
        SetField(card, "_canUpdate", update);
        SetField(card, "_canReadFiles", filesRead);
        return card;
    }

    private static void SetField(object target, string name, object value) =>
        typeof(PropertiesCard).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static T Call<T>(PropertiesCard card, string method, params object[] args) =>
        (T)typeof(PropertiesCard).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(card, args)!;

    private static readonly ExistingProperty Row = new()
    {
        PropertyId = Guid.Parse("21021021-0000-0000-0000-000000000003"),
        Name = "Storgata 14",
        Description = "Primary residence",
        Type = PropertyType.RealEstate,
        CurrencyCode = "NOK",
    };

    /// <summary>POST …/files needs properties.update AND files.read (§7.2) — either alone offers nothing.</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Attach_documents_needs_both_properties_update_and_files_read(bool update, bool filesRead, bool offered)
    {
        var items = Call<IReadOnlyList<OdsMenuItem>>(CardWith(update, filesRead), "RowActions", Row);

        var attach = items.Where(i => i.Label == "Attach documents").ToList();
        Assert.Equal(offered ? 1 : 0, attach.Count);
        if (offered)
            Assert.Equal("attach_file", attach[0].Icon);
    }

    /// <summary>The menu item expands the row and issues a fresh token for that property alone.</summary>
    [Fact]
    public void Attach_documents_expands_the_row_and_issues_a_fresh_token()
    {
        var card = CardWith(update: true, filesRead: true);
        var other = Guid.NewGuid();

        Assert.Null(Call<Guid?>(card, "AttachTokenFor", Row.PropertyId));

        Call<object?>(card, "AttachDocuments", Row.PropertyId);
        var first = Call<Guid?>(card, "AttachTokenFor", Row.PropertyId);
        Call<object?>(card, "AttachDocuments", Row.PropertyId);
        var second = Call<Guid?>(card, "AttachTokenFor", Row.PropertyId);

        Assert.True(Call<bool>(card, "IsExpanded", Row.PropertyId));
        Assert.NotNull(first);
        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(first, second);
        Assert.Null(Call<Guid?>(card, "AttachTokenFor", other));
    }

    /// <summary>
    /// Collapsing or switching the open card consumes a pending request: the section is created afresh on
    /// every expand and cannot remember a token it already handled, so a kept token would reopen the
    /// dialog on every re-expand.
    /// </summary>
    [Fact]
    public void Collapsing_the_card_consumes_the_pending_attach_and_estimate_requests()
    {
        var card = CardWith(update: true, filesRead: true);
        Call<object?>(card, "AttachDocuments", Row.PropertyId);
        Call<object?>(card, "NewEstimate", Row.PropertyId);

        Call<object?>(card, "ToggleExpand", Row.PropertyId);
        Call<object?>(card, "ToggleExpand", Row.PropertyId);

        Assert.True(Call<bool>(card, "IsExpanded", Row.PropertyId));
        Assert.Null(Call<Guid?>(card, "AttachTokenFor", Row.PropertyId));
        Assert.Null(Call<Guid?>(card, "NewEstimateTokenFor", Row.PropertyId));
    }

    /// <summary>
    /// The delete dialog gets the count the section last reported, and nothing — not zero — for a
    /// property whose section never loaded.
    /// </summary>
    [Fact]
    public void The_card_hands_the_delete_dialog_the_last_reported_count_or_none()
    {
        var card = CardWith(update: true, filesRead: true);

        Assert.Null(Call<int?>(card, "DocumentCountFor", Row.PropertyId));

        Call<object?>(card, "OnDocumentCountChanged", Row.PropertyId, 0);
        Assert.Equal(0, Call<int?>(card, "DocumentCountFor", Row.PropertyId));

        Call<object?>(card, "OnDocumentCountChanged", Row.PropertyId, 4);
        Assert.Equal(4, Call<int?>(card, "DocumentCountFor", Row.PropertyId));
    }
}
