using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Pages.Internet;
using Odyssey.Client.Services;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The snackbar provider (issue #473 §6.7, AC 29, 37): configured once, and its container is NOT a live region — so a
/// page's own announcer is what speaks a status change, exactly once, and the snackbar never says it a second time.
/// </summary>
public class NewsSnackbarTests
{
    private static BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices(config => SnackbarDefaults.Configure(config.SnackbarConfiguration));
        return ctx;
    }

    [Fact]
    public async Task The_app_configuration_is_bottom_right_with_a_close_icon_and_no_duplicates()
    {
        await using var ctx = Context();
        var snackbar = ctx.Services.GetRequiredService<ISnackbar>();

        Assert.Equal(Defaults.Classes.Position.BottomRight, snackbar.Configuration.PositionClass);
        Assert.True(snackbar.Configuration.ShowCloseIcon);
        Assert.True(snackbar.Configuration.PreventDuplicates);
    }

    /// <summary>
    /// §6.7: "A component test asserts that the snackbar container is not a live region. If MudBlazor makes it one,
    /// the announcer call is skipped, so the message is never spoken twice." MudBlazor 9 does make it one, so this
    /// pins both halves together: the provider's live roles and <see cref="NewsFeedback.SnackbarSpeaks"/> agree, and a
    /// MudBlazor that drops them fails here rather than leaving every news message unspoken.
    /// </summary>
    [Theory]
    [InlineData(Severity.Normal)]
    [InlineData(Severity.Success)]
    [InlineData(Severity.Error)]
    public async Task The_announcer_is_skipped_exactly_when_the_snackbar_container_is_a_live_region(Severity severity)
    {
        await using var ctx = Context();
        var cut = ctx.Render<MudSnackbarProvider>();
        var snackbar = ctx.Services.GetRequiredService<ISnackbar>();

        await cut.InvokeAsync(() => snackbar.Add("1,234 articles marked as read.", severity, c => c.RequireInteraction = severity == Severity.Error));

        cut.WaitForAssertion(() => Assert.Contains("1,234 articles marked as read.", cut.Markup, StringComparison.Ordinal));
        var live = cut.FindAll("[aria-live], [role=status], [role=alert], [role=log]").Count > 0;
        Assert.Equal(NewsFeedback.SnackbarSpeaks, live);
        Assert.Equal(live ? null : "x", NewsFeedback.Spoken("x"));
    }

    [Fact]
    public void Error_snackbars_stay_until_dismissed()
    {
        var options = new SnackbarOptions(Severity.Error, new SnackbarConfiguration());
        var snackbar = new Moq.Mock<ISnackbar>();
        snackbar.Setup(s => s.Add(Moq.It.IsAny<string>(), Moq.It.IsAny<Severity>(), Moq.It.IsAny<Action<SnackbarOptions>>(), Moq.It.IsAny<string>()))
            .Callback((string _, Severity _, Action<SnackbarOptions>? configure, string? _) => configure?.Invoke(options));

        NewsFeedback.Failure(snackbar.Object, new Odyssey.ApiClient.ApiProblem { Detail = "x" }, "Couldn't save");

        Assert.True(options.RequireInteraction);
    }
}
