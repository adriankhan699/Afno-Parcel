// Simple, original illustrations for each stage of a parcel's journey.
// Each one is a 120×120 picture with its own accessible title.
import type { ReactNode } from 'react';

interface IllustrationProps {
  title: string;
  className?: string;
}

function Frame({ title, className, bg, children }: IllustrationProps & { bg: string; children: ReactNode }) {
  return (
    <svg viewBox="0 0 120 120" role="img" aria-label={title} className={className}>
      <circle cx="60" cy="60" r="56" fill={bg} />
      {children}
    </svg>
  );
}

function Box({ x, y, s }: { x: number; y: number; s: number }) {
  return (
    <g>
      <rect x={x} y={y} width={s} height={s * 0.8} rx="2" fill="#d6a46c" stroke="#a16207" strokeWidth="1.5" />
      <rect x={x + s / 2 - 3} y={y} width="6" height={s * 0.8} fill="#fbbf24" />
    </g>
  );
}

/** 1. Customer books online or at the counter. */
export function BookIllustration(props: IllustrationProps) {
  return (
    <Frame {...props} bg="#fee2e2">
      <rect x="38" y="20" width="44" height="80" rx="8" fill="#1e293b" />
      <rect x="42" y="28" width="36" height="62" rx="3" fill="#ffffff" />
      <Box x={47} y={36} s={26} />
      <rect x="46" y="64" width="28" height="5" rx="2" fill="#cbd5e1" />
      <rect x="46" y="74" width="28" height="9" rx="3" fill="#c8102e" />
      <circle cx="60" cy="95" r="2.5" fill="#64748b" />
      <circle cx="90" cy="34" r="12" fill="#16a34a" />
      <path d="M84 34 l4 4 8-8" fill="none" stroke="#fff" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" />
    </Frame>
  );
}

/** 2. Parcel received at the branch. */
export function BranchIllustration(props: IllustrationProps) {
  return (
    <Frame {...props} bg="#dbeafe">
      <rect x="22" y="48" width="76" height="50" fill="#ffffff" stroke="#003893" strokeWidth="2" />
      <path d="M18 48 l8 -18 h68 l8 18 z" fill="#003893" />
      {[26, 42, 58, 74, 90].map((x, i) => (
        <path key={x} d={`M${x - 8} 48 q8 10 16 0`} fill={i % 2 === 0 ? '#c8102e' : '#ffffff'} />
      ))}
      <rect x="50" y="66" width="20" height="32" fill="#93c5fd" />
      <rect x="28" y="62" width="16" height="14" fill="#bae6fd" />
      <rect x="76" y="62" width="16" height="14" fill="#bae6fd" />
      <Box x={78} y={82} s={20} />
      <text x="60" y="43" textAnchor="middle" fontFamily="ui-sans-serif, system-ui" fontSize="9" fontWeight="700" fill="#ffffff">
        BRANCH
      </text>
    </Frame>
  );
}

/** 3. Parcel travels between cities. */
export function TransitIllustration(props: IllustrationProps) {
  return (
    <Frame {...props} bg="#fef3c7">
      <rect x="10" y="86" width="100" height="4" rx="2" fill="#94a3b8" />
      <rect x="16" y="44" width="58" height="38" rx="4" fill="#c8102e" />
      <path d="M74 54 h18 l12 14 v14 h-30 z" fill="#a50d26" />
      <path d="M78 58 h12 l8 10 h-20 z" fill="#bae6fd" />
      <Box x={24} y={52} s={18} />
      <Box x={46} y={56} s={16} />
      {[34, 88].map((cx) => (
        <g key={cx}>
          <circle cx={cx} cy="84" r="9" fill="#1e293b" />
          <circle cx={cx} cy="84" r="3.5" fill="#cbd5e1" />
        </g>
      ))}
      {[30, 38, 46].map((y) => (
        <rect key={y} x="2" y={y + 8} width={y === 38 ? 10 : 6} height="3" rx="1.5" fill="#f59e0b" />
      ))}
    </Frame>
  );
}

/** 4. Rider heads out for delivery. */
export function OutForDeliveryIllustration(props: IllustrationProps) {
  return (
    <Frame {...props} bg="#ede9fe">
      <rect x="10" y="92" width="100" height="4" rx="2" fill="#94a3b8" />
      {/* Scooter */}
      <circle cx="34" cy="86" r="10" fill="#1e293b" />
      <circle cx="34" cy="86" r="4" fill="#cbd5e1" />
      <circle cx="88" cy="86" r="10" fill="#1e293b" />
      <circle cx="88" cy="86" r="4" fill="#cbd5e1" />
      <path d="M30 78 h40 l10 -26 h6 l6 34 h-12 l-4 -8 h-40 z" fill="#7c3aed" />
      <rect x="82" y="46" width="12" height="4" rx="2" fill="#1e293b" />
      {/* Delivery box on the back */}
      <rect x="18" y="50" width="30" height="26" rx="3" fill="#c8102e" />
      <rect x="22" y="56" width="22" height="4" rx="2" fill="#ffffff" />
      {/* Rider */}
      <circle cx="66" cy="30" r="9" fill="#f59e0b" />
      <path d="M57 30 a9 9 0 0 1 18 0 z" fill="#c8102e" />
      <path d="M58 40 h16 l6 22 h-14 l-6 -8 z" fill="#003893" />
      <path d="M74 44 l12 6" stroke="#003893" strokeWidth="5" strokeLinecap="round" />
    </Frame>
  );
}

/** 5. Parcel delivered to the recipient. */
export function DeliveredIllustration(props: IllustrationProps) {
  return (
    <Frame {...props} bg="#dcfce7">
      <path d="M24 56 l36 -28 l36 28" fill="none" stroke="#a50d26" strokeWidth="6" strokeLinejoin="round" strokeLinecap="round" />
      <rect x="32" y="54" width="56" height="44" fill="#ffffff" stroke="#16a34a" strokeWidth="2" />
      <rect x="52" y="70" width="16" height="28" fill="#d6a46c" />
      <rect x="38" y="62" width="10" height="10" fill="#bae6fd" />
      <rect x="72" y="62" width="10" height="10" fill="#bae6fd" />
      <Box x={66} y={84} s={16} />
      <circle cx="90" cy="30" r="14" fill="#16a34a" />
      <path d="M83 30 l5 5 9-10" fill="none" stroke="#fff" strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round" />
    </Frame>
  );
}
