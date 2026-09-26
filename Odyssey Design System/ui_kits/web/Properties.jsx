/* Properties — /properties
   ----------------------------------------------------------------------------
   The frontend for *Property — Backend (Draft v8)*. A sibling of Accounts and
   Contracts: the same PageHeader + expandable RecordCard list, one card open at
   a time. A property is standalone in v1 — no links to contacts, accounts,
   contracts or files, no photos, and its value is NOT summed into net worth —
   so the expanded record holds exactly three things:

     1. DETAILS   — the common fields + the one detail sub-object its type has.
     2. ESTIMATED VALUE — the shared estimate surface (AccountEstimates) over the
                    sibling PropertyEstimates table: same chart, same history,
                    same in-force rule. The "Current value" block is omitted: it
                    compares against a transaction balance and says "In net
                    worth", and neither is true of a property.
     3. SMART TAGS — the shared DS AccountSmartTagsSection, subject "property",
                    capped by GET /api/property-limits.

   Claims (§7): properties.read / create / update / delete and
   properties.estimates.read / write. User and Guest get the two reads only; the
   Tweaks panel switches between the Owner/Admin and User/Guest grants. */

const PR_H = window.OdysseyHelpers;
const PR_D = window.OdysseyData;

const PropertyStatusChip = ({ status }) => {
  const m = PR_H.propStatusMeta(status);
  return <Chip tone={m.tone} dot={m.dot}>{m.label}</Chip>;
};

/* The estimate surface expects an account-shaped owner; this is the adapter.
   `type` picks the glyph + hue from the Property / Vehicle account types. */
const propEstOwner = (p) => ({ id: p.id, name: p.name, currency: p.currencyCode, type: PR_H.propTypeInfo(p.type).estimateType });

/* ====================== Smart tags ====================== */
const PropertySmartTags = ({ property, tagIds, setTagIds, onNavigate, canWrite, cap, limitsDegraded }) => {
  const { useState, useEffect, useRef } = React;
  const DSSection = (window.OdysseyDesignSystem_d5aa51 || {}).AccountSmartTagsSection;
  const [loading, setLoading] = useState(false);
  const [addError, setAddError] = useState(null);
  const first = useRef(true);
  useEffect(() => {
    if (first.current) { first.current = false; return; }
    setLoading(true);
    const t = setTimeout(() => setLoading(false), 420);
    return () => clearTimeout(t);
  }, [tagIds.join(',')]);
  if (!DSSection) return null;

  const matches = PR_D.transactions.filter(t => PR_D.txnTagIds(t).some(id => tagIds.includes(id)));
  const configured = tagIds.map(id => PR_D.tagById[id]).filter(Boolean).map(t => ({ id: t.id, label: t.name }));
  const options = PR_D.tags.filter(t => !t.archived).map(t => ({ value: t.id, label: t.name }));
  /* The server answers 422 with the effective number interpolated; the page
     only pre-checks, and stops doing so when the limits read is degraded. */
  const add = (id) => {
    if (cap != null && tagIds.length >= cap) { setAddError(`A property may have at most ${cap} smart tags.`); return; }
    setAddError(null);
    setTagIds(prev => (prev.includes(id) ? prev : [...prev, id]));
  };
  const remove = (id) => { setAddError(null); setTagIds(prev => prev.filter(x => x !== id)); };

  return (
    <DSSection chrome={false} subject="property" tags={configured} tagOptions={options} transactions={matches}
      onAddTag={add} onRemoveTag={remove} canWrite={canWrite} loading={loading} maxTags={cap}
      limitsDegraded={limitsDegraded} addError={addError} onDismissAddError={() => setAddError(null)}
      emptyDesc={canWrite
        ? 'Pin the tags its costs are booked under — maintenance, tax, fuel, insurance — and those transactions read here.'
        : 'No tags are being watched on this property.'}
      noMatchDesc="No transactions carry the watched tags yet."
      formatAmount={(n) => PR_H.signedMoney(n, 'USD')}
      renderTable={(rows) => (
        <div className="acct-txn-table">
          <InlinePager items={rows}>{(pageRows) => <TxnTable txns={pageRows} onNavigate={onNavigate} />}</InlinePager>
        </div>
      )} />
  );
};

