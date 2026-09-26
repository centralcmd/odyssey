/* Transaction tags — search + status filter + sortable table. Rows don't
   expand: Name / Description / Status is the whole record, so there's nothing
   a detail panel could add. Editing runs through the tag dialog.

   Fields mirror the Odyssey.Finance.Dtos TransactionTag DTOs:
     ExistingTransactionTag — TransactionTagId, Name (≤64), Description (≤256),
                              Archived (datetime?, null = active)
     NewTransactionTag      — Name, Description, Archived (bool) */

const TAG_TONE = { bg: 'oklch(0.72 0.16 295 / 0.16)', fg: 'oklch(0.78 0.13 295)' };
const TAG_STATUS_OPTIONS = [
  { value: 'active',   label: 'Active' },
  { value: 'archived', label: 'Archived' },
];

const tagSortVal = (t, key) => {
  switch (key) {
    case 'name':        return t.name.toLowerCase();
    case 'description': return (t.description || '~').toLowerCase();
    case 'status':      return t.archived ? 1 : 0;
    default:            return 0;
  }
};

/* ---------- Sortable table ----------
   The row/sort machinery is the shared DS RecordTable; this wrapper only
   declares the tag-specific columns and row actions. Rows don't expand: the
   three columns already show every field a tag has, so a detail panel would
   only repeat them. Editing happens in the tag dialog. */
const TagTable = ({ tags, onSave, onDelete, onEdit, sort, onSortChange, empty, ariaLabel = 'Transaction tags' }) => {
  const H = window.OdysseyHelpers;
  return (
    <RecordTable
      rows={tags}
      ariaLabel={ariaLabel}
      rowKey={(t) => t.id}
      defaultSort={{ key: 'name', dir: 'asc' }}
      sort={sort}
      onSortChange={onSortChange}
      leading={() => <Avatar icon="local_offer" tone={TAG_TONE} />}
      columns={[
        {
          key: 'name', header: 'Name', sortable: true, sortType: 'text', sortValue: (t) => tagSortVal(t, 'name'),
          cell: (t, ctx) => (
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
              {t.name}{ctx.justSaved && <Chip tone="income" dot>Saved</Chip>}
            </span>
          ),
        },
        {
          key: 'description', header: 'Description', sortable: true, sortType: 'text', className: 'muted',
          sortValue: (t) => tagSortVal(t, 'description'),
          cell: (t) => t.description || '—',
        },
        {
          key: 'status', header: 'Status', sortable: true, sortType: 'status', sortValue: (t) => tagSortVal(t, 'status'),
          cell: (t) => { const s = H.archivedStatus(t); return <Chip tone={s.tone} dot>{s.label}</Chip>; },
        },
      ]}
      actions={(t, ctx) => [
        { icon: 'edit', label: 'Edit', onClick: () => onEdit(t) },
        { icon: t.archived ? 'unarchive' : 'archive', label: t.archived ? 'Restore' : 'Archive', onClick: () => onSave(t.id, { archived: t.archived ? null : new Date().toISOString() }) },
        { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(t.id); } },
        { divider: true },
        { icon: 'delete', label: 'Delete', danger: true, onClick: ctx.remove },
      ]}
      onSave={onSave}
      onDelete={onDelete}
      empty={empty}
    />
  );
};

/* ---------- New / Edit tag dialog (New/ExistingTransactionTag DTO) ----------
   One dialog serves both create and edit: pass an existing `tag` to prefill and
   switch into edit mode (title, submit copy, and save callback all follow).

   Tag names are unique case-insensitively, ARCHIVED ROWS INCLUDED — a tag name
   is an identity now (it names every budget item planning for the tag), so two
   same-named tags would render as two indistinguishable budget rows. The server
   answers a clash with a 409 keyed to `name`; this dialog renders it at the name
   field, naming the archived case, which has no inline remedy: restoring or
   renaming an archived tag is a row action on this page. */
