import type { ReactNode } from 'react';

interface ToggleRowProps {
  id: string;
  label: string;
  description?: ReactNode;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}

/** An on/off switch with its label and a line of explanation. A native checkbox, so it works with the keyboard and screen readers. */
export default function ToggleRow({ id, label, description, checked, onChange, disabled = false }: ToggleRowProps) {
  return (
    <label htmlFor={id} className={`flex items-start gap-3 rounded-lg border border-slate-200 p-3 ${disabled ? 'opacity-60' : 'cursor-pointer hover:bg-slate-50'}`}>
      <input
        id={id}
        type="checkbox"
        role="switch"
        checked={checked}
        disabled={disabled}
        onChange={(event) => onChange(event.target.checked)}
        aria-describedby={description ? `${id}-description` : undefined}
        className="mt-0.5 h-5 w-5 shrink-0 accent-brand-600"
      />
      <span>
        <span className="block text-sm font-medium text-slate-900">{label}</span>
        {description && (
          <span id={`${id}-description`} className="mt-0.5 block text-xs text-slate-600">
            {description}
          </span>
        )}
      </span>
    </label>
  );
}
