import { useState } from 'react';
import { Link, useParams } from 'react-router';
import { BigButton, BigLink, CodBadge, InfoRow, Panel, StatusChip } from '../../components/agent/AgentBits';
import { CollectCashSheet, FailSheet, ReturnSheet } from '../../components/agent/AgentSheets';
import ProofPanel from '../../components/agent/ProofPanel';
import ConfirmDialog from '../../components/ConfirmDialog';
import { Alert } from '../../components/form';
import { api } from '../../lib/apiClient';
import { agentStage, deliverySteps, mapsUrl, nextDeliveryStep, telHref } from '../../lib/delivery';
import { deliveryApi } from '../../lib/deliveryApi';
import { formatNepalDateTime, formatNpr } from '../../lib/format';
import { formatWeight, serviceLabel } from '../../lib/labels';
import { useGuardedAction } from '../../lib/useGuardedAction';
import { useStableApi } from '../../lib/useStableApi';
import type { AgentAssignmentResponse, DeliveryProofResponse } from '../../types/api';

type Sheet = 'fail' | 'cash' | 'return' | 'complete' | null;

function proofLabel(proof: DeliveryProofResponse): string {
  const who = proof.recipientName ? ` · ${proof.recipientName}` : '';
  switch (proof.type) {
    case 'Otp':
      return `Code checked${who}`;
    case 'Signature':
      return `Signature${who}`;
    case 'Photo':
      return `Photo${who}`;
    default:
      return `${proof.type}${who}`;
  }
}

