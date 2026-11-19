import { adminApi } from '../lib/adminApi';
import { formatNepalDateTime } from '../lib/format';
import { SCAN_TYPE_OPTIONS } from '../lib/scan';
import { useApi } from '../lib/useApi';
import type { ParcelMovementResponse } from '../types/api';
import { Alert } from './form';
import { TableShell, Th, theadClass } from './NetworkBits';

const scanTypeLabel = (value: string) => SCAN_TYPE_OPTIONS.find((o) => o.value === value)?.label ?? value;

/** Where a parcel has been inside the network: every warehouse scan, oldest first. Staff only. */
export default function MovementHistory({ parcelId, reloadKey = 0 }: { parcelId: string; reloadKey?: number }) {
  const result = useApi<ParcelMovementResponse[]>(adminApi.parcelMovements(parcelId), reloadKey);

  return (
    <div aria-live="polite">
      {result.status === 'loading' && <p className="text-slate-500">Loading movements…</p>}
      {result.status === 'error' && <Alert>{result.message}</Alert>}
      {result.status === 'loaded' && result.data.length === 0 && <p className="text-slate-600">This parcel has not been scanned at any warehouse yet.</p>}
      {result.status === 'loaded' && result.data.length > 0 && (
        <TableShell>
          <thead className={theadClass}>
            <tr>
              <Th>When</Th>
              <Th>Scan</Th>
              <Th>Warehouse</Th>
              <Th>Heading to</Th>
              <Th>Staff</Th>
              <Th>Remarks</Th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {result.data.map((m) => (
              <tr key={m.id}>
                <td className="whitespace-nowrap px-4 py-3">{formatNepalDateTime(m.occurredAtUtc)}</td>
                <td className="whitespace-nowrap px-4 py-3">
                  {scanTypeLabel(m.scanType)}
                  {m.isUnexpectedArrival && (
                    <span className="ml-2 rounded-full bg-amber-50 px-2 py-0.5 text-xs font-semibold text-amber-800" title="It arrived at a warehouse it was not sent to">
                      Unexpected
                    </span>
                  )}
                </td>
                <td className="px-4 py-3">{m.warehouseName}</td>
                <td className="px-4 py-3">{m.toWarehouseName ?? '—'}</td>
                <td className="px-4 py-3">{m.staffName ?? '—'}</td>
                <td className="px-4 py-3 text-slate-600">{m.remarks ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </TableShell>
      )}
    </div>
  );
}
