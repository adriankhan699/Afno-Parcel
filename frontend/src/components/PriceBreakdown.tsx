import { formatNpr } from '../lib/format';
import { formatWeight } from '../lib/labels';
import type { PriceBreakdownDto } from '../types/api';

/** Itemised delivery charge. All numbers come from the server; nothing is calculated here. */
export default function PriceBreakdown({ breakdown, className = '' }: { breakdown: PriceBreakdownDto; className?: string }) {
  const b = breakdown;
  const showVolumetric = b.volumetricWeightKg > 0;

  const rows: { label: string; value: string; muted?: boolean }[] = [
    { label: 'Weight entered', value: formatWeight(b.actualWeightKg), muted: true },
    ...(showVolumetric ? [{ label: 'Size-based weight', value: formatWeight(b.volumetricWeightKg), muted: true }] : []),
    { label: 'Weight charged', value: formatWeight(b.chargeableWeightKg), muted: true },
    { label: 'Base charge', value: formatNpr(b.baseCharge) },
    { label: 'Extra weight charge', value: formatNpr(b.weightCharge) },
    ...(b.codFee > 0 ? [{ label: 'Cash-on-delivery fee', value: formatNpr(b.codFee) }] : []),
    { label: `VAT (${Number(b.vatPercent)}%)`, value: formatNpr(b.vatAmount) },
  ];

  return (
    <dl className={`text-sm ${className}`}>
      {rows.map((row) => (
        <div key={row.label} className="flex justify-between gap-4 py-1">
          <dt className={row.muted ? 'text-slate-500' : 'text-slate-600'}>{row.label}</dt>
          <dd className={row.muted ? 'text-slate-500' : 'text-slate-900'}>{row.value}</dd>
        </div>
      ))}
      <div className="mt-1 flex justify-between gap-4 border-t border-slate-200 pt-2 font-semibold">
        <dt>Delivery charge</dt>
        <dd>{formatNpr(b.totalCharge)}</dd>
      </div>
    </dl>
  );
}