/* ====================== Expanded detail ====================== */
const PropertyDetail = ({ property: p, estimates, tagIds, setTagIds, perms, cap, limitsDegraded, onNavigate, onNewEstimate, onEditEstimate, onDeleteEstimate }) => {
  const ti = PR_H.propTypeInfo(p.type);
  const kind = PR_H.propKindInfo(p);
  const d = PR_H.propDetails(p);
  const status = PR_H.propStatus(p);
  const today = PR_H.propToday();
  const disposeFuture = p.disposedDate && p.disposedDate > today;
  const cur = PR_D.currencies.find(c => c.code === p.currencyCode);

  const subtypeTiles = p.type === 'Vehicle' ? [
    d.registrationNumber && <InfoTile key="reg" icon="pin" label="Registration" value={d.registrationNumber} />,
    d.vin && <InfoTile key="vin" icon="qr_code_2" label={d.kind === 'Boat' ? 'Hull number' : 'VIN'} value={d.vin} className="wrapvalue" />,
    (d.make || d.model) && <InfoTile key="mm" icon={kind.icon} label="Make and model" value={[d.make, d.model].filter(Boolean).join(' ')} valueVariant="text" />,
    d.modelYear && <InfoTile key="my" icon="calendar_today" label="Model year" value={String(d.modelYear)} />,
    d.firstRegisteredDate && <InfoTile key="fr" icon="app_registration" label="First registered" value={PR_H.dateLong(d.firstRegisteredDate)} valueVariant="sm" />,
  ] : [
    /* Same treatment as a contact's address tile: location_on in the address
       hue, the one-line summary in that hue, full row. */
    (d.addressLine || d.city) && <InfoTile key="addr" icon="location_on" iconColor="oklch(0.77 0.14 55)" iconSoft="oklch(0.77 0.14 55 / 0.15)"
      label="Address" value={<span style={{ color: 'oklch(0.77 0.14 55)' }}>{PR_H.propAddressText(d)}</span>} className="prop-addr-tile" wide
      foot={kind.label} />,
    d.cadastralNumber && <InfoTile key="cad" icon="map" label="Cadastral number" value={d.cadastralNumber} foot="land registry" />,
    d.livingAreaSqm != null && <InfoTile key="la" icon="square_foot" label="Living area" value={PR_H.propArea(d.livingAreaSqm)} />,
    d.plotAreaSqm != null && <InfoTile key="pa" icon="crop_free" label="Plot area" value={PR_H.propArea(d.plotAreaSqm)} />,
    d.buildYear && <InfoTile key="by" icon="construction" label="Built" value={String(d.buildYear)} />,
  ];
  const shownSubtype = subtypeTiles.filter(Boolean);

  return (
    <React.Fragment>
      <InfoTileGrid>
        <InfoTile icon={kind.icon} label="Kind" value={kind.label} valueVariant="text" foot={ti.label} />
        <InfoTile icon="payments" label="Currency" value={p.currencyCode} foot={cur ? cur.name : null} />
        <InfoTile icon="event_available" label="Acquired" value={p.acquiredDate ? PR_H.dateLong(p.acquiredDate) : 'Unknown'} valueVariant={p.acquiredDate ? 'sm' : 'text'} />
        {p.disposedDate ? (
          <InfoTile icon="output" label={disposeFuture ? 'Disposal on' : 'Disposed'} value={PR_H.dateLong(p.disposedDate)} valueVariant="sm"
            className={disposeFuture ? 'tone-info' : undefined} foot={disposeFuture ? 'still owned until then' : 'sold, written off or scrapped'} />
        ) : null}
        {p.archived ? <InfoTile icon="inventory_2" label="Archived" value={PR_H.dateLong(p.archived)} valueVariant="sm" foot="hidden from the default list" /> : null}
      </InfoTileGrid>

      <div className="con-section">
        <SectionDivider label={p.type === 'Vehicle' ? 'Vehicle' : 'Real estate'} meta={kind.label} />
        {shownSubtype.length ? <InfoTileGrid>{shownSubtype}</InfoTileGrid> : (
          <EmptyLine>{p.type === 'Vehicle' ? 'No registration, VIN or model recorded.' : 'No address, registry number or areas recorded.'}{perms.update ? ' Edit the property to add them.' : ''}</EmptyLine>
        )}
        {p.notes ? <InfoTileGrid><InfoTile icon="sticky_note_2" label="Notes" value={p.notes} wide /></InfoTileGrid> : null}
      </div>

      {perms.estimatesRead ? (
        <PropertyEstimates property={p} estimates={estimates} canWrite={perms.estimatesWrite}
          onNew={onNewEstimate} onEdit={onEditEstimate} onDelete={onDeleteEstimate} />
      ) : null}

      <div className="con-section">
        <SectionDivider label="Smart tags" meta={tagIds.length ? `${tagIds.length} watched` : 'none watched'} />
        <PropertySmartTags property={p} tagIds={tagIds} setTagIds={setTagIds} onNavigate={onNavigate}
          canWrite={perms.update} cap={cap} limitsDegraded={limitsDegraded} />
      </div>
    </React.Fragment>
  );
};

