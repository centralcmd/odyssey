/**
 * Odyssey DS — TaskBoard
 * The three-column kanban board for the to-do list: **Backlog · Doing · Done**
 * (Archived is not a board column — it's hidden from the board and reached via a
 * status filter). Each column is a labelled landmark region whose heading states
 * its name and count as text. Cards expose their status in text (via the
 * consumer's `renderCard`, which should include a TodoStatusChip) — never by
 * column position alone.
 *
 * Interaction is dual-path (accessibility): pointer users drag cards within and
 * across columns (HTML5 DnD); keyboard users get an always-present button
 * cluster on every card — move up / down within the column and move to the
 * previous / next column (disabled at the ends) — each a ≥24px control with an
 * accessible name. Every move (drag or button) is announced through a single
 * polite live region.
 *
 * Controlled: `tasks` is the full array (each `{ id, status, position }` + your
 * own fields); ordering within a column is by ascending `position`. Every move
 * calls `onMove(id, toStatus, toIndex)` — the consumer re-sequences and updates
 * state. `renderCard(task)` renders the card body. Styled by `.odc-board`.
 */

const ODC_BOARD_COLUMNS = [
  { key: 'Backlog', label: 'Backlog' },
  { key: 'Doing',   label: 'Doing' },
  { key: 'Done',    label: 'Done' },
];

export function TaskBoard({
  tasks = [],
  columns = ODC_BOARD_COLUMNS,
  renderCard,
  onMove,
  emptyColumnText = 'Nothing here yet.',
  className = '',
  style,
}) {
  const { useState, useRef, useCallback } = React;
  const [dragId, setDragId] = useState(null);
  const [overCol, setOverCol] = useState(null);
  const liveRef = useRef(null);

  const colKeys = columns.map((c) => c.key);
  const byCol = {};
  colKeys.forEach((k) => { byCol[k] = []; });
  tasks.forEach((t) => { if (byCol[t.status]) byCol[t.status].push(t); });
  colKeys.forEach((k) => byCol[k].sort((a, b) => (a.position ?? 0) - (b.position ?? 0)));

  const announce = useCallback((msg) => {
    if (liveRef.current) liveRef.current.textContent = msg;
  }, []);

  const move = useCallback((id, toStatus, toIndex, verb) => {
    if (!onMove) return;
    const t = tasks.find((x) => x.id === id);
    onMove(id, toStatus, toIndex);
    if (t) {
      const label = columns.find((c) => c.key === toStatus);
      announce(`${t.title || 'Task'} ${verb} ${label ? label.label : toStatus}, position ${toIndex + 1}.`);
    }
  }, [onMove, tasks, columns, announce]);

  const moveWithin = (t, dir) => {
    const list = byCol[t.status];
    const i = list.findIndex((x) => x.id === t.id);
    const j = i + dir;
    if (j < 0 || j >= list.length) return;
    move(t.id, t.status, j, 'reordered in');
  };
  const moveColumn = (t, dir) => {
    const ci = colKeys.indexOf(t.status);
    const cj = ci + dir;
    if (cj < 0 || cj >= colKeys.length) return;
    move(t.id, colKeys[cj], byCol[colKeys[cj]].length, 'moved to');
  };

  const onDropAt = (toStatus, toIndex) => {
    if (dragId == null) return;
    move(dragId, toStatus, toIndex, 'moved to');
    setDragId(null);
    setOverCol(null);
  };

  return (
    <div className={`odc-board${className ? ' ' + className : ''}`} style={style}>
      <div className="odc-board-live" aria-live="polite" role="status" ref={liveRef} />
      {columns.map((col) => {
        const list = byCol[col.key] || [];
        const ci = colKeys.indexOf(col.key);
        return (
          <section
            key={col.key}
            className={`odc-board-col${overCol === col.key ? ' dragover' : ''}`}
            aria-label={`${col.label} (${list.length})`}
            onDragOver={(e) => { if (dragId != null) { e.preventDefault(); setOverCol(col.key); } }}
            onDragLeave={(e) => { if (e.currentTarget === e.target) setOverCol(null); }}
            onDrop={(e) => { e.preventDefault(); onDropAt(col.key, list.length); }}>
            <header className="odc-board-colhead">
              <span className="odc-board-coltitle">{col.label}</span>
              <span className="odc-board-colcount">{list.length}</span>
            </header>

            <div className="odc-board-cards">
              {list.length === 0 ? (
                <div className="odc-board-empty">{emptyColumnText}</div>
              ) : (
                list.map((t, i) => (
                  <article
                    key={t.id}
                    className={`odc-board-card${dragId === t.id ? ' dragging' : ''}`}
                    draggable
                    onDragStart={(e) => { setDragId(t.id); e.dataTransfer.effectAllowed = 'move'; }}
                    onDragEnd={() => { setDragId(null); setOverCol(null); }}
                    onDragOver={(e) => { if (dragId != null && dragId !== t.id) e.preventDefault(); }}
                    onDrop={(e) => { e.preventDefault(); e.stopPropagation(); onDropAt(col.key, i); }}>
                    <div className="odc-board-cardbody">
                      {renderCard ? renderCard(t) : <div className="odc-board-cardtitle">{t.title}</div>}
                    </div>
                    <div className="odc-board-moves" role="group" aria-label={`Move ${t.title || 'task'}`}>
                      <button type="button" className="odc-board-move" aria-label="Move to previous column"
                        disabled={ci === 0} onClick={() => moveColumn(t, -1)}>
                        <span className="material-icons" aria-hidden="true">chevron_left</span>
                      </button>
                      <button type="button" className="odc-board-move" aria-label="Move up"
                        disabled={i === 0} onClick={() => moveWithin(t, -1)}>
                        <span className="material-icons" aria-hidden="true">keyboard_arrow_up</span>
                      </button>
                      <button type="button" className="odc-board-move" aria-label="Move down"
                        disabled={i === list.length - 1} onClick={() => moveWithin(t, 1)}>
                        <span className="material-icons" aria-hidden="true">keyboard_arrow_down</span>
                      </button>
                      <button type="button" className="odc-board-move" aria-label="Move to next column"
                        disabled={ci === colKeys.length - 1} onClick={() => moveColumn(t, 1)}>
                        <span className="material-icons" aria-hidden="true">chevron_right</span>
                      </button>
                    </div>
                  </article>
                ))
              )}
            </div>
          </section>
        );
      })}
    </div>
  );
}
