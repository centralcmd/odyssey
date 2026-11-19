using Odyssey.Client.Pages.Finance.Instruments;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The price-source panel's own words (design system · ui_kits/web/instruments-data.js and
/// InstrumentPriceSources.jsx): the four failures a source reports, the empty list and the
/// every-source-added note naming the instrument's type.
/// </summary>
public class InstrumentPriceSourcesCopyTests
{
    [Theory]
    [InlineData(JobFailureCategory.ProviderRateLimited, "Rate limited")]
    [InlineData(JobFailureCategory.UnsupportedInput, "Not recognised by the source")]
    [InlineData(JobFailureCategory.Configuration, "Credential problem")]
    [InlineData(JobFailureCategory.ProviderUnavailable, "Couldn’t reach the source")]
    public void A_source_failure_reads_in_the_panels_words(JobFailureCategory failure, string expected) =>
        Assert.Equal(expected, InstrumentPriceSourcesPanel.FailureWord(failure));

    [Fact]
    public void Any_other_failure_keeps_the_run_logs_word() =>
        Assert.Equal(
            Odyssey.Client.Pages.JobRuns.BackgroundJobPresentation.FailureOf(JobFailureCategory.Internal),
            InstrumentPriceSourcesPanel.FailureWord(JobFailureCategory.Internal));

    [Theory]
    [InlineData(true, "No price sources yet. Prices are entered by hand until you add one.")]
    [InlineData(false, "No price sources yet. Prices are entered by hand.")]
    public void The_empty_list_says_prices_are_entered_by_hand(bool editable, string expected) =>
        Assert.Equal(expected, InstrumentPriceSourcesPanel.NoSourcesText(editable));

    [Theory]
    [InlineData(InstrumentType.Stock, "stocks")]
    [InlineData(InstrumentType.Etf, "ETFs")]
    [InlineData(InstrumentType.Fund, "funds")]
    public void The_all_added_note_names_the_type(InstrumentType type, string plural) =>
        Assert.Equal($"Every installed source that supports {plural} is already added.",
            InstrumentPriceSourcesPanel.AllAddedText(type));
}
