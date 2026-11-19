import { useState, type FormEvent } from 'react';
import { Link, Navigate, useLocation, useNavigate } from 'react-router';
import { useAuth } from '../auth/useAuth';
import { Alert, PrimaryButton, TextField } from '../components/form';
import { validateRegister } from '../lib/accountForms';
import { ApiError } from '../lib/apiClient';
import { safeReturnPath } from '../lib/auth';
import type { FormErrors } from '../lib/booking';
import { problemToFieldErrors, problemSummary } from '../lib/problems';
import { register } from '../lib/session';

export default function RegisterPage() {
  const auth = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const returnTo = safeReturnPath((location.state as { from?: unknown } | null)?.from);

  const [form, setForm] = useState({ fullName: '', email: '', phoneNumber: '', password: '' });
  const [errors, setErrors] = useState<FormErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (auth.isAuthenticated) {
    return <Navigate to={returnTo} replace />;
  }

  const set = (key: keyof typeof form) => (value: string) => setForm((f) => ({ ...f, [key]: value }));

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const found = validateRegister(form);
    setErrors(found);
    setFormError(null);
    if (Object.keys(found).length > 0) return;

    setBusy(true);
    try {
      await register({
        fullName: form.fullName.trim(),
        email: form.email.trim(),
        phoneNumber: form.phoneNumber.trim(),
        password: form.password,
      });
      navigate(returnTo, { replace: true });
    } catch (err) {
      if (err instanceof ApiError) {
        setErrors(problemToFieldErrors(err.problem));
        setFormError(problemSummary(err.problem));
      } else {
        setFormError('Could not reach the server. Please try again shortly.');
      }
      setBusy(false);
    }
  }

  return (
    <div className="mx-auto max-w-md">
      <h1 className="text-2xl font-bold tracking-tight">Create your account</h1>
      <p className="mt-2 text-sm text-slate-600">A free account lets you book parcels online and track your bookings.</p>

      <form onSubmit={handleSubmit} noValidate className="mt-6 space-y-4 rounded-xl border border-slate-200 bg-white p-6 shadow-sm">
        {formError && <Alert>{formError}</Alert>}
        <TextField id="fullName" label="Full name" autoComplete="name" required maxLength={150} value={form.fullName} onChange={set('fullName')} error={errors.fullName} />
        <TextField id="email" label="Email" type="email" autoComplete="email" required value={form.email} onChange={set('email')} error={errors.email} />
        <TextField
          id="phoneNumber"
          label="Phone number"
          type="tel"
          autoComplete="tel"
          inputMode="tel"
          required
          placeholder="98XXXXXXXX"
          value={form.phoneNumber}
          onChange={set('phoneNumber')}
          error={errors.phoneNumber}
        />
        <TextField
          id="password"
          label="Password"
          type="password"
          autoComplete="new-password"
          required
          hint="At least 10 characters with upper-case, lower-case and a digit."
          value={form.password}
          onChange={set('password')}
          error={errors.password}
        />
        <PrimaryButton type="submit" disabled={busy} className="w-full">
          {busy ? 'Creating account…' : 'Create account'}
        </PrimaryButton>
      </form>

      <p className="mt-4 text-center text-sm text-slate-600">
        Already registered?{' '}
        <Link to="/login" state={location.state} className="font-medium text-brand-700 underline">
          Sign in
        </Link>
      </p>
    </div>
  );
}
