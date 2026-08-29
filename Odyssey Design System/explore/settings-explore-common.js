/* Shared plumbing for the three System-settings structure explorations.
   Loads the REAL catalogue (ui_kits/web/system-settings-data.js) and the real
   DS controls, so each candidate differs only in STRUCTURE. Permission locking,
   loading/error states and the review bar are omitted here — they are
   orthogonal to the layout question and carry over unchanged. */
const DSX = window.OdysseyDesignSystem_d5aa51 || {};
const { Switch, NumberField, CapacityField, TextInputField, Button, MIcon, SearchField, ErrorSummary } = DSX;
const exFmt = (n) => Number(n).toLocaleString();
const EX_CAP = { capacity: true };
const EX_NUM = { number: true, size: true };
const exCeil = (row) => (row.ceiling != null ? row.ceiling : row.max);

function exErrorFor(row, vals) {
  if (EX_CAP[row.type]) {
    const c = vals[row.key];
    if (c.unlimited) return null;
    if (c.value == null || c.value === '') return 'Enter a value';
    if (c.value < row.min || c.value > row.max) return `Must be between ${exFmt(row.min)} and ${exFmt(row.max)}`;
    return null;
  }
  if (EX_NUM[row.type]) {
    const v = vals[row.key], hi = exCeil(row);
    if (v == null || v === '') return 'Enter a value';
    if (v < row.min || v > hi) return `Must be between ${exFmt(row.min)} and ${exFmt(hi)}`;
    return null;
  }
  if (row.type === 'text') {
    const v = (vals[row.key] || '').trim();
    if (!v) return 'Enter a value';
    if (row.key === 'aiPrivacyNoticeUrl' && !/^https:\/\/\S+\.\S+/.test(v)) return 'Enter a full https:// URL';
    if (row.key === 'emailFromAddress' && !/^[^\s@,<>]+@[^\s@,<>]+\.[^\s@,<>]+$/.test(v)) return 'Enter one plain mailbox address';
    if (row.maxLength && v.length > row.maxLength) return `Must be ${exFmt(row.maxLength)} characters or fewer`;
    return null;
  }
  if (row.type === 'percent') {
    const v = vals[row.key];
    if (v == null || v === '') return 'Enter a value';
    if (v < 0 || v > 1) return 'Must be between 0% and 100%';
    return null;
  }
  return null;
}

function exRoundTrip(group, vals) {
  const rt = group.roundTrip;
  if (!rt) return null;
  const eff = (k) => (vals[k].unlimited ? Infinity : vals[k].value);
  const exp = eff(rt.exportKey), imp = eff(rt.importKey);
  if (exp == null || imp == null || Number.isNaN(exp) || Number.isNaN(imp)) return null;
  if (exp <= imp) return null;
  const lbl = (k) => (vals[k].unlimited ? 'no limit' : exFmt(vals[k].value));
  return `Export limit (${lbl(rt.exportKey)}) must not exceed the import limit (${lbl(rt.importKey)}), or an exported file could not be imported back.`;
}

function exDirty(row, vals, snap) {
  if (row.type === 'export') return false;
  if (EX_CAP[row.type]) {
    const a = vals[row.key], b = snap[row.key];
    return a.unlimited !== b.unlimited || (!a.unlimited && a.value !== b.value);
  }
  return vals[row.key] !== snap[row.key];
}

const exWide = (row) => row.type === 'text';

function useExDraft() {
  const { useState, useMemo } = React;
  const [vals, setVals] = useState(SS_SAVED);
  const [snap, setSnap] = useState(SS_SAVED);
  const [saving, setSaving] = useState(false);
  const [justSaved, setJustSaved] = useState(false);
  const touch = () => setJustSaved(false);
  const api = {
    vals, snap,
    setScalar: (k, v) => { setVals(s => ({ ...s, [k]: v })); touch(); },
    setCapValue: (k, v) => { setVals(s => ({ ...s, [k]: { ...s[k], value: v } })); touch(); },
    setCapUnlimited: (k, on) => { setVals(s => ({ ...s, [k]: { ...s[k], unlimited: on } })); touch(); },
    saving, justSaved,
  };
  api.dirtyRow = (row) => exDirty(row, vals, snap);
  api.dirtyIn = (g) => g.rows.filter(r => exDirty(r, vals, snap)).length;
  api.errorFor = (row) => exErrorFor(row, vals);
  api.rtFor = (g) => exRoundTrip(g, vals);
  api.errorsIn = (g) => g.rows.filter(r => exErrorFor(r, vals)).length + (exRoundTrip(g, vals) ? 1 : 0);
  api.dirtyCount = useMemo(() => SS_GROUPS.reduce((n, g) => n + g.rows.filter(r => exDirty(r, vals, snap)).length, 0), [vals, snap]);
  api.problems = useMemo(() => {
    const out = [];
    SS_GROUPS.forEach(g => {
      if (exRoundTrip(g, vals)) out.push({ label: 'Export limit exceeds the import limit', section: g.group, targetId: `ex-rt-${g.group}` });
      g.rows.forEach(r => {
        const e = exErrorFor(r, vals);
        if (e) out.push({ label: `${r.title} — ${e.charAt(0).toLowerCase()}${e.slice(1)}`, section: g.group, targetId: `ex-in-${r.key}` });
      });
    });
    return out;
  }, [vals]);
  api.hasErrors = api.problems.length > 0;
  api.discard = () => { setVals(snap); touch(); };
  api.save = () => {
    if (saving || api.hasErrors || api.dirtyCount === 0) return;
    setSaving(true);
    setTimeout(() => { setSaving(false); setSnap({ ...vals }); setJustSaved(true); setTimeout(() => setJustSaved(false), 2200); }, 800);
  };
  return api;
}

