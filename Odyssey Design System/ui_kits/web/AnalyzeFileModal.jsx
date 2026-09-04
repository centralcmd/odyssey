/* AnalyzeFileModal — the "Analyze" action on a statement file row (Accounts
   detail file list + the flat Files page).

   Mirrors the implemented feature (Odyssey.Finance.FileAnalysisService):
     1. POST analyze  → a FileAnalysisJob runs (status Running).
     2. GET  job      → ExistingFileAnalysisJob + extracted candidate txns.
     3. user reviews/edits the candidates and selects which to keep.
     4. POST import   → selected candidates become real transactions; the server
        returns ImportResponse { Imported, Failed, Failures[] }.

   Server rules reflected here:
     • Only files of type Statement can be analyzed (others are blocked up-front).
     • Per-candidate import overrides are limited to Date / Description / Amount /
       Currency — exactly the ImportCandidateRequest fields. Merchant, category
       hint, confidence and reference are extraction output, shown read-only.
     • Provider/model come from FileAnalysisOptions (Claude · claude-opus-4-7).

   The candidate table is the budget "Edit multiple" batch grid, widened: there
   is no separate view mode — you land straight in the editable table.

   Phases: blocked · featureDisabled · configUnavailable · analyzing · review · empty · failed · done.
   Props:
     file, account            — context
     onClose()                — dismiss
     onImported(txns)         — optional; receives NewTransaction-shaped imports
     onNavigateTransactions() — optional; "View transactions" from the done screen
     disclosure               — optional; the resolved disclosure (defaults to
                                OdysseyData.analysisDisclosure()). `enabled:false`
                                opens on featureDisabled; `disclosureVersion` is
                                echoed on send and re-checked — a mismatch is the
                                409 disclosure_changed re-prompt.
     staleDisclosure          — specimen hook: render the post-409 gate directly
     initialPhase, instant    — specimen/demo hooks (skip the analyzing delay) */

const FAN_CURRENCIES = ['USD', 'EUR', 'GBP', 'NOK', 'SEK', 'JPY', 'CAD']
  .map(c => ({ value: c, label: c }));

const fanUid = (() => { let n = 0; return () => `imp-${Date.now()}-${++n}`; })();

/* ---- Provider chip (Claude · claude-opus-4-7) ---------------------------- */
const FanProviderChip = ({ job }) => (
  <span className="fan-provider">
    <MIcon name="auto_awesome" size={14} />
    <span className="fan-provider-name">{job.analyzerProvider}</span>
    {job.analyzerModel && <span className="fan-provider-model mono">{job.analyzerModel}</span>}
  </span>
);

/* ---- Read-only confidence meter (subtle, never editable) ----------------- */
const FanConfidence = ({ value }) => {
  const H = window.OdysseyHelpers;
  const band = H.confidenceBand(value);
  if (band.pct == null) return <span className="fan-conf empty">—</span>;
  return (
    <span className={`fan-conf ${band.tone}`} title={`${band.label} confidence`}>
      <span className="fan-conf-meter"><span className="fan-conf-fill" style={{ width: `${band.pct}%` }} /></span>
      <span className="fan-conf-pct mono">{band.pct}%</span>
    </span>
  );
};

/* ---- Merchant cell → a Contact. Adopts the DS `Combobox` (OdsCombobox):
   typeahead, keyboard, a11y name, focusable clear, and an inline "Create …" row
   — the last shown ONLY when the reviewer holds contacts.create (so a
   User-role reviewer never meets a 403 on a happy-path control). Below the field,
   a MatchIndicator states where the value came from; a sub-threshold match shows
   the interactive "Suggested: … — Apply" chip instead of auto-filling. --------- */
const FanMerchantCell = ({ row, options, canCreate, onChange, onCreate, onApply, onDismiss }) => {
  const sugg = row.merchantSuggestion;
  const state = row.contactId ? row.merchantSource : (sugg ? 'suggestion' : 'none');
  // No-match recovery: when extraction returned a raw merchant string that didn't
  // match an existing contact, offer to create + link it inline ("Create
  // 'Nopa'") — same create+select path as the Combobox row, gated on canCreate.
  const rawMerchant = (row.merchant || '').trim();
  const offerCreate = state === 'none' && canCreate && !!rawMerchant && rawMerchant.toLowerCase() !== 'unknown';
  const createAndLink = () => {
    const made = onCreate(row.uid, rawMerchant);
    if (made) onChange(row.uid, made.value, made);
  };
  return (
    <div className="fan-matchcell">
      <Combobox
        value={row.contactId || ''}
        onChange={(v, opt) => onChange(row.uid, v, opt)}
        options={options}
        ariaLabel="Merchant"
        placeholder={row.merchant || 'Search contacts…'}
        emptyText={canCreate ? 'No matches — type to create' : 'No matches'}
        clearable
        onCreate={canCreate ? ((text) => onCreate(row.uid, text)) : undefined}
        createLabel="Create"
      />
      {state === 'suggestion' ? (
        <MatchIndicator state="suggestion" name={sugg.name} confidence={sugg.conf}
          onApply={() => onApply(row.uid)} onDismiss={() => onDismiss(row.uid)} />
      ) : (
        <MatchIndicator state={state} confidence={row.merchantConf}
          createName={offerCreate ? rawMerchant : undefined}
          onCreate={offerCreate ? createAndLink : undefined} />
      )}
    </div>
  );
};

/* ---- Category cell → TransactionTags (0..N). Keeps the existing TagMultiSelect;
   v1 has NO inline tag-create (contacts only), so no onCreate here. The
   MatchIndicator mirrors the merchant cell: AI / chosen / none, or a sub-threshold
   suggestion chip that applies the suggested tag set. ------------------------- */
const FanCategoryCell = ({ row, tagOptions, onChange, onApply, onDismiss }) => {
  const sugg = row.catSuggestion;
  const state = row.tagIds.length ? row.catSource : (sugg ? 'suggestion' : 'none');
  return (
    <div className="fan-matchcell">
      <TagMultiSelect value={row.tagIds || []} onChange={(ids) => onChange(row.uid, ids)}
        options={tagOptions} placeholder={row.categoryHint ? row.categoryHint : 'Set tags'} addLabel="Tag" />
      {state === 'suggestion' ? (
        <MatchIndicator state="suggestion" name={sugg.names.join(', ')} confidence={sugg.conf}
          onApply={() => onApply(row.uid)} onDismiss={() => onDismiss(row.uid)} />
      ) : (
        <MatchIndicator state={state} confidence={row.catConf} />
      )}
    </div>
  );
};

