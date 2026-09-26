import * as React from 'react';

export interface PropertyEventType {
  /** Enum key — matches the C# PropertyEventType member. */
  key: string;
  label: string;
  /** Numeric enum value, 100–199. Disjoint from ContractEventType (0–99). */
  enumValue: number;
  icon: string;
  color: string;
  soft: string;
  /** 'common' (both property types), 'RealEstate' or 'Vehicle'. */
  scope: 'common' | 'RealEstate' | 'Vehicle';
  /** Written only by the server — never offered on create. */
  systemOnly?: boolean;
  /** Clause for "Recorded automatically when …" on a system row. */
  auto?: string;
  desc: string;
}

/** Canonical registry in reading order, `Other` (ordinal 108) last. */
export declare const PROPERTY_EVENT_TYPES: PropertyEventType[];
/** Archived · Unarchived · AcquisitionDateCleared · DisposalReversed. */
export declare const PROPERTY_EVENT_SYSTEM_ONLY: string[];
/** Legal keys per PropertyType — 17 each, 34 of 42 cells. */
export declare const PROPERTY_EVENT_TYPE_MATRIX: { RealEstate: string[]; Vehicle: string[] };

export declare function propertyEventTypeLegality(propertyType: string, key: string): 'legal' | 'systemOnly' | 'illegal';
export declare function propertyEventTypesFor(propertyType?: string, keepType?: string, types?: PropertyEventType[]): (PropertyEventType & { group: 'specific' | 'common' | 'system' })[];

export interface PropertyEventTypeSelectProps {
  value?: string;
  onChange?: (value: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  label?: string;
  /** 'RealEstate' | 'Vehicle' — limits the list to the legal members, type-specific first. */
  propertyType?: string;
  /** A system-only key the row being edited already carries — offered alone so the PUT can keep it. */
  keepType?: string;
  types?: PropertyEventType[];
  placeholder?: string;
  help?: string;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * Single-select over PropertyEventType, filtered by the property's type through
 * PROPERTY_EVENT_TYPE_MATRIX. System-only members are never offered unless
 * passed as `keepType`. A typed wrapper over `RegistrySelect`.
 */
export declare function PropertyEventTypeSelect(props: PropertyEventTypeSelectProps): JSX.Element;
