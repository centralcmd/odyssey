/* PropertyEvents — the "Events" section, the last zone in an expanded property
   record. Frontend for *Property Events — Backend (Draft v2)* / issue #167.

   The same log as a contract's (ContractEvents.jsx), on the same DS EventRail,
   with the same attribution sentence, the same Today / "added" anchors and the
   same no-create-button rule: "New event" lives in the property's ⋯ menu.

   What differs, and why:

   • THE TYPE LIST DEPENDS ON THE PROPERTY'S TYPE. PropertyEventTypeMatrix
     makes 17 of 21 members legal for each type. The dialog only offers
     those, so the 422 is never met through the UI.

   • NO FILTERS IN V1. The endpoint carries search, types, from/to, source and
     the sort keys; the section surfaces none of them — newest first, paged,
     exactly as on contracts.

   • SYSTEM ROWS COME FROM THREE PROPERTY FIELDS ONLY: acquired date, disposed
     date, archive. Never from estimates (a properties.read reader must not
     learn what properties.estimates.read withholds) and never from detail
     edits. The empty state names exactly those three.

   • THE FOOT MARKER HAS NO AUTHOR. Property carries CreatedAt but no
     CreatedByUserId, so the marker is a date only. An Acquired event is
     usually older than the record (the purchase came before the data entry)
     and correctly sorts below it.

   No event changes the property: a hand-written Disposed does not set
   DisposedDate (Non-Goal 5), so nothing here touches the status chip.

   Props: property, events, canUpdate (properties.update — presentation only),
   pageSize, onEdit, onDelete, onAnnounce (the record's single live region). */

const PEV_H = window.OdysseyHelpers;
const PEV_D = window.OdysseyData;

const PropertyEvents = ({ property, events = [], canUpdate = true, pageSize = 25, onEdit, onDelete, onAnnounce }) => {
  const { useState, useMemo, useEffect } = React;
  const NS = window.OdysseyDesignSystem_d5aa51 || {};
  const DSRowActions = NS.RowActions, DSPager = NS.Pager;
  const { EventRail, EventRailItem, EventRailMarker } = NS;
  const headingId = `prop-events-heading-${property.id}`;
  const [page, setPage] = useState(1);
  useEffect(() => { setPage(1); }, [events]);

  const filtered = useMemo(
    () => events.slice().sort((a, b) => new Date(b.occurredAt) - new Date(a.occurredAt)),
    [events]);

  const total = filtered.length;
  const start = (page - 1) * pageSize;
  const rows = filtered.slice(start, start + pageSize);

  const removeEvent = (ev) => {
    onDelete && onDelete(ev);
    setTimeout(() => { const h = document.getElementById(headingId); if (h) h.focus(); }, 0);
    const left = Math.max(events.length - 1, 0);
    onAnnounce && onAnnounce(`Event deleted. ${left} ${left === 1 ? 'entry' : 'entries'} in the log.`);
  };

  const examples = property.type === 'Vehicle' ? 'a service, a tyre change, the periodic inspection' : 'a repair, a renovation, a tenancy';

  if (events.length === 0) {
    return (
      <div className="con-section">
        <SectionDivider label="Events" headingId={headingId} meta="0 entries" />
        <EmptyLine>
          No events yet. Odyssey records when this property is acquired, disposed of, archived or restored,
          and you can add anything else: {examples}.
        </EmptyLine>
      </div>
    );
  }

  /* Anchors: Today on page 1, the record's creation placed chronologically. */
  const track = [];
  const firstPage = page === 1;
  const lastPage = start + rows.length >= total;
  if (firstPage) track.push({ cap: 'now' });
  let lastYear = firstPage ? new Date().getUTCFullYear() : null;
  for (const ev of rows) {
    const y = PEV_H.cevYear(ev.occurredAt);
    if (y !== lastYear) { track.push({ tick: y }); lastYear = y; }
    track.push({ ev });
  }
  const addedAt = property.createdAt;
  if (addedAt) {
    const cap = { cap: 'origin', text: `Property added ${PEV_H.cevDate(addedAt)}` };
    let at = track.findIndex(x => x.ev && new Date(x.ev.occurredAt) < new Date(addedAt));
    // Never land under an older year's tick: go above it when the years differ.
    if (at > 0 && track[at - 1].tick && track[at - 1].tick !== PEV_H.cevYear(addedAt)) at -= 1;
    if (at !== -1) track.splice(at, 0, cap);
    else if (lastPage) track.push(cap);
  }

  const item = (ev) => {
    const info = PEV_H.pevTypeInfo(ev.type);
    const system = PEV_H.pevIsSystem(ev);
    return (
      <EventRailItem key={ev.id} icon={info.icon} iconLabel={info.label}
        title={ev.title}
        date={PEV_H.cevDateTime(ev.occurredAt)}
        desc={ev.description || undefined}
        actions={canUpdate ? (
          <DSRowActions actions={[
            { icon: 'edit', label: `Edit ${ev.title}`, onClick: () => onEdit && onEdit(ev) },
            { icon: 'delete', label: `Delete ${ev.title}`, danger: true, onClick: () => removeEvent(ev) },
          ]} />
        ) : undefined}>
        <div className={`odc-er-meta${ev.createdByUserId ? '' : ' cev-by-unknown'}`}>
          Recorded by <span className="cev-by-who">{PEV_H.cevCreatedBy(ev.createdByUserId)}</span> at {PEV_H.cevDateTime(ev.createdAtUtc)}
          {system ? <span className="cev-auto-note"> (automatically generated)</span> : null}
        </div>
      </EventRailItem>
    );
  };

  const meta = `${total} ${total === 1 ? 'entry' : 'entries'} · newest first`;

  return (
    <div className="con-section">
      <SectionDivider label="Events" headingId={headingId} meta={meta} />
      <div className="cev-rail">
          <EventRail capTop={firstPage} capEnd={lastPage}>
            {track.map(x => {
              if (x.cap === 'now') return <EventRailMarker tone="open" key="cap-now">Today · {PEV_H.cevDate(new Date().toISOString())}</EventRailMarker>;
              if (x.cap === 'origin') return <EventRailMarker tone="filled" key="cap-origin">{x.text}</EventRailMarker>;
              if (x.tick) return <EventRailMarker key={`y${x.tick}`}>{x.tick}</EventRailMarker>;
              return item(x.ev);
            })}
          </EventRail>
        </div>

      {total > pageSize ? (
        <DSPager page={page} pageSize={pageSize} totalCount={total} showPageSize={false}
          onPageChange={setPage} label="Event log pagination" />
      ) : null}
    </div>
  );
};

Object.assign(window, { PropertyEvents });
