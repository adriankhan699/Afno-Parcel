import type { ButtonHTMLAttributes, InputHTMLAttributes, ReactNode } from 'react';
import { assignmentLabel, assignmentTone } from '../../lib/delivery';
import { formatNpr } from '../../lib/format';

// Building blocks for the phone-sized agent screens: large touch targets (at least 44px, mostly 48px), large type.

type Variant = 'primary' | 'secondary' | 'danger' | 'dangerOutline';

const variants: Record<Variant, string> = {
  primary: 'bg-brand-600 text-white hover:bg-brand-700 active:bg-brand-800',
  secondary: 'border border-slate-300 bg-white text-slate-800 hover:bg-slate-50 active:bg-slate-100',
  danger: 'bg-red-700 text-white hover:bg-red-800',
  dangerOutline: 'border border-red-300 bg-white text-red-800 hover:bg-red-50',
};

interface BigButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
}

export function BigButton({ variant = 'primary', className = '', children, type = 'button', ...props }: BigButtonProps) {
  return (
    <button
      {...props}
      type={type}
      className={`inline-flex min-h-12 w-full items-center justify-center rounded-xl px-4 py-3 text-base font-semibold disabled:cursor-not-allowed disabled:opacity-60 ${variants[variant]} ${className}`}
    >
      {children}
    </button>
  );
}

/** A link that looks like a big button (call, open maps). */
export function BigLink({ variant = 'secondary', className = '', children, ...props }: React.AnchorHTMLAttributes<HTMLAnchorElement> & { variant?: Variant }) {
  return (
    <a
      {...props}
      className={`inline-flex min-h-12 w-full items-center justify-center rounded-xl px-4 py-3 text-base font-semibold ${variants[variant]} ${className}`}
    >
      {children}
    </a>
  );
}

export function StatusChip({ status }: { status: string }) {
  return <span className={`whitespace-nowrap rounded-full px-2.5 py-1 text-xs font-semibold ${assignmentTone(status)}`}>{assignmentLabel(status)}</span>;
}

export function CodBadge({ amount }: { amount: number }) {
  if (amount <= 0) return null;
  return <span className="whitespace-nowrap rounded-full bg-amber-100 px-2.5 py-1 text-xs font-bold text-amber-900">COD {formatNpr(amount)}</span>;
}

export function Panel({ title, children, className = '' }: { title?: string; children: ReactNode; className?: string }) {
  return (
    <section className={`rounded-2xl border border-slate-200 bg-white p-4 shadow-sm ${className}`}>
      {title && <h2 className="text-sm font-semibold uppercase tracking-wide text-slate-500">{title}</h2>}
      <div className={title ? 'mt-2' : ''}>{children}</div>
    </section>
  );
}

export function InfoRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex justify-between gap-4 py-1.5 text-base">
      <dt className="text-slate-500">{label}</dt>
      <dd className="text-right font-medium text-slate-900">{children}</dd>
    </div>
  );
}

const fieldClass =
  'min-h-12 w-full rounded-xl border border-slate-300 bg-white px-4 py-3 text-base outline-none focus:border-brand-600 focus:ring-2 focus:ring-brand-100 disabled:bg-slate-100';

interface AgentFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'onChange'> {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  error?: string;
  hint?: string;
}

/** A large text input with its label, hint and error, wired for screen readers. */
export function AgentField({ id, label, value, onChange, error, hint, ...props }: AgentFieldProps) {
  return (
    <div>
      <label htmlFor={id} className="mb-1 block text-sm font-medium text-slate-700">
        {label}
      </label>
      <input
        {...props}
        id={id}
        name={id}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${id}-error` : hint ? `${id}-hint` : undefined}
        className={`${fieldClass} ${error ? 'border-red-400' : ''}`}
      />
      {hint && !error && (
        <p id={`${id}-hint`} className="mt-1 text-xs text-slate-500">
          {hint}
        </p>
      )}
      {error && (
        <p id={`${id}-error`} role="alert" className="mt-1 text-sm text-red-700">
          {error}
        </p>
      )}
    </div>
  );
}

interface AgentTextAreaProps {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  error?: string;
  maxLength?: number;
  rows?: number;
}

export function AgentTextArea({ id, label, value, onChange, error, maxLength, rows = 3 }: AgentTextAreaProps) {
  return (
    <div>
      <label htmlFor={id} className="mb-1 block text-sm font-medium text-slate-700">
        {label}
      </label>
      <textarea
        id={id}
        name={id}
        value={value}
        rows={rows}
        maxLength={maxLength}
        onChange={(e) => onChange(e.target.value)}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${id}-error` : undefined}
        className={`${fieldClass} ${error ? 'border-red-400' : ''}`}
      />
      {error && (
        <p id={`${id}-error`} role="alert" className="mt-1 text-sm text-red-700">
          {error}
        </p>
      )}
    </div>
  );
}

/** A big on/off switch ("Available for deliveries"). */
export function AvailabilityToggle({ isAvailable, busy, onChange }: { isAvailable: boolean; busy: boolean; onChange: (next: boolean) => void }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={isAvailable}
      disabled={busy}
      onClick={() => onChange(!isAvailable)}
      className="flex min-h-12 w-full items-center justify-between gap-3 rounded-xl border border-slate-300 bg-white px-4 py-2 text-left disabled:opacity-60"
    >
      <span>
        <span className="block text-base font-semibold text-slate-900">{isAvailable ? 'Available for deliveries' : 'Not available'}</span>
        <span className="block text-xs text-slate-500">{isAvailable ? 'Managers can give you new parcels.' : 'Managers will not give you new parcels.'}</span>
      </span>
      <span aria-hidden className={`relative inline-block h-7 w-12 shrink-0 rounded-full transition-colors ${isAvailable ? 'bg-emerald-500' : 'bg-slate-300'}`}>
        <span className={`absolute top-0.5 h-6 w-6 rounded-full bg-white shadow transition-all ${isAvailable ? 'left-[1.375rem]' : 'left-0.5'}`} />
      </span>
    </button>
  );
}
