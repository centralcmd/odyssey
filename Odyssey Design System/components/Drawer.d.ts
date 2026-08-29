import * as React from 'react';

export interface DrawerProps {
  /** Brand lockup node, pinned to the top of the rail. */
  brand?: React.ReactNode;
  /** Footer nav group (Preferences / User Account / About). */
  footer?: React.ReactNode;
  /** Primary nav items (NavItem / Drawer.Section). */
  children?: React.ReactNode;
  ariaLabel?: string;
}

export interface DrawerSectionProps {
  children?: React.ReactNode;
}

/** The 240px left rail — the app's only chrome surface. */
export declare function Drawer(props: DrawerProps): JSX.Element & {
  /** Uppercase group label for nav sections. */
  Section: (props: DrawerSectionProps) => JSX.Element;
};

export interface NavItemProps {
  /** Leading Material Icons ligature. */
  icon?: string;
  label: React.ReactNode;
  /** Active route — tide tint + brand text + aria-current="page". */
  active?: boolean;
  /** Render as an <a> with this href; otherwise a <button>. */
  href?: string;
  onClick?: (e: React.MouseEvent) => void;
  /** Trailing count/badge. */
  badge?: React.ReactNode;
  ariaLabel?: string;
}

/** One nav row — icon + label with an active state. */
export declare function NavItem(props: NavItemProps): JSX.Element;
