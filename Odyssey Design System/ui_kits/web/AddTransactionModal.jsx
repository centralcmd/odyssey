/* AddTransactionModal — dialog opened from:
     • an Account's "Add transaction" action menu (account preselected & locked-in
       via defaultAccount), and
     • the Transactions page "Add transaction" button (account chosen in the form).

   Built on the shared .aam-* modal shell (portal + scrim + head/body/foot,
   Esc-to-close, click-out-to-close), exactly like AddAccountModal / AddFileModal.

   Field set mirrors the NewTransaction creation DTO (Odyssey.Finance.Dtos):
     • Description       (required)            — string
     • Amount            (required)            — decimal, SIGNED. The UI captures a
                                                 positive magnitude + an Expense/Income
                                                 toggle and emits a signed value.
     • AccountId         (required)            — Guid, preselected from launch context
     • TimeStamp         (optional → today)    — DateTime?
     • TransactionTagIds Guid[] (zero, one, or many tags — many-to-many)
     • ContactId    (optional)            — Guid? (pick existing or create new)
     • CurrencyCode      (default "USD")       — 3-letter
     • Status            (default New)         — TransactionStatus enum
     • StatusComment     (optional, ≤256)      — string?
     • ExternalId        (optional, ≤64)       — string?
     • InternalId        (optional, ≤64)       — string?
     • ExtraData         (optional, ≤1024)     — string?

   onCreate(newTransaction) receives the assembled DTO-shaped object. */

const ATM_CURRENCIES = ['USD', 'EUR', 'GBP', 'NOK', 'SEK', 'JPY', 'CAD']
  .map(c => ({ value: c, label: c }));
const ATM_CURRENCY_SYMBOL = { USD: '$', EUR: '€', GBP: '£', JPY: '¥', NOK: 'kr', SEK: 'kr', CAD: '$' };

const ATM_STATUSES = [
  { key: 'New',      label: 'New',      icon: 'fiber_new',     color: 'oklch(0.76 0.13 225)', soft: 'oklch(0.76 0.13 225 / 0.16)' },
  { key: 'Approved', label: 'Approved', icon: 'check_circle',  color: 'oklch(0.80 0.15 150)', soft: 'oklch(0.80 0.15 150 / 0.16)' },
  { key: 'Flagged',  label: 'Flagged',  icon: 'flag',          color: 'oklch(0.72 0.16 22)',  soft: 'oklch(0.72 0.16 22 / 0.16)' },
];

/* Contact-type → short label for the picker rows. */
const ATM_CP_TYPE_LABEL = Object.fromEntries(
  (window.OdysseyData.contactTypes || []).map(t => [t.key, t.label])
);

/* Contact-type → icon + color, from the canonical registry. */
const ATM_CP_FALLBACK = { key: 'Other', label: 'Other', icon: 'category', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' };
const atmCpType = (key) => (window.OdysseyData.contactTypeByKey || {})[key] || ATM_CP_FALLBACK;

const atmToday = () => {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
};

const atmTypeInfo = (key) => window.OdysseyData.accountTypeById[key]
  || { key, label: key || 'Account', icon: 'account_balance', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)' };

let atmCpUid = 0;

/* ---- Account picker (shows the colored type glyph + name + number) ----------
   Same fixed-position popover vocabulary as AddAccountModal's type picker. When
   `locked` (launched from a specific account) it renders as a static, non-interactive
   summary tile so the context is obvious and can't be changed by accident. */
const AccountPicker = ({ value, onChange, error, locked }) => {
  const { useState, useRef, useEffect } = React;
  const [open, setOpen] = useState(false);
  const apId = React.useId();
  const [pos, setPos] = useState(null);
  const ref = useRef(null);
  const btnRef = useRef(null);
  const d = window.OdysseyData;
  const options = d.accounts.filter(a => !a.closed && !a.archived);
  const sel = d.accountById[value] || null;

  const openWith = () => {
    if (btnRef.current) {
      const r = btnRef.current.getBoundingClientRect();
      setPos({ top: r.bottom + 6, left: r.left, width: r.width });
    }
    setOpen(true);
  };
  const toggle = () => (open ? setOpen(false) : openWith());

  useEffect(() => {
    if (!open) return;
    const onDoc = (e) => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
    const onScroll = (e) => { if (ref.current && ref.current.contains(e.target)) return; setOpen(false); };
    const onResize = () => setOpen(false);
    document.addEventListener('mousedown', onDoc);
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onResize);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onResize);
    };
  }, [open]);

  const tile = (a) => {
    const ti = atmTypeInfo(a.type);
    return (
      <React.Fragment>
        <span className="aam-type-ic sm" style={{ background: ti.soft, color: ti.color }}>
          <MIcon name={ti.icon} size={16} />
        </span>
        <span className="atm-acct-name">{a.name}</span>
        <span className="atm-acct-num mono">{a.number}</span>
      </React.Fragment>
    );
  };

  if (locked && sel) {
    return (
      <FieldShell label="Account">
        <div className="atm-acct-locked">
          {tile(sel)}
          <span className="atm-locked-pill"><MIcon name="lock" size={13} />From this account</span>
        </div>
      </FieldShell>
    );
  }

  return (
    <FieldShell label="Account" error={error}>
      <div className="multiselect" ref={ref}>
        <button type="button" ref={btnRef}
          className={`multiselect-trigger ${open ? 'active' : ''} ${error ? 'has-error' : ''} ${sel ? '' : 'placeholder'}`}
          aria-haspopup="listbox" aria-expanded={open} aria-controls={open ? apId : undefined}
          onClick={toggle}>
          <span className="atm-acct-current">
            {sel ? tile(sel) : <span className="multiselect-summary">Choose an account…</span>}
          </span>
          <MIcon name="expand_more" size={20} className={`chev ${open ? 'open' : ''}`} />
        </button>
        {open && pos && (
          <div id={apId} className="acct-menu-pop atm-acct-pop" role="listbox" aria-label="Account"
            style={{ top: pos.top, left: pos.left, minWidth: pos.width }}>
            {options.map(a => {
              const on = a.id === value;
              return (
                <button key={a.id} type="button" role="option" aria-selected={on}
                  className={`aam-type-item ${on ? 'selected' : ''}`}
                  onClick={() => { onChange(a.id); setOpen(false); }}>
                  {tile(a)}
                  {on && <MIcon name="check" size={18} className="aam-type-check" />}
                </button>
              );
            })}
          </div>
        )}
      </div>
    </FieldShell>
  );
};

