/**
 * Odyssey DS — Skeleton
 * Loading placeholder for content that's still fetching — the finance app
 * pulls a lot of data, so tables, stat tiles and cards should render their
 * shape immediately and shimmer until the data lands. Maps to MudSkeleton.
 *
 * `variant` picks the shape: text (a line — pass `lines` for a paragraph),
 * circle (avatars), block (cards, charts, tiles). Size with `width`/`height`
 * (CSS lengths). The shimmer respects prefers-reduced-motion (falls static).
 */
export function Skeleton({
  variant = 'text',
  width,
  height,
  lines = 1,
  className = '',
  style,
}) {
  if (variant === 'text' && lines > 1) {
    return (
      <div className="odc-skeleton-lines" aria-hidden="true">
        {Array.from({ length: lines }).map((_, i) => (
          <span
            key={i}
            className="odc-skeleton text"
            style={{ width: i === lines - 1 ? '70%' : (width || '100%'), height }}
          />
        ))}
      </div>
    );
  }
  const cls = `odc-skeleton ${variant}${className ? ' ' + className : ''}`;
  return (
    <span
      className={cls}
      aria-hidden="true"
      style={{ width, height, ...(style || {}) }}
    />
  );
}

/**
 * SkeletonRow — a full-width placeholder row for a loading <Table>. Render a
 * few inside <tbody> (or pass as the table's `empty`/loading slot) while rows
 * fetch. `columns` controls the cell count; `align` (per-index 'end') right-
 * sizes the numeric columns so the shimmer matches the real layout.
 */
export function SkeletonRow({ columns = 4, align = [] }) {
  return (
    <tr className="odc-skel-row" aria-hidden="true">
      {Array.from({ length: columns }).map((_, i) => (
        <td key={i} className={align[i] === 'end' ? 'num' : undefined}>
          <span
            className="odc-skeleton text"
            style={{ width: align[i] === 'end' ? '56px' : `${50 + ((i * 17) % 40)}%`, marginLeft: align[i] === 'end' ? 'auto' : undefined }}
          />
        </td>
      ))}
    </tr>
  );
}