/* ====================== Delete confirmation ======================
   Deletion is hard and cascades the detail row, every estimate and every
   smart-tag link in one transaction. Nothing else references a property, so
   there is no blocker — only the statement of what goes. */
const DeletePropertyModal = ({ property, estimateCount, tagCount, canArchive, onArchive, onClose, onConfirm }) => (
  <Modal title={`Delete ${property.name}?`} icon="delete" onClose={onClose}
    subtitle="This can’t be undone."
    footer={<React.Fragment>
      <Button variant="text" onClick={onClose}>Cancel</Button>
      {canArchive && !property.archived ? <Button variant="outlined" icon="inventory_2" onClick={onArchive}>Archive instead</Button> : null}
      <Button variant="danger" icon="delete_forever" onClick={onConfirm}>Delete property</Button>
    </React.Fragment>}>
    <div className="prop-del-list">
      <div><MIcon name="home_work" size={16} />The property and its {property.type === 'Vehicle' ? 'vehicle' : 'real estate'} details</div>
      <div><MIcon name="monitor" size={16} />{estimateCount === 0 ? 'No estimates' : `${estimateCount} estimate${estimateCount === 1 ? '' : 's'} — the whole value history`}</div>
      <div><MIcon name="sell" size={16} />{tagCount === 0 ? 'No smart tags' : `${tagCount} smart-tag link${tagCount === 1 ? '' : 's'}`}</div>
    </div>
    <p className="prop-del-foot">The transaction tags themselves and the transactions carrying them are not affected. Archiving keeps everything and hides the property from the default list.</p>
  </Modal>
);

/* ====================== One list item ====================== */
const PropertyListItem = ({ row, perms, cap, limitsDegraded, open, onToggle, onNavigate, onUpdate, onDelete, estimates, setEstimates, tagIds, setTagIds }) => {
  const { useState } = React;
  const p = row;
  const [showEdit, setShowEdit] = useState(false);
  const [estModal, setEstModal] = useState(null);
  const [confirmDel, setConfirmDel] = useState(false);
  const ti = PR_H.propTypeInfo(p.type);
  const kind = PR_H.propKindInfo(p);
  const d = PR_H.propDetails(p);
  const status = PR_H.propStatus(p);
  const current = PR_H.propCurrentEstimate(estimates);

  const saveEstimate = (dto, id) => {
    setEstimates(prev => id ? prev.map(e => (e.id === id ? { ...e, ...dto } : e))
      : [...prev, { id: `pe-new-${Date.now()}`, propertyId: p.id, createdAtUtc: new Date().toISOString(), ...dto }]);
    setEstModal(null);
  };
  const toggleArchive = () => onUpdate({ ...p, archived: p.archived ? null : new Date().toISOString(), updatedAt: new Date().toISOString() });
  const where = p.type === 'Vehicle' ? [d.make, d.model, d.modelYear].filter(Boolean).join(' ') : [d.city, d.countryCode].filter(Boolean).join(', ');

  const menu = [
    ...(perms.update ? [{ icon: 'edit', label: 'Edit property', onClick: () => setShowEdit(true) }] : []),
    ...(perms.estimatesWrite ? [{ icon: 'monitor', label: 'New estimate', onClick: () => { onToggle(true); setEstModal({ mode: 'new' }); } }] : []),
    { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(p.id); } },
    ...((perms.update || perms.delete) ? [{ divider: true }] : []),
    ...(perms.update ? [{ icon: p.archived ? 'unarchive' : 'inventory_2', label: p.archived ? 'Restore' : 'Archive', onClick: toggleArchive }] : []),
    ...(perms.delete ? [{ icon: 'delete', label: 'Delete', danger: true, onClick: () => setConfirmDel(true) }] : []),
  ];

  return (
    <div className="prop-card">
      <RecordCard
        icon={kind.icon} accent={ti.color} accentSoft={ti.soft}
        name={p.name}
        chips={status !== 'Owned' ? <PropertyStatusChip status={status} /> : null}
        meta={[kind.label, p.description, ...(where ? [where] : [])]}
        counts={[
          ...(perms.estimatesRead ? [{ icon: 'monitor', value: estimates.length, label: 'Estimates' }] : []),
          { icon: 'sell', value: tagIds.length, label: 'Smart tags' },
        ]}
        figure={perms.estimatesRead ? {
          value: current ? PR_H.money(current.value, p.currencyCode) : '—',
          caption: current ? `estimated · ${new Date(current.effectiveFrom + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', year: 'numeric' })}` : 'no estimate',
          tone: status === 'Owned' && current ? 'income' : 'muted',
        } : undefined}
        dimmed={!!p.archived}
        open={open} onToggle={onToggle}
        actions={<ActionMenu items={menu} />}>
        <PropertyDetail property={p} estimates={estimates} tagIds={tagIds} setTagIds={setTagIds} perms={perms}
          cap={cap} limitsDegraded={limitsDegraded} onNavigate={onNavigate}
          onNewEstimate={() => setEstModal({ mode: 'new' })}
          onEditEstimate={(e) => setEstModal({ mode: 'edit', estimate: e })}
          onDeleteEstimate={(e) => setEstimates(prev => prev.filter(x => x.id !== e.id))} />
      </RecordCard>

      {showEdit && <AddPropertyModal property={p} estimateCount={estimates.length} onClose={() => setShowEdit(false)}
        onSave={(dto) => { onUpdate({ ...p, ...dto, updatedAt: new Date().toISOString() }); setShowEdit(false); }} />}
      {estModal && <AddEstimateModal account={propEstOwner(p)} ownerNoun="property" showHint={false} leadIcon="monitor"
        estimate={estModal.mode === 'edit' ? estModal.estimate : null} existing={estimates}
        onClose={() => setEstModal(null)} onSave={saveEstimate} />}
      {confirmDel && <DeletePropertyModal property={p} estimateCount={estimates.length} tagCount={tagIds.length}
        canArchive={perms.update} onClose={() => setConfirmDel(false)}
        onArchive={() => { toggleArchive(); setConfirmDel(false); }}
        onConfirm={() => { setConfirmDel(false); onDelete(p.id); }} />}
    </div>
  );
};

