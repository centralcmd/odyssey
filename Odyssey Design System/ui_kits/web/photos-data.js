/* Photos library — seed data + helpers (plain JS, registers on window).
   Loaded before Photos.jsx. Each photo is a first-class library record with a
   distinct Title + Caption, tag ids (→ OdysseyData.photoTags), people as
   existing Person-contact ids (→ OdysseyData.contacts, type Person),
   capture date/time, coordinates + location name, provenance, and archival —
   mirroring the Photo Library data model. No real image bytes exist in the kit,
   so each photo renders as a deterministic gradient scene (plPhotoBg); a real
   deployment sets `src` from GET /api/files/{fileId}/content. */
(function () {
  const PL_SCENES = [
    ['#F7C873', '#EC6F3D', '#7C3F8C'],
    ['#173A5E', '#2C7DA0', '#9FE0DF'],
    ['#22331E', '#41722F', '#A7C957'],
    ['#14131F', '#2C2456', '#C46FC0'],
    ['#ECD9A6', '#CB8A3B', '#9A5A2C'],
    ['#E2E9F0', '#A8BCD0', '#68809A'],
    ['#3A2C27', '#7C5638', '#DBA268'],
    ['#0F343B', '#2C6A60', '#E5B463'],
  ];
  function plPhotoBg(seed) {
    const s = Math.abs(seed);
    const [a, b, c] = PL_SCENES[s % PL_SCENES.length];
    const ang = 90 + (s % 7) * 18;
    const px = 20 + (s % 5) * 15;
    const py = 15 + ((s >> 2) % 5) * 14;
    return {
      background:
        'radial-gradient(120% 90% at ' + px + '% ' + py + '%, ' + c + ' 0%, transparent 55%),' +
        'radial-gradient(140% 120% at ' + (100 - px) + '% ' + (100 - py) + '%, ' + a + ' 0%, transparent 60%),' +
        'linear-gradient(' + ang + 'deg, ' + a + ' 0%, ' + b + ' 52%, ' + c + ' 100%)',
    };
  }

  const PL_ALBUMS = [
    { id: 'al1', name: 'Sunset Ave flat', description: 'Move-in and the first weeks in the new place.', cover: 3 },
    { id: 'al2', name: 'Lisbon trip', description: 'Five days in Alfama, April 2026.', cover: 11 },
    { id: 'al3', name: 'Home & garden', description: '', cover: 22 },
    { id: 'al4', name: 'Family', description: '', cover: 30 },
    { id: 'al5', name: 'Car & garage', description: '', cover: 37 },
  ];
  const PL_ASPECTS = [[1, 1], [4, 3], [3, 4], [3, 2], [2, 3], [16, 9], [4, 5]];

  // ---- Photo tags (record shape; the tag table the library links by id) ----
  // Same DTO shape as journal/task/transaction tags so they share the generic
  // createTagsPage surface (the /photo-tags management page).
  const PHOTO_TAGS = [
    { id: 'pt1', name: 'Milestone', normalizedName: 'MILESTONE', description: 'A notable moment worth keeping.', archived: null },
    { id: 'pt2', name: 'Property',  normalizedName: 'PROPERTY',  description: 'Home, renovations, and maintenance shots.', archived: null },
    { id: 'pt3', name: 'Travel',    normalizedName: 'TRAVEL',    description: 'Trips and days away.', archived: null },
    { id: 'pt4', name: 'Family',    normalizedName: 'FAMILY',    description: null, archived: null },
    { id: 'pt5', name: 'Vehicle',   normalizedName: 'VEHICLE',   description: null, archived: null },
    { id: 'pt6', name: 'Old album', normalizedName: 'OLD ALBUM', description: 'Retired — kept for history.', archived: '2025-03-01T00:00:00Z' },
  ];
  const PL_TAG_NAME = {}; PHOTO_TAGS.forEach(function (t) { PL_TAG_NAME[t.id] = t.name; });
  const TAG_ID_BY_NAME = {}; PHOTO_TAGS.forEach(function (t) { TAG_ID_BY_NAME[t.name] = t.id; });
  // Active-tag options for pickers/filters — {value:id,label:name}.
  const PL_TAG_OPTIONS = PHOTO_TAGS.filter(function (t) { return !t.archived; }).map(function (t) { return { value: t.id, label: t.name }; });

  // ---- People = existing Person contacts (linked by id, never created
  //      from the photo library). Registered onto OdysseyData.contacts so
  //      they are real Person records the picker/chip resolve by id. ----
  const PL_PERSONS = [
    { id: 'pp1', name: 'Mom' },
    { id: 'pp2', name: 'Dad' },
    { id: 'pp3', name: 'Sam' },
    { id: 'pp4', name: 'Alex' },
  ];
  const PL_PERSON_NAME = {}; PL_PERSONS.forEach(function (p) { PL_PERSON_NAME[p.id] = p.name; });
  const PERSON_ID_BY_NAME = {}; PL_PERSONS.forEach(function (p) { PERSON_ID_BY_NAME[p.name] = p.id; });
  const PL_PERSON_OPTIONS = PL_PERSONS.map(function (p) { return { value: p.id, label: p.name }; });
  if (window.OdysseyData && Array.isArray(window.OdysseyData.contacts)) {
    PL_PERSONS.forEach(function (p) {
      if (!window.OdysseyData.contacts.some(function (c) { return c.id === p.id; })) {
        window.OdysseyData.contacts.push({ id: p.id, name: p.name, normalizedName: p.name.toUpperCase(), type: 'Person', description: null, archived: null });
      }
    });
  }

  const USER = (window.OdysseyData && window.OdysseyData.user && window.OdysseyData.user.name) || 'Jordan Ellis';

  const PHOTOS = (function () {
    const plan = [
      { album: 'al1', n: 9, ym: '2026-06', stem: 'flat', tags: ['Property', 'Milestone'] },
      { album: 'al2', n: 11, ym: '2026-04', stem: 'lisbon', tags: ['Travel'] },
      { album: 'al3', n: 8, ym: '2026-05', stem: 'garden', tags: ['Property'] },
      { album: 'al4', n: 8, ym: '2026-06', stem: 'family', tags: ['Family', 'Milestone'] },
      { album: 'al5', n: 6, ym: '2026-05', stem: 'garage', tags: ['Vehicle'] },
    ];
    // Distinct embedded Title (IPTC/XMP) for a subset — the rest stay null so
    // display/alt exercise the filename fallback.
    const TITLES = {
      lisbon: 'Alfama rooftops at golden hour',
      family: 'Everyone in one frame',
      flat: 'Move-in day',
      garden: 'First tomatoes of the season',
      garage: 'Weekend in the garage',
    };
    const out = [];
    let seed = 1;
    plan.forEach(function (p) {
      for (let i = 0; i < p.n; i++) {
        const asp = PL_ASPECTS[seed % PL_ASPECTS.length];
        const day = ((seed * 7) % 26) + 1;
        const dd = String(day).padStart(2, '0');
        const date = p.ym + '-' + dd + 'T' + String(8 + (seed % 11)).padStart(2, '0') + ':' + String((seed * 13) % 60).padStart(2, '0') + ':00';
        out.push({
          id: 'ph' + seed,
          fileId: 'file-ph' + seed,                        // Files-store image id
          title: (TITLES[p.stem] && i === 0) ? TITLES[p.stem] : null,
          name: p.stem + '_' + String(i + 1).padStart(2, '0') + '.jpg',
          album: p.album,
          date: date,                                       // TakenAt (local wall-clock, no tz)
          w: asp[0], h: asp[1],
          pxW: asp[0] * 640, pxH: asp[1] * 640,             // PixelWidth / PixelHeight
          seed: seed,
          fav: seed % 6 === 0,
          tagIds: (p.tags || []).map(function (n) { return TAG_ID_BY_NAME[n]; }).filter(Boolean),
          personIds: [],
          lat: null, lng: null,
          createdBy: USER, createdAt: date, updatedBy: null, updatedAt: date,
        });
        seed++;
      }
    });
    out.sort(function (x, y) { return x.date < y.date ? 1 : -1; });
    const CAPTIONS = {
      lisbon: 'Golden hour over the Alfama rooftops — last evening of the trip.',
      family: 'Everyone finally in one frame.',
      flat: 'Move-in day. Empty rooms, big plans.',
      garden: 'First tomatoes of the season.',
    };
    const PEOPLE = { family: ['Mom', 'Dad', 'Sam'], flat: ['Alex'] };
    const LOCATIONS = { lisbon: { name: 'Alfama, Lisbon', lat: 38.7139, lng: -9.1275 }, family: { name: 'Home', lat: null, lng: null }, garden: { name: 'Back garden', lat: null, lng: null } };
    out.forEach(function (p, i) {
      const stem = p.name.split('_')[0];
      if (CAPTIONS[stem] && i % 2 === 0) p.caption = CAPTIONS[stem];
      if (PEOPLE[stem]) p.personIds = PEOPLE[stem].map(function (n) { return PERSON_ID_BY_NAME[n]; }).filter(Boolean);
      if (LOCATIONS[stem] && i % 3 !== 1) { p.location = LOCATIONS[stem].name; p.lat = LOCATIONS[stem].lat; p.lng = LOCATIONS[stem].lng; }
    });
    return out;
  })();

  const PL_ALBUM_BY_ID = Object.fromEntries(PL_ALBUMS.map(function (a) { return [a.id, a]; }));

  function plFmtDate(iso) {
    return new Date(iso).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
  }
  function plDateTime(iso) {
    return new Date(iso).toLocaleString('en-US', { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' });
  }
  function plMonthKey(iso) {
    return new Date(iso).toLocaleDateString('en-US', { month: 'long', year: 'numeric' });
  }
  function plTime(iso) {
    return new Date(iso).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' });
  }
  function plTagName(id) { return PL_TAG_NAME[id] || id; }
  function plPersonName(id) { return PL_PERSON_NAME[id] || id; }

  Object.assign(window, {
    PHOTOS: PHOTOS, PL_ALBUMS: PL_ALBUMS, PL_ALBUM_BY_ID: PL_ALBUM_BY_ID,
    PL_TAG_OPTIONS: PL_TAG_OPTIONS, PL_PERSON_OPTIONS: PL_PERSON_OPTIONS, PL_PERSONS: PL_PERSONS,
    PL_ASPECTS: PL_ASPECTS,
    plPhotoBg: plPhotoBg, plFmtDate: plFmtDate, plDateTime: plDateTime, plMonthKey: plMonthKey, plTime: plTime,
    plTagName: plTagName, plPersonName: plPersonName,
  });

  if (window.OdysseyData) {
    window.OdysseyData.photoTags = PHOTO_TAGS;
  }
})();
