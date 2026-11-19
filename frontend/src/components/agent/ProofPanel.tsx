import { useEffect, useRef, useState } from 'react';
import { api } from '../../lib/apiClient';
import {
  buildFileProofForm,
  buildOtpProofRequest,
  validateDeliveryCode,
  validateRecipientName,
  validateSignature,
} from '../../lib/delivery';
import { deliveryApi } from '../../lib/deliveryApi';
import { ImageRejectedError, preparePhoto, signatureToBlob } from '../../lib/imageProcessing';
import type { GuardedAction } from '../../lib/useGuardedAction';
import type { AgentAssignmentResponse, DeliveryCodeSentResponse } from '../../types/api';
import { Alert } from '../form';
import { AgentField, BigButton } from './AgentBits';
import SignaturePad from './SignaturePad';

type Tab = 'otp' | 'signature' | 'photo';

const TABS: { key: Tab; label: string }[] = [
  { key: 'otp', label: 'Code' },
  { key: 'signature', label: 'Signature' },
  { key: 'photo', label: 'Photo' },
];

interface ProofPanelProps {
  assignment: AgentAssignmentResponse;
  action: GuardedAction;
  /** Called after the server accepted a proof, so the page can reload the assignment. */
  onRecorded: () => void;
}

/** Record proof of delivery: a one-time code the receiver reads out, a signature, or a photo of the handover. */
export default function ProofPanel({ assignment, action, onRecorded }: ProofPanelProps) {
  const [tab, setTab] = useState<Tab>('otp');

  return (
    <div>
      <div role="tablist" aria-label="Kind of proof" className="grid grid-cols-3 gap-1 rounded-xl bg-slate-100 p-1">
        {TABS.map((t) => (
          <button
            key={t.key}
            id={`proof-tab-${t.key}`}
            role="tab"
            type="button"
            aria-selected={tab === t.key}
            aria-controls={`proof-panel-${t.key}`}
            onClick={() => setTab(t.key)}
            className={`min-h-11 rounded-lg px-2 text-base font-semibold ${tab === t.key ? 'bg-white text-brand-700 shadow-sm' : 'text-slate-600'}`}
          >
            {t.label}
          </button>
        ))}
      </div>

      <div role="tabpanel" id={`proof-panel-${tab}`} aria-labelledby={`proof-tab-${tab}`} className="mt-4">
        {tab === 'otp' && <OtpProof assignment={assignment} action={action} onRecorded={onRecorded} />}
        {tab === 'signature' && <SignatureProof assignment={assignment} action={action} onRecorded={onRecorded} />}
        {tab === 'photo' && <PhotoProof assignment={assignment} action={action} onRecorded={onRecorded} />}
      </div>
    </div>
  );
}

function OtpProof({ assignment, action, onRecorded }: ProofPanelProps) {
  const [sent, setSent] = useState<DeliveryCodeSentResponse | null>(null);
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [errors, setErrors] = useState<{ code?: string; name?: string }>({});

  async function sendCode() {
    const result = await action.run(() => api.postEmpty<DeliveryCodeSentResponse>(deliveryApi.deliveryCode(assignment.id)));
    if (result.ok) setSent(result.value);
  }

  async function verify() {
    const codeError = validateDeliveryCode(code);
    const nameError = validateRecipientName(name);
    setErrors({ code: codeError ?? undefined, name: nameError ?? undefined });
    if (codeError || nameError) return;
    const done = await action.run(() => api.post(deliveryApi.proofOtp(assignment.id), buildOtpProofRequest(code, name)), 'Code accepted. Proof saved.');
    if (done.ok) {
      setCode('');
      onRecorded();
    }
  }

  return (
    <div className="space-y-4">
      <p className="text-base text-slate-700">We send a 6-digit code by SMS to the receiver. Ask them to read it out to you.</p>
      <BigButton variant="secondary" disabled={action.busy} onClick={() => void sendCode()}>
        {sent ? 'Send the code again' : 'Send code to receiver'}
      </BigButton>
      {sent && <Alert tone="info">{sent.message}</Alert>}
      <AgentField
        id="delivery-code"
        label="Code the receiver read out"
        inputMode="numeric"
        autoComplete="one-time-code"
        maxLength={6}
        placeholder="123456"
        value={code}
        onChange={(value) => setCode(value.replace(/\D/g, ''))}
        error={errors.code}
      />
      <AgentField id="otp-name" label="Receiver's name (optional)" autoComplete="off" value={name} onChange={setName} error={errors.name} />
      <BigButton disabled={action.busy} onClick={() => void verify()}>
        {action.busy ? 'Checking…' : 'Check code and save proof'}
      </BigButton>
    </div>
  );
}