const AddTagModal = ({ onClose, onCreate, onSave, tag = null, siblings = [], subtitle = 'Tags group transactions and budget items by category.' }) => {
  const { useState } = React;
  const editing = !!tag;
  const [draft, setDraft] = useState({ name: tag?.name || '', description: tag?.description || '' });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => {
    setDraft(d => ({ ...d, [k]: v }));
    if (errors[k]) setErrors(e => ({ ...e, [k]: undefined }));
  };

  // (Esc-to-close, scrim click and focus handling come from the DS Modal shell.)

  const submit = () => {
    const name = draft.name.trim();
    if (!name) { setErrors({ name: 'Give the tag a name.' }); return; }
    const clash = siblings.find(t => t.id !== (tag && tag.id) && t.name.trim().toLowerCase() === name.toLowerCase());
    if (clash) {
      setErrors({ name: clash.archived
        ? `A tag called “${clash.name}” already exists but is archived. Restore it, or rename it, to reuse the name.`
        : `A tag called “${clash.name}” already exists. Pick a different name.` });
      return;
    }
    const dto = {
      name,
      description: draft.description.trim() || undefined,
    };
    if (editing) {
      // Preserve the tag's archive state — that's toggled from the row action.
      onSave && onSave(tag.id, dto);
    } else {
      onCreate && onCreate({ ...dto, archived: null });
    }
  };

  return (
    <Modal
      title={editing ? 'Edit tag' : 'New tag'}
      subtitle={subtitle}
      icon="local_offer"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create tag'}
          </Button>
        </React.Fragment>
      }>
      <Field label="Name" value={draft.name} onChange={set('name')}
        placeholder="e.g. Groceries" error={errors.name} helper="Up to 64 characters · must be unique" autoFocus />
      <Field label="Description" value={draft.description} onChange={set('description')}
        placeholder="What this tag is for" helper="Up to 256 characters" />
    </Modal>
  );
};

/* ---------- Delete refusal (409) ----------
   A tag in use is not deletable: the link rows are RESTRICT, and the service
   pre-checks so the refusal can name the surface instead of answering a
   generic conflict. One clause per blocker class — a tag blocked by several
   reports all of them — and COUNTS ONLY: naming the contracts or budgets would
   reach past what `transactions.tags.delete` is allowed to read. */
const TagDeleteBlockedModal = ({ tag, clauses, onClose }) => (
  <Modal
    title="Unable to delete this tag"
    subtitle={<React.Fragment>“<strong>{tag.name}</strong>” is still in use. Remove it where it is used, then delete it.</React.Fragment>}
    icon="block"
    onClose={onClose}
    footer={<Button variant="filled" color="primary" onClick={onClose}>Close</Button>}>
    <ul className="tag-block-list">
      {clauses.map((c, i) => (
        <li key={i}><MIcon name="link" size={16} /><span>{c}</span></li>
      ))}
    </ul>
    <p className="tag-block-foot">Archiving it instead keeps every link intact and retires the tag from the pickers.</p>
  </Modal>
);

/* ---------- Page ----------
   One generic tags-management page. Journal tags, task tags and transaction
   tags share the exact same DTO shape (Name / Description / Archived) and
   surface, so they're all instances of this factory — only the seed source and
   the surrounding copy differ. */
