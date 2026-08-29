import * as React from 'react';

export interface ToastAction {
  label: string;
  onClick: () => void;
}

export interface ToastProps {
  message: React.ReactNode;
  /** default = no icon (terse confirmation) · success/warning/error/info tint a leading icon. */
  severity?: 'default' | 'success' | 'warning' | 'error' | 'info';
  /** Optional inline action (e.g. Undo). */
  action?: ToastAction;
  /** Render a close button + enable auto-dismiss. Called on dismiss/timeout. */
  onClose?: () => void;
  /** Auto-dismiss after this many ms (needs onClose). Omit to keep until dismissed. */
  duration?: number;
  /** Override the severity icon with a Material Icons ligature. */
  icon?: string;
}

/** Transient bottom-corner confirmation. Render inside a ToastStack. */
export declare function Toast(props: ToastProps): JSX.Element;

export interface ToastStackProps {
  /** Corner anchor. end = bottom-right (default), start = bottom-left, center = bottom-center. */
  align?: 'start' | 'end' | 'center';
  children?: React.ReactNode;
}

/** Fixed positioner for live toasts. Defaults to bottom-right. */
export declare function ToastStack(props: ToastStackProps): JSX.Element;