/** One delivery, start to finish: who and where, then a guided set of steps driven by what the server says is possible. */
export default function AgentAssignmentPage() {
  const { id = '' } = useParams();
  const [reloadKey, setReloadKey] = useState(0);
  const [sheet, setSheet] = useState<Sheet>(null);
  const action = useGuardedAction();
  const result = useStableApi<AgentAssignmentResponse>(deliveryApi.myAssignment(id), reloadKey);
  const reload = () => setReloadKey((k) => k + 1);

  const a = result.data;
  if (!a) {
    return (
      <div className="space-y-4">
        <Link to="/agent" className="inline-flex min-h-11 items-center text-base font-semibold text-brand-700">
          ← Today
        </Link>
        {result.loading && <p className="text-base text-slate-500">Loading…</p>}
        {result.error && <Alert>{result.httpStatus === 404 ? 'This delivery is not available to you. Go back to your list.' : result.error}</Alert>}
      </div>
    );
  }

  const stage = agentStage(a);
  const steps = deliverySteps(a);
  const next = nextDeliveryStep(a);
  const open = stage === 'assigned' || stage === 'deliver';
  const call = telHref(a.receiverPhone);
  const map = mapsUrl(a.receiverAddress, a.receiverArea);

  /** Runs a simple action and then reloads, whether it worked or not, so the screen always shows the truth. */
  async function act(run: () => Promise<unknown>, success: string) {
    await action.run(run, success);
    reload();
  }

  return (
    <div className="space-y-4">
      <Link to="/agent" className="inline-flex min-h-11 items-center text-base font-semibold text-brand-700">
        ← Today
      </Link>

      <header>
        <div className="flex items-start justify-between gap-2">
          <h1 className="font-mono text-2xl font-bold tracking-tight">{a.trackingNumber}</h1>
          <StatusChip status={a.status} />
        </div>
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <CodBadge amount={a.codAmount} />
          {a.attemptNumber > 1 && <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-700">Attempt {a.attemptNumber}</span>}
        </div>
      </header>

      {action.error && <Alert>{action.error}</Alert>}
      {action.notice && <Alert tone="success">{action.notice}</Alert>}
      {result.error && <Alert>{result.error}</Alert>}
      {a.failureReason && <Alert>Delivery failed: {a.failureReason}</Alert>}

      <Panel title="Receiver">
        <p className="text-xl font-semibold text-slate-900">{a.receiverName}</p>
        {open && a.receiverAddress ? <p className="mt-1 text-base text-slate-800">{a.receiverAddress}</p> : <p className="mt-1 text-base text-slate-800">{a.receiverArea}</p>}
        {open && a.receiverInstructions && (
          <p className="mt-2 rounded-lg bg-slate-50 p-3 text-base text-slate-700">
            <span className="font-semibold">Directions: </span>
            {a.receiverInstructions}
          </p>
        )}
        {open && (call || map) && (
          <div className="mt-3 grid grid-cols-2 gap-3">
            {call ? (
              <BigLink href={call} aria-label={`Call ${a.receiverName}`}>
                Call {a.receiverPhone}
              </BigLink>
            ) : (
              <span />
            )}
            {map && (
              <BigLink href={map} target="_blank" rel="noopener noreferrer">
                Open in maps
              </BigLink>
            )}
          </div>
        )}
      </Panel>

      {a.codAmount > 0 && (
        <Panel title="Cash on delivery">
          <p className="text-3xl font-bold text-amber-900">{formatNpr(a.codAmount)}</p>
          {a.codCollected ? (
            <p className="mt-1 text-base font-semibold text-emerald-700">Collected{a.codCollectedAmount ? `: ${formatNpr(a.codCollectedAmount)}` : ''}</p>
          ) : (
            <p className="mt-1 text-base text-slate-600">Collect exactly this amount from the receiver before you hand over the parcel.</p>
          )}
          {stage === 'deliver' && !a.codCollected && (
            <BigButton className="mt-3" disabled={action.busy} onClick={() => setSheet('cash')}>
              Collect cash
            </BigButton>
          )}
        </Panel>
      )}

      <Panel title="Parcel">
        <dl className="divide-y divide-slate-100">
          <InfoRow label="Contents">{a.description}</InfoRow>
          <InfoRow label="Pieces">{a.quantity}</InfoRow>
          <InfoRow label="Weight">{formatWeight(a.weightKg)}</InfoRow>
          <InfoRow label="Service">{serviceLabel(a.serviceType)}</InfoRow>
          <InfoRow label="Given to you">{formatNepalDateTime(a.assignedAtUtc)}</InfoRow>
        </dl>
      </Panel>

      {stage === 'deliver' && (
        <Panel title="To finish this delivery">
          <ol className="space-y-2">
            {steps.map((step) => (
              <li key={step.key} className={`flex items-center gap-3 rounded-xl p-3 text-base ${step.done ? 'bg-emerald-50 text-emerald-900' : step.key === next ? 'bg-brand-50 font-semibold text-brand-900' : 'bg-slate-50 text-slate-700'}`}>
                <span aria-hidden className="text-lg">
                  {step.done ? '✓' : step.key === next ? '→' : '○'}
                </span>
                <span>
                  {step.label}
                  <span className="sr-only">{step.done ? ' (done)' : step.key === next ? ' (next)' : ' (to do)'}</span>
                </span>
              </li>
            ))}
          </ol>
        </Panel>
      )}

      {a.proofs.length > 0 && (
        <Panel title="Proof recorded">
          <ul className="space-y-1 text-base">
            {a.proofs.map((p) => (
              <li key={p.id} className="flex justify-between gap-3">
                <span>{proofLabel(p)}</span>
                <span className="text-sm text-slate-500">{formatNepalDateTime(p.capturedAtUtc)}</span>
              </li>
            ))}
          </ul>
        </Panel>
      )}

      {stage === 'deliver' && (
        <Panel title="Proof of delivery">
          <ProofPanel assignment={a} action={action} onRecorded={reload} />
        </Panel>
      )}

      {stage === 'completed' && <Alert tone="success">Delivered{a.completedAtUtc ? ` on ${formatNepalDateTime(a.completedAtUtc)}` : ''}. Well done.</Alert>}
      {stage === 'closed' && <Alert tone="info">This delivery was cancelled by a manager.</Alert>}

      {/* The main buttons stay in reach of a thumb, just above the bottom navigation. */}
      <div className="fixed inset-x-0 bottom-[calc(3.5rem+env(safe-area-inset-bottom))] z-10 border-t border-slate-200 bg-white/95 px-4 py-3 backdrop-blur">
        <div className="mx-auto max-w-xl space-y-2">
          {stage === 'assigned' && (
            <div className="grid grid-cols-2 gap-3">
              {a.status === 'Assigned' ? (
                <BigButton variant="secondary" disabled={action.busy} onClick={() => void act(() => api.postEmpty(deliveryApi.accept(a.id)), 'Accepted.')}>
                  Accept
                </BigButton>
              ) : (
                <span />
              )}
              <BigButton disabled={action.busy || !a.canStart} onClick={() => void act(() => api.postEmpty(deliveryApi.start(a.id)), 'Delivery started.')}>
                Start delivery
              </BigButton>
            </div>
          )}
          {stage === 'deliver' && (
            <div className="grid grid-cols-2 gap-3">
              <BigButton variant="dangerOutline" disabled={action.busy || !a.canFail} onClick={() => setSheet('fail')}>
                Delivery failed
              </BigButton>
              <BigButton disabled={action.busy || !a.canComplete} onClick={() => setSheet('complete')}>
                Confirm delivery
              </BigButton>
            </div>
          )}
          {stage === 'failed' && (
            <BigButton disabled={action.busy || !a.canReturnToWarehouse} onClick={() => setSheet('return')}>
              Return to warehouse
            </BigButton>
          )}
          {(stage === 'completed' || stage === 'closed') && <BigLink href="/agent">Back to today</BigLink>}
          {stage === 'deliver' && !a.canComplete && <p className="text-center text-xs text-slate-500">Confirm delivery turns on after {a.codAmount > 0 && !a.codCollected ? 'you collect the cash and ' : ''}record a proof.</p>}
        </div>
      </div>

      <FailSheet open={sheet === 'fail'} assignment={a} action={action} onClose={() => setSheet(null)} onDone={reload} />
      <CollectCashSheet open={sheet === 'cash'} assignment={a} action={action} onClose={() => setSheet(null)} onDone={reload} />
      <ReturnSheet open={sheet === 'return'} assignment={a} action={action} onClose={() => setSheet(null)} onDone={reload} />
      <ConfirmDialog
        open={sheet === 'complete'}
        title="Confirm delivery"
        message={`Confirm that ${a.trackingNumber} was handed to ${a.receiverName}. This cannot be undone.`}
        confirmLabel="Yes, it was delivered"
        onConfirm={async () => {
          try {
            await api.post(deliveryApi.complete(a.id), {});
          } finally {
            reload();
          }
        }}
        onClose={() => setSheet(null)}
      />
    </div>
  );
}