const createTagsPage = (cfg) => () => {
  const { useState, useEffect, useMemo } = React;
  const d = window.OdysseyData;

  const [q, setQ] = useState('');
  const [debouncedQ, setDebouncedQ] = useState('');
  const [statusFilter, setStatusFilter] = useState([]);
  const [adding, setAdding] = useState(false);
  const [editingTag, setEditingTag] = useState(null);
  // The tag whose delete was refused, with the clauses naming each blocker.
  const [blocked, setBlocked] = useState(null);
  const [tags, setTags] = useState(cfg.source(d));
  // Shared sort (§6.7): Name is the single curated field — the toolbar
  // renders a direction toggle only — but the one {key,dir} still syncs with
  // every sortable header.
  const [sort, setSort] = useState({ key: 'name', dir: 'asc' });
  // Server pagination: page (1-based) + rows-per-page (footer Pager owns it,
  // toolbar PageSizeSelect mirrors it).
  const [pageSize, setPageSize] = useState(25);
  const [page, setPage] = useState(1);

  useEffect(() => { const t = setTimeout(() => setDebouncedQ(q.trim()), 300); return () => clearTimeout(t); }, [q]);

  const createTag = (dto) => {
    setTags(prev => [{ id: `${cfg.idPrefix}${Date.now()}`, ...dto }, ...prev]);
    setAdding(false);
  };
  const onSave = (id, patch) => { setTags(prev => prev.map(t => t.id === id ? { ...t, ...patch } : t)); setEditingTag(null); };
  const onDelete = (id) => setTags(prev => prev.filter(t => t.id !== id));
  /* The pre-check the service runs before the delete. Refused → the 409's
     clauses, nothing removed; clear → the ordinary delete. */
  const tryDelete = (id) => {
    const tag = tags.find(t => t.id === id);
    const clauses = tag && cfg.blockers ? cfg.blockers(tag, d) : [];
    if (clauses.length) { setBlocked({ tag, clauses }); return; }
    onDelete(id);
  };

  const filtered = useMemo(() => tags.filter(t => {
    const st = t.archived ? 'archived' : 'active';
    if (statusFilter.length && !statusFilter.includes(st)) return false;
    if (debouncedQ) {
      const hay = `${t.name} ${t.description || ''}`.toLowerCase();
      if (!hay.includes(debouncedQ.toLowerCase())) return false;
    }
    return true;
  }), [tags, statusFilter, debouncedQ]);

  // Any search / filter / sort / size change returns to page 1 (server contract).
  useEffect(() => { setPage(1); }, [debouncedQ, statusFilter, sort, pageSize]);
  const totalCount = filtered.length;
  const paged = useMemo(() => {
    if (pageSize === 'all') return filtered;
    const start = (page - 1) * pageSize;
    return filtered.slice(start, start + pageSize);
  }, [filtered, page, pageSize]);

  const activeCount = tags.filter(t => !t.archived).length;
  const archivedCount = tags.length - activeCount;
  const hasFilters = !!(debouncedQ || statusFilter.length);
  const clearFilters = () => { setQ(''); setStatusFilter([]); };

  return (
    <div className="col gap-6">
      <PageHeader
        title={cfg.title}
        icon="local_offer"
        sub={`${activeCount} active · ${archivedCount} archived`}
        overview={(
          <div className="odc-summary-grid">
            <BreakdownTile label="By status" empty="No tags."
              rows={odcStatusRows(tags, [
                { key: 'active', label: 'Active', tone: 'income', icon: 'task_alt' },
                { key: 'archived', label: 'Archived', tone: 'outline', icon: 'inventory_2' },
              ], (t) => (t.archived ? 'archived' : 'active'))} />
          </div>
        )}
        overviewDefaultOpen
        searchDefaultOpen
        search={(
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap', alignItems: 'flex-end' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <Field placeholder={cfg.searchPlaceholder} value={q} onChange={setQ} clearable />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Any status" value={statusFilter} onChange={setStatusFilter} options={TAG_STATUS_OPTIONS} />
            </div>
            <SortSelect sort={sort} onSort={setSort}
              fields={[{ key: 'name', label: 'Name', type: 'text' }]} />
            <PageSizeSelect value={pageSize} onChange={setPageSize} />
          </div>
        )}
        primary={{ label: 'New tag', icon: 'add', onClick: () => setAdding(true) }}
      />

      {adding && <AddTagModal onClose={() => setAdding(false)} onCreate={createTag} siblings={tags} subtitle={cfg.modalSubtitle} />}
      {editingTag && <AddTagModal tag={editingTag} onClose={() => setEditingTag(null)} onSave={onSave} siblings={tags} subtitle={cfg.modalSubtitle} />}
      {blocked && <TagDeleteBlockedModal tag={blocked.tag} clauses={blocked.clauses} onClose={() => setBlocked(null)} />}

      <Card>
        <CardBody style={{ padding: 0 }}>
          <TagTable
            tags={paged}
            ariaLabel={cfg.title}
            onSave={onSave}
            onDelete={tryDelete}
            onEdit={setEditingTag}
            sort={sort}
            onSortChange={setSort}
            empty={(
              <EmptyState icon="local_offer" mutedIcon
                title="No tags match"
                desc={hasFilters ? 'Try a different search or clear the filters to see everything.' : cfg.emptyDesc}
                action={hasFilters
                  ? <Button variant="outlined" icon="close" onClick={clearFilters}>Clear filters</Button>
                  : <Button variant="filled" color="primary" icon="add" onClick={() => setAdding(true)}>New tag</Button>} />
            )}
          />
          {totalCount > 0 && (
            <Pager page={page} pageSize={pageSize} totalCount={totalCount}
              onPageChange={setPage} onPageSizeChange={setPageSize} />
          )}
        </CardBody>
      </Card>
    </div>
  );
};

