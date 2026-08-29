using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos;

namespace Odyssey.Dtos.Journal;

public sealed class CalendarsQueryParams : QueryParams<CalendarSortBy>
{
}

public sealed class CalendarEventsQueryParams : QueryParams<CalendarEventSortBy>
{
    // Nullable at the binding layer (a missing non-nullable value type silently defaults rather than
    // failing to bind); CalendarEventService enforces that both are present and span <= 92 days,
    // returning a 400 DomainValidationException otherwise.
    public DateTime? From { get; set; } // UTC

    public DateTime? To { get; set; } // UTC

    public Guid[]? CalendarIds { get; set; }
}

public sealed class RecurrencePatternsQueryParams : QueryParams<RecurrencePatternSortBy>
{
    public Guid? CalendarId { get; set; }
}

// Query contract for the aggregate/filtered .ics export (issue #340). Deliberately does NOT derive
// from QueryParams<TSortBy> — export returns the full matched set as one file, so paging/sorting have
// no meaning here and are omitted rather than accepted-and-ignored.
public sealed class CalendarEventsIcsExportQueryParams
{
    // Nullable at the binding layer; both-or-neither, span <= 92 days, and To >= From are enforced by
    // CalendarIcsService (400 DomainValidationException otherwise). Omitting both is valid and means
    // "no date bound" — a deliberate deviation from CalendarEventsQueryParams' both-required rule.
    public DateTime? From { get; set; } // UTC

    public DateTime? To { get; set; } // UTC

    public Guid[]? CalendarIds { get; set; }

    // Matches Title only (EF.Functions.Like), same as CalendarEventsQueryParams.Search — not Location.
    [StringLength(ListDefaults.MaxSearchLength)]
    public string? Search { get; set; }
}
