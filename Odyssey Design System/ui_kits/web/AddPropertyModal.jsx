/* AddPropertyModal — New / Edit property. Mirrors NewProperty (§5 endpoint 3/4):
   the common fields plus EXACTLY ONE detail sub-object, the one matching Type.

   • Type is chosen once, on create (CardSelect). On edit it is shown locked:
     the PUT must carry the stored type and a different one is a 422, so the
     dialog never offers the change — it states the route instead (delete and
     re-create). Switching type on CREATE keeps both drafts in memory but only
     the matching sub-object is sent, so the XOR rule holds by construction.
   • Currency is the property's own (its estimates are recorded in it).
   • Registration number / VIN preview the service's uppercase + strip.
   • BuildYear rejects the future; ModelYear allows at most next year.
   • Archived is not here — it is the row menu's Archive / Restore, which lands
     on the same PUT. */

const APM_CURRENCIES = (window.OdysseyData.currencies || []).filter(c => !c.archived).map(c => ({ value: c.code, label: c.name }));

const AddPropertyModal = ({ property = null, onClose, onSave, estimateCount = 0 }) => {
  const { useState } = React;
  const H = window.OdysseyHelpers, D = window.OdysseyData;
  const editing = !!property;
  const thisYear = new Date().getFullYear();

  const [type, setType] = useState(property ? property.type : 'RealEstate');
  const [draft, setDraft] = useState(() => ({
    name: property?.name || '', description: property?.description || '',
    currencyCode: property?.currencyCode || 'USD',
    acquiredDate: property?.acquiredDate || '', disposedDate: property?.disposedDate || '',
    notes: property?.notes || '',
  }));
  const [re, setRe] = useState(() => ({ kind: 'House', addressLine: '', postalCode: '', city: '', countryCode: '', cadastralNumber: '', livingAreaSqm: null, plotAreaSqm: null, buildYear: null, ...(property?.realEstateDetails || {}) }));
  const [ve, setVe] = useState(() => ({ kind: 'Car', registrationNumber: '', vin: '', make: '', model: '', modelYear: null, firstRegisteredDate: '', ...(property?.vehicleDetails || {}) }));
  const [errors, setErrors] = useState({});
  const clr = (k) => { if (errors[k]) setErrors(e => ({ ...e, [k]: undefined })); };
  const set = (k) => (v) => { setDraft(d => ({ ...d, [k]: v })); clr(k); };
  const setR = (k) => (v) => { setRe(d => ({ ...d, [k]: v })); clr('re.' + k); };
  const setV = (k) => (v) => { setVe(d => ({ ...d, [k]: v })); clr('ve.' + k); };

  const ti = H.propTypeInfo(type);
  const currencyChanged = editing && draft.currencyCode !== property.currencyCode && estimateCount > 0;
  const blank = (v) => { const t = (v == null ? '' : String(v)).trim(); return t === '' ? null : t; };

  const submit = () => {
    const n = {};
    if (!draft.name.trim()) n.name = 'Give the property a name.';
    else if (draft.name.length > 256) n.name = 'Keep the name under 256 characters.';
    if (!draft.description.trim()) n.description = 'Add a short description.';
    if (draft.acquiredDate && draft.disposedDate && draft.disposedDate < draft.acquiredDate) n.disposedDate = 'Disposed can’t be before Acquired.';
    if (type === 'RealEstate') {
      if (re.countryCode && !/^[A-Za-z]{2}$/.test(re.countryCode)) n['re.countryCode'] = 'Two letters, e.g. NO or US.';
      if (re.buildYear != null && (re.buildYear < 1000 || re.buildYear > thisYear)) n['re.buildYear'] = re.buildYear > thisYear ? 'Build year can’t be in the future.' : 'Enter a year after 1000.';
      ['livingAreaSqm', 'plotAreaSqm'].forEach(k => { if (re[k] != null && (re[k] < 0 || re[k] > 1000000)) n['re.' + k] = 'Between 0 and 1,000,000 m².'; });
    } else {
      if (ve.modelYear != null && (ve.modelYear < 1900 || ve.modelYear > thisYear + 1)) n['ve.modelYear'] = ve.modelYear > thisYear + 1 ? `No later than ${thisYear + 1}.` : 'Enter a year after 1900.';
      if (H.propNormPlate(ve.registrationNumber).length > 16) n['ve.registrationNumber'] = 'At most 16 characters.';
    }
    if (Object.keys(n).length) { setErrors(n); return; }
    const dto = {
      name: draft.name.trim(), description: draft.description.trim(), type,
      currencyCode: draft.currencyCode,
      acquiredDate: draft.acquiredDate || null, disposedDate: draft.disposedDate || null,
      notes: blank(draft.notes),
      realEstateDetails: type === 'RealEstate' ? {
        kind: re.kind, addressLine: blank(re.addressLine), postalCode: blank(re.postalCode), city: blank(re.city),
        countryCode: re.countryCode ? re.countryCode.toUpperCase() : null, cadastralNumber: blank(re.cadastralNumber),
        livingAreaSqm: re.livingAreaSqm ?? null, plotAreaSqm: re.plotAreaSqm ?? null, buildYear: re.buildYear ?? null,
      } : null,
      vehicleDetails: type === 'Vehicle' ? {
        kind: ve.kind, registrationNumber: blank(H.propNormPlate(ve.registrationNumber)), vin: blank(H.propNormPlate(ve.vin)),
        make: blank(ve.make), model: blank(ve.model), modelYear: ve.modelYear ?? null, firstRegisteredDate: ve.firstRegisteredDate || null,
      } : null,
    };
    onSave(dto);
  };

  const kindOpts = (arr) => arr.map(k => ({ value: k.key, label: k.label, icon: k.icon }));
  const plate = H.propNormPlate(ve.registrationNumber);
  const vin = H.propNormPlate(ve.vin);

  return (
    <Modal
      title={editing ? 'Edit property' : 'New property'}
      subtitle={editing ? 'Update the details. Estimates and smart tags are managed from the property.' : 'Record a house, cabin, car or boat. Add its estimated value and smart tags once it exists.'}
      icon={editing ? 'edit' : 'home_work'}
      className="prop-dialog"
      onClose={onClose}
      footer={<React.Fragment>
        <Button variant="text" onClick={onClose}>Cancel</Button>
        <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>{editing ? 'Save changes' : 'Create property'}</Button>
      </React.Fragment>}>

      {editing ? (
        <div className="prop-type-locked" style={{ '--rec': ti.color, '--rec-soft': ti.soft }}>
          <span className="prop-type-glyph"><MIcon name={ti.icon} size={20} /></span>
          <div className="prop-type-text">
            <div className="prop-type-label">{ti.label}<MIcon name="lock" size={14} /></div>
            <div className="prop-type-help">A property’s type is fixed at creation. To change it, delete this property and create a new one of the right type.</div>
          </div>
        </div>
      ) : (
        <FieldShell label="What is it?" required>
          <CardSelect ariaLabel="Property type" value={type} onChange={setType} columns={2} maxItemWidth={200} center
            options={D.propertyTypes.map(t => ({ value: t.key, label: t.label, icon: t.icon, color: t.color, soft: t.soft }))} />
        </FieldShell>
      )}

      <Field label="Name" required value={draft.name} onChange={set('name')} autoFocus error={errors.name}
        placeholder={type === 'Vehicle' ? 'e.g. Subaru Outback' : 'e.g. Storgata 14'} />
      <Field label="Description" required value={draft.description} onChange={set('description')} error={errors.description}
        placeholder={type === 'Vehicle' ? 'e.g. Family car' : 'e.g. Primary residence'} />

      <FormRow>
        <Select label="Kind" required value={type === 'Vehicle' ? ve.kind : re.kind}
          onChange={type === 'Vehicle' ? setV('kind') : setR('kind')}
          options={kindOpts(type === 'Vehicle' ? D.vehicleKinds : D.realEstateKinds)} />
        <CurrencySelect required value={draft.currencyCode} onChange={set('currencyCode')} options={APM_CURRENCIES} searchThreshold={0}
          helper="Estimates are recorded in this currency." />
      </FormRow>
      {currencyChanged ? (
        <Alert severity="warning">
          {estimateCount} estimate{estimateCount === 1 ? ' is' : 's are'} recorded in <b>{property.currencyCode}</b>. They keep that currency; new estimates must use <b>{draft.currencyCode}</b>.
        </Alert>
      ) : null}

      <FormRow>
        <div className="field">
          <Field type="date" label="Acquired" value={draft.acquiredDate} onChange={set('acquiredDate')} placeholder="Unknown" />
          <div className="helper">Leave empty if you don’t know. Setting or clearing it records an event.</div>
        </div>
        <div className="field">
          <Field type="date" label="Disposed" value={draft.disposedDate} onChange={set('disposedDate')} placeholder="Still owned" />
          {errors.disposedDate ? <div className="helper aam-err">{errors.disposedDate}</div> : <div className="helper">Sold, written off or scrapped. Setting or clearing it records an event.</div>}
        </div>
      </FormRow>

      <SectionDivider label={type === 'Vehicle' ? 'Vehicle details' : 'Real estate details'} meta="all optional" />

      {type === 'RealEstate' ? (
        <React.Fragment>
          <Field label="Address" value={re.addressLine || ''} onChange={setR('addressLine')} placeholder="Street and number" />
          <FormRow cols={3}>
            <Field label="Postal code" value={re.postalCode || ''} onChange={setR('postalCode')} />
            <Field label="City" value={re.city || ''} onChange={setR('city')} />
            <Field label="Country" value={re.countryCode || ''} onChange={(v) => setR('countryCode')(v.slice(0, 2).toUpperCase())}
              placeholder="NO" error={errors['re.countryCode']} helper="ISO code, two letters" />
          </FormRow>
          <Field label="Cadastral number" value={re.cadastralNumber || ''} onChange={setR('cadastralNumber')}
            placeholder="e.g. 208/451 or APN 3612-044" helper="The land-registry identifier, as written. Not checked." />
          <FormRow cols={3}>
            <NumberField label="Living area" unit="m²" min={0} step={0.5} value={re.livingAreaSqm} onChange={setR('livingAreaSqm')} error={errors['re.livingAreaSqm']} optional />
            <NumberField label="Plot area" unit="m²" min={0} step={1} value={re.plotAreaSqm} onChange={setR('plotAreaSqm')} error={errors['re.plotAreaSqm']} optional />
            <NumberField label="Build year" min={1000} max={thisYear} step={1} value={re.buildYear} onChange={setR('buildYear')} error={errors['re.buildYear']} optional />
          </FormRow>
        </React.Fragment>
      ) : (
        <React.Fragment>
          <FormRow>
            <Field label="Registration number" value={ve.registrationNumber || ''} onChange={setV('registrationNumber')} error={errors['ve.registrationNumber']}
              helper={plate && plate !== ve.registrationNumber ? <React.Fragment>Saved as <b className="mono">{plate}</b></React.Fragment> : 'Spaces removed, uppercased.'} />
            <Field label="VIN / hull number" value={ve.vin || ''} onChange={setV('vin')}
              helper={vin && vin !== ve.vin ? <React.Fragment>Saved as <b className="mono">{vin}</b></React.Fragment> : 'Stored as written — no check digit.'} />
          </FormRow>
          <FormRow>
            <Field label="Make" value={ve.make || ''} onChange={setV('make')} placeholder="e.g. Subaru" />
            <Field label="Model" value={ve.model || ''} onChange={setV('model')} placeholder="e.g. Outback Limited" />
          </FormRow>
          <FormRow>
            <NumberField label="Model year" min={1900} max={thisYear + 1} step={1} value={ve.modelYear} onChange={setV('modelYear')} error={errors['ve.modelYear']} optional />
            <Field type="date" label="First registered" value={ve.firstRegisteredDate || ''} onChange={setV('firstRegisteredDate')} placeholder="Unknown" />
          </FormRow>
        </React.Fragment>
      )}

      <NoteField label="Notes" optional maxLength={1024} value={draft.notes} onChange={set('notes')}
        placeholder="Anything worth keeping with it — renovations, condition, where the keys are." />
    </Modal>
  );
};

Object.assign(window, { AddPropertyModal });