const TransactionTags = createTagsPage({
  title: 'Transaction tags',
  source: (d) => d.tags,
  idPrefix: 'tag-',
  searchPlaceholder: 'Search name or description…',
  modalSubtitle: 'Tags group transactions and budget items by category.',
  emptyDesc: 'Create your first tag to start categorizing transactions.',
  /* Both link classes that RESTRICT a tag delete today: budget items plan for a
     tag, and contracts watch one as a smart tag. Each reports its own clause. */
  blockers: (tag, d) => {
    const items = (d.budgets || []).reduce((n, b) => n + (b.items || []).filter(i => i.tagId === tag.id).length, 0);
    const contracts = Object.values(d.contractSmartTagSeed || {}).filter(ids => ids.includes(tag.id)).length;
    // Fifth clause (Property backend §5): PropertySmartTags, counts only.
    const properties = Object.values(d.propertySmartTagSeed || {}).filter(ids => ids.includes(tag.id)).length;
    return [
      items ? `${items} budget item${items === 1 ? '' : 's'} plan${items === 1 ? 's' : ''} for this tag.` : null,
      contracts ? `This tag is a smart tag on ${contracts} contract${contracts === 1 ? '' : 's'}.` : null,
      properties ? `This tag is a smart tag on ${properties} propert${properties === 1 ? 'y' : 'ies'}. Remove it there first.` : null,
    ].filter(Boolean);
  },
});

const JournalTags = createTagsPage({
  title: 'Journal tags',
  source: (d) => d.journalTags,
  idPrefix: 'jt-',
  searchPlaceholder: 'Search name or description…',
  modalSubtitle: 'Tags group journal entries by category.',
  emptyDesc: 'Create your first tag to start categorizing journal entries.',
});

const TaskTags = createTagsPage({
  title: 'Task tags',
  source: (d) => d.taskTags,
  idPrefix: 'kt-',
  searchPlaceholder: 'Search name or description…',
  modalSubtitle: 'Tags group tasks by category.',
  emptyDesc: 'Create your first tag to start categorizing tasks.',
});

const PhotoTags = createTagsPage({
  title: 'Photo tags',
  source: (d) => d.photoTags,
  idPrefix: 'pt-',
  searchPlaceholder: 'Search name or description…',
  modalSubtitle: 'Tags group photos in your library by category.',
  emptyDesc: 'Create your first tag to start categorizing photos.',
});

Object.assign(window, { TransactionTags, JournalTags, TaskTags, PhotoTags, TagTable, AddTagModal });
