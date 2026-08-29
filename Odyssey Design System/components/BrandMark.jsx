/**
 * Odyssey DS — BrandMark
 * Static inline-SVG render of the official Odyssey compass-rose logomark
 * (bold weight). Pure rendering — the colors are the brand's exact hex values
 * (see the Brand cards in the Design System tab). `withWordmark` adds the
 * spaced-caps ODYSSEY wordmark under the compass (the lockup used by the
 * drawer and the login card). Self-contained.
 */
export function BrandMark({ size = 28, withWordmark = false }) {
  const FRAME = '#006B5A';
  const GLOW  = '#00F5D4';
  const GRAY  = '#707070';
  const DARK  = '#404040';
  const vb = withWordmark ? '0 0 200 240' : '0 0 200 210';
  // Render compass at the (100,105) center, r=90 outer. Bold strokes + rose.
  return (
    <svg width={size} height={size * (withWordmark ? 1.2 : 1.05)} viewBox={vb} fill="none" role="img" aria-label="Odyssey">
      {/* Rings */}
      <circle cx="100" cy="105" r="90" fill="none" stroke={FRAME} strokeWidth="13"/>
      <circle cx="100" cy="105" r="66" fill="none" stroke={FRAME} strokeWidth="2.5" strokeDasharray="6 4"/>
      {/* Cardinal ticks */}
      <line x1="100" y1="9"   x2="100" y2="29"  stroke={FRAME} strokeWidth="7"/>
      <line x1="100" y1="181" x2="100" y2="201" stroke={FRAME} strokeWidth="7"/>
      <line x1="4"   y1="105" x2="24"  y2="105" stroke={FRAME} strokeWidth="7"/>
      <line x1="176" y1="105" x2="196" y2="105" stroke={FRAME} strokeWidth="7"/>
      {/* Ordinal ticks */}
      <line x1="36"  y1="41"  x2="50"  y2="55"  stroke={FRAME} strokeWidth="5"/>
      <line x1="164" y1="41"  x2="150" y2="55"  stroke={FRAME} strokeWidth="5"/>
      <line x1="36"  y1="169" x2="50"  y2="155" stroke={FRAME} strokeWidth="5"/>
      <line x1="164" y1="169" x2="150" y2="155" stroke={FRAME} strokeWidth="5"/>
      {/* Compass rose */}
      <polygon points="100,25  91,105 100,82  109,105" fill={GLOW}/>
      <polygon points="100,105 91,105 100,82  109,105" fill={FRAME}/>
      <polygon points="100,185 91,105 100,128 109,105" fill={GRAY}/>
      <polygon points="100,105 91,105 100,128 109,105" fill={DARK}/>
      <polygon points="185,105 100,96 123,105 100,114" fill={GRAY}/>
      <polygon points="100,105 100,96 123,105 100,114" fill={DARK}/>
      <polygon points="15,105  100,96 77,105  100,114" fill={GRAY}/>
      <polygon points="100,105 100,96 77,105  100,114" fill={DARK}/>
      {/* Center pivot */}
      <circle cx="100" cy="105" r="11" fill="none" stroke={FRAME} strokeWidth="3"/>
      <circle cx="100" cy="105" r="5"  fill={GLOW}/>
      {withWordmark && (
        <text x="100" y="226" textAnchor="middle"
              fontFamily="Roboto, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif"
              fontWeight="500" fontSize="18" letterSpacing="5" fill={GLOW}>ODYSSEY</text>
      )}
    </svg>
  );
}
