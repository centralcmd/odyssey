using Bunit;
using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The problem rows' action (issue #473 §4.2, AC 62): the trailing arrow is <c>aria-hidden</c> on every
/// page, and <see cref="PageHeaderProblem.ActionAriaLabel"/> overrides the action's name when set.
/// </summary>
public class PageHeaderProblemActionTests
{
    private static IRenderedComponent<PageHeader> Render(BunitContext ctx, string? actionAriaLabel) =>
        ctx.Render<PageHeader>(p => p
            .Add(h => h.Title, "News")
            .Add(h => h.ProblemsLabel, "Attention")
            .Add(h => h.Problems, [new PageHeaderProblem
            {
                Lead = "Example News",
                Message = "Failing.",
                ViewLabel = "Review",
                ActionAriaLabel = actionAriaLabel,
                OnView = EventCallback.Factory.Create(new object(), () => { }),
            }]));

    [Fact]
    public void The_arrow_is_aria_hidden_and_the_name_is_the_label_alone()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var action = Render(ctx, null).Find("button.alert-fix");

        var arrow = action.QuerySelector("span[aria-hidden='true']")!;
        Assert.Equal("→", arrow.TextContent);
        Assert.False(action.HasAttribute("aria-label"));
        Assert.Equal("Review", OdsInfiniteListRemoteTests.SpokenText(action));
        Assert.Equal("Review →", action.TextContent.Trim());   // the visible text is unchanged
    }

    [Fact]
    public void ActionAriaLabel_overrides_the_actions_name()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var action = Render(ctx, "Review Example News on Feeds").Find("button.alert-fix");

        Assert.Equal("Review Example News on Feeds", action.GetAttribute("aria-label"));
    }

    [Fact]
    public void The_problems_toggle_keeps_its_label_count_name()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var toggle = Render(ctx, "Review Example News").Find("button.ph-signal-btn");

        Assert.Equal("Attention, 1", System.Text.RegularExpressions.Regex.Replace(toggle.TextContent, @"\s+", " ").Trim());
    }
}