function ExControl({ row, d }) {
  const err = d.errorFor(row) || undefined;
  if (row.type === 'export') {
    return <Button variant="filled" icon="file_download" onClick={() => {}}>Export database JSON</Button>;
  }
  if (row.type === 'switch') {
    return <Switch checked={!!d.vals[row.key]} aria-label={row.title} onChange={(c) => d.setScalar(row.key, c)} />;
  }
  if (EX_CAP[row.type]) {
    const c = d.vals[row.key];
    return <CapacityField value={c.value} unlimited={c.unlimited} label={row.title} min={row.min} max={row.max}
      error={err} ariaLabelledBy={`ex-ttl-${row.key}`}
      onValueChange={(v) => d.setCapValue(row.key, v)} onUnlimitedChange={(on) => d.setCapUnlimited(row.key, on)} />;
  }
  if (row.type === 'percent') {
    const stored = d.vals[row.key];
    return <div className="ex-numwrap"><NumberField className="ex-num" id={`ex-in-${row.key}`}
      value={stored == null ? null : Math.round(stored * 100)} min={0} max={100} step={1} align="right" unit="%"
      error={err} ariaLabelledBy={`ex-ttl-${row.key}`}
      onChange={(v) => d.setScalar(row.key, v == null ? null : v / 100)} /></div>;
  }
  if (row.type === 'text') {
    return <TextInputField id={`ex-in-${row.key}`} value={d.vals[row.key] || ''}
      placeholder={row.key === 'aiPrivacyNoticeUrl' ? 'https://…' : undefined}
      inputMode={row.key === 'aiPrivacyNoticeUrl' ? 'url' : row.key === 'emailFromAddress' ? 'email' : 'text'}
      maxLength={row.maxLength} error={err} ariaLabelledBy={`ex-ttl-${row.key}`}
      onChange={(v) => d.setScalar(row.key, v)} />;
  }
  return <div className="ex-numwrap"><NumberField className="ex-num" id={`ex-in-${row.key}`} value={d.vals[row.key]}
    min={row.min} max={exCeil(row)} step={1} align="right" unit={row.type === 'size' ? 'MB' : row.unit}
    help={row.ceiling != null ? `${exFmt(row.min)}–${exFmt(row.ceiling)}` : undefined}
    error={err} ariaLabelledBy={`ex-ttl-${row.key}`} onChange={(v) => d.setScalar(row.key, v)} /></div>;
}

// The most recent "last changed" stamp in a group — one line per card instead
// of a history line on all 42 rows.
function exGroupMeta(g) {
  const withMeta = g.rows.filter(r => r.meta);
  if (!withMeta.length) return null;
  const last = withMeta[withMeta.length - 1];
  return { by: last.meta.by, on: last.meta.on, n: withMeta.length };
}

// Jump to a blocking field the way every other page's rollup does: scroll its
// block into the nearest scroller, move focus, flash a one-shot ring.
function exJump(p) {
  const el = document.getElementById(p.targetId);
  if (!el) return;
  const block = el.closest('.exe-cell,.ex-row,.exd-row,.exb-row,.exc-row,.odc-setting-row') || el;
  let scroller = block.parentElement;
  while (scroller && scroller !== document.body) {
    const oy = getComputedStyle(scroller).overflowY;
    if ((oy === 'auto' || oy === 'scroll') && scroller.scrollHeight > scroller.clientHeight) break;
    scroller = scroller.parentElement;
  }
  requestAnimationFrame(() => {
    const r = block.getBoundingClientRect();
    if (scroller && scroller !== document.body) {
      scroller.scrollTo({ top: scroller.scrollTop + (r.top - scroller.getBoundingClientRect().top) - 24, behavior: 'smooth' });
    } else {
      window.scrollTo({ top: window.scrollY + r.top - 96, behavior: 'smooth' });
    }
    el.focus({ preventScroll: true });
    block.classList.remove('ex-flash');
    requestAnimationFrame(() => block.classList.add('ex-flash'));
    setTimeout(() => block.classList.remove('ex-flash'), 2200);
  });
}

function ExSaveBar({ d }) {
  const jump = exJump;
  return (
    <div className="ex-savebar">
      {d.problems.length > 0 ? <ErrorSummary problems={d.problems} onJump={jump} /> : null}
      {d.dirtyCount > 0 && !d.hasErrors ? <button type="button" className="ex-discard" onClick={d.discard}>Discard</button> : null}
      <Button variant="filled" icon={d.justSaved ? 'check' : 'save'} loading={d.saving}
        badge={d.dirtyCount || undefined} badgeLabel="unsaved changes"
        disabled={d.hasErrors || d.dirtyCount === 0} onClick={d.save}>
        {d.justSaved ? 'Saved' : 'Save changes'}
      </Button>
    </div>
  );
}

Object.assign(window, { exFmt, exWide, exCeil, exGroupMeta, useExDraft, ExControl, ExSaveBar, EX_CAP, EX_NUM });
