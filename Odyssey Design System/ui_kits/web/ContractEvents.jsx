/* ContractEvents — the "Events" section inside an expanded contract record
   (Contracts → contract detail), under Parties and Documents.

   Tracks "Contract Event Automation — Frontend (Draft v3)" / backend #154.

   A contract records what an agreement IS; an event records what has HAPPENED
   to it. The section is a chronological log: a type, a required title, an
   optional description, optional notes, and when.

   Four decisions this component draws, rather than assumes:

   • THE RAIL SHOWS TITLE AND DESCRIPTION, NEVER NOTES (§4.1). The three
     free-text fields have three roles: the title labels the entry, the
     description is the account of what happened, and notes are the user's own
     working notes. Keeping notes off the rail is the spec's own presentation
     rule — and ONLY a presentation rule: notes sit behind the same
     contracts.read claim, are searched by the same term and are exported with
     everything else, so nothing here may imply they are private.

   • THE LOG HAS TWO HALVES AND ONE CHRONOLOGY. Pausing, resuming, marking
     ready, clearing a signed date, archiving, restoring, adding or removing a
     party now each write their own row (#154 — this reverses Draft v6's
     Non-Goal 4). Those rows are marked in the ATTRIBUTION LINE and nowhere
     else — "Recorded by Jane Doe at … (automatically generated)":

       – ORIGIN AND AUTHORSHIP ARE ONE SENTENCE, by product decision, which
         overrides the frontend spec's §4 and AC 1/2/17. Those asked for a
         persistent chip in the rail item's `tag` slot, on the argument that
         `.odc-er-meta` is hover-gated and a system row (which keeps its ⋯
         menu, so it is never `.no-actions`) would show nothing until hovered.
         That is still true and is the accepted cost: on a pointer device a
         recorded row is indistinguishable from a hand-written one until the
         reader hovers it. It was taken because one sentence carrying who and
         how beats a pill per row competing with the record head's status
         vocabulary. If the marker is ever wanted back at a glance, the place
         for it is a PERSISTENT slot — never a second hover-gated one.
       – It is TEXT, in the same line as the author, so nothing is carried by
         hue or glyph and the row reads the same in greyscale.
       – NO read-only treatment. A system row carries the same two actions as
         any other, is editable and is deletable (Non-Goal 1). Editing one
         does not re-author it: `source` survives the save.
       – NO two-tier list, no grouping, no roll-up (Non-Goal 2), and NO source
         filter (Non-Goal 3).
       – Attribution is the PERSON who acted, not a service account, and reads
         "Unknown user" for a deleted author exactly as a hand-written row does.
       – The dialog still says it where a screen reader cannot miss it: the
         Title field's help text on a recorded row (§6.9), which is the one
         place the fact is programmatically wired rather than hover-gated.

   • THE LOG IS PAGED, NOT INLINED. GET /api/contracts/{id} deliberately does
     not carry events (§5): parties and files are bounded, a log is not. So the
     section reads its own endpoint, newest first, and pages. Search, the type
     filter, the date window, the sort key and `?source=` are not in v1 — the
     endpoint carries them all, the surface asks for none of them.

   • AN ARCHIVED CONTRACT STAYS WRITABLE HERE (§8.6) — as it does in every
     other section. Archival hides a contract; it does not lock it. It does now
     write an Archived event, which is a log entry, not a lock.

   No event affects ContractStatus (§4.2) — a Terminated event does not expire
   the contract, and a Paused EVENT does not pause anything; the contract's own
   edit dialog is what moves it, and this section only reports the move — so
   nothing here touches the status chip.

   Props:
     contract   — the contract record (the route's {contractId})
     events     — the page of rows (owned by the parent, as parties/files are)
     view       — 'rail' (default) | 'spine' — the spine adds the horizontal
                  overview strip ABOVE the rail; the rail always carries the log
     canUpdate  — contracts.update; false = read-only, no per-row actions.
                  PRESENTATION ONLY: the server's 403 on every write path is
                  the enforcement, never the hidden control
     pageSize   — the list query's Limit
     onEdit / onDelete — write handlers. There is no onNew: CREATING an event is
                  the contract's own action, and lives in the contract record's
                  action menu beside Add party and Attach document — not as a
                  button the section carries.
     onAnnounce — a sentence for the PAGE's single live region, raised after a
                  delete. The section mounts no announcer of its own. */

const CEV_H = window.OdysseyHelpers;
const CEV_D = window.OdysseyData;

