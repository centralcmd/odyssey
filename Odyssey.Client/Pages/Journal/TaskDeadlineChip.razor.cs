using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;

namespace Odyssey.Client.Pages.Journal;

public partial class TaskDeadlineChip
{
    [Parameter] public DateOnly? Deadline { get; set; }

    private static DateOnly TodayLocal => DateOnly.FromDateTime(DateTime.Now);

    private static OdsChipTone Tone(DateOnly d)
    {
        var n = d.DayNumber - TodayLocal.DayNumber;
        if (n < 0)
        {
            return OdsChipTone.Expense;
        }

        return n <= 3 ? OdsChipTone.Pending : OdsChipTone.Outline;
    }

    private static string RelativeLabel(DateOnly d)
    {
        var n = d.DayNumber - TodayLocal.DayNumber;
        if (n < 0)
        {
            return $"{Math.Abs(n)}d overdue";
        }

        return n switch
        {
            0 => "due today",
            1 => "due tomorrow",
            _ => $"in {n}d",
        };
    }
}