/* ====================== Header overview ======================
   Counts by type and status, and the in-force estimates summed PER CURRENCY.
   Nothing is converted and nothing here feeds net worth (Non-Goal 6). */
const PropertiesSummary = ({ properties, estById, perms }) => {
  const live = properties.filter(p => !p.archived);
  const typeRows = PR_D.propertyTypes.map(t => ({ key: t.key, icon: t.icon, iconColor: t.color, label: t.label, count: live.filter(p => p.type === t.key).length }));
  const TONE = { income: 'var(--finance-income)', outline: 'var(--mud-palette-text-secondary)' };
  const statusRows = PR_D.propertyStatuses.map(s => ({ key: s.key, icon: s.icon, iconColor: TONE[s.tone], label: s.label, count: properties.filter(p => PR_H.propStatus(p) === s.key).length }));
  const valCell = (v, c) => <span className={`con-bd-net ${v > 0 ? 'net-pos' : v < 0 ? 'net-neg' : 'net-flat'}`}>{PR_H.money(v, c)}</span>;
  const byCur = {};
  properties.filter(p => PR_H.propStatus(p) === 'Owned').forEach(p => {
    const c = PR_H.propCurrentEstimate(estById[p.id]);
    if (!c) return;
    byCur[p.currencyCode] = byCur[p.currencyCode] || { sum: 0, n: 0 };
    byCur[p.currencyCode].sum += c.value; byCur[p.currencyCode].n += 1;
  });
  const valueRows = Object.keys(byCur).sort().map(k => ({ key: k, icon: 'payments', iconColor: 'var(--mud-palette-text-secondary)',
    label: `${k} · ${byCur[k].n} ${byCur[k].n === 1 ? 'property' : 'properties'}`, count: valCell(byCur[k].sum, k) }));
  /* Total in the main currency, via the latest rate (same convert the contract
     run rate uses). A currency with no rate is named, never counted 1:1. */
  const main = (PR_D.userPreferences || {}).mainCurrency || 'USD';
  /* Latest AsOf for the pair, either direction (rates are append-only). */
  const rateOf = (f, t) => {
    const pick = (a, b) => (PR_D.exchangeRates || []).filter(r => r.from === a && r.to === b).sort((x, y) => (x.asOf < y.asOf ? 1 : -1))[0];
    const d = pick(f, t); if (d) return d.rate;
    const i = pick(t, f); return i ? 1 / i.rate : null;
  };
  const convert = (a, f, t) => { if (f === t) return a; const r = rateOf(f, t); return r == null ? null : a * r; };
  let total = 0; const missing = [];
  Object.keys(byCur).forEach(k => { const v = convert(byCur[k].sum, k, main); if (v == null) missing.push(k); else total += v; });
  const totalCell = valueRows.length ? (
    <span title={missing.length ? `${missing.join(', ')} excluded — no rate to ${main}` : `Converted to ${main} at the latest rate`}>
      {valCell(Math.round(total * 100) / 100, main)}{missing.length ? <span style={{ color: 'var(--mud-palette-text-secondary)', fontWeight: 400 }}> · {missing.join(', ')} excl.</span> : null}
    </span>
  ) : false;
  return (
    <div className="con-stats prop-stats">
      <BreakdownTile label="By type" rows={typeRows} empty="No properties." />
      <BreakdownTile label="By status" rows={statusRows} total={properties.length} empty="No properties." />
      {perms.estimatesRead ? (
        <BreakdownTile className="con-bd-money" label="Estimated value, owned" rows={valueRows}
          total={totalCell} totalLabel={`Total · ${main}`} empty="No estimates in force." />
      ) : null}
    </div>
  );
};

