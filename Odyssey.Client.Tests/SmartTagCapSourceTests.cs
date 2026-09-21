using System.Text.RegularExpressions;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Source-lints for the per-account smart-tag cap (issue #434 key 15), joining the upload-cap lints in
/// <see cref="UploadCapSourceTests"/>.
///
/// <para>
/// <c>AccountSmartTagsSection</c> held the cap as <c>private const int MaxTags = 20</c>, mirroring a
/// server constant. Once the server value became admin-editable that mirror was the defect class
/// <c>CLAUDE.md</c> names outright: lowering the setting would let a user add tags the server then
/// refused, and raising it would be unusable because the local pre-check still stopped at 20.
/// </para>
///
/// <para>
/// Lints rather than reflection tests, for the same reason as the upload ones: the defect is a literal
/// in source. A page that reintroduces its own constant compiles, passes every behavioural test, and
/// simply stops honouring the administrator's value.
/// </para>
/// </summary>
public class SmartTagCapSourceTests
{
    /// <summary>
    /// Scoped to <c>Pages/</c>, matching both existing cap lints. Scoping to "any client file" would flag
    /// <see cref="AccountLimitsCache.Fallback"/> — the ONE place the number legitimately appears — which
    /// is a false positive that trains people to weaken the test.
    /// </summary>
    private static IEnumerable<(string File, string Text)> SmartTagPages() =>
        ClientSource.RazorFilesIn("Pages")
            .Select(file => (File: file, Text: File.ReadAllText(file)))
            .Where(pair => pair.Text.Contains("SmartTag", StringComparison.Ordinal));

    /// <summary>
    /// The same text with <c>//</c> comment bodies blanked, line numbers preserved. A lint a COMMENT can
    /// trip is a bad lint: the note recording what the deleted constant used to be quotes it verbatim,
    /// and that note is worth keeping.
    /// </summary>
    private static string WithoutLineComments(string text) =>
        Regex.Replace(text, @"//[^\r\n]*", string.Empty);

    [Fact]
    public void No_page_declares_its_own_smart_tag_cap_constant()
    {
        var offenders = new List<string>();

        foreach (var (file, text) in SmartTagPages())
        {
            foreach (Match match in Regex.Matches(
                WithoutLineComments(text), @"const\s+int\s+Max(Tags|SmartTags)\w*\s*="))
            {
                offenders.Add($"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, match.Index)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Pages holding their own smart-tag cap constant — it must come from IAccountLimitsCache, or "
            + "an administrator's change reaches the server and not the pre-check: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// A hardcoded cap number in a smart-tag page's user-visible text. The message must name the number
    /// actually in force, or it goes stale the moment an administrator changes the setting — and the old
    /// text named no number at all, which left the user guessing what the limit was.
    /// </summary>
    [Fact]
    public void No_page_states_a_literal_smart_tag_limit()
    {
        var offenders = new List<string>();

        foreach (var (file, text) in SmartTagPages())
        {
            foreach (Match match in Regex.Matches(
                WithoutLineComments(text), @"[Tt]ag limit[^<@\r\n]*\b\d+\b"))
            {
                offenders.Add($"{ClientSource.Relative(file)}:{ClientSource.LineAt(text, match.Index)} ('{match.Value}')");
            }
        }

        Assert.True(offenders.Count == 0,
            "Smart-tag pages stating a literal cap — interpolate the effective one instead: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The at-cap message must actually interpolate the cap. Without this the two lints above are
    /// satisfiable by simply not mentioning a number at all — which is the state the component shipped
    /// in, and the reason satisfying "interpolate the effective number" meant authoring a NEW string
    /// rather than editing an existing one.
    ///
    /// <para>
    /// Issue #166 moved the composition up: the adder now renders a ready-made <c>FootNote</c>, because
    /// there is a second advisory (a degraded limits read) with no number to name at all. So the
    /// assertion follows it — the adder must still RENDER the sentence, and the section must still
    /// build it from the live <c>_maxTags</c> rather than from a literal.
    /// </para>
    /// </summary>
    [Fact]
    public void The_at_cap_message_interpolates_the_cap()
    {
        var adder = File.ReadAllText(
            Path.Combine(ClientSource.Root, "Pages", "Finance", "AccountSmartTagAdder.razor"));

        Assert.Contains("@FootNote", adder, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public string? FootNote", adder, StringComparison.Ordinal);

        var section = File.ReadAllText(
            Path.Combine(ClientSource.Root, "Pages", "Finance", "AccountSmartTagsSection.razor.cs"));

        // The number in the sentence is the LIVE one. A literal here is the defect the whole file exists
        // to prevent, and it would satisfy every other assertion in it.
        Assert.Contains("{_maxTags}", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contract host reads its own cap, from its own claim-free endpoint, through its own cache —
    /// never the account one (issue #166). Sharing a cache would make an administrator's contract cap
    /// pre-check against the account number and vice versa.
    /// </summary>
    [Fact]
    public void The_contract_host_reads_the_contract_cap()
    {
        var section = File.ReadAllText(
            Path.Combine(ClientSource.Root, "Pages", "Finance", "AccountSmartTagsSection.razor.cs"));

        Assert.Contains("ContractLimits.GetAsync()", section, StringComparison.Ordinal);
        Assert.Contains("MaxSmartTagsPerContract", section, StringComparison.Ordinal);
        Assert.Equal(
            SystemSettingsDefaults.ContractMaxSmartTagsPerContract,
            ContractLimitsCache.FallbackMaxSmartTagsPerContract);
    }

    /// <summary>
    /// A degraded limits read stops the client-side pre-check rather than guessing a ceiling
    /// (issue #166). Without this the section would fall back to the shipped default and block adds an
    /// administrator had raised the cap to allow — a guessed number presented as the configured one.
    /// </summary>
    [Fact]
    public void A_degraded_limits_read_stops_the_pre_check_rather_than_guessing()
    {
        var section = File.ReadAllText(
            Path.Combine(ClientSource.Root, "Pages", "Finance", "AccountSmartTagsSection.razor.cs"));

        // The cap is only "known" while the read is healthy, and only a known cap can block an add.
        Assert.Contains("_capKnown => !_limitsDegraded", section, StringComparison.Ordinal);
        Assert.Contains("_atCap => _capKnown &&", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// The section must read the live cap rather than assuming a value, and the fallback must be the
    /// shared constant — the same one the migration seeds and the server's <c>[Range]</c> names — rather
    /// than a hopeful literal.
    /// </summary>
    [Fact]
    public void The_section_reads_the_live_cap_and_falls_back_to_the_shared_default()
    {
        var section = File.ReadAllText(
            Path.Combine(ClientSource.Root, "Pages", "Finance", "AccountSmartTagsSection.razor.cs"));

        Assert.Contains("AccountLimits.GetAsync()", section, StringComparison.Ordinal);
        Assert.Equal(
            SystemSettingsDefaults.AccountMaxSmartTagsPerAccount,
            AccountLimitsCache.Fallback.MaxSmartTagsPerAccount);
    }

    /// <summary>
    /// There is deliberately no disable-on-failure branch. <see cref="AccountLimitsCache.GetAsync"/>
    /// cannot fail — it ends in <c>?? Fallback</c> — the upload surfaces this mirrors do not disable
    /// either, and the server remains the control. Fail-closed here would also be fail-closed in the
    /// wrong layer.
    /// </summary>
    [Fact]
    public void The_cache_cannot_fail_so_there_is_no_disable_branch()
    {
        var cache = File.ReadAllText(
            Path.Combine(ClientSource.Root, "Services", "AccountLimitsCache.cs"));

        Assert.Contains("result ?? Fallback", cache, StringComparison.Ordinal);
    }
}
