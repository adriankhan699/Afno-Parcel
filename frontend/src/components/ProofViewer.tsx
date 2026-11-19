import { useEffect, useState } from 'react';
import { ApiError, apiBlob } from '../lib/apiClient';
import { proofSummary } from '../lib/delivery';
import { deliveryApi } from '../lib/deliveryApi';
import { formatNepalDateTime } from '../lib/format';
import type { DeliveryProofResponse } from '../types/api';
import Dialog from './Dialog';
import { Alert, SecondaryButton } from './form';

/** Proofs as small buttons. A proof with a file opens in a viewer; a code proof has nothing to show and is plain text. */
export function ProofChips({ proofs, onOpen }: { proofs: readonly DeliveryProofResponse[]; onOpen: (proof: DeliveryProofResponse) => void }) {
  if (proofs.length === 0) return <span className="text-slate-500">None</span>;
  return (
    <ul className="flex flex-wrap gap-1">
      {proofs.map((p) =>
        p.hasFile ? (
          <li key={p.id}>
            <button type="button" onClick={() => onOpen(p)} className="rounded-full bg-brand-50 px-2 py-0.5 text-xs font-semibold text-brand-800 underline">
              {proofSummary(p)}
            </button>
          </li>
        ) : (
          <li key={p.id} className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-semibold text-slate-700">
            {proofSummary(p)}
          </li>
        ),
      )}
    </ul>
  );
}

/**
 * Shows a proof image. The file is protected, so it is fetched with the bearer token and shown from an in-memory object URL
 * (never a link with a token in it). The URL is released when the viewer closes.
 */
export default function ProofViewer({ proof, onClose }: { proof: DeliveryProofResponse | null; onClose: () => void }) {
  return (
    <Dialog open={proof !== null} onClose={onClose} title={proof ? proofSummary(proof) : 'Proof'} wide>
      {proof && <ProofImage proof={proof} onClose={onClose} />}
    </Dialog>
  );
}

function ProofImage({ proof, onClose }: { proof: DeliveryProofResponse; onClose: () => void }) {
  const [url, setUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let objectUrl: string | null = null;
    apiBlob(deliveryApi.proofFile(proof.id), controller.signal)
      .then((blob) => {
        objectUrl = URL.createObjectURL(blob);
        setUrl(objectUrl);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setError(err instanceof ApiError ? (err.status === 403 ? 'You are not allowed to see this file.' : err.message) : 'Could not load the file.');
      });
    return () => {
      controller.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [proof.id]);

  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-600">
        Recorded {formatNepalDateTime(proof.capturedAtUtc)}
        {proof.recipientName ? ` · received by ${proof.recipientName}` : ''}
      </p>
      {error && <Alert>{error}</Alert>}
      {!url && !error && <p className="text-sm text-slate-500">Loading…</p>}
      {url && <img src={url} alt={`${proofSummary(proof)}, recorded ${formatNepalDateTime(proof.capturedAtUtc)}`} className="max-h-[70vh] w-full rounded-lg border border-slate-200 bg-white object-contain" />}
      <div className="flex justify-end">
        <SecondaryButton type="button" onClick={onClose}>
          Close
        </SecondaryButton>
      </div>
    </div>
  );
}