function SignatureProof({ assignment, action, onRecorded }: ProofPanelProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [hasInk, setHasInk] = useState(false);
  const [clearKey, setClearKey] = useState(0);
  const [name, setName] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});

  async function save() {
    const found = validateSignature(hasInk, name);
    setErrors(found);
    const canvas = canvasRef.current;
    if (Object.keys(found).length > 0 || !canvas) return;

    const done = await action.run(async () => {
      const blob = await signatureToBlob(canvas);
      return api.postForm(deliveryApi.proofFileUpload(assignment.id), buildFileProofForm('Signature', blob, 'signature.png', name));
    }, 'Signature saved.');
    if (done.ok) {
      setClearKey((k) => k + 1);
      setName('');
      onRecorded();
    }
  }

  return (
    <div className="space-y-4">
      <div>
        <p className="mb-2 text-base text-slate-700">Ask the receiver to sign in the box.</p>
        <SignaturePad canvasRef={canvasRef} onInkChange={setHasInk} clearKey={clearKey} invalid={Boolean(errors.signature)} />
        {errors.signature && (
          <p role="alert" className="mt-1 text-sm text-red-700">
            {errors.signature}
          </p>
        )}
      </div>
      <BigButton variant="secondary" disabled={action.busy || !hasInk} onClick={() => setClearKey((k) => k + 1)}>
        Clear signature
      </BigButton>
      <AgentField id="signer-name" label="Name of the person who signed" required autoComplete="off" value={name} onChange={setName} error={errors.recipientName} />
      <BigButton disabled={action.busy} onClick={() => void save()}>
        {action.busy ? 'Saving…' : 'Save signature'}
      </BigButton>
    </div>
  );
}

function PhotoProof({ assignment, action, onRecorded }: ProofPanelProps) {
  const [photo, setPhoto] = useState<Blob | null>(null);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [preparing, setPreparing] = useState(false);
  const [name, setName] = useState('');
  const [nameError, setNameError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // Show the prepared photo, and let go of its memory when it is replaced or the screen closes.
  useEffect(() => {
    if (!photo) return;
    const url = URL.createObjectURL(photo);
    // eslint-disable-next-line react-hooks/set-state-in-effect -- the object URL only exists once the effect has made it
    setPreviewUrl(url);
    return () => URL.revokeObjectURL(url);
  }, [photo]);

  async function handlePicked(file: File | undefined) {
    setProblem(null);
    setPhoto(null);
    setPreviewUrl(null);
    if (!file) return;
    setPreparing(true);
    try {
      setPhoto(await preparePhoto(file));
    } catch (err) {
      setProblem(err instanceof ImageRejectedError ? err.message : 'That photo could not be prepared. Try again.');
    } finally {
      setPreparing(false);
    }
  }

  async function upload() {
    const nameProblem = validateRecipientName(name);
    setNameError(nameProblem);
    if (!photo || nameProblem) return;
    const done = await action.run(
      () => api.postForm(deliveryApi.proofFileUpload(assignment.id), buildFileProofForm('Photo', photo, 'photo.jpg', name)),
      'Photo saved.',
    );
    if (done.ok) {
      setPhoto(null);
      setPreviewUrl(null);
      setName('');
      if (inputRef.current) inputRef.current.value = '';
      onRecorded();
    }
  }

  return (
    <div className="space-y-4">
      <p className="text-base text-slate-700">Take a clear photo of the parcel with the receiver, or at the door if nobody is home.</p>
      <div>
        <label htmlFor="proof-photo" className="mb-1 block text-sm font-medium text-slate-700">
          Photo
        </label>
        <input
          ref={inputRef}
          id="proof-photo"
          type="file"
          accept="image/*"
          capture="environment"
          disabled={action.busy || preparing}
          onChange={(e) => void handlePicked(e.target.files?.[0])}
          className="block min-h-12 w-full rounded-xl border border-slate-300 bg-white p-2 text-base file:mr-3 file:min-h-10 file:rounded-lg file:border-0 file:bg-brand-600 file:px-4 file:py-2 file:font-semibold file:text-white"
        />
      </div>
      {preparing && <p className="text-sm text-slate-600">Preparing photo…</p>}
      {problem && <Alert>{problem}</Alert>}
      {previewUrl && <img src={previewUrl} alt="Preview of the photo that will be uploaded" className="max-h-72 w-full rounded-xl border border-slate-200 object-contain" />}
      <AgentField id="photo-name" label="Receiver's name (optional)" autoComplete="off" value={name} onChange={setName} error={nameError ?? undefined} />
      <BigButton disabled={action.busy || preparing || !photo} onClick={() => void upload()}>
        {action.busy ? 'Uploading…' : 'Upload photo'}
      </BigButton>
    </div>
  );
}
