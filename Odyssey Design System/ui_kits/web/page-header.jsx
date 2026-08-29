/* PageHeader — BRIDGED to the typed design-system component.
   SINGLE SOURCE OF TRUTH: components/PageHeader.jsx (+ .d.ts), loaded from
   `_ds_bundle.js` off window.OdysseyDesignSystem_d5aa51. The full prop
   contract (title / sub / chips / icon, Overview · Search · Reference ·
   Signal regions, actions / primary / menu, card) is documented there and in
   the Design System tab (Components · page header). This file only globalizes
   it for the kit's Babel-script page files — there is no second
   implementation here. */

/* The old kit implementation (still inside the previous bundle build) already
   globalizes a working PageHeader, so only override it once the rebuilt bundle
   actually carries the typed component — never clobber it with undefined. */
const __dsPageHeader = (window.OdysseyDesignSystem_d5aa51 || {}).PageHeader;
if (__dsPageHeader) Object.assign(window, { PageHeader: __dsPageHeader });
