/* Photos — /photos
   ----------------------------------------------------------------------------
   The personal photo library. Every photo the user has uploaded (today they
   arrive attached to Journal entries) gathered into one browsable surface:
   a uniform thumbnail grid + an Albums view, with search/filter (incl. a
   date-taken range), a sort control, page-size paging (default 24), favourites,
   archive, multi-select bulk actions, a detail modal, a metadata editor, and
   album management (labels model — a photo can belong to several albums, with
   per-album ordering + a chosen cover).

   Every photo is a first-class record: distinct Title + Caption, tag ids
   (→ photoTags), people as existing Person-contact ids (never invented
   here), capture date/time, coordinates + location name, dimensions, and
   provenance (created/updated-by display names).

   Registers window.Photos (mounted by templates/photos/Photos.dc.html via
   kit-app.js). Depends on photos-data.js (window.PHOTOS etc) + photos.css, and
   on the kit/DS components already on window (PageHeader, Button, Field, …). */

(function () {
  const { useState, useEffect, useRef, useMemo } = React;
  const MI = function (props) { const M = window.MIcon; return M ? React.createElement(M, props) : null; };
  const PHOTOS = window.PHOTOS, PL_ALBUMS = window.PL_ALBUMS;
  const PL_TAG_OPTIONS = window.PL_TAG_OPTIONS, PL_PERSON_OPTIONS = window.PL_PERSON_OPTIONS;
  const plPhotoBg = window.plPhotoBg, plFmtDate = window.plFmtDate, plTime = window.plTime, plDateTime = window.plDateTime;
  const plTagName = window.plTagName, plPersonName = window.plPersonName;
  const PAGE_SIZES = [24, 48, 96];

  const titleOf = function (p) { return (p.title && p.title.trim()) || p.name; };

  /* ---- Library state: filters, favourites, archive, album membership ------ */
  function useLibrary() {
    const [q, setQ] = useState('');
    const [albums, setAlbums] = useState([]);
    const [tags, setTags] = useState([]);
    const [people, setPeople] = useState([]);
    const [favOnly, setFavOnly] = useState(false);
    const [archivedView, setArchivedView] = useState(false);
    const [from, setFrom] = useState('');   // date-taken range (inclusive)
    const [to, setTo] = useState('');
    const [sort, setSort] = useState({ key: 'date', dir: 'desc' });

    const [pool, setPool] = useState(PHOTOS);            // all photos (uploads prepend)
    const [edits, setEdits] = useState({});              // photoId → metadata patch
    const [favs, setFavs] = useState(function () { return new Set(PHOTOS.filter(function (p) { return p.fav; }).map(function (p) { return p.id; })); });
    const [archived, setArchived] = useState(function () { return new Set(); });
    const [albumList, setAlbumList] = useState(function () { return PL_ALBUMS.map(function (a) { return Object.assign({}, a); }); });
    // Ordered membership: albumId → [photoId,…] (Position = array index).
    const [members, setMembers] = useState(function () {
      const m = {}; PL_ALBUMS.forEach(function (a) { m[a.id] = []; });
      PHOTOS.forEach(function (p) { if (m[p.album]) m[p.album].push(p.id); });
      return m;
    });
    const [covers, setCovers] = useState({});
    const uploadSeq = useRef(0);

    const decorate = useMemo(function () {
      return pool.map(function (p) {
        const inAlbums = albumList.filter(function (a) { return (members[a.id] || []).indexOf(p.id) !== -1; }).map(function (a) { return a.id; });
        return Object.assign({}, p, edits[p.id] || {}, { fav: favs.has(p.id), archived: archived.has(p.id), albums: inAlbums });
      });
    }, [pool, edits, favs, archived, members, albumList]);
    const byId = useMemo(function () { const m = {}; decorate.forEach(function (p) { m[p.id] = p; }); return m; }, [decorate]);

    const matches = function (p) {
      if (p.archived !== archivedView) return false;
      if (albums.length && !albums.some(function (a) { return p.albums.indexOf(a) !== -1; })) return false;
      if (tags.length && !(p.tagIds || []).some(function (t) { return tags.indexOf(t) !== -1; })) return false;
      if (people.length && !(p.personIds || []).some(function (n) { return people.indexOf(n) !== -1; })) return false;
      if (favOnly && !p.fav) return false;
      if (from && p.date.slice(0, 10) < from) return false;
      if (to && p.date.slice(0, 10) > to) return false;
      if (q) {
        const hay = [titleOf(p), p.name, p.caption, p.location]
          .concat((p.tagIds || []).map(plTagName), (p.personIds || []).map(plPersonName))
          .filter(Boolean).join(' ').toLowerCase();
        if (hay.indexOf(q.toLowerCase()) === -1) return false;
      }
      return true;
    };
    const sortRows = function (list) {
      const dir = sort.dir === 'asc' ? 1 : -1;
      const val = function (p) { return sort.key === 'title' ? titleOf(p).toLowerCase() : sort.key === 'added' ? (p.createdAt || '') : p.date; };
      return list.slice().sort(function (a, b) { const av = val(a), bv = val(b); return av < bv ? -dir : av > bv ? dir : 0; });
    };
    const rows = useMemo(function () { return sortRows(decorate.filter(matches)); }, [decorate, albums, tags, people, favOnly, q, archivedView, from, to, sort]);

    const albumPhotos = function (id) {
      const order = members[id] || [];
      return order.map(function (pid) { return byId[pid]; }).filter(function (p) { return p && !p.archived; });
    };
    const coverSeed = function (a) {
      if (covers[a.id]) { const ph = byId[covers[a.id]]; if (ph) return ph.seed; }
      const list = albumPhotos(a.id); return list.length ? list[0].seed : a.cover;
    };
    const coverId = function (a) { return covers[a.id] || null; };

    const state = { q: q, albums: albums, tags: tags, people: people, favOnly: favOnly, archivedView: archivedView, from: from, to: to, sort: sort };
    const set = function (patch) {
      if ('q' in patch) setQ(patch.q);
      if ('albums' in patch) setAlbums(patch.albums);
      if ('tags' in patch) setTags(patch.tags);
      if ('people' in patch) setPeople(patch.people);
      if ('favOnly' in patch) setFavOnly(patch.favOnly);
      if ('archivedView' in patch) setArchivedView(patch.archivedView);
      if ('from' in patch) setFrom(patch.from);
      if ('to' in patch) setTo(patch.to);
      if ('sort' in patch) setSort(patch.sort);
    };
    const toggleFav = function (id) { setFavs(function (prev) { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; }); };
    const toggleArchive = function (id) { setArchived(function (prev) { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; }); };
    const archiveMany = function (ids, on) { setArchived(function (prev) { const n = new Set(prev); ids.forEach(function (id) { on ? n.add(id) : n.delete(id); }); return n; }); };
    const updatePhoto = function (id, patch) {
      setEdits(function (e) { const n = Object.assign({}, e); n[id] = Object.assign({}, n[id], patch, { updatedBy: (window.OdysseyData && window.OdysseyData.user && window.OdysseyData.user.name) || 'You', updatedAt: new Date().toISOString() }); return n; });
    };
    const setAlbumMembers = function (albumId, photoIds) { setMembers(function (m) { const n = Object.assign({}, m); n[albumId] = photoIds.slice(); return n; }); };
    const addToAlbums = function (photoIds, albumIds) {
      setMembers(function (m) { const n = Object.assign({}, m); albumIds.forEach(function (aid) { const arr = (n[aid] || []).slice(); photoIds.forEach(function (pid) { if (arr.indexOf(pid) === -1) arr.push(pid); }); n[aid] = arr; }); return n; });
    };
    const removeFromAlbums = function (photoIds, albumIds) {
      setMembers(function (m) { const n = Object.assign({}, m); albumIds.forEach(function (aid) { n[aid] = (n[aid] || []).filter(function (pid) { return photoIds.indexOf(pid) === -1; }); }); return n; });
      // Removing the cover photo nulls the cover (spec §7 album PUT eval order).
      setCovers(function (c) { const n = Object.assign({}, c); albumIds.forEach(function (aid) { if (photoIds.indexOf(n[aid]) !== -1) delete n[aid]; }); return n; });
    };
    const moveInAlbum = function (albumId, photoId, delta) {
      setMembers(function (m) {
        const arr = (m[albumId] || []).slice(); const i = arr.indexOf(photoId); const j = i + delta;
        if (i === -1 || j < 0 || j >= arr.length) return m;
        arr.splice(j, 0, arr.splice(i, 1)[0]);
        const n = Object.assign({}, m); n[albumId] = arr; return n;
      });
    };
    const createAlbum = function (name, photoIds, description) {
      const id = 'al' + (Date.now() % 1000000);
      setAlbumList(function (a) { return a.concat([{ id: id, name: name || 'Untitled album', cover: 0, description: description || '' }]); });
      setMembers(function (m) { const n = Object.assign({}, m); n[id] = (photoIds || []).slice(); return n; });
      return id;
    };
    const updateAlbum = function (id, patch) { setAlbumList(function (a) { return a.map(function (x) { return x.id === id ? Object.assign({}, x, patch) : x; }); }); };
    const deleteAlbum = function (id) { setAlbumList(function (a) { return a.filter(function (x) { return x.id !== id; }); }); };
    const setCover = function (albumId, photoId) { setCovers(function (c) { const n = Object.assign({}, c); n[albumId] = photoId; return n; }); };
    const upload = function (n) {
      const count = n || 1;
      const ups = [];
      const now = new Date().toISOString();
      const user = (window.OdysseyData && window.OdysseyData.user && window.OdysseyData.user.name) || 'You';
      for (let i = 0; i < count; i++) {
        const seed = 900 + (uploadSeq.current++);
        ups.push({ id: 'up' + seed, fileId: 'file-up' + seed, title: null, name: 'upload_' + (seed - 899) + '.jpg', album: null, seed: seed, w: 1, h: 1, pxW: 640, pxH: 640, tagIds: [], personIds: [], lat: null, lng: null, date: now.slice(0, 19), createdBy: user, createdAt: now, updatedBy: null, updatedAt: now });
      }
      setPool(function (prev) { return ups.concat(prev); });
      return ups.map(function (u) { return u.id; });
    };

    return {
      state: state, set: set, rows: rows, decorate: decorate, byId: byId,
      albumList: albumList, members: members, albumPhotos: albumPhotos, coverSeed: coverSeed, coverId: coverId, covers: covers,
      toggleFav: toggleFav, toggleArchive: toggleArchive, archiveMany: archiveMany, updatePhoto: updatePhoto,
      setAlbumMembers: setAlbumMembers, addToAlbums: addToAlbums, removeFromAlbums: removeFromAlbums, moveInAlbum: moveInAlbum,
      createAlbum: createAlbum, updateAlbum: updateAlbum, deleteAlbum: deleteAlbum, setCover: setCover, upload: upload,
    };
  }

  function useSelection() {
    const [selecting, setSelecting] = useState(false);
    const [sel, setSel] = useState(function () { return new Set(); });
    const toggle = function (id) { setSel(function (prev) { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; }); };
    const clear = function () { setSel(new Set()); };
    const done = function () { setSelecting(false); clear(); };
    const start = function () { setSelecting(function (s) { if (s) clear(); return !s; }); };
    const setAll = function (ids) { setSel(new Set(ids)); };
    const startWith = function (id) { setSelecting(true); setSel(new Set([id])); };
    return { selecting: selecting, sel: sel, toggle: toggle, clear: clear, done: done, start: start, setAll: setAll, startWith: startWith };
  }

  /* ---- Tile ---------------------------------------------------------------- */
  const PhotoTile = function (props) {
    const p = props.photo;
    const label = titleOf(p);
    return React.createElement('div', {
      className: 'pl-tile' + (props.selected ? ' is-selected' : ''),
      style: { borderRadius: 'var(--radius-md)' },
      onClick: function (e) { if (props.selectable) { props.onToggleSelect(p.id); e.stopPropagation(); } else props.onOpen(p); },
    },
      React.createElement('div', { className: 'pl-tile-img', style: plPhotoBg(p.seed), role: 'img', 'aria-label': label }),
      React.createElement('div', { className: 'pl-tile-scrim' }),
      React.createElement('span', { className: 'pl-tile-cap' }, label),
      p.archived ? React.createElement('span', { className: 'pl-tile-arch' }, 'Archived') : null,
      React.createElement('button', { type: 'button', className: 'pl-check' + (props.selected ? ' on' : ''), 'aria-label': props.selected ? 'Deselect' : 'Select', onClick: function (e) { e.stopPropagation(); if (props.selectable) props.onToggleSelect(p.id); else if (props.onStartSelect) props.onStartSelect(p.id); } }, React.createElement(MI, { name: 'check', size: 15 })),
      React.createElement('button', { type: 'button', className: 'pl-heart' + (p.fav ? ' on' : ''), 'aria-label': p.fav ? 'Unfavourite' : 'Favourite', onClick: function (e) { e.stopPropagation(); props.onToggleFav(p.id); } }, React.createElement(MI, { name: p.fav ? 'favorite' : 'favorite_border', size: 16 }))
    );
  };

  /* ---- Detail modal (view) ------------------------------------------------- */
  const DetailModal = function (props) {
    const photos = props.photos, index = props.index, lib = props.lib || {};
    const contained = props.contained !== false;
    const p = photos[index];
    useEffect(function () {
      const onKey = function (e) {
        if (e.key === 'Escape') props.onClose();
        else if (e.key === 'ArrowRight') props.onIndex((index + 1) % photos.length);
        else if (e.key === 'ArrowLeft') props.onIndex((index - 1 + photos.length) % photos.length);
      };
      window.addEventListener('keydown', onKey);
      return function () { window.removeEventListener('keydown', onKey); };
    }, [index, photos.length]);
    if (!p) return null;
    const Button = window.Button, Chip = window.Chip, ContactChip = (window.OdysseyDesignSystem_d5aa51 || {}).ContactChip;
    const go = function (d) { props.onIndex((index + d + photos.length) % photos.length); };
    const albumNames = (p.albums || []).map(function (id) { const a = (lib.albumList || []).find(function (x) { return x.id === id; }); return a && a.name; }).filter(Boolean);
    const tagNames = (p.tagIds || []).map(plTagName);
    const nav = function (dir) {
      return React.createElement('button', { type: 'button', className: 'pl-mnav ' + dir, 'aria-label': dir === 'prev' ? 'Previous' : 'Next', onClick: function () { go(dir === 'next' ? 1 : -1); } }, React.createElement(MI, { name: dir === 'prev' ? 'chevron_left' : 'chevron_right', size: 28 }));
    };
    const row = function (icon, text) { return React.createElement('div', { className: 'pl-vrow' }, React.createElement(MI, { name: icon, size: 16 }), React.createElement('span', null, text)); };
    const hasCoords = p.lat != null && p.lng != null;
    return React.createElement('div', { className: 'odc-scrim' + (contained ? ' pl-ods-scoped' : ''), onMouseDown: function (e) { if (e.target === e.currentTarget) props.onClose(); } },
      React.createElement('div', { className: 'odc-modal pl-ods-wide', style: { width: 'min(940px, 100%)' }, role: 'dialog', 'aria-modal': 'true', 'aria-label': titleOf(p) },
        React.createElement('div', { className: 'odc-modal-head' },
          React.createElement('div', { className: 'odc-modal-lead' }, React.createElement(MI, { name: 'photo_library', size: 21 })),
          React.createElement('div', { className: 'odc-modal-titles' },
            React.createElement('div', { className: 'odc-modal-title' }, titleOf(p)),
            React.createElement('div', { className: 'odc-modal-sub' }, plFmtDate(p.date), ' · ', React.createElement('span', { className: 'mono' }, (index + 1) + ' of ' + photos.length))
          ),
          (lib.toggleFav) ? React.createElement('button', { type: 'button', className: 'odc-iconbtn pl-headheart' + (p.fav ? ' on' : ''), 'aria-label': p.fav ? 'Unfavourite' : 'Favourite', onClick: function () { lib.toggleFav(p.id); } }, React.createElement('span', { className: 'material-icons', 'aria-hidden': 'true' }, p.fav ? 'favorite' : 'favorite_border')) : null,
          React.createElement('button', { type: 'button', className: 'odc-iconbtn', 'aria-label': 'Close', onClick: props.onClose }, React.createElement('span', { className: 'material-icons', 'aria-hidden': 'true' }, 'close'))
        ),
        React.createElement('div', { className: 'odc-modal-body pl-ods2' },
          React.createElement('div', { className: 'pl-ods2-stage' },
            React.createElement('div', { className: 'pl-ods-img', style: plPhotoBg(p.seed) }), nav('prev'), nav('next')
          ),
          React.createElement('aside', { className: 'pl-ods2-rail pl-vrail' },
            p.archived ? React.createElement('span', { className: 'pl-vbadge' }, 'Archived') : null,
            p.caption ? React.createElement('p', { className: 'pl-vcaption' }, p.caption) : null,
            React.createElement('div', { className: 'pl-vwhen' }, React.createElement(MI, { name: 'event', size: 18 }),
              React.createElement('div', { className: 'pl-vwhen-txt' }, React.createElement('span', { className: 'pl-vwhen-date' }, plFmtDate(p.date)), React.createElement('span', { className: 'pl-vwhen-time' }, plTime(p.date)))),
            albumNames.length ? row('photo_album', albumNames.join(', ')) : null,
            p.location ? row('place', p.location) : null,
            hasCoords ? row('my_location', React.createElement('span', { className: 'mono' }, p.lat.toFixed(4) + ', ' + p.lng.toFixed(4))) : null,
            hasCoords ? React.createElement('div', { className: 'pl-rail-map', 'aria-hidden': 'true' }, React.createElement('span', { className: 'mono' }, 'map')) : null,
            tagNames.length ? React.createElement('div', { className: 'pl-vgroup' }, React.createElement('span', { className: 'pl-vgroup-lab' }, 'Tags'), React.createElement('span', { className: 'pl-vchips' }, tagNames.map(function (t) { return Chip ? React.createElement(Chip, { key: t, tone: 'outline' }, t) : React.createElement('span', { key: t }, t); }))) : null,
            (p.personIds && p.personIds.length) ? React.createElement('div', { className: 'pl-vgroup' }, React.createElement('span', { className: 'pl-vgroup-lab' }, 'People'), React.createElement('span', { className: 'pl-vpeople' }, p.personIds.map(function (id) { const nm = plPersonName(id); return ContactChip ? React.createElement(ContactChip, { key: id, name: nm, type: 'Person', size: 'sm' }) : React.createElement('span', { key: id }, nm); }))) : null,
            React.createElement('div', { className: 'pl-vtech' },
              React.createElement('div', null, (p.pxW || p.w * 640) + '×' + (p.pxH || p.h * 640) + ' · JPEG'),
              React.createElement('div', { className: 'pl-vprov' }, 'Added by ' + (p.createdBy || '—')),
              (p.updatedBy && p.updatedBy !== p.createdBy) ? React.createElement('div', { className: 'pl-vprov' }, 'Edited by ' + p.updatedBy) : null
            )
          )
        ),
        React.createElement('div', { className: 'odc-modal-foot' },
          (Button && props.onEdit) ? React.createElement(Button, { variant: 'text', icon: 'edit', onClick: function () { props.onEdit(p); } }, 'Edit') : null,
          Button && React.createElement(Button, { variant: 'text', icon: 'download' }, 'Download'),
          (Button && lib.toggleArchive) ? React.createElement(Button, { variant: 'text', icon: p.archived ? 'unarchive' : 'inventory_2', onClick: function () { lib.toggleArchive(p.id); props.onClose(); } }, p.archived ? 'Unarchive' : 'Archive') : null,
          (Button && props.onDelete) ? React.createElement(Button, { variant: 'text', icon: 'delete', onClick: function () { props.onDelete(p); } }, 'Delete') : (Button ? React.createElement(Button, { variant: 'text', icon: 'delete' }, 'Delete') : null)
        )
      )
    );
  };

  /* ---- Edit metadata dialog ------------------------------------------------ */
  const EditDialog = function (props) {
    const p = props.photo, lib = props.lib || {};
    const contained = props.contained !== false;
    const Button = window.Button, Field = window.Field, DateField = window.DateField, TimeField = window.TimeField, CoordinateField = window.CoordinateField, NumberField = window.NumberField, FormRow = window.FormRow, TagMultiSelect = window.TagMultiSelect, MultiSelect = window.MultiSelect;
    const d = new Date(p.date);
    const [form, setForm] = useState({
      title: p.title || '', name: p.name, date: p.date.slice(0, 10),
      time: String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0'),
      albums: (p.albums || []).slice(), tags: (p.tagIds || []).slice(), people: (p.personIds || []).slice(),
      caption: p.caption || '', location: p.location || '',
      lat: p.lat != null ? String(p.lat) : '', lng: p.lng != null ? String(p.lng) : '',
    });
    // Tags may be created; PEOPLE are chosen from EXISTING Person contacts only (no create).
    const [tagOpts, setTagOpts] = useState(function () { return PL_TAG_OPTIONS.slice(); });
    const peopleOpts = PL_PERSON_OPTIONS;
    const set = function (patch) { setForm(function (f) { return Object.assign({}, f, patch); }); };
    useEffect(function () {
      const onKey = function (e) { if (e.key === 'Escape') props.onClose(); };
      window.addEventListener('keydown', onKey); return function () { window.removeEventListener('keydown', onKey); };
    }, []);
    const albumOpts = (lib.albumList || []).map(function (a) { return { value: a.id, label: a.name }; });
    const save = function () {
      // Album membership diff.
      const cur = new Set(p.albums || []); const next = new Set(form.albums);
      const toAdd = form.albums.filter(function (id) { return !cur.has(id); });
      const toRemove = (p.albums || []).filter(function (id) { return !next.has(id); });
      if (toAdd.length && lib.addToAlbums) lib.addToAlbums([p.id], toAdd);
      if (toRemove.length && lib.removeFromAlbums) lib.removeFromAlbums([p.id], toRemove);
      // Metadata patch.
      const latN = parseFloat(form.lat), lngN = parseFloat(form.lng);
      const date = form.date + 'T' + (form.time || '00:00') + ':00';
      if (lib.updatePhoto) lib.updatePhoto(p.id, {
        title: form.title.trim() || null, name: form.name.trim() || p.name,
        caption: form.caption.trim() || null, location: form.location.trim() || null,
        lat: isNaN(latN) ? null : latN, lng: isNaN(lngN) ? null : lngN,
        tagIds: form.tags.slice(), personIds: form.people.slice(), date: date,
      });
      props.onClose();
    };
    return React.createElement('div', { className: 'odc-scrim' + (contained ? ' pl-ods-scoped' : ''), onMouseDown: function (e) { if (e.target === e.currentTarget) props.onClose(); } },
      React.createElement('div', { className: 'odc-modal', style: { width: 'min(620px, 100%)' }, role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Edit ' + titleOf(p) },
        React.createElement('div', { className: 'odc-modal-head' },
          React.createElement('div', { className: 'odc-modal-lead' }, React.createElement(MI, { name: 'edit', size: 20 })),
          React.createElement('div', { className: 'odc-modal-titles' }, React.createElement('div', { className: 'odc-modal-title' }, 'Edit details'), React.createElement('div', { className: 'odc-modal-sub mono' }, p.name)),
          React.createElement('button', { type: 'button', className: 'odc-iconbtn', 'aria-label': 'Close', onClick: props.onClose }, React.createElement('span', { className: 'material-icons', 'aria-hidden': 'true' }, 'close'))
        ),
        React.createElement('div', { className: 'odc-modal-body pl-editbody' },
          React.createElement('div', { className: 'pl-edit-banner' }, React.createElement('div', { className: 'pl-edit-bannerimg', style: Object.assign({}, plPhotoBg(p.seed), { aspectRatio: p.w + ' / ' + p.h }) })),
          Field && React.createElement(Field, { label: 'Title', value: form.title, onChange: function (v) { set({ title: v }); }, icon: 'title', maxLength: 200, placeholder: 'A short title (optional)', help: 'Falls back to the file name when empty.' }),
          Field && React.createElement(Field, { label: 'File name', value: form.name, onChange: function (v) { set({ name: v }); }, icon: 'image' }),
          FormRow ? React.createElement(FormRow, { cols: 2 },
            DateField && React.createElement(DateField, { label: 'Date taken', value: form.date, onChange: function (v) { set({ date: v }); } }),
            TimeField && React.createElement(TimeField, { label: 'Time', value: form.time, onChange: function (v) { set({ time: v || '' }); }, step: 15 })
          ) : null,
          MultiSelect && React.createElement('div', { className: 'odc-field' },
            React.createElement('label', { className: 'odc-field-label' }, 'Albums'),
            React.createElement(MultiSelect, { allLabel: 'No album', value: form.albums, onChange: function (v) { set({ albums: v }); }, options: albumOpts })
          ),
          TagMultiSelect && React.createElement(TagMultiSelect, { label: 'Tags', value: form.tags, onChange: function (v) { set({ tags: v }); }, options: tagOpts, addLabel: 'Add tag', placeholder: 'No tags', onCreate: function (name) { const id = 'pt-new-' + name.toLowerCase().replace(/\s+/g, '-'); setTagOpts(function (o) { return o.some(function (x) { return x.label.toLowerCase() === name.toLowerCase(); }) ? o : o.concat([{ value: id, label: name }]); }); return { value: id, label: name }; } }),
          // People: existing Person contacts only — NO onCreate (spec §9).
          TagMultiSelect && React.createElement(TagMultiSelect, { label: 'People', value: form.people, onChange: function (v) { set({ people: v }); }, options: peopleOpts, addLabel: 'Tag a person', placeholder: 'No one tagged', emptyText: 'No matching person. People come from your Person contacts.' }),
          Field && React.createElement(Field, { label: 'Caption', value: form.caption, onChange: function (v) { set({ caption: v }); }, multiline: true, rows: 2, maxLength: 2000, placeholder: 'Write a caption…' }),
          Field && React.createElement(Field, { label: 'Location', value: form.location, onChange: function (v) { set({ location: v }); }, icon: 'place', maxLength: 256, placeholder: 'Add a place — e.g. Lisbon, Portugal' }),
          CoordinateField && React.createElement(CoordinateField, { value: { lat: form.lat, lng: form.lng }, onChange: function (c) { set({ lat: c.lat == null ? '' : String(c.lat), lng: c.lng == null ? '' : String(c.lng) }); }, optional: true }),
          (form.lat || form.lng || form.location) ? React.createElement('div', { className: 'pl-rail-map', 'aria-hidden': 'true', style: { height: 84 } }, React.createElement('span', { className: 'mono' }, 'map preview')) : null
        ),
        React.createElement('div', { className: 'odc-modal-foot' },
          React.createElement(Button, { variant: 'text', onClick: props.onClose }, 'Cancel'),
          React.createElement(Button, { variant: 'filled', color: 'primary', icon: 'check', onClick: save }, 'Save changes')
        )
      )
    );
  };

  /* ---- Album form dialog (new / edit) -------------------------------------- */
  const AlbumFormDialog = function (props) {
    const lib = props.lib, editId = props.editId; // editId null => new
    const Button = window.Button, Field = window.Field;
    const existing = editId ? lib.albumList.find(function (a) { return a.id === editId; }) : null;
    const [name, setName] = useState(existing ? existing.name : '');
    const [desc, setDesc] = useState(existing ? (existing.description || '') : '');
    const [added, setAdded] = useState(0);          // uploaded this session
    const [newIds, setNewIds] = useState([]);        // uploads to attach on save (new-album case)
    const members = editId ? lib.albumPhotos(editId) : [];
    const coverId = editId ? lib.coverId(existing || {}) : null;
    const existingCount = editId ? members.length : 0;
    useEffect(function () { const onKey = function (e) { if (e.key === 'Escape') props.onClose(); }; window.addEventListener('keydown', onKey); return function () { window.removeEventListener('keydown', onKey); }; }, []);

    const receive = function (n) {
      const ids = lib.upload(n);
      setAdded(function (a) { return a + ids.length; });
      if (editId) lib.addToAlbums(ids, [editId]);     // edit: attach immediately
      else setNewIds(function (prev) { return prev.concat(ids); }); // new: attach on create
    };
    const save = function () {
      if (editId) { lib.updateAlbum(editId, { name: name.trim() || existing.name, description: desc }); }
      else { lib.createAlbum(name.trim() || 'Untitled album', newIds, desc); }
      props.onClose();
    };

    // Keyboard-operable ordering + cover control (edit only).
    const memberList = editId ? React.createElement('div', { className: 'odc-field' },
      React.createElement('label', { className: 'odc-field-label' }, 'Photos in this album — order & cover'),
      members.length ? React.createElement('ul', { className: 'pl-memlist' }, members.map(function (m, i) {
        const isCover = coverId ? coverId === m.id : i === 0;
        return React.createElement('li', { className: 'pl-memrow', key: m.id },
          React.createElement('span', { className: 'pl-memthumb', style: plPhotoBg(m.seed) }),
          React.createElement('span', { className: 'pl-memname' }, titleOf(m)),
          React.createElement('span', { className: 'pl-mempos mono', 'aria-hidden': 'true' }, '#' + (i + 1)),
          React.createElement('button', { type: 'button', className: 'pl-memcover' + (isCover ? ' on' : ''), 'aria-label': isCover ? titleOf(m) + ' is the cover' : 'Set ' + titleOf(m) + ' as cover', 'aria-pressed': isCover, onClick: function () { lib.setCover(editId, m.id); } }, React.createElement(MI, { name: isCover ? 'star' : 'star_border', size: 17 })),
          React.createElement('button', { type: 'button', className: 'pl-membtn', 'aria-label': 'Move ' + titleOf(m) + ' up', disabled: i === 0, onClick: function () { lib.moveInAlbum(editId, m.id, -1); } }, React.createElement(MI, { name: 'arrow_upward', size: 16 })),
          React.createElement('button', { type: 'button', className: 'pl-membtn', 'aria-label': 'Move ' + titleOf(m) + ' down', disabled: i === members.length - 1, onClick: function () { lib.moveInAlbum(editId, m.id, 1); } }, React.createElement(MI, { name: 'arrow_downward', size: 16 })),
          React.createElement('button', { type: 'button', className: 'pl-membtn', 'aria-label': 'Remove ' + titleOf(m) + ' from album', onClick: function () { lib.removeFromAlbums([m.id], [editId]); } }, React.createElement(MI, { name: 'close', size: 16 }))
        );
      })) : React.createElement('div', { className: 'pl-drop-hint' }, 'No photos yet — upload below or add from the library.')
    ) : null;

    return React.createElement('div', { className: 'odc-scrim pl-ods-scoped', onMouseDown: function (e) { if (e.target === e.currentTarget) props.onClose(); } },
      React.createElement('div', { className: 'odc-modal', style: { width: 'min(600px, 100%)' }, role: 'dialog', 'aria-modal': 'true', 'aria-label': editId ? 'Edit album' : 'New album' },
        React.createElement('div', { className: 'odc-modal-head' },
          React.createElement('div', { className: 'odc-modal-lead' }, React.createElement(MI, { name: editId ? 'edit' : 'add_photo_alternate', size: 20 })),
          React.createElement('div', { className: 'odc-modal-titles' }, React.createElement('div', { className: 'odc-modal-title' }, editId ? 'Edit album' : 'New album'), editId ? React.createElement('div', { className: 'odc-modal-sub' }, existing.name) : null),
          React.createElement('button', { type: 'button', className: 'odc-iconbtn', 'aria-label': 'Close', onClick: props.onClose }, React.createElement('span', { className: 'material-icons', 'aria-hidden': 'true' }, 'close'))
        ),
        React.createElement('div', { className: 'odc-modal-body pl-albform' },
          Field && React.createElement(Field, { label: 'Album name', value: name, onChange: setName, placeholder: 'e.g. Summer 2026', icon: 'photo_album', autoFocus: !editId }),
          Field && React.createElement(Field, { label: 'Description', value: desc, onChange: setDesc, multiline: true, rows: 2, maxLength: 1024, placeholder: 'What\u2019s this album about? (optional)' }),
          memberList,
          React.createElement('div', { className: 'odc-field' },
            React.createElement('label', { className: 'odc-field-label' }, 'Upload new photos to this album'),
            React.createElement(UploadDrop, { added: added, onReceive: receive, style: { minHeight: 160 }, hint: (added || existingCount) ? ((added + existingCount) + ' photo' + ((added + existingCount) === 1 ? '' : 's') + ' in this album') : null })
          )
        ),
        React.createElement('div', { className: 'odc-modal-foot' },
          editId ? React.createElement('button', { type: 'button', className: 'pl-danger-link', onClick: function () { lib.deleteAlbum(editId); props.onClose(); } }, React.createElement(MI, { name: 'delete', size: 16 }), 'Delete album') : null,
          editId ? React.createElement('span', { style: { flex: 1 } }) : null,
          React.createElement(Button, { variant: 'text', onClick: props.onClose }, 'Cancel'),
          React.createElement(Button, { variant: 'filled', color: 'primary', icon: 'check', onClick: save }, editId ? 'Save' : 'Create')
        )
      )
    );
  };

  /* ---- Add-to-album dialog (from selection) -------------------------------- */
  const AddToAlbumDialog = function (props) {
    const lib = props.lib, ids = props.photoIds;
    const Button = window.Button, Field = window.Field;
    const [checked, setChecked] = useState(function () { return new Set(); });
    const [creating, setCreating] = useState(false);
    const [newName, setNewName] = useState('');
    const toggle = function (id) { setChecked(function (prev) { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; }); };
    const addNew = function () { const nm = newName.trim(); if (!nm) return; const id = lib.createAlbum(nm, []); setChecked(function (c) { return new Set(Array.from(c).concat([id])); }); setNewName(''); setCreating(false); };
    const done = function () { lib.addToAlbums(ids, Array.from(checked)); props.onClose(); };
    return React.createElement('div', { className: 'odc-scrim pl-ods-scoped', onMouseDown: function (e) { if (e.target === e.currentTarget) props.onClose(); } },
      React.createElement('div', { className: 'odc-modal', style: { width: 'min(460px, 100%)' }, role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Add to album' },
        React.createElement('div', { className: 'odc-modal-head' },
          React.createElement('div', { className: 'odc-modal-lead' }, React.createElement(MI, { name: 'photo_album', size: 20 })),
          React.createElement('div', { className: 'odc-modal-titles' }, React.createElement('div', { className: 'odc-modal-title' }, 'Add to album'), React.createElement('div', { className: 'odc-modal-sub' }, ids.length + ' photo' + (ids.length === 1 ? '' : 's'))),
          React.createElement('button', { type: 'button', className: 'odc-iconbtn', 'aria-label': 'Close', onClick: props.onClose }, React.createElement('span', { className: 'material-icons', 'aria-hidden': 'true' }, 'close'))
        ),
        React.createElement('div', { className: 'odc-modal-body' },
          React.createElement('div', { className: 'pl-alblist' },
            lib.albumList.map(function (a) {
              const on = checked.has(a.id);
              return React.createElement('button', { type: 'button', key: a.id, className: 'pl-albrow' + (on ? ' on' : ''), onClick: function () { toggle(a.id); } },
                React.createElement('span', { className: 'pl-albrow-cover', style: plPhotoBg(lib.coverSeed(a)) }),
                React.createElement('span', { className: 'pl-albrow-name' }, a.name),
                React.createElement('span', { className: 'pl-albrow-check' + (on ? ' on' : '') }, on ? React.createElement(MI, { name: 'check', size: 15 }) : null)
              );
            })
          ),
          creating ? React.createElement('div', { className: 'pl-albcreate' },
            Field && React.createElement(Field, { label: 'New album name', value: newName, onChange: setNewName, placeholder: 'Album name', autoFocus: true }),
            React.createElement('div', { className: 'pl-albcreate-actions' }, React.createElement(Button, { variant: 'text', onClick: function () { setCreating(false); setNewName(''); } }, 'Cancel'), React.createElement(Button, { variant: 'filled', color: 'primary', icon: 'add', onClick: addNew }, 'Create'))
          ) : React.createElement('button', { type: 'button', className: 'pl-albadd', onClick: function () { setCreating(true); } }, React.createElement(MI, { name: 'add', size: 18 }), 'New album')
        ),
        React.createElement('div', { className: 'odc-modal-foot' },
          React.createElement(Button, { variant: 'text', onClick: props.onClose }, 'Cancel'),
          React.createElement(Button, { variant: 'filled', color: 'primary', icon: 'check', onClick: done }, 'Done')
        )
      )
    );
  };

  /* ---- Selection bar ------------------------------------------------------- */
  const SelectionBar = function (props) {
    const act = function (icon, label, onClick) { return React.createElement('button', { type: 'button', className: 'pl-selact', key: label, onClick: onClick }, React.createElement(MI, { name: icon, size: 17 }), React.createElement('span', null, label)); };
    return React.createElement('div', { className: 'pl-selbar' },
      React.createElement('div', { className: 'pl-selbar-l' },
        React.createElement('button', { type: 'button', className: 'pl-selclose', 'aria-label': 'Exit selection', onClick: props.onDone }, React.createElement(MI, { name: 'close', size: 18 })),
        React.createElement('span', { className: 'pl-selcount' }, React.createElement('b', null, props.n), ' selected'),
        React.createElement('button', { type: 'button', className: 'pl-sellink', onClick: props.n >= props.total ? props.onClear : props.onAll }, props.n >= props.total ? 'Clear' : ('Select all ' + props.total))
      ),
      React.createElement('div', { className: 'pl-selbar-r' },
        act('download', 'Download'),
        !props.archivedView && act('photo_album', 'Add to album', props.onAddToAlbum),
        !props.archivedView && act('favorite', 'Favourite', props.onFavourite),
        act(props.archivedView ? 'unarchive' : 'inventory_2', props.archivedView ? 'Unarchive' : 'Archive', props.onArchive),
        act('delete', 'Delete')
      )
    );
  };

  /* ---- Header -------------------------------------------------------------- */
  const Header = function (props) {
    const lib = props.lib, state = lib.state, set = lib.set;
    const PageHeader = window.PageHeader, SearchField = window.SearchField, MultiSelect = window.MultiSelect, InfoTile = window.InfoTile, BreakdownTile = window.BreakdownTile, SortSelect = window.SortSelect, DateRangePicker = window.DateRangePicker, PageSizeSelect = window.PageSizeSelect;
    const albumOpts = lib.albumList.map(function (a) { return { value: a.id, label: a.name }; });
    const tagOpts = PL_TAG_OPTIONS;
    const peopleOpts = PL_PERSON_OPTIONS;
    const albumRows = lib.albumList.map(function (a) { return { key: a.id, icon: 'photo_album', iconColor: 'var(--tide-400)', label: a.name, count: lib.albumPhotos(a.id).length }; });
    const active = lib.decorate.filter(function (p) { return !p.archived; });
    const tagRows = PL_TAG_OPTIONS.map(function (t) { return { key: t.value, icon: 'sell', iconColor: 'var(--mud-palette-text-secondary)', label: t.label, count: active.filter(function (p) { return (p.tagIds || []).indexOf(t.value) !== -1; }).length }; }).filter(function (r) { return r.count; });
    const peopleRows = PL_PERSON_OPTIONS.map(function (n) { return { key: n.value, icon: 'person', iconColor: 'var(--mud-palette-text-secondary)', label: n.label, count: active.filter(function (p) { return (p.personIds || []).indexOf(n.value) !== -1; }).length }; }).filter(function (r) { return r.count; });
    const total = lib.decorate.filter(function (p) { return !p.archived; }).length;
    const favCount = lib.decorate.filter(function (p) { return p.fav && !p.archived; }).length;
    const sortFields = [{ key: 'date', label: 'Date taken' }, { key: 'title', label: 'Title' }, { key: 'added', label: 'Recently added' }];

    return React.createElement(PageHeader, {
      title: props.view === 'albums' ? 'Albums' : 'Photos', icon: props.view === 'albums' ? 'photo_album' : 'photo_library', sub: total + ' photos · ' + lib.albumList.length + ' albums',
      overview: React.createElement('div', { className: 'je-overview' },
        React.createElement('div', { className: 'je-stat-tiles' },
          React.createElement(InfoTile, { icon: 'photo_library', iconColor: 'var(--tide-400)', label: 'Photos', value: String(total), foot: 'in your library' }),
          React.createElement(InfoTile, { icon: 'favorite', iconColor: 'var(--rose-500, #F2557A)', label: 'Favourites', value: String(favCount), foot: 'loved' })
        ),
        React.createElement(BreakdownTile, { label: 'By album', empty: 'No albums.', rows: albumRows }),
        React.createElement(BreakdownTile, { label: 'By tag', empty: 'No tags.', rows: tagRows }),
        React.createElement(BreakdownTile, { label: 'People', empty: 'No people tagged.', rows: peopleRows })
      ),
      searchDefaultOpen: true,
      search: React.createElement('div', { className: 'row gap-3', style: { flexWrap: 'wrap', alignItems: 'center' } },
        React.createElement('div', { style: { minWidth: 220, flex: 1 } }, React.createElement(SearchField, { placeholder: props.view === 'albums' ? 'Search albums…' : 'Search title, caption, location…', value: state.q, onChange: function (v) { set({ q: v }); } })),
        props.view === 'library' ? React.createElement('div', { style: { minWidth: 140 } }, React.createElement(MultiSelect, { allLabel: 'Any album', value: state.albums, onChange: function (v) { set({ albums: v }); }, options: albumOpts })) : null,
        props.view === 'library' ? React.createElement('div', { style: { minWidth: 120 } }, React.createElement(MultiSelect, { allLabel: 'Any tag', value: state.tags, onChange: function (v) { set({ tags: v }); }, options: tagOpts })) : null,
        props.view === 'library' ? React.createElement('div', { style: { minWidth: 130 } }, React.createElement(MultiSelect, { allLabel: 'Anyone', value: state.people, onChange: function (v) { set({ people: v }); }, options: peopleOpts })) : null,
        props.view === 'library' && DateRangePicker ? React.createElement(DateRangePicker, {
          label: 'Taken', icon: 'event', ariaLabel: 'Filter by date taken',
          value: { from: state.from || null, to: state.to || null },
          onChange: function (r) { set({ from: r.from || '', to: r.to || '' }); },
        }) : null,
        props.view === 'library' && SortSelect ? React.createElement(SortSelect, { sort: state.sort, onSort: function (s) { set({ sort: s }); }, fields: sortFields }) : null,
        props.view === 'library' && PageSizeSelect ? React.createElement(PageSizeSelect, { prefix: 'Show', suffix: 'per page', label: 'Photos per page', value: props.pageSize, onChange: props.onPageSize, options: PAGE_SIZES }) : null,
        props.view === 'library' ? React.createElement('button', { type: 'button', className: 'pl-toolbtn' + (state.favOnly ? ' on' : ''), onClick: function () { set({ favOnly: !state.favOnly }); } }, React.createElement(MI, { name: state.favOnly ? 'favorite' : 'favorite_border', size: 16 }), ' Favourites') : null,
        props.view === 'library' ? React.createElement('button', { type: 'button', className: 'pl-toolbtn' + (state.archivedView ? ' on' : ''), onClick: function () { set({ archivedView: !state.archivedView }); } }, React.createElement(MI, { name: 'inventory_2', size: 16 }), ' ', state.archivedView ? 'Archived' : 'Archive') : null,
        props.view === 'library' ? React.createElement('button', { type: 'button', className: 'pl-toolbtn' + (props.selecting ? ' on' : ''), onClick: props.onToggleSelecting }, React.createElement(MI, { name: 'check_box', size: 16 }), ' Select') : null
      ),
      primary: props.view === 'albums'
        ? { label: 'New album', icon: 'add', onClick: props.onNew }
        : { label: 'Upload', icon: 'upload', onClick: props.onUpload },
    });
  };

  /* ---- Shared upload drop zone (used by Upload dialog + album dialog) ------ */
  const UploadDrop = function (props) {
    const [drag, setDrag] = useState(false);
    return React.createElement('div', {
      className: 'pl-drop' + (drag ? ' over' : ''), style: props.style || { minHeight: 220 },
      onDragOver: function (e) { e.preventDefault(); setDrag(true); }, onDragLeave: function () { setDrag(false); },
      onDrop: function (e) { e.preventDefault(); setDrag(false); props.onReceive(4); },
    },
      React.createElement('div', { className: 'pl-drop-ic' }, React.createElement(MI, { name: 'add_photo_alternate', size: 40 })),
      React.createElement('div', { className: 'pl-drop-title' }, props.added ? 'Drop more to keep adding' : 'Drag photos here to upload'),
      React.createElement('div', { className: 'pl-drop-sub' }, props.sub || ('or click to browse — JPEG, PNG, GIF, or WebP, up to ' + plEffectiveCap() + '\u00A0MB each.')),
      React.createElement('button', { type: 'button', className: 'pl-drop-btn', onClick: function () { props.onReceive(3); } }, React.createElement(MI, { name: 'upload', size: 16 }), ' Choose files'),
      props.hint ? React.createElement('div', { className: 'pl-drop-hint' }, props.hint) : null
    );
  };

  /* ---- Upload dialog: upload new photos ------------------------------------ */
  const UploadDialog = function (props) {
    const Button = window.Button;
    const lib = props.lib;
    const [added, setAdded] = useState(0);
    useEffect(function () { const onKey = function (e) { if (e.key === 'Escape') props.onClose(); }; window.addEventListener('keydown', onKey); return function () { window.removeEventListener('keydown', onKey); }; }, []);
    const receive = function (n) { const ids = lib.upload(n); setAdded(function (a) { return a + ids.length; }); };
    return React.createElement('div', { className: 'odc-scrim pl-ods-scoped', onMouseDown: function (e) { if (e.target === e.currentTarget) props.onClose(); } },
      React.createElement('div', { className: 'odc-modal', style: { width: 'min(560px, 100%)' }, role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Upload photos' },
        React.createElement('div', { className: 'odc-modal-head' },
          React.createElement('div', { className: 'odc-modal-lead' }, React.createElement(MI, { name: 'add_photo_alternate', size: 20 })),
          React.createElement('div', { className: 'odc-modal-titles' }, React.createElement('div', { className: 'odc-modal-title' }, 'Upload photos'), React.createElement('div', { className: 'odc-modal-sub' }, added ? (added + ' added to your library') : 'Add new photos to your library')),
          React.createElement('button', { type: 'button', className: 'odc-iconbtn', 'aria-label': 'Close', onClick: props.onClose }, React.createElement('span', { className: 'material-icons', 'aria-hidden': 'true' }, 'close'))
        ),
        React.createElement('div', { className: 'odc-modal-body' },
          React.createElement(UploadDrop, { added: added, onReceive: receive, style: { minHeight: 260 }, hint: 'Photos you attach to journal entries show up here too.' })
        ),
        React.createElement('div', { className: 'odc-modal-foot' },
          React.createElement(Button, { variant: 'text', onClick: props.onClose }, added ? 'Done' : 'Cancel')
        )
      )
    );
  };

  /* ---- The page ------------------------------------------------------------ */
  function Photos(props) {
    const lib = useLibrary();
    const selc = useSelection();
    const view = props.mode === 'albums' ? 'albums' : 'library';
    const [uploadOpen, setUploadOpen] = useState(false);
    const [openIdx, setOpenIdx] = useState(null);     // detail modal index into rows
    const [editPhoto, setEditPhoto] = useState(null);
    const [albumDialog, setAlbumDialog] = useState(null); // {editId} | 'new'
    const [addToAlbum, setAddToAlbum] = useState(null);   // photoIds[]
    const [openAlbum, setOpenAlbum] = useState(null);     // album id (drill-in)
    const [pageSize, setPageSize] = useState(24);
    const [visible, setVisible] = useState(24);

    const rows = lib.rows;
    const gridRows = openAlbum ? lib.albumPhotos(openAlbum) : rows;
    const shown = gridRows.slice(0, visible);
    // Reset paging when the filtered set changes materially.
    useEffect(function () { setVisible(pageSize); }, [pageSize, lib.state.q, lib.state.albums, lib.state.tags, lib.state.people, lib.state.favOnly, lib.state.archivedView, lib.state.from, lib.state.to, lib.state.sort]);

    // Empty library (nothing uploaded at all) → upload-first empty state.
    const libraryEmpty = lib.decorate.filter(function (p) { return !p.archived; }).length === 0;

    return React.createElement('div', { className: 'pl-page' },
      React.createElement(Header, { lib: lib, view: view, selecting: selc.selecting, pageSize: pageSize, onPageSize: setPageSize, onToggleSelecting: selc.start, onUpload: function () { setUploadOpen(true); }, onNew: function () { setAlbumDialog('new'); } }),

      (view === 'library' && selc.selecting) ? React.createElement(SelectionBar, {
        n: selc.sel.size, total: rows.length, archivedView: lib.state.archivedView,
        onAll: function () { selc.setAll(rows.map(function (p) { return p.id; })); }, onClear: selc.clear, onDone: selc.done,
        onAddToAlbum: function () { setAddToAlbum(Array.from(selc.sel)); }, onFavourite: function () { selc.sel.forEach(function (id) { lib.toggleFav(id); }); },
        onArchive: function () { lib.archiveMany(Array.from(selc.sel), !lib.state.archivedView); selc.done(); },
      }) : null,

      // ---- LIBRARY (grid) ----
      view === 'library' ? (
        libraryEmpty ? React.createElement(EmptyDrop, { onUpload: function () { setUploadOpen(true); } }) :
        React.createElement(React.Fragment, null,
          React.createElement('div', { className: 'pl-grid' },
            shown.map(function (p) {
              return React.createElement(PhotoTile, {
                key: p.id, photo: p, onOpen: function (ph) { setOpenIdx(rows.findIndex(function (x) { return x.id === ph.id; })); },
                selectable: selc.selecting, selected: selc.sel.has(p.id), onToggleSelect: selc.toggle, onStartSelect: selc.startWith, onToggleFav: lib.toggleFav,
              });
            })
          ),
          gridRows.length > visible ? React.createElement('div', { className: 'pl-loadmore' },
            React.createElement('span', { className: 'pl-loadmore-txt mono' }, 'Showing ' + shown.length + ' of ' + gridRows.length),
            React.createElement('button', { type: 'button', className: 'pl-drop-btn', onClick: function () { setVisible(function (v) { return v + pageSize; }); } }, 'Load ' + Math.min(pageSize, gridRows.length - visible) + ' more')
          ) : null
        )
      ) : null,

      // ---- ALBUMS ----
      view === 'albums' ? (openAlbum ? React.createElement(AlbumDetail, {
        lib: lib, albumId: openAlbum, onBack: function () { setOpenAlbum(null); }, onEdit: function () { setAlbumDialog({ editId: openAlbum }); },
        onAddToAlbum: function (ids) { setAddToAlbum(ids); },
        onOpenPhoto: function (list, i) { setOpenIdx({ list: list, i: i }); },
      }) : React.createElement(AlbumsGrid, { lib: lib, onOpen: setOpenAlbum, onNew: function () { setAlbumDialog('new'); }, onEdit: function (id) { setAlbumDialog({ editId: id }); } })) : null,

      // ---- Detail modal ----
      (openIdx != null && typeof openIdx === 'number') ? React.createElement(DetailModal, {
        photos: rows, index: openIdx, lib: lib, onIndex: setOpenIdx, onClose: function () { setOpenIdx(null); }, onEdit: function (p) { setOpenIdx(null); setEditPhoto(p); },
      }) : null,
      (openIdx != null && typeof openIdx === 'object') ? React.createElement(DetailModal, {
        photos: openIdx.list, index: openIdx.i, lib: lib, onIndex: function (i) { setOpenIdx({ list: openIdx.list, i: i }); }, onClose: function () { setOpenIdx(null); }, onEdit: function (p) { setOpenIdx(null); setEditPhoto(p); },
      }) : null,

      editPhoto ? React.createElement(EditDialog, { photo: lib.byId[editPhoto.id] || editPhoto, lib: lib, onClose: function () { setEditPhoto(null); } }) : null,
      albumDialog ? React.createElement(AlbumFormDialog, { lib: lib, editId: albumDialog === 'new' ? null : albumDialog.editId, onClose: function () { setAlbumDialog(null); } }) : null,
      addToAlbum ? React.createElement(AddToAlbumDialog, { lib: lib, photoIds: addToAlbum, onClose: function () { setAddToAlbum(null); selc.done(); } }) : null,
      uploadOpen ? React.createElement(UploadDialog, { lib: lib, onClose: function () { setUploadOpen(false); } }) : null
    );
  }

  /* ---- Albums grid + detail ------------------------------------------------ */
  const AlbumsGrid = function (props) {
    const lib = props.lib;
    return React.createElement(React.Fragment, null,
      React.createElement('div', { className: 'pl-albgrid' },
        lib.albumList.map(function (a) {
          const list = lib.albumPhotos(a.id);
          const coverPid = lib.coverId(a);
          const ordered = coverPid ? [lib.byId[coverPid]].filter(Boolean).concat(list.filter(function (x) { return x.id !== coverPid; })) : list;
          const covers4 = ordered.slice(0, 4);
          return React.createElement('div', { className: 'pl-albcard pl-albcard-manage', key: a.id, onClick: function () { props.onOpen(a.id); }, role: 'button', tabIndex: 0, style: { cursor: 'pointer' } },
            React.createElement('div', { className: 'pl-albcover n' + (covers4.length || 1) },
              covers4.length ? covers4.map(function (c) { return React.createElement('div', { key: c.id, className: 'pl-albcover-cell', style: plPhotoBg(c.seed) }); }) : React.createElement('div', { className: 'pl-albcover-cell', style: plPhotoBg(lib.coverSeed(a)) })
            ),
            React.createElement('div', { className: 'pl-albmeta' }, React.createElement('span', { className: 'pl-albname' }, a.name), React.createElement('span', { className: 'pl-albcount mono' }, list.length + ' photo' + (list.length === 1 ? '' : 's'))),
            React.createElement('button', { type: 'button', className: 'pl-albedit', 'aria-label': 'Edit ' + a.name, onClick: function (e) { e.stopPropagation(); props.onEdit(a.id); } }, React.createElement(MI, { name: 'edit', size: 16 }))
          );
        }),
        React.createElement('button', { type: 'button', className: 'pl-albnew', onClick: props.onNew }, React.createElement(MI, { name: 'add', size: 26 }), React.createElement('span', null, 'New album'))
      )
    );
  };

  const AlbumDetail = function (props) {
    const lib = props.lib, a = lib.albumList.find(function (x) { return x.id === props.albumId; });
    const list = lib.albumPhotos(props.albumId);
    const selc = useSelection();
    const Button = window.Button;
    useEffect(function () { selc.done(); }, [props.albumId]);
    const ids = Array.from(selc.sel);
    return React.createElement(React.Fragment, null,
      React.createElement('div', { className: 'pl-backrow' },
        React.createElement('button', { type: 'button', className: 'pl-backrow-back', onClick: props.onBack },
          React.createElement(MI, { name: 'arrow_back', size: 18 }), ' All albums'),
        React.createElement('span', { className: 'pl-backrow-title' }, a ? a.name : ''),
        React.createElement('span', { className: 'pl-daycount mono' }, list.length),
        React.createElement('span', { style: { flex: 1 } }),
        list.length ? React.createElement('button', { type: 'button', className: 'pl-toolbtn' + (selc.selecting ? ' on' : ''), onClick: selc.start }, React.createElement(MI, { name: 'check_box', size: 16 }), ' Select') : null,
        Button && React.createElement(Button, { variant: 'text', icon: 'edit', onClick: props.onEdit }, 'Edit album')
      ),
      selc.selecting ? React.createElement('div', { className: 'pl-selbar' },
        React.createElement('div', { className: 'pl-selbar-l' },
          React.createElement('button', { type: 'button', className: 'pl-selclose', 'aria-label': 'Exit selection', onClick: selc.done }, React.createElement(MI, { name: 'close', size: 18 })),
          React.createElement('span', { className: 'pl-selcount' }, React.createElement('b', null, selc.sel.size), ' selected'),
          React.createElement('button', { type: 'button', className: 'pl-sellink', onClick: selc.sel.size >= list.length ? selc.clear : function () { selc.setAll(list.map(function (p) { return p.id; })); } }, selc.sel.size >= list.length ? 'Clear' : ('Select all ' + list.length))
        ),
        React.createElement('div', { className: 'pl-selbar-r' },
          React.createElement('button', { type: 'button', className: 'pl-selact', onClick: function () { props.onAddToAlbum(ids); } }, React.createElement(MI, { name: 'photo_album', size: 17 }), React.createElement('span', null, 'Add to album')),
          React.createElement('button', { type: 'button', className: 'pl-selact', disabled: !selc.sel.size, onClick: function () { if (!selc.sel.size) return; lib.removeFromAlbums(ids, [props.albumId]); selc.done(); } }, React.createElement(MI, { name: 'remove_circle_outline', size: 17 }), React.createElement('span', null, 'Remove from album'))
        )
      ) : null,
      React.createElement('div', { className: 'pl-grid' },
        list.map(function (p, i) {
          return React.createElement(PhotoTile, { key: p.id, photo: p, onOpen: function () { props.onOpenPhoto(list, i); }, selectable: selc.selecting, selected: selc.sel.has(p.id), onToggleSelect: selc.toggle, onStartSelect: selc.startWith, onToggleFav: lib.toggleFav });
        })
      )
    );
  };

  /* ---- Empty state --------------------------------------------------------- */
  const EmptyDrop = function (props) {
    const [drag, setDrag] = useState(false);
    return React.createElement('div', {
      className: 'pl-drop' + (drag ? ' over' : ''),
      onDragOver: function (e) { e.preventDefault(); setDrag(true); }, onDragLeave: function () { setDrag(false); },
      onDrop: function (e) { e.preventDefault(); setDrag(false); props.onUpload(); },
    },
      React.createElement('div', { className: 'pl-drop-ic' }, React.createElement(MI, { name: 'add_photo_alternate', size: 40 })),
      React.createElement('div', { className: 'pl-drop-title' }, 'Drag photos here to upload'),
      React.createElement('div', { className: 'pl-drop-sub' }, 'or click to browse — JPEG, PNG, GIF, or WebP, up to ' + plEffectiveCap() + '\u00A0MB each.'),
      React.createElement('button', { type: 'button', className: 'pl-drop-btn', onClick: props.onUpload }, React.createElement(MI, { name: 'upload', size: 16 }), ' Choose files'),
      React.createElement('div', { className: 'pl-drop-hint' }, 'Photos you attach to journal entries show up here too.')
    );
  };

  window.PhotoDetailModal = DetailModal;
  window.PhotoEditDialog = EditDialog;
  window.Photos = Photos;
})();
