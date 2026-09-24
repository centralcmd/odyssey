namespace Odyssey.Dtos.Finance;

public enum TermValueUnit
{
    Percentage = 0,
    Amount = 1,

    /// <summary>One plain line of free text (issue #192). Contract-owned terms only.</summary>
    Text = 2,

    /// <summary>An instant, stored in UTC (issue #192). Contract-owned terms only.</summary>
    DateTime = 3,
}
