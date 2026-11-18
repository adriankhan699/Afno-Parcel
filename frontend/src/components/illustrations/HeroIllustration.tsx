/** Delivery van carrying parcels along a road below the Himalayas. Original artwork. */
export default function HeroIllustration({ className = '' }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 480 320"
      role="img"
      aria-labelledby="hero-illustration-title"
      className={className}
    >
      <title id="hero-illustration-title">
        An Afno Parcel delivery van carrying parcels on a road below the Himalayan mountains
      </title>

      {/* Sky */}
      <rect width="480" height="320" rx="24" fill="#e0f2fe" />
      <circle cx="400" cy="62" r="26" fill="#fde68a" />

      {/* Himalayas */}
      <polygon points="0,200 70,110 130,170 200,70 280,170 340,100 420,180 480,130 480,230 0,230" fill="#94a3b8" />
      <polygon points="200,70 222,98 210,95 200,104 190,94 178,98" fill="#ffffff" />
      <polygon points="340,100 358,122 348,120 340,128 332,120 322,123" fill="#ffffff" />
      <polygon points="70,110 86,130 78,128 70,135 62,128 54,131" fill="#ffffff" />
      <polygon points="0,230 60,190 140,215 230,180 330,215 420,190 480,210 480,250 0,250" fill="#86efac" />

      {/* Road */}
      <rect x="0" y="250" width="480" height="70" fill="#475569" />
      <rect x="0" y="250" width="480" height="6" fill="#334155" />
      {[20, 110, 200, 290, 380].map((x) => (
        <rect key={x} x={x} y="283" width="50" height="6" rx="3" fill="#f8fafc" />
      ))}

      {/* Route line with pins */}
      <path d="M60 60 C 140 20, 220 120, 300 50" fill="none" stroke="#c8102e" strokeWidth="3" strokeDasharray="6 6" />
      <g transform="translate(48 30)">
        <path d="M12 0a12 12 0 0 1 12 12c0 9-12 22-12 22S0 21 0 12A12 12 0 0 1 12 0z" fill="#003893" />
        <circle cx="12" cy="12" r="4.5" fill="#fff" />
      </g>
      <g transform="translate(288 18)">
        <path d="M12 0a12 12 0 0 1 12 12c0 9-12 22-12 22S0 21 0 12A12 12 0 0 1 12 0z" fill="#c8102e" />
        <circle cx="12" cy="12" r="4.5" fill="#fff" />
      </g>

      {/* Van body */}
      <g>
        <rect x="120" y="168" width="200" height="88" rx="10" fill="#c8102e" />
        <path d="M320 186 h44 a10 10 0 0 1 8 4 l26 34 a10 10 0 0 1 2 6 v18 a8 8 0 0 1 -8 8 h-72 z" fill="#a50d26" />
        <path d="M330 194 h32 l22 30 h-54 z" fill="#bae6fd" />
        <rect x="390" y="236" width="12" height="8" rx="2" fill="#fde68a" />
        <rect x="132" y="180" width="176" height="44" rx="6" fill="#ffffff" opacity="0.15" />
        <text x="220" y="208" textAnchor="middle" fontFamily="ui-sans-serif, system-ui" fontSize="18" fontWeight="700" fill="#ffffff">
          Afno Parcel
        </text>
        <text x="220" y="228" textAnchor="middle" fontFamily="ui-sans-serif, system-ui" fontSize="11" fill="#fee2e2">
          Across all 7 provinces
        </text>
      </g>

      {/* Wheels */}
      {[170, 350].map((cx) => (
        <g key={cx}>
          <circle cx={cx} cy="258" r="22" fill="#1e293b" />
          <circle cx={cx} cy="258" r="9" fill="#cbd5e1" />
        </g>
      ))}

      {/* Parcels on the roof */}
      <Parcel x={140} y={128} w={56} h={40} />
      <Parcel x={200} y={138} w={44} h={30} />
      <Parcel x={248} y={120} w={60} h={48} />
    </svg>
  );
}

function Parcel({ x, y, w, h }: { x: number; y: number; w: number; h: number }) {
  return (
    <g>
      <rect x={x} y={y} width={w} height={h} rx="3" fill="#d6a46c" stroke="#a16207" strokeWidth="1.5" />
      <rect x={x + w / 2 - 4} y={y} width="8" height={h} fill="#fbbf24" opacity="0.8" />
      <rect x={x + 6} y={y + h - 14} width={w / 2 - 12} height="8" rx="1" fill="#ffffff" />
    </g>
  );
}