/* ---- One editable candidate row ------------------------------------------ */
const FanCandidateRow = ({ row, tagOptions, cpOptions, canCreate, onChange, merchant, category, onToggle, onRemove }) => {
  const amt = Number(row.amount);
  const dir = amt < 0 ? 'expense' : 'income';
  const low = row.llmConfidence != null && row.llmConfidence < 0.6;
  return (
    <div className={`fan-row ${row.selected ? '' : 'off'} ${low ? 'low' : ''}`}>
      <Checkbox checked={row.selected} onChange={() => onToggle(row.uid)}
        aria-label={row.selected ? 'Exclude from import' : 'Include in import'} />

      {/* Editable: date → TimeStamp */}
      <div className="fan-cell">
        <DateField value={row.transactionDate} onChange={(v) => onChange(row.uid, { transactionDate: v })} />
      </div>

      {/* Editable: description → Description */}
      <div className="fan-cell">
        <input className="fan-input" value={row.description} aria-label="Description"
          onChange={(e) => onChange(row.uid, { description: e.target.value })} />
      </div>

      {/* Editable: merchant → ContactId (Combobox + match indicator) */}
      <div className="fan-cell">
        <FanMerchantCell row={row} options={cpOptions} canCreate={canCreate}
          onChange={merchant.onChange} onCreate={merchant.onCreate} onApply={merchant.onApply} onDismiss={merchant.onDismiss} />
      </div>

      {/* Editable: category → TransactionTagIds (0..N) + match indicator */}
      <div className="fan-cell">
        <FanCategoryCell row={row} tagOptions={tagOptions}
          onChange={category.onChange} onApply={category.onApply} onDismiss={category.onDismiss} />
      </div>

      {/* Editable: amount (signed) → Amount. Compact grid cell, so this stays an
         inline input rather than a labelled MoneyField — but it follows the same
         rules: one decimal separator, minus only leading, and −/+ flip the sign. */}
      <div className="fan-cell">
        <div className={`fan-amount ${dir}`}>
          <input className="fan-input ta-r mono" inputMode="decimal" value={row.amount} aria-label="Amount"
            onKeyDown={(e) => {
              if (e.key !== '-' && e.key !== '−' && e.key !== '+') return;
              e.preventDefault();
              const mag = String(row.amount).replace(/^\s*-/, '');
              onChange(row.uid, { amount: (e.key === '+' ? '' : '-') + mag });
            }}
            onChange={(e) => {
              const raw = e.target.value.replace(/[^0-9.,\-\s]/g, '');
              const neg = /^\s*-/.test(raw);
              const body = raw.replace(/-/g, '');
              if ((body.match(/[.,]/g) || []).length > 1) { e.target.value = row.amount; return; }
              onChange(row.uid, { amount: (neg ? '-' : '') + body });
            }} />
        </div>
      </div>

      {/* Editable: currency → CurrencyCode */}
      <div className="fan-cell fan-cur">
        <CurrencySelect label={null} value={row.currency} onChange={(v) => onChange(row.uid, { currency: v })} options={FAN_CURRENCIES} searchThreshold={0} showName={false} />
      </div>

      {/* Read-only: EXTRACTION confidence (is this a real transaction) — distinct
          from the per-cell MATCH confidence shown in the merchant/category cells. */}
      <div className="fan-cell ro fan-conf-cell">
        <FanConfidence value={row.llmConfidence} />
      </div>

      {/* Editable: reference → ExternalId / InternalId */}
      <div className="fan-cell">
        <input className="fan-input mono" value={row.reference} aria-label="Reference"
          placeholder="—" onChange={(e) => onChange(row.uid, { reference: e.target.value })} />
      </div>

      <div className="fan-cell fan-rm">
        <button className="bgt-rowbtn del" aria-label="Remove candidate" onClick={() => onRemove(row.uid)}>
          <MIcon name="delete_outline" size={18} />
        </button>
      </div>
    </div>
  );
};

/* ---- Statement preview — a quiet rendering of the document's first page so the
   user sees exactly what leaves Odyssey. Decorative (aria-hidden); the redaction
   bar marks the account number that rides along in the full file. -------------- */
const FanStatementPreview = () => (
  <div className="fan-docprev" aria-hidden="true">
    <div className="fan-docprev-page">
      <div className="fan-docprev-head">
        <span className="fan-docprev-logo" />
        <div className="fan-docprev-headmeta"><span /><span /></div>
      </div>
      <div className="fan-docprev-acctline">
        <span className="fan-docprev-acctlabel">Account</span>
        <span className="fan-docprev-redact"><MIcon name="lock" size={9} />redacted in this preview only</span>
      </div>
      <div className="fan-docprev-rows">
        {Array.from({ length: 6 }).map((_, i) => (
          <div className="fan-docprev-row" key={i}>
            <span className="d" /><span className="desc" style={{ width: `${52 + (i * 13) % 34}%` }} /><span className="amt" />
          </div>
        ))}
      </div>
    </div>
  </div>
);