/* ====================== Page ====================== */
const Properties = ({ tweaks = {}, onNavigate }) => {
  const { useState } = React;
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  const role = tweaks.propRole || 'owner';
  const perms = role === 'owner'
    ? { read: true, create: true, update: true, delete: true, estimatesRead: true, estimatesWrite: true }
    : { read: true, create: false, update: false, delete: false, estimatesRead: true, estimatesWrite: false };
  /* GET /api/property-limits — a 503 leaves the cap unknown. */
  const limitsDegraded = !!tweaks.propLimitsDegraded;
  const cap = limitsDegraded ? null : (tweaks.propSmartTagCap != null ? tweaks.propSmartTagCap : PR_D.PROPERTY_MAX_SMART_TAGS_PER_PROPERTY);

  const [properties, setProperties] = useState(() => (tweaks.propEmpty ? [] : PR_D.properties));
  const [estById, setEstById] = useState(() => Object.fromEntries(PR_D.properties.map(p => [p.id, (PR_D.propertyEstimates[p.id] || []).slice()])));
  /* The tag-delete blocker (TransactionTags) reads this live seed. */
  const [tagsById, setTagsById] = useState(() => Object.fromEntries(PR_D.properties.map(p => [p.id, (PR_D.propertySmartTagSeed[p.id] || []).slice()])));
  const [openId, setOpenId] = useState('p-storgata');
  const [q, setQ] = useState('');
  const [types, setTypes] = useState([]);
  const [statuses, setStatuses] = useState([]);
  const [sort, setSort] = useState({ key: 'name', dir: 'asc' });
  const [batch, setBatch] = useState(25);
  const [showAdd, setShowAdd] = useState(false);

  React.useEffect(() => { setProperties(tweaks.propEmpty ? [] : PR_D.properties); }, [tweaks.propEmpty]);

  const setEst = (id) => (fn) => setEstById(m => ({ ...m, [id]: typeof fn === 'function' ? fn(m[id] || []) : fn }));
  const setTags = (id) => (fn) => setTagsById(m => {
    const next = { ...m, [id]: typeof fn === 'function' ? fn(m[id] || []) : fn };
    PR_D.propertySmartTagSeed = next;
    return next;
  });

  const sortFields = [
    { key: 'name', label: 'Name', type: 'text', sortValue: (p) => (p.name || '').toLowerCase() },
    { key: 'type', label: 'Type', type: 'status', sortValue: (p) => PR_H.propTypeInfo(p.type).enumValue },
    { key: 'acquired', label: 'Acquired', type: 'date', sortValue: (p) => p.acquiredDate || null },
    /* PropertySortBy.Value — the in-force estimate as of now, raw magnitude in
       each property's own currency, nulls last in both directions. */
    ...(perms.estimatesRead ? [{ key: 'value', label: 'Value', type: 'number', sortValue: (p) => { const c = PR_H.propCurrentEstimate(estById[p.id]); return c ? c.value : null; } }] : []),
  ];

  const rows = properties.filter(p => {
    if (types.length && !types.includes(p.type)) return false;
    if (statuses.length && !statuses.includes(PR_H.propStatus(p))) return false;
    if (q && !PR_H.propSearchHay(p).includes(q.toLowerCase())) return false;
    return true;
  });
  const sorted = DS.SortHelpers ? DS.SortHelpers.sortRows(rows, sortFields, sort, (p) => p.id) : rows;
  const mixedCurrencies = sort.key === 'value' && new Set(rows.map(p => p.currencyCode)).size > 1;
  const owned = properties.filter(p => PR_H.propStatus(p) === 'Owned').length;

  const create = (dto) => {
    const id = `p-new-${Date.now()}`;
    const now = new Date().toISOString();
    setEstById(m => ({ ...m, [id]: [] }));
    setTagsById(m => ({ ...m, [id]: [] }));
    setProperties(prev => [{ id, archived: null, createdAt: now, updatedAt: now, ...dto }, ...prev]);
    setOpenId(id);
    setShowAdd(false);
  };
  const update = (p) => setProperties(prev => prev.map(x => (x.id === p.id ? p : x)));
  const remove = (id) => {
    setProperties(prev => prev.filter(x => x.id !== id));
    setTags(id)([]);
  };

  return (
    <div className="col gap-6">
      <PageHeader
        title="Properties"
        icon="home_work"
        sub={`${owned} owned · ${properties.length} on file`}
        overview={properties.length ? <PropertiesSummary properties={properties} estById={estById} perms={perms} /> : null}
        overviewDefaultOpen
        searchDefaultOpen
        search={
          <div className="col gap-2">
            <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap' }}>
              <div style={{ minWidth: 280, flex: 1 }}>
                <SearchField placeholder="Search name, address, registration, VIN…" value={q} onChange={setQ} />
              </div>
              <div style={{ minWidth: 160 }}>
                <MultiSelect allLabel="Any type" value={types} onChange={setTypes}
                  options={PR_D.propertyTypes.map(t => ({ value: t.key, label: t.label, icon: t.icon, iconColor: t.color }))} />
              </div>
              <div style={{ minWidth: 160 }}>
                <MultiSelect allLabel="Any status" value={statuses} onChange={setStatuses}
                  options={PR_D.propertyStatuses.map(s => ({ value: s.key, label: s.label }))} />
              </div>
              <SortSelect sort={sort} onSort={setSort} fields={sortFields} />
              <PageSizeSelect prefix="Load" suffix="at a time" label="Properties per batch" value={batch} onChange={setBatch} options={[25, 50, 100]} />
            </div>
            {mixedCurrencies ? (
              <div className="prop-sort-note"><MIcon name="info" size={15} />Sorted by the raw amount in each property’s own currency. Values in different currencies are not converted.</div>
            ) : null}
          </div>
        }
        primary={perms.create ? { label: 'New property', icon: 'add', onClick: () => setShowAdd(true) } : undefined}
      />

      {properties.length === 0 ? (
        <EmptyState icon="home_work" title="No properties yet"
          description="Record a house, cabin, car or boat with its address or registration, track what it’s worth over time, and watch the transactions that belong to it."
          action={perms.create ? <Button variant="filled" color="primary" icon="add" onClick={() => setShowAdd(true)}>New property</Button> : null} />
      ) : (
        <div className="acct-list">
          <InfiniteList items={sorted} batchSize={batch} itemKey={(p) => p.id} noun="properties"
            renderItem={(p) => (
              <PropertyListItem row={p} perms={perms} cap={cap} limitsDegraded={limitsDegraded} onNavigate={onNavigate}
                open={openId === p.id} onToggle={(o) => setOpenId(o ? p.id : null)}
                estimates={estById[p.id] || []} setEstimates={setEst(p.id)}
                tagIds={tagsById[p.id] || []} setTagIds={setTags(p.id)}
                onUpdate={update} onDelete={remove} />
            )}
            empty={<EmptyLine align="center" pad="lg">No properties match your filters.</EmptyLine>}
            trailing={perms.create ? <AddRow title="New property" sub="A house, apartment, cabin or plot — or a car, motorcycle, boat or trailer." onClick={() => setShowAdd(true)} /> : null} />
        </div>
      )}

      {showAdd && <AddPropertyModal onClose={() => setShowAdd(false)} onSave={create} />}
    </div>
  );
};

Object.assign(window, { Properties, PropertyStatusChip });