/* The focus destination for a delete. It is an id rather than a ref because
   the heading belongs to the DS divider, and the divider only becomes
   focusable when it is given one. */
const CEV_HEADING_ID = 'con-events-heading';

/* The horizontal spine — the whole life of the contract in one strip. It is an
   OVERVIEW, never the log itself: it carries no title and no text at all,
   because a horizontal axis has nowhere to put them.

   The axis has a fixed width and the log does not have a fixed length, so the
   spine has two renderings and picks by density:
     • up to CEV_SPINE_MAX events, one clickable node each — clicking selects
       that entry in the rail below;
     • beyond it, one BAR PER MONTH, height by count and colour by the month's
       dominant type. A node per entry would be 220 overlapping circles on five
       pixels of axis apiece, which says nothing at all.
   Under two events it does not render: a single point is not a timeline. */
const CEV_SPINE_MAX = 40;

const EventSpine = ({ events, selectedId, onSelect }) => {
  if (events.length < 2) return null;
  const times = events.map(e => new Date(e.occurredAt).getTime());
  const min = Math.min(...times), max = Math.max(...times);
  const span = Math.max(max - min, 1);
  const ticks = [];
  const y0 = new Date(min).getUTCFullYear(), y1 = new Date(max).getUTCFullYear();
  for (let y = y0; y <= y1; y++) {
    const t = Date.UTC(y, 0, 1);
    if (t >= min && t <= max) ticks.push({ y, pct: ((t - min) / span) * 100 });
  }
  /* Dense: monthly bins. The mark is a bar, not a node, so nothing pretends to
     be one clickable event — the rail below is where an entry is reached. */
  if (events.length > CEV_SPINE_MAX) {
    const bins = {};
    for (const ev of events) {
      const d = new Date(ev.occurredAt);
      const k = `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}`;
      (bins[k] = bins[k] || []).push(ev);
    }
    const keys = Object.keys(bins).sort();
    const peak = Math.max(...keys.map(k => bins[k].length));
    return (
      <div className="cev-spine dense">
        <div className="cev-spine-bins" style={{ gridTemplateColumns: `repeat(${keys.length}, 1fr)` }}>
          {keys.map(k => {
            const rows = bins[k];
            const counts = {};
            for (const ev of rows) counts[ev.type] = (counts[ev.type] || 0) + 1;
            const top = Object.keys(counts).sort((a, b) => counts[b] - counts[a])[0];
            const info = CEV_H.cevTypeInfo(top);
            const [y, m] = k.split('-');
            const month = new Date(Date.UTC(+y, +m - 1, 1)).toLocaleDateString('en-GB', { month: 'short', timeZone: 'UTC' });
            return (
              <span key={k} className="cev-bin" title={`${month} ${y} · ${rows.length} event${rows.length === 1 ? '' : 's'} · mostly ${info.label}`}>
                <i style={{ height: `${Math.max(8, (rows.length / peak) * 100)}%`, background: info.color }} />
              </span>
            );
          })}
        </div>
        <div className="cev-spine-ends">
          <span>{CEV_H.cevDate(new Date(min).toISOString())}</span>
          <span>{events.length} entries · one bar per month, tallest {peak}</span>
          <span>{CEV_H.cevDate(new Date(max).toISOString())}</span>
        </div>
      </div>
    );
  }

  return (
    <div className="cev-spine">
      <div className="cev-spine-axis">
        {ticks.map(t => (
          <span key={t.y} className="cev-spine-tick" style={{ left: `${t.pct}%` }}><i />{t.y}</span>
        ))}
        {events.map(ev => {
          const info = CEV_H.cevTypeInfo(ev.type);
          const pct = ((new Date(ev.occurredAt).getTime() - min) / span) * 100;
          return (
            <button key={ev.id} type="button"
              className={`cev-spine-node${ev.id === selectedId ? ' on' : ''}`}
              style={{ left: `${pct}%`, '--cev-node': info.color }}
              aria-label={`${info.label} · ${ev.title} · ${CEV_H.cevDate(ev.occurredAt)}`}
              title={`${CEV_H.cevDate(ev.occurredAt)} · ${ev.title}`}
              onClick={() => onSelect(ev.id === selectedId ? null : ev.id)} />
          );
        })}
      </div>
      <div className="cev-spine-ends">
        <span>{CEV_H.cevDate(new Date(min).toISOString())}</span>
        <span>{CEV_H.cevDate(new Date(max).toISOString())}</span>
      </div>
    </div>
  );
};

