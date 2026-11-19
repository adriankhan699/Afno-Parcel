import type { ButtonHTMLAttributes, ReactNode } from 'react';

const base =
  'w-full rounded-lg border bg-white px-3 py-2 text-sm outline-none focus:border-brand-600 focus:ring-2 focus:ring-brand-100 disabled:bg-slate-100 disabled:text-slate-500';

const inputClass = (invalid: boolean) => `${base} ${invalid ? 'border-red-400' : 'border-slate-300'}`;

interface CommonProps {
  id: string;
  label: string;
  error?: string;
  hint?: string;
  required?: boolean;
  className?: string;
}

function FieldShell({ id, label, error, hint, required, className, children }: CommonProps & { children: ReactNode }) {
  return (
    <div className={className}>
      <label htmlFor={id} className="mb-1 block text-sm font-medium text-slate-700">
        {label}
        {required && (
          <span aria-hidden className="text-brand-600">
            {' '}
            *
          </span>
        )}
      </label>
      {children}
      {hint && !error && (
        <p id={`${id}-hint`} className="mt-1 text-xs text-slate-500">
          {hint}
        </p>
      )}
      {error && (
        <p id={`${id}-error`} role="alert" className="mt-1 text-xs text-red-700">
          {error}
        </p>
      )}
    </div>
  );
}

const describedBy = (id: string, error?: string, hint?: string) => (error ? `${id}-error` : hint ? `${id}-hint` : undefined);

interface TextFieldProps extends CommonProps {
  value: string;
  onChange: (value: string) => void;
  type?: 'text' | 'email' | 'password' | 'tel' | 'number' | 'date' | 'search';
  inputMode?: 'text' | 'numeric' | 'decimal' | 'tel' | 'email';
  maxLength?: number;
  autoComplete?: string;
  placeholder?: string;
  disabled?: boolean;
}

export function TextField({
  id,
  label,
  error,
  hint,
  required,
  className,
  value,
  onChange,
  type = 'text',
  inputMode,
  maxLength,
  autoComplete,
  placeholder,
  disabled,
}: TextFieldProps) {
  return (
    <FieldShell id={id} label={label} error={error} hint={hint} required={required} className={className}>
      <input
        id={id}
        name={id}
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        inputMode={inputMode}
        maxLength={maxLength}
        autoComplete={autoComplete}
        placeholder={placeholder}
        disabled={disabled}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(id, error, hint)}
        aria-required={required || undefined}
        className={inputClass(Boolean(error))}
      />
    </FieldShell>
  );
}

export interface SelectOption {
  value: string;
  label: string;
  disabled?: boolean;
}

interface SelectFieldProps extends CommonProps {
  value: string;
  onChange: (value: string) => void;
  options: SelectOption[];
  placeholder?: string;
  disabled?: boolean;
}

export function SelectField({
  id,
  label,
  error,
  hint,
  required,
  className,
  value,
  onChange,
  options,
  placeholder,
  disabled,
}: SelectFieldProps) {
  return (
    <FieldShell id={id} label={label} error={error} hint={hint} required={required} className={className}>
      <select
        id={id}
        name={id}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(id, error, hint)}
        aria-required={required || undefined}
        className={inputClass(Boolean(error))}
      >
        {placeholder !== undefined && <option value="">{placeholder}</option>}
        {options.map((o) => (
          <option key={o.value} value={o.value} disabled={o.disabled}>
            {o.label}
          </option>
        ))}
      </select>
    </FieldShell>
  );
}

interface TextAreaFieldProps extends CommonProps {
  value: string;
  onChange: (value: string) => void;
  maxLength?: number;
  rows?: number;
  placeholder?: string;
}

export function TextAreaField({
  id,
  label,
  error,
  hint,
  required,
  className,
  value,
  onChange,
  maxLength,
  rows = 3,
  placeholder,
}: TextAreaFieldProps) {
  return (
    <FieldShell id={id} label={label} error={error} hint={hint} required={required} className={className}>
      <textarea
        id={id}
        name={id}
        value={value}
        rows={rows}
        maxLength={maxLength}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(id, error, hint)}
        aria-required={required || undefined}
        className={inputClass(Boolean(error))}
      />
    </FieldShell>
  );
}

export function Alert({ tone = 'error', children }: { tone?: 'error' | 'success' | 'info'; children: ReactNode }) {
  const tones = {
    error: 'bg-red-50 text-red-800',
    success: 'bg-emerald-50 text-emerald-800',
    info: 'bg-blue-50 text-blue-800',
  } as const;
  return (
    <div role={tone === 'error' ? 'alert' : 'status'} className={`rounded-lg p-4 text-sm ${tones[tone]}`}>
      {children}
    </div>
  );
}

export function PrimaryButton({ className = '', children, ...props }: ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button
      {...props}
      className={`rounded-lg bg-brand-600 px-5 py-2.5 text-sm font-semibold text-white hover:bg-brand-700 disabled:cursor-not-allowed disabled:opacity-60 ${className}`}
    >
      {children}
    </button>
  );
}

export function SecondaryButton({ className = '', children, ...props }: ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button
      {...props}
      className={`rounded-lg border border-slate-300 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60 ${className}`}
    >
      {children}
    </button>
  );
}