const AnalyzeFileModal = ({ file, account, onClose, onImported, onNavigateTransactions,
  initialPhase, resumeSummary, onResolved, instant, demoFreeze, matchStatus: matchStatusProp, canCreateContact,
  disclosure: disclosureProp, staleDisclosure }) => {
  const { useState, useEffect, useRef } = React;
  const H = window.OdysseyHelpers;
  const D = window.OdysseyData;
  const matchConfig = D.matchConfig;
  const vocab = D.analysisVocabulary();

  const eligible = H.canAnalyze(file);
  const job = useRef(eligible ? H.analysisJobForFile(file) : null).current;

  // contacts.create gate — the inline "Create …" row is rendered ONLY when
  // the reviewer holds the claim (Admin/Owner today, NOT the User role), so a
  // User-role reviewer never meets a 403 on a happy-path control. Server-side
  // [Authorize(contacts.create)] stays the real enforcement.
  const canCreate = canCreateContact != null ? canCreateContact
    : (D.can ? D.can('contacts.create') : true);

  // Resolve the starting phase. The HOST decides the initial phase and passes it
  // in (it already loaded the account-scoped resumable map): Resume review →
  // 'resumeLoading'; Analyze with a resumable job present → 'reanalyzeConfirm';
  // Analyze with none → 'consent'. A non-statement is blocked before anything runs.
  // The disclosure the gate renders, and the version that binds the user's
  // affirmation to it (GET /api/file-analysis/disclosure). `enabled` is the LIVE
  // kill switch — an admin can turn analysis off between the row rendering and
  // this dialog opening, and the honest answer then is the feature-off state,
  // never a consent gate for a transfer that cannot happen.
  const readDisclosure = () => (disclosureProp || (D.analysisDisclosure ? D.analysisDisclosure() : null));
  const disc0 = readDisclosure();
  const featureOff = !!(disc0 && disc0.enabled === false);
  // A model or base URL that could not be used is published as null — never
  // substituted — so the target cannot be constructed and analysis refuses.
  const configBad = !!(disc0 && disc0.enabled !== false && (disc0.model == null || disc0.baseUrlUsable === false));
  const gateInitial = !initialPhase || initialPhase === 'consent' || initialPhase === 'reanalyzeConfirm';
  const firstPhase = !eligible ? 'blocked'
    : (featureOff && gateInitial) ? 'featureDisabled'
    : (configBad && gateInitial) ? 'configUnavailable'
    : (initialPhase || 'consent');
  const [phase, setPhase] = useState(firstPhase);
  const [consentChecked, setConsentChecked] = useState(false);
  // The disclosure this gate is currently showing. Replaced — never patched in
  // place — when the server answers 409 disclosure_changed, so the text, the
  // affirmation and the version compared on send always come from one tuple.
  const [gateDisc, setGateDisc] = useState(disc0);
  const [stale, setStale] = useState(!!staleDisclosure);
  const staleRef = useRef(null);
  // Match-step status — orthogonal to the extraction status. Drives the per-cell
  // suggestions and the non-blocking degraded notice; never gates the import.
  const [matchStatus, setMatchStatus] = useState(matchStatusProp || (job ? job.matchStatus : 'Completed') || 'Completed');
  const transfer = D.analysisTransfer;
  // Every sentence in the gate is composed from THIS object, so the panel and
  // the affirmed text cannot name two different processors.
  const gate = gateDisc || transfer;
  // The count shown while resuming / confirming, from the host's resumable summary.
  const pendingN = (resumeSummary && (resumeSummary.pendingCount ?? resumeSummary.candidateCount)) || (job ? job.candidates.length : 0);

  // Contacts available to the Merchant combobox (active seed + inline-created).
  const [extraCps, setExtraCps] = useState([]);
  const createdCpIds = useRef(new Set()).current;   // synchronous "created here" set
  const contacts = [...D.contacts.filter(c => !c.archived), ...extraCps];
  const cpById = (id) => contacts.find(c => c.id === id) || D.contactById[id] || null;
  const cpOptions = contacts.map(c => ({ value: c.id, label: c.name, icon: 'storefront' }));
  const tagOptions = D.tags.filter(t => !t.archived).map(t => ({ value: t.id, label: t.name }));
  const tagName = (id) => (D.tagById[id] ? D.tagById[id].name : id);
  const addCp = (cp) => setExtraCps(prev => [...prev, cp]);

  // Build a row from a candidate, applying the match step's returns when matching
  // succeeded (`matched`). A return ≥ AutoLinkThreshold auto-links (source 'ai');
  // a sub-threshold return is kept as a SUGGESTION (chip), not auto-filled; no
  // return is 'No match'. When matching failed/skipped, NO suggestions apply —
  // raw candidates, manual linking — so a match failure never blocks the import.
  const T = matchConfig.autoLinkThreshold;
  const buildRow = (c, matched) => {
    let contactId = null, merchantSource = 'none', merchantConf = null, merchantSuggestion = null;
    if (matched && c.matchContactId) {
      const conf = c.matchContactConfidence;
      if (conf != null && conf >= T) { contactId = c.matchContactId; merchantSource = 'ai'; merchantConf = conf; }
      else { const cp = cpById(c.matchContactId); merchantSuggestion = { id: c.matchContactId, name: cp ? cp.name : 'a contact', conf }; }
    }
    let tagIds = [], catSource = 'none', catConf = null, catSuggestion = null;
    if (matched && c.matchTagIds && c.matchTagIds.length) {
      const conf = c.matchTagConfidence;
      if (conf != null && conf >= T) { tagIds = c.matchTagIds.slice(); catSource = 'ai'; catConf = conf; }
      else { catSuggestion = { ids: c.matchTagIds.slice(), names: c.matchTagIds.map(tagName), conf }; }
    }
    return {
      uid: fanUid(), cid: c.id,
      transactionDate: c.transactionDate,
      description: c.description,
      merchant: c.merchant || '',
      contactId, merchantSource, merchantConf, merchantSuggestion,    // → ContactId + match meta
      categoryHint: c.categoryHint,
      tagIds, catSource, catConf, catSuggestion,                           // → TransactionTagIds + match meta
      amount: c.amount,
      currency: c.currency,
      reference: c.referenceNumber || c.externalId || '',                  // → ExternalId / InternalId
      llmConfidence: c.llmConfidence,
      selected: c.llmConfidence == null || c.llmConfidence >= 0.6,         // low EXTRACTION confidence starts unchecked
    };
  };
  const seedRows = (status) => {
    const matched = (status || matchStatus) === 'Completed';
    return job ? job.candidates.map(c => buildRow(c, matched)) : [];
  };

  const [rows, setRows] = useState(() => seedRows(matchStatusProp || (job ? job.matchStatus : 'Completed')));
  const [result, setResult] = useState(null);

  // (Esc-to-close, scrim click and focus handling come from the DS Modal shell.)
  const rematchRef = useRef(false);

  // Extraction job: when it completes, hand off to the MATCH step (matching) — or
  // straight to empty when nothing was extracted. Matching never re-extracts.
  useEffect(() => {
    if (phase !== 'analyzing' || demoFreeze) return;
    const next = (job && job.candidates.length === 0) ? 'empty' : 'matching';
    if (instant) { setPhase(next); return; }
    const t = setTimeout(() => setPhase(next), 1800);
    return () => clearTimeout(t);
  }, [phase, instant, job, demoFreeze]);

  // Match step: send the contact + tag NAMES, map returns back to records,
  // then open Review. A re-match (rematchRef) refreshes suggestions while PRESERVING
  // rows the reviewer already curated (MatchMethod = Manual / created here).
  useEffect(() => {
    if (phase !== 'matching' || demoFreeze) return;
    const finish = () => {
      if (rematchRef.current) {
        rematchRef.current = false;
        setMatchStatus('Completed');
        applyMatchesPreservingManual();
      }
      setPhase('review');
    };
    if (instant) { finish(); return; }
    const t = setTimeout(finish, 1500);
    return () => clearTimeout(t);
  }, [phase, instant, demoFreeze]);

  // Resume: GET /api/file-analysis/{jobId} + the reference-data loads (counter-
  // parties / tags / currencies) BOTH complete before seeding rows and showing
  // Review — seeding before reference data is loaded would drop merchant/tag
  // prefills. In the prototype reference data is already in memory, so the wait
  // is just the fetch; on a vanished/no-longer-resumable job we'd land on
  // 'noLongerAvailable' (driven here by the host re-validating the summary).
  useEffect(() => {
    if (phase !== 'resumeLoading' || demoFreeze) return;
    if (resumeSummary && resumeSummary.gone) { setPhase('noLongerAvailable'); return; }
    if (instant) { setRows(seedRows()); setPhase('review'); return; }
    const t = setTimeout(() => { setRows(seedRows()); setPhase('review'); }, 900);
    return () => clearTimeout(t);
  }, [phase, instant, demoFreeze]);

  const updateRow = (uid, patch) => setRows(prev => prev.map(r => (r.uid === uid ? { ...r, ...patch } : r)));
  const toggleRow = (uid) => setRows(prev => prev.map(r => (r.uid === uid ? { ...r, selected: !r.selected } : r)));
  const removeRow = (uid) => setRows(prev => prev.filter(r => r.uid !== uid));

  // ---- Match handlers (merchant) ----
  // A picked value resolves its provenance from the synchronous created-here set:
  // an inline-created contact reads 'created', any existing one 'manual'.
  const onMerchantChange = (uid, value, opt) => {
    if (!value) { updateRow(uid, { contactId: null, merchantSource: 'none', merchantConf: null, merchantSuggestion: null }); return; }
    const created = createdCpIds.has(value);
    updateRow(uid, {
      contactId: value,
      merchant: (opt && opt.label) || (cpById(value) || {}).name || '',
      merchantSource: created ? 'created' : 'manual',
      merchantConf: null, merchantSuggestion: null,
    });
  };
  const onMerchantCreate = (uid, text) => {
    const name = String(text || '').trim();
    if (!name) return undefined;
    const cp = { id: `cp-fan-${fanUid()}`, name, type: 'Merchant' };  // POST /api/contacts — Name only
    createdCpIds.add(cp.id);
    addCp(cp);  // added to the in-memory option list ⇒ selectable on every row
    return { value: cp.id, label: cp.name, icon: 'storefront' };
  };
  const applyMerchantSuggestion = (uid) => setRows(prev => prev.map(r => (r.uid === uid && r.merchantSuggestion
    ? { ...r, contactId: r.merchantSuggestion.id, merchant: r.merchantSuggestion.name, merchantSource: 'manual', merchantConf: null, merchantSuggestion: null }
    : r)));
  const dismissMerchantSuggestion = (uid) => updateRow(uid, { merchantSuggestion: null });

  // ---- Match handlers (category) ----
  const onCategoryChange = (uid, ids) => updateRow(uid, { tagIds: ids, catSource: 'manual', catConf: null, catSuggestion: null });
  const applyCatSuggestion = (uid) => setRows(prev => prev.map(r => (r.uid === uid && r.catSuggestion
    ? { ...r, tagIds: r.catSuggestion.ids, catSource: 'manual', catConf: null, catSuggestion: null }
    : r)));
  const dismissCatSuggestion = (uid) => updateRow(uid, { catSuggestion: null });

  // Re-run idempotency: refresh None/Llm suggestions from the candidates, but keep
  // any row the reviewer set to Manual / created (a re-run never clobbers a human
  // decision). Mirrors the transactional delete-then-insert preserving Manual rows.
  const applyMatchesPreservingManual = () => setRows(prev => prev.map(r => {
    const c = job && job.candidates.find(x => x.id === r.cid);
    if (!c) return r;
    const next = { ...r };
    if (r.merchantSource !== 'manual' && r.merchantSource !== 'created') {
      const conf = c.matchContactConfidence;
      if (c.matchContactId && conf != null && conf >= T) { next.contactId = c.matchContactId; next.merchantSource = 'ai'; next.merchantConf = conf; next.merchantSuggestion = null; }
      else if (c.matchContactId) { const cp = cpById(c.matchContactId); next.contactId = null; next.merchantSource = 'none'; next.merchantConf = null; next.merchantSuggestion = { id: c.matchContactId, name: cp ? cp.name : 'a contact', conf }; }
      else { next.contactId = null; next.merchantSource = 'none'; next.merchantConf = null; next.merchantSuggestion = null; }
    }
    if (r.catSource !== 'manual') {
      const conf = c.matchTagConfidence;
      if (c.matchTagIds && c.matchTagIds.length && conf != null && conf >= T) { next.tagIds = c.matchTagIds.slice(); next.catSource = 'ai'; next.catConf = conf; next.catSuggestion = null; }
      else if (c.matchTagIds && c.matchTagIds.length) { next.tagIds = []; next.catSource = 'none'; next.catConf = null; next.catSuggestion = { ids: c.matchTagIds.slice(), names: c.matchTagIds.map(tagName), conf }; }
      else { next.tagIds = []; next.catSource = 'none'; next.catConf = null; next.catSuggestion = null; }
    }
    return next;
  }));

  const selected = rows.filter(r => r.selected);
  const allOn = rows.length > 0 && selected.length === rows.length;
  const someOn = selected.length > 0 && !allOn;
  const toggleAll = () => setRows(prev => prev.map(r => ({ ...r, selected: !allOn })));

  const net = selected.reduce((s, r) => s + (parseFloat(String(r.amount).replace(/,/g, '')) || 0), 0);
  // Re-match: re-run the match step only (no re-extract, no second consent).
  const reMatch = () => { rematchRef.current = true; setPhase('matching'); };
  // Re-analyze: re-extract from scratch (consent already given) → analyzing → matching.
  const reanalyze = () => { setMatchStatus('Completed'); setRows(seedRows('Completed')); setResult(null); setPhase('analyzing'); };

  // Consent — the affirmation is bound to the disclosure it was given against.
  // The version the gate rendered is echoed on analyze and re-checked against
  // the server's per-run snapshot: on a mismatch the server answers 409
  // disclosure_changed, creates NO job row and makes NO provider request. The
  // dialog stays open and usable, the checkbox resets (the previous affirmation
  // was given for different facts), the reason is announced through the live
  // region, and focus moves to it — so a keyboard or screen-reader user lands on
  // the explanation rather than on a box that silently unchecked beneath them.
  const proceedFromConsent = () => {
    if (!consentChecked) return;
    const current = readDisclosure();
    if (current && current.enabled === false) { setPhase('featureDisabled'); return; }
    const echoed = gate && gate.disclosureVersion;
    if (current && current.disclosureVersion !== echoed) {
      setGateDisc(current);
      setConsentChecked(false);
      setStale(true);
      requestAnimationFrame(() => { if (staleRef.current) staleRef.current.focus(); });
      return;
    }
    setStale(false);
    if (window.OdysseyData.recordAnalysisConsent) {
      window.OdysseyData.recordAnalysisConsent({
        user: { name: 'You', email: 'you@odyssey.app' },
        file: file ? { id: file.id, name: file.name, kind: file.kind } : null,
        account: account ? { name: account.name, number: account.number } : null,
        pages: file && file.pages ? file.pages : null,
        size: file ? file.size : null,
        // The disclosure in force at the moment of transfer, stamped on the job
        // alongside the destination host — the region in particular decides
        // whether this was a third-country transfer (GDPR Art. 44–49).
        processorInForce: gate.processor,
        processorRegionInForce: gate.processorRegion,
        consent: { recorded: true, method: 'Per-document checkbox', text: gate.consentText || transfer.consentText, disclosureVersion: echoed },
      });
    }
    setPhase('analyzing');
  };

  const doImport = () => {
    // Assemble NewTransaction-shaped payloads from the selected, edited rows.
    const txns = selected.map(r => {
      const cp = contacts.find(c => c.id === r.contactId) || null;
      const amount = Number(parseFloat(String(r.amount).replace(/,/g, '')) || 0);
      return {
        id: fanUid(),
        Description: String(r.description || '').trim(),
        Amount: amount,
        TimeStamp: r.transactionDate || null,
        AccountId: account ? account.id : null,
        ContactId: r.contactId || null,   // from Merchant
        TransactionTagIds: r.tagIds || [],           // from Category (many)
        CurrencyCode: r.currency,
        ExternalId: String(r.reference || '').trim() || null, // from Reference
        Status: 'New',
        // prototype list conveniences (Transactions page shape)
        desc: String(r.description || '').trim(),
        date: r.transactionDate,
        amount,
        account: account ? account.id : null,
        tags: r.tagIds || [],
        contact: cp ? cp.name : (r.merchant || null),
        dir: amount < 0 ? 'expense' : 'income',
        icon: 'auto_awesome',
        status: 'New',
      };
    });
    // Mirror ImportResponse: validate description present (server rule).
    const failures = selected
      .filter(r => !String(r.description || '').trim())
      .map(r => ({ uid: r.uid, reason: 'Description is required.' }));
    const imported = txns.filter(t => t.Description).length;
    setResult({ imported, failed: failures.length, failures });
    onImported && onImported(txns.filter(t => t.Description));
    // The review is now finished — tell the host to refresh its resumable map so
    // the file's “Review pending” hint clears (no stale indicator).
    onResolved && onResolved(file, 'imported');
    setPhase('done');
  };

  /* ---- Header (shared by review / empty) ---- */
  const FileContext = () => (
    <div className="fan-filemeta">
      <span className="fan-file-ic"><MIcon name="description" size={18} /></span>
      <span className="fan-file-name">{file ? file.name : 'statement.pdf'}</span>
      {account && <span className="fan-file-acct">{account.name} <span className="mono">{account.number}</span></span>}
    </div>
  );

  /* ===================== Phase bodies ===================== */
  let body, foot, title, sub, wide = false;

  if (phase === 'consent') {
    title = 'Send this statement to Claude?';
    sub = 'Analysis is done by an external AI provider. Review what leaves Odyssey, then confirm.';
    body = (
      <div className="fan-consent">
        {/* 409 disclosure_changed — the facts moved while this gate was open. Not
            an error state: the dialog stays open, nothing typed is lost, and no
            document was sent. The affirmation above resets because it was given
            for different facts. */}
        {stale && (
          <div className="fan-degraded" role="note" tabIndex={-1} ref={staleRef}>
            <MIcon name="info_outline" size={18} />
            <div className="fan-degraded-text">
              <b>The details of who processes your document changed while this dialog was open.</b>{' '}
              Please review them again before continuing — nothing has been sent, and your consent has
              been cleared because it was given for different details.
            </div>
          </div>
        )}
        {/* Transfer route — from Odyssey out to the processor */}
        <div className="fan-xfer">
          <div className="fan-xfer-node">
            <span className="fan-xfer-ic in"><MIcon name="lock" size={17} /></span>
            <div className="fan-xfer-meta">
              <div className="fan-xfer-t">Odyssey</div>
              <div className="fan-xfer-s">Your workspace</div>
            </div>
          </div>
          <span className="fan-xfer-arrow"><MIcon name="arrow_forward" size={18} /></span>
          <div className="fan-xfer-node">
            <div className="fan-xfer-meta">
              <div className="fan-xfer-t">{gate.processor} · Claude</div>
              <div className="fan-xfer-s mono">{gate.model || (job ? job.analyzerModel : 'claude-opus-4-7')} · {gate.processorRegion}</div>
            </div>
            <span className="fan-xfer-ic out"><MIcon name="auto_awesome" size={17} /></span>
          </div>
        </div>

        <div className="fan-consent-grid">
          <div className="fan-consent-doc">
            <FanStatementPreview />
            <div className="fan-doc-cap">
              <MIcon name="description" size={13} />
              <span><b>{file ? file.name : 'statement.pdf'}</b> · full document{file && file.size ? ` · ${file.size}` : ''}</span>
            </div>
          </div>

          <ul className="fan-facts">
            <li>
              <MIcon name="cloud_upload" size={18} />
              <div><b>The whole file is uploaded.</b> Every page goes to {gate.processor}’s Claude API — the model needs the complete statement to read it.</div>
            </li>
            <li>
              <MIcon name="account_balance" size={18} />
              <div><b>Whatever the file contains is sent.</b> Odyssey doesn’t inspect it first — the whole document is uploaded as-is, including any personal or financial data inside.</div>
            </li>
            <li>
              <MIcon name="sync_alt" size={18} />
              <div><b>Your contact and tag names are sent too.</b> After reading the statement, Claude matches each transaction to your existing records — <b>names only, for matching</b>. No notes, organization numbers, or other fields. <span className="mono">{vocab.total} names</span> this time.</div>
            </li>
            <li>
              <MIcon name="verified_user" size={18} />
              <div><b>Not used to train models.</b> Anthropic retains the data for a limited period under their <a href={gate.privacyNoticeUrl} className="fan-link" target="_blank" rel="noopener noreferrer">privacy policy</a>.</div>
            </li>
            <li>
              <MIcon name="history" size={18} />
              <div><b>This transfer is logged.</b> Your name, the file and the time are written to the analysis log.</div>
            </li>
          </ul>
        </div>

        <div className="fan-consent-check">
          <Checkbox checked={consentChecked} onChange={(next) => setConsentChecked(next)}
            label={<span className="fan-consent-text">{gate.consentText || transfer.consentText}</span>} />
        </div>

        <div className="fan-consent-basis">
          <MIcon name="gavel" size={14} />
          <span>Lawful basis recorded: <b>{gate.lawfulBasis}</b></span>
        </div>
      </div>
    );
    foot = (
      <React.Fragment>
        <Button variant="text" onClick={onClose}>Cancel</Button>
        <Button variant="filled" color="primary" icon="cloud_upload"
          disabled={!consentChecked} onClick={proceedFromConsent}>
          Send &amp; analyze
        </Button>
      </React.Fragment>
    );
  }

  else if (phase === 'reanalyzeConfirm') {
    title = 'You’ve already analyzed this file';
    sub = 'Pick up the review you left — or send the statement to Claude again.';
    body = (
      <div className="fan-fork">
        <div className="fan-fork-lead">
          <span className="fan-fork-ic"><MIcon name="history" size={22} /></span>
          <div>
            <div className="fan-fork-t">A review is waiting for this file</div>
            <p className="fan-fork-s">
              “{file ? file.name : 'This statement'}” already has an open analysis with{' '}
              <b>{pendingN} candidate{pendingN === 1 ? '' : 's'}</b> still pending. Resume to continue
              reviewing exactly where you left off — nothing is sent to Claude and you aren’t billed again.
            </p>
          </div>
        </div>
        <div className="fan-fork-note">
          <MIcon name="info_outline" size={14} />
          <span>Analyzing again uploads the whole statement to Claude a second time and creates a new, duplicate set of candidates.</span>
        </div>
      </div>
    );
    foot = (
      <React.Fragment>
        <Button variant="outlined" icon="refresh" onClick={() => { setConsentChecked(false); setPhase('consent'); }}>Analyze again</Button>
        <Button variant="filled" color="primary" icon="playlist_add_check" onClick={() => setPhase('resumeLoading')}>Resume review</Button>
      </React.Fragment>
    );
  }

  else if (phase === 'resumeLoading') {
    title = 'Opening your review';
    sub = 'Loading the candidates you saved — no new analysis.';
    body = (
      <div className="fan-state">
        <span className="fan-spinner" aria-hidden="true" />
        <FileContext />
        <div className="fan-steps">
          <div className="fan-step done"><MIcon name="check" size={15} />Found saved review</div>
          <div className="fan-step active"><span className="fan-step-dot" />Loading candidates…</div>
          <div className="fan-step"><span className="fan-step-dot" />Ready to review</div>
        </div>
        <p className="fan-state-note">Reopening the {pendingN} pending candidate{pendingN === 1 ? '' : 's'} from your last review.</p>
      </div>
    );
    foot = <Button variant="text" onClick={onClose}>Cancel</Button>;
  }

  else if (phase === 'noLongerAvailable') {
    title = 'This review is no longer available';
    sub = 'The saved analysis can’t be opened.';
    body = (
      <div className="fan-state" role="alert">
        <span className="fan-state-ic warn"><MIcon name="unpublished" size={30} /></span>
        <div className="fan-state-title">Nothing left to resume</div>
        <p className="fan-state-text">
          The review for “{file ? file.name : 'this statement'}” has already been imported or removed,
          so there are no candidates to pick up. You can run a fresh analysis instead.
        </p>
      </div>
    );
    foot = (
      <React.Fragment>
        <Button variant="text" onClick={onClose}>Close</Button>
        <Button variant="filled" color="primary" icon="auto_fix_high"
          onClick={() => { setConsentChecked(false); setPhase('consent'); }}>Analyze</Button>
      </React.Fragment>
    );
  }

  else if (phase === 'featureDisabled') {
    // 503 feature_disabled. Reachable even though the row's Analyze action is
    // disabled while the switch is off: the switch is read live, so it can flip
    // between the row rendering and this dialog opening. Never a consent gate
    // for a transfer that cannot happen.
    title = 'AI document analysis is turned off';
    sub = 'No document can be sent for analysis on this instance.';
    body = (
      <div className="fan-state" role="alert">
        <span className="fan-state-ic warn"><MIcon name="block" size={30} /></span>
        <div className="fan-state-title">Nothing was sent</div>
        <p className="fan-state-text">
          An administrator has turned AI document analysis off for this instance, so
          “{file ? file.name : 'this statement'}” was not transferred anywhere and no consent was
          recorded. You can still add its transactions by hand, or ask an administrator to turn
          analysis back on in System settings.
        </p>
      </div>
    );
    foot = <Button variant="filled" color="primary" onClick={onClose}>Close</Button>;
  }

  else if (phase === 'configUnavailable') {
    // 503 configuration_unavailable. The stored model or base URL could not be
    // used, so the analysis REFUSES rather than falling back to the shipped
    // default — substituting would stamp a model that did not run, or send the
    // document to a processor neither the administrator nor the user chose.
    // The detail is static text: it never names the stored value, the host or
    // the parse error (those go to the server log).
    title = 'Document analysis is unavailable';
    sub = 'A configuration problem is stopping analysis on this instance.';
    body = (
      <div className="fan-state" role="alert">
        <span className="fan-state-ic err"><MIcon name="error_outline" size={30} /></span>
        <div className="fan-state-title">Nothing was sent</div>
        <p className="fan-state-text">
          Document analysis is temporarily unavailable while the server recovers a configuration
          problem. “{file ? file.name : 'This statement'}” was not transferred anywhere and no consent
          was recorded. An administrator can check the model and provider base URL in System settings.
        </p>
      </div>
    );
    foot = <Button variant="filled" color="primary" onClick={onClose}>Close</Button>;
  }

  else if (phase === 'blocked') {
    title = 'This file can’t be analyzed';
    sub = 'Analysis extracts transactions from bank statements.';
    body = (
      <div className="fan-state">
        <span className="fan-state-ic warn"><MIcon name="block" size={30} /></span>
        <div className="fan-state-title">Only statements can be analyzed</div>
        <p className="fan-state-text">
          “{file ? file.name : 'This file'}” is a <b>{file ? file.kind : 'file'}</b>. To pull transactions out of it,
          open <b>Edit</b> on the file and change its document type to <b>Statement</b>, then try again.
        </p>
      </div>
    );
    foot = <Button variant="filled" color="primary" onClick={onClose}>Close</Button>;
  }

  else if (phase === 'analyzing') {
    title = 'Analyzing statement';
    sub = 'Reading the document and extracting candidate transactions.';
    body = (
      <div className="fan-state">
        <span className="fan-spinner" aria-hidden="true" />
        <FileContext />
        <FanProviderChip job={job} />
        <div className="fan-steps">
          <div className="fan-step done"><MIcon name="check" size={15} />Uploaded &amp; queued</div>
          <div className="fan-step active"><span className="fan-step-dot" />Extracting transactions…</div>
          <div className="fan-step"><span className="fan-step-dot" />Ready to review</div>
        </div>
        <p className="fan-state-note">This usually takes a few seconds. You can keep working — we’ll hold the results here.</p>
      </div>
    );
    foot = <Button variant="text" onClick={onClose}>Cancel</Button>;
  }

  else if (phase === 'matching') {
    // The second LLM step — runs after extraction completes, before Review. Sends
    // the contact + tag NAMES only; announced via the dialog's live region.
    title = 'Matching merchants and categories';
    sub = 'Comparing each candidate against your contacts and tags.';
    body = (
      <div className="fan-state">
        <span className="fan-spinner" aria-hidden="true" />
        <FileContext />
        <FanProviderChip job={job} />
        <div className="fan-steps">
          <div className="fan-step done"><MIcon name="check" size={15} />Extracted {rows.length} candidate{rows.length === 1 ? '' : 's'}</div>
          <div className="fan-step active"><span className="fan-step-dot" />Matching against your records…</div>
          <div className="fan-step"><span className="fan-step-dot" />Ready to review</div>
        </div>
        <p className="fan-state-note">Only your contact and tag <b>names</b> are sent for matching — <span className="mono">{vocab.total} names</span> this time. The statement isn’t re-sent.</p>
      </div>
    );
    foot = <Button variant="text" onClick={onClose}>Cancel</Button>;
  }

  else if (phase === 'failed') {
    title = 'Analysis failed';
    sub = 'The statement couldn’t be processed.';
    body = (
      <div className="fan-state">
        <span className="fan-state-ic err"><MIcon name="error_outline" size={30} /></span>
        <div className="fan-state-title">We couldn’t read this statement</div>
        <p className="fan-state-text">
          {job && job.failureMessage
            ? job.failureMessage
            : 'The analyzer returned an error before any transactions were extracted. This can happen with scanned images or password-protected files.'}
        </p>
        <FanProviderChip job={job} />
      </div>
    );
    foot = (
      <React.Fragment>
        <Button variant="text" onClick={onClose}>Close</Button>
        <Button variant="filled" color="primary" icon="refresh" onClick={reanalyze}>Try again</Button>
      </React.Fragment>
    );
  }

  else if (phase === 'empty') {
    title = 'No transactions found';
    sub = 'The analysis completed, but nothing looked like a transaction.';
    body = (
      <div className="fan-state">
        <span className="fan-state-ic"><MIcon name="search_off" size={30} /></span>
        <div className="fan-state-title">Nothing to import</div>
        <p className="fan-state-text">
          Odyssey didn’t find any transactions in “{file ? file.name : 'this statement'}”. If this is a multi-page
          statement, check that every page uploaded, or add the transactions manually.
        </p>
        <FanProviderChip job={job} />
      </div>
    );
    foot = (
      <React.Fragment>
        <Button variant="text" onClick={onClose}>Close</Button>
        <Button variant="outlined" icon="refresh" onClick={reanalyze}>Re-analyze</Button>
      </React.Fragment>
    );
  }

  else if (phase === 'done') {
    const r = result || { imported: selected.length, failed: 0, failures: [] };
    title = 'Import complete';
    sub = `Candidates committed to ${account ? account.name : 'your account'} as New transactions.`;
    body = (
      <div className="fan-state">
        <span className="fan-state-ic ok"><MIcon name="check_circle" size={30} /></span>
        <div className="fan-state-title">
          Imported {r.imported} transaction{r.imported === 1 ? '' : 's'}
        </div>
        <p className="fan-state-text">
          {r.imported} candidate{r.imported === 1 ? ' was' : 's were'} added to <b>{account ? account.name : 'your account'}</b>
          {r.failed > 0 ? ` · ${r.failed} skipped` : ''}. They’ll appear in Transactions with status <b>New</b>.
        </p>
        {r.failed > 0 && (
          <div className="fan-failures">
            {r.failures.map((f, i) => (
              <div key={i} className="fan-failure"><MIcon name="error_outline" size={15} />{f.reason}</div>
            ))}
          </div>
        )}
      </div>
    );
    foot = (
      <React.Fragment>
        <Button variant="text" onClick={onClose}>Done</Button>
        {onNavigateTransactions && (
          <Button variant="filled" color="primary" iconRight="arrow_forward"
            onClick={() => { onNavigateTransactions(); onClose(); }}>View transactions</Button>
        )}
      </React.Fragment>
    );
  }

  else { // review
    wide = true;
    title = 'Review candidate transactions';
    sub = 'Edit any row, untick what you don’t want, then import the rest.';
    body = (
      <div className="fan-review">
        <div className="fan-toolbar">
          <FileContext />
          <div className="fan-toolbar-right">
            <FanProviderChip job={job} />
            {matchStatus === 'Completed' && rows.length > 0 && (
              <button type="button" className="fan-rematch" onClick={reMatch}>
                <MIcon name="sync" size={14} />Re-match
              </button>
            )}
            <span className="fan-foundpill">{rows.length} found</span>
          </div>
        </div>

        {/* Match-degraded — non-blocking. Review still opens with raw candidates;
            the reviewer links by hand or retries the match step (no re-extract). */}
        {(matchStatus === 'Failed' || matchStatus === 'Skipped') && rows.length > 0 && (
          <div className="fan-degraded" role="alert">
            <MIcon name={matchStatus === 'Skipped' ? 'filter_alt_off' : 'sync_problem'} size={18} />
            <div className="fan-degraded-text">
              {matchStatus === 'Skipped'
                ? <React.Fragment><b>Matching was skipped.</b> You have more than {matchConfig.maxVocabulary} contacts or tags to compare, so nothing was sent for matching. Link merchants and categories yourself, or re-match.</React.Fragment>
                : <React.Fragment><b>Couldn’t match this statement.</b> {(job && job.matchFailureMessage) || 'The matching provider returned an error.'} The candidates are intact — link merchants and categories yourself, or re-match.</React.Fragment>}
            </div>
            <Button variant="outlined" icon="sync" onClick={reMatch}>Re-match</Button>
          </div>
        )}

        {rows.length === 0 ? (
          <div className="fan-allgone">
            <MIcon name="inbox" size={26} />
            <div>Every candidate was removed. Re-analyze to start over.</div>
            <Button variant="outlined" icon="refresh" onClick={reanalyze}>Re-analyze</Button>
          </div>
        ) : (
          <div className="fan-table-wrap">
            <div className="fan-table">
              <div className="fan-row-head">
                <Checkbox checked={allOn} indeterminate={someOn} onChange={toggleAll} aria-label="Select all" />
                <span>Date</span>
                <span>Description</span>
                <span>Merchant</span>
                <span>Category</span>
                <span className="ta-r">Amount</span>
                <span>Currency</span>
                <span>Confidence</span>
                <span>Reference</span>
                <span />
              </div>
              {rows.map(r => (
                <FanCandidateRow key={r.uid} row={r} tagOptions={tagOptions} cpOptions={cpOptions} canCreate={canCreate}
                  onChange={updateRow} onToggle={toggleRow} onRemove={removeRow}
                  merchant={{ onChange: onMerchantChange, onCreate: onMerchantCreate, onApply: applyMerchantSuggestion, onDismiss: dismissMerchantSuggestion }}
                  category={{ onChange: onCategoryChange, onApply: applyCatSuggestion, onDismiss: dismissCatSuggestion }} />
              ))}
            </div>
          </div>
        )}

        <div className="fan-editnote">
          <MIcon name="auto_awesome" size={14} />
          <span>Merchants and categories are matched to your existing records — each cell shows where its value came from. <b>Confidence</b> is the extraction score (is this a real transaction), separate from the per-cell match score. Edit any field; untick a row to leave it out.</span>
        </div>
      </div>
    );
    foot = (
      <div className="fan-foot-split">
        <div className="fan-foot-summary">
          <span className="fan-foot-count">{selected.length} of {rows.length} selected</span>
          {selected.length > 0 && (
            <span className={`fan-foot-net mono ${net < 0 ? 'expense' : 'income'}`}>
              net {window.OdysseyHelpers.signedMoney(net)}
            </span>
          )}
        </div>
        <div className="row gap-2">
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon="playlist_add_check"
            disabled={selected.length === 0} onClick={doImport}>
            Import {selected.length} transaction{selected.length === 1 ? '' : 's'}
          </Button>
        </div>
      </div>
    );
  }

  // Dialog-scoped polite live region (the dialog had none): the async resume
  // state is announced here; the load-failure state announces via role="alert"
  // on its own body.
  const liveMsg = phase === 'resumeLoading' ? `Opening your saved review for ${file ? file.name : 'this statement'}.`
    : phase === 'matching' ? 'Matching merchants and categories against your contacts and tags.'
    : (phase === 'consent' && stale) ? 'The details of who processes your document changed while this dialog was open. Review them again before continuing. Your consent has been cleared.'
    : '';

  const headIcon = phase === 'consent' ? 'shield'
    : phase === 'reanalyzeConfirm' ? 'history'
    : phase === 'noLongerAvailable' ? 'unpublished'
    : phase === 'featureDisabled' ? 'block'
    : phase === 'configUnavailable' ? 'error_outline'
    : 'document_scanner';
  const headTone = (phase === 'consent' || phase === 'noLongerAvailable' || phase === 'featureDisabled') ? 'warning' : 'brand';

  return (
    <Modal
      title={title}
      subtitle={sub}
      icon={headIcon}
      iconTone={headTone}
      wide={wide}
      className={`fan-dialog ${phase === 'consent' ? 'consent' : ''}`}
      bodyClassName={`fan-body ${wide ? 'wide' : ''}`}
      onClose={onClose}
      footer={foot}>
      <div className="fan-live" aria-live="polite" role="status">{liveMsg}</div>
      {body}
    </Modal>
  );
};

Object.assign(window, { AnalyzeFileModal, FAN_CURRENCIES });
