import * as React from 'react';

/** A calendar event as the grid consumes it (a flattened view-model, already
 *  resolved to its calendar's colour). Times are local wall-clock ISO strings
 *  (`YYYY-MM-DDTHH:mm`). For an all-day event `end` is EXCLUSIVE — the midnight
 *  after the last day — mirroring the API's storage semantics. */
export interface CalendarEventVM {
  id: string;
  calendarId: string;
  title: string;
  /** Start, ISO `YYYY-MM-DDTHH:mm`. */
  start: string;
  /** End, ISO `YYYY-MM-DDTHH:mm`. Exclusive when `isAllDay`. */
  end: string;
  isAllDay?: boolean;
  /** Calendar swatch hex. */
  color: string;
  /** Baked, contrast-safe foreground for `color`. */
  fg: string;
  /** True when this row is one occurrence of a recurring series (shows a glyph). */
  recurring?: boolean;
}

export interface CalendarGridProps {
  /** Any date inside the month to display — ISO `YYYY-MM-DD` / `YYYY-MM`, or a Date. */
  month: string | Date;
  events: CalendarEventVM[];
  /** Calendars whose events are drawn. Omit/null = all events shown. */
  visibleCalendarIds?: Iterable<string> | null;
  /** Empty-cell click → start a new event on that day (ISO `YYYY-MM-DD`). */
  onDayClick?: (iso: string) => void;
  /** Chip / popover-row click → open that event. */
  onEventClick?: (id: string) => void;
  /** Drag a chip onto another day → reschedule. Fires with the event id, the
   *  drop day (ISO `YYYY-MM-DD`), and the day the chip was grabbed from (so the
   *  consumer can shift a multi-day event by the day delta). Enables drag when set. */
  onEventDrop?: (id: string, toDate: string, fromDate: string) => void;
  /** Max item rows before a cell collapses the remainder into "+N more". Default 3. */
  maxPerDay?: number;
  /** 0 = Sunday-first (default), 1 = Monday-first. */
  weekStartsOn?: 0 | 1;
  /** Override "today" (ISO) — for specimens/tests. Defaults to the real today. */
  today?: string;
}

/** The month grid — day cells with colour-coded event chips, multi-day all-day
 *  events as flush spanning strips, per-cell "+N more" overflow popover, a
 *  roving-tabindex keyboard grid and full ARIA. The Calendar page's primary
 *  view; week/day/agenda are page-level compositions around the same data. */
export declare function CalendarGrid(props: CalendarGridProps): JSX.Element;