/* ---- Contact combobox — the DS Combobox (search an existing contact
   or type a new name to create one inline), matching the insurer / company
   pickers in the other create modals. `extra` carries the contacts created
   inline during this session so they stay selectable. ----- */
const ContactPicker = ({ value, extra, onChange, onCreate }) => {
  const cpId = React.useId();
  const all = [...window.OdysseyData.contacts, ...extra];
  const options = all.map((c) => {
    const m = atmCpType(c.type);
    return { value: c.id, label: c.name, icon: m.icon, iconColor: m.color };
  });
  const handleCreate = (text) => {
    const name = text.trim();
    if (!name) return null;
    const cp = { id: `cp-new-${++atmCpUid}`, name, type: 'Other' };
    onCreate(cp);
    return { value: cp.id, label: cp.name };
  };
  return (
    <FieldShell label="Contact" htmlFor={cpId} optional
      helper="Search an existing contact, or type a new name to add one.">
      <Combobox id={cpId} value={value || ''} onChange={(v) => onChange(v || null)}
        options={options} onCreate={handleCreate} createLabel="Add"
        placeholder="Who is it with?" ariaLabel="Contact" clearable />
    </FieldShell>
  );
};

const AddTransactionModal = ({ onClose, onCreate, onSave, transaction = null, defaultAccount = '', lockAccount = false }) => {
  const { useState, useEffect } = React;
  const d = window.OdysseyData;
  const H = window.OdysseyHelpers;
  const editing = !!transaction;
  const [draft, setDraft] = useState({
    account: transaction ? transaction.account : (defaultAccount || ''),
    desc: transaction ? transaction.desc : '',
    amount: transaction ? String(Math.abs(transaction.amount)) : '',
    dir: transaction ? transaction.dir : 'expense',            // expense → negative, income → positive
    date: transaction ? transaction.date : atmToday(),
    contact: transaction ? (transaction.contact || null) : null,
    tags: transaction ? d.txnTagIds(transaction) : [],
    currency: transaction ? (transaction.currency || 'USD') : 'USD',
    status: transaction ? transaction.status : 'New',
    statusComment: transaction ? (transaction.statusComment || '') : '',
    externalId: transaction ? (transaction.externalId || '') : '',
    internalId: transaction ? (transaction.internalId || '') : '',
    extraData: transaction ? (transaction.extraData || '') : '',
  });
  const [extraCps, setExtraCps] = useState([]);
  const [extraTags, setExtraTags] = useState([]);   // tags created inline in the picker
  const [files, setFiles] = useState([]);   // brand-new uploads, reusing AddFileModal rows
  const [existingFiles, setExistingFiles] = useState(() => transaction ? H.filesForTransaction(transaction) : []);
  const removeExisting = (f) => setExistingFiles(prev => prev.filter(x => x.id !== f.id));
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => {
    setDraft(d => ({ ...d, [k]: v }));
    if (errors[k]) setErrors(e => ({ ...e, [k]: undefined }));
  };

  // (Esc-to-close, scrim click and focus handling come from the DS Modal shell.)

  const tagOptions = [
    ...window.OdysseyData.tags.filter(t => !t.archived).map(t => ({ value: t.id, label: t.name })),
    ...extraTags,
  ];
  const createTag = (name) => {
    const opt = { value: `tag-new-${Date.now()}`, label: name };
    setExtraTags(prev => [...prev, opt]);
    return opt.value;
  };
  const sym = ATM_CURRENCY_SYMBOL[draft.currency] || draft.currency;

  const submit = () => {
    const next = {};
    if (!draft.account) next.account = 'Choose which account this belongs to.';
    if (!draft.desc.trim()) next.desc = 'Add a short description.';
    const mag = parseFloat(String(draft.amount).replace(/,/g, ''));
    if (!draft.amount || isNaN(mag) || mag <= 0) next.amount = 'Enter an amount greater than zero.';
    if (Object.keys(next).length) { setErrors(next); return; }

    const signed = draft.dir === 'expense' ? -Math.abs(mag) : Math.abs(mag);

    if (editing) {
      // Edit path: emit a row-shaped patch (matches the table's onSave(id, patch)),
      // folding staged uploads into the surviving existing files.
      const dirChanged = draft.dir !== transaction.dir;
      const mergedFiles = [
        ...existingFiles,
        ...files.map((f, i) => ({
          id: `tf-${Date.now()}-${i}`,
          name: f.name.trim() || f.name,
          kind: f.kind,
          size: window.afmFmtSize(f.sizeBytes),
          uploaded: draft.date || new Date().toISOString().slice(0, 10),
        })),
      ];
      onSave && onSave({
        desc: draft.desc.trim() || transaction.desc,
        account: draft.account,
        status: draft.status,
        tags: draft.tags,
        date: draft.date,
        dir: draft.dir,
        amount: Number(signed.toFixed(2)),
        contact: draft.contact || undefined,
        currency: draft.currency,
        statusComment: draft.statusComment.trim() || undefined,
        externalId: draft.externalId.trim() || undefined,
        internalId: draft.internalId.trim() || undefined,
        extraData: draft.extraData.trim() || undefined,
        files: mergedFiles,
        icon: dirChanged ? (draft.dir === 'income' ? 'arrow_downward' : 'shopping_cart') : transaction.icon,
      });
      return;
    }

    // Assemble the NewTransaction DTO shape — Amount is signed by direction.
    onCreate && onCreate({
      Description: draft.desc.trim(),
      Amount: Number(signed.toFixed(2)),
      AccountId: draft.account,
      TimeStamp: draft.date || null,
      TransactionTagIds: draft.tags,
      ContactId: draft.contact || null,
      CurrencyCode: draft.currency,
      Status: draft.status,
      StatusComment: draft.statusComment.trim() || null,
      ExternalId: draft.externalId.trim() || null,
      InternalId: draft.internalId.trim() || null,
      ExtraData: draft.extraData.trim() || null,
      // Attachments uploaded with the transaction (AccountFile shape from data.js)
      Files: files.map((f, i) => ({
        id: `tf-${Date.now()}-${i}`,
        name: f.name.trim(),
        kind: f.kind,
        size: window.afmFmtSize(f.sizeBytes),
        uploaded: draft.date || new Date().toISOString().slice(0, 10),
      })),
      // UI conveniences for the prototype list (dir/icon derived from direction)
      dir: draft.dir,
    });
  };

  return (
    <Modal
      title={editing ? 'Edit transaction' : 'New transaction'}
      subtitle={editing ? 'Update this transaction’s details, tags, or attachments.' : 'Record money moving in or out of an account.'}
      icon="receipt_long"
      className="atm-dialog"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create transaction'}
          </Button>
        </React.Fragment>
      }>
          <div className="odc-form-grid">
          {/* Amount — the hero. Direction toggle drives the sign of the DTO Amount. */}
          <div className="atm-amount-block odc-form-grid-wide">
            {/* Value picker, not tabs — radio semantics (matches DS SegmentedControl). */}
            <div className="atm-seg" role="radiogroup" aria-label="Direction">
              <button type="button" role="radio" aria-checked={draft.dir === 'expense'}
                className={`atm-seg-btn ${draft.dir === 'expense' ? 'on expense' : ''}`}
                onClick={() => set('dir')('expense')}>
                <MIcon name="south_west" size={16} />Expense
              </button>
              <button type="button" role="radio" aria-checked={draft.dir === 'income'}
                className={`atm-seg-btn ${draft.dir === 'income' ? 'on income' : ''}`}
                onClick={() => set('dir')('income')}>
                <MIcon name="north_east" size={16} />Income
              </button>
            </div>
            <div className={`atm-amount ${draft.dir} ${errors.amount ? 'has-error' : ''}`}>
              <span className="atm-amount-sign">{draft.dir === 'expense' ? '−' : '+'}</span>
              <span className="atm-amount-cur">{sym}</span>
              <input
                inputMode="decimal"
                placeholder="0.00"
                value={draft.amount}
                autoFocus
                onChange={(e) => {
                  const v = e.target.value.replace(/[^0-9.,]/g, '');
                  set('amount')(v);
                }}
              />
            </div>
            {errors.amount && <div className="helper aam-err">{errors.amount}</div>}
          </div>

          <Select label="Currency" value={draft.currency} onChange={set('currency')} options={ATM_CURRENCIES} />
          <DateField label="Date" value={draft.date} onChange={set('date')} helper="Defaults to today" />

          <div className="odc-form-grid-wide">
            <Field
              label="Description"
              value={draft.desc}
              onChange={set('desc')}
              placeholder="e.g. Whole Foods Market · Mission"
              error={errors.desc}
            />
          </div>

          <div className="atm-account-cell">
            <AccountPicker
              value={draft.account}
              onChange={set('account')}
              error={errors.account}
              locked={lockAccount}
            />
          </div>
          <ContactPicker
            value={draft.contact}
            extra={extraCps}
            onChange={set('contact')}
            onCreate={(cp) => setExtraCps(prev => [...prev, cp])}
          />

          <div className="odc-form-grid-wide">
            <TagMultiSelect
              label="Tags"
              optional
              value={draft.tags}
              onChange={set('tags')}
              options={tagOptions}
              placeholder="No tags"
              onCreate={createTag}
              help="Add as many as fit — e.g. a category plus Reimbursable."
            />
          </div>

          {/* Status + the optional id / metadata fields from the DTO — shown
              inline with the rest of the form (previously behind a disclosure). */}
          <FieldShell label="Status" className="odc-form-grid-wide">
            <div className="atm-status-row">
              {ATM_STATUSES.map(s => {
                const on = s.key === draft.status;
                return (
                  <button key={s.key} type="button"
                    className={`atm-status-chip ${on ? 'on' : ''}`}
                    style={on ? { background: s.soft, color: s.color, borderColor: 'transparent' } : {}}
                    onClick={() => set('status')(s.key)}>
                    <MIcon name={s.icon} size={15} />{s.label}
                  </button>
                );
              })}
            </div>
          </FieldShell>

          <div className="odc-form-grid-wide">
            <Field label="Status comment" optional value={draft.statusComment} maxLength={256}
              placeholder="Why this status?" onChange={set('statusComment')} />
          </div>

          <Field label="External ID" value={draft.externalId} onChange={set('externalId')} placeholder="Optional" />
          <Field label="Internal ID" value={draft.internalId} onChange={set('internalId')} placeholder="Optional" />

          <div className="odc-form-grid-wide">
            <NoteField label="Extra data" optional maxLength={1024} value={draft.extraData} onChange={set('extraData')}
              placeholder="Notes or raw metadata to keep with this transaction" />
          </div>

          {/* Attachments — last, since the file list grows downward as files are added. */}
          <FieldShell label="Attachments" optional className="odc-form-grid-wide">
            {editing && existingFiles.length > 0 && (
              <div style={{ marginBottom: 12 }}>
                <InlinePager items={existingFiles}>
                  {(pageRows) => <FilesTable files={pageRows} account={d.accountById[draft.account]} onDelete={removeExisting}
                    kinds={window.OdysseyData.transactionFileTypes} showValidity={false} />}
                </InlinePager>
              </div>
            )}
            <FileUpload
              compact
              files={files}
              kinds={window.OdysseyData.transactionFileTypes}
              guessKind={(name) => window.afmGuessKind(name, 'transaction')}
              onChange={setFiles}
              hint="Attach a receipt or document · PDF, JPG, PNG · drop or browse"
            />
          </FieldShell>
          </div>
    </Modal>
  );
};

Object.assign(window, { AddTransactionModal, ContactPicker, AccountPicker, ATM_CURRENCIES, ATM_STATUSES, ATM_CURRENCY_SYMBOL });