const ContractEvents = ({ contract, events = [], view = 'rail', canUpdate = true, pageSize = 25, onEdit, onDelete, onAnnounce }) => {
  const { useState, useMemo, useEffect } = React;
  const NS = window.OdysseyDesignSystem_d5aa51 || {};
  // Resolved off the bundle namespace rather than the kit's window globals:
  // not every atom the kit uses is re-exported there. EventRail, not Timeline:
  // Timeline's rail restarts per item, and this log needs one unbroken line
  // running through the year markers between rows.
  const DSRowActions = NS.RowActions, DSPager = NS.Pager;
  const { EventRail, EventRailItem, EventRailMarker } = NS;
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState(null);

  useEffect(() => { setPage(1); }, [events]);

  /* The one ordering v1 offers: OccurredAt descending, the endpoint's default.
     Search, the Types filter and the sort key exist on the query string and are
     deliberately not surfaced yet. */
  const ordered = useMemo(
    () => events.slice().sort((a, b) => new Date(b.occurredAt) - new Date(a.occurredAt)),
    [events]);

  const total = ordered.length;
  const start = (page - 1) * pageSize;
  const rows = ordered.slice(start, start + pageSize);

  /* A delete takes the focused node off the page, so focus and the
     announcement are part of the operation, not a nicety after it.

     Focus goes to the section heading: the row is gone, the next row is not
     "the same place", and letting the browser drop focus to <body> loses a
     keyboard user's position entirely. The heading can only receive it because
     the divider is given a headingId below.

     The sentence goes UP to the page's single live announcer (Non-Goal 8) —
     this section mounts none of its own.

     The focus move is DEFERRED PAST A MACROTASK, and that is not defensive
     padding: the row's ⋯ menu restores focus to its own invoker as it closes,
     on the microtask after this handler, and a synchronous focus() here is
     silently overwritten by it — landing the user on the page's search field.
     The node is looked up again inside the timeout because the delete
     re-renders the section. Same pattern as SystemSettings'
     `setTimeout(() => opener.focus(), 0)`. */
  const removeEvent = (ev) => {
    onDelete && onDelete(ev);
    setTimeout(() => {
      const heading = document.getElementById(CEV_HEADING_ID);
      if (heading) heading.focus();
    }, 0);
    const left = Math.max(total - 1, 0);
    onAnnounce && onAnnounce(`Event deleted. ${left} ${left === 1 ? 'entry' : 'entries'} in the log.`);
  };

  if (events.length === 0) {
    return (
      <div className="con-section">
        {/* The heading stays focusable even here: deleting the last row lands
            on this state, and the focus target has to survive the transition. */}
        <SectionDivider label="Events" headingId={CEV_HEADING_ID} meta="0 entries" />
        {/* Both halves of the log, in that order — what Odyssey records, then
            what the user can add. The old copy named only the second. */}
        <EmptyLine>
          No events yet. Odyssey records what it does to this agreement — pausing it, signing it,
          archiving it — and you can add anything else: notice given, a price renegotiated, an email
          sent.
        </EmptyLine>
      </div>
    );
  }

  /* A flat track, not a stack of per-year lists: the line runs unbroken from
     the newest entry to the oldest, and a year is a marker sitting ON it.

     The top is ANCHORED to TODAY on page 1: newest-first means the top of the
     rail is the present, the log is live, and the next entry lands there.

     The other anchor is WHEN THE CONTRACT RECORD WAS ADDED — not its start
     date. The two are different facts and the start date is the wrong one: a
     contract can start in 2026 and still carry a "signed" event from 2025, so
     pinning the foot of the rail to the start would put an event below its own
     origin. The record's creation is the moment the log became possible.

     It is placed CHRONOLOGICALLY, not pinned to the bottom, because an event
     may legitimately be backdated before the record was added — a history
     entered after the fact. Usually it is older than everything and lands at
     the foot anyway, which is the anchor this is for. */
  const track = [];
  const firstPage = page === 1;
  const lastPage = start + rows.length >= total;
  if (firstPage) track.push({ cap: 'now' });
  // The Today cap already carries the current year, so a marker repeating it
  // one row later says nothing.
  let lastYear = firstPage ? new Date().getUTCFullYear() : null;
  for (const ev of rows) {
    const y = CEV_H.cevYear(ev.occurredAt);
    if (y !== lastYear) { track.push({ tick: y }); lastYear = y; }
    track.push({ ev });
  }
  const addedAt = contract.createdAtUtc;
  if (addedAt) {
    const cap = {
      cap: 'origin',
      text: `Contract added ${CEV_H.cevDate(addedAt)}`,
      /* Contract.CreatedByUserId — REQUESTED, not yet shipped. Resolved through
         the same claim-aware label resolver as an event's, so a deleted author
         reads "Unknown user" here too. Until the field lands this reads as an
         unknown author rather than disappearing, which is also exactly how a
         SET NULL row will read. */
      by: CEV_H.cevCreatedBy(contract.createdByUserId),
    };
    // Where it falls among this page's rows — appended only when this page
    // actually holds the end of the log.
    const at = track.findIndex(x => x.ev && new Date(x.ev.occurredAt) < new Date(addedAt));
    if (at !== -1) track.splice(at, 0, cap);
    else if (lastPage) track.push(cap);
  }

  /* One entry: the title, then the description under it, clamped. Notes are
     not here — that is §4.1's presentation rule, and the dialog is where they
     are read and written. */
  const item = (ev) => {
    const info = CEV_H.cevTypeInfo(ev.type);
    const system = CEV_H.cevIsSystem(ev);
    return (
      /* v1: the node is neutral — no `color` passed. The glyph carries the
         type, and holding the hue back keeps eighteen colours from competing
         with the status vocabulary the record head already uses. The row
         actions sit inline after the date rather than pinned to the card edge:
         they belong to the entry being read. */
      <EventRailItem key={ev.id} icon={info.icon} iconLabel={info.label}
        selected={ev.id === selected}
        title={ev.title}
        date={CEV_H.cevDateTime(ev.occurredAt)}
        desc={ev.description || undefined}
        actions={canUpdate ? (
          /* The same two actions on a system row as on any other (Non-Goal 1). */
          <DSRowActions actions={[
            { icon: 'edit', label: `Edit ${ev.title}`, onClick: () => onEdit && onEdit(ev) },
            { icon: 'delete', label: `Delete ${ev.title}`, danger: true, onClick: () => removeEvent(ev) },
          ]} />
        ) : undefined}>
        {/* Attribution is a display LABEL from the claim-aware resolver —
            never the raw CreatedByUserId. A deleted author reads Unknown.

            ORIGIN RIDES THE SAME SENTENCE. On a recorded row the line closes
            with "(automatically generated)" — text, in the author's own line,
            so who and how are read together. It inherits this slot's
            hover-reveal, which is the tradeoff the product decision accepted;
            see the note at the top of this file. */}
        <div className={`odc-er-meta${ev.createdByUserId ? '' : ' cev-by-unknown'}`}>
          Recorded by <span className="cev-by-who">{CEV_H.cevCreatedBy(ev.createdByUserId)}</span> at {CEV_H.cevDateTime(ev.createdAtUtc)}
          {system ? <span className="cev-auto-note"> (automatically generated)</span> : null}
        </div>
      </EventRailItem>
    );
  };

  return (
    <div className="con-section">
      <SectionDivider label="Events" headingId={CEV_HEADING_ID}
        meta={`${total} ${total === 1 ? 'entry' : 'entries'} · newest first`} />

      {view === 'spine' ? <EventSpine events={ordered} selectedId={selected} onSelect={setSelected} /> : null}

      <div className="cev-rail">
        <EventRail capTop={firstPage} capEnd={lastPage}>
          {track.map(x => {
            if (x.cap === 'now') return <EventRailMarker tone="open" key="cap-now">Today · {CEV_H.cevDate(new Date().toISOString())}</EventRailMarker>;
            if (x.cap === 'origin') return <EventRailMarker tone="filled" key="cap-origin" meta={<React.Fragment>Added by <span className="cev-by-who">{x.by}</span> at {CEV_H.cevDateTime(addedAt)}</React.Fragment>}>{x.text}</EventRailMarker>;
            if (x.tick) return <EventRailMarker key={`y${x.tick}`}>{x.tick}</EventRailMarker>;
            return item(x.ev);
          })}
        </EventRail>
      </div>

      {total > pageSize ? (
        <div>
          <DSPager page={page} pageSize={pageSize} totalCount={total} showPageSize={false}
            onPageChange={setPage} label="Event log pagination" />
        </div>
      ) : null}
    </div>
  );
};

Object.assign(window, { ContractEvents, EventSpine });
