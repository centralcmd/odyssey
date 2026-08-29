export interface TabItem {
  value: string;
  label: React.ReactNode;
  /** id of the panel this tab controls — sets aria-controls. */
  panelId?: string;
  /** id applied to the tab button, so its panel can reference it via aria-labelledby. */
  tabId?: string;
}

export interface TabsProps {
  /** Tab definitions as {value,label} objects or plain strings. */
  tabs: Array<TabItem | string>;
  /** The active tab's value (controlled). */
  value?: string;
  onChange?: (value: string) => void;
}

/** A horizontal tab strip (tablist only — render the panel yourself and swap it on onChange). */
export declare function Tabs(props: TabsProps): JSX.Element;
