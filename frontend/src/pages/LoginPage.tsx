import { useState, type FormEvent } from 'react';
import { Link, Navigate, useLocation, useNavigate } from 'react-router';
import { useAuth } from '../auth/useAuth';
import { Alert, PrimaryButton, TextField } from '../components/form';
import { validateLogin } from '../lib/accountForms';
import { ApiError } from '../lib/apiClient';
import { safeReturnPath } from '../lib/auth';
import type { FormErrors } from '../lib/booking';
import { login } from '../lib/session';

export default function LoginPage() {
  const auth = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const returnTo = safeReturnPath((location.state as { from?: unknown } | null)?.from);

  const [form, setForm] = useState({ email: '', password: '' });
  const [errors, setErrors] = useState<FormErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (auth.isAuthenticated) {
    return <Navigate to={returnTo} replace />;
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const found = validateLogin(form);
    setErrors(found);
    setFormError(null);
    if (Object.keys(found).length > 0) return;

    setBusy(true);
    try {
      await login({ email: form.email.trim(), password: form.password });
      navigate(returnTo, { replace: true });
    } catch (err) {
      setFormError(err instanceof ApiError ? err.message : 'Could not reach the server. Please try again shortly.');
      setBusy(false);
    }
  }

  return (
    <div className="mx-auto max-w-md">
      <h1 className="text-2xl font-bold tracking-tight">Sign in</h1>
      <p className="mt-2 text-sm text-slate-600">Sign in to book parcels and see your bookings.</p>

      <form onSubmit={handleSubmit} noValidate className="mt-6 space-y-4 rounded-xl border border-slate-200 bg-white p-6 shadow-sm">
        {formError && <Alert>{formError}</Alert>}
        <TextField
          id="email"
          label="Email"
          type="email"
          autoComplete="email"
          required
          value={form.email}
          onChange={(email) => setForm((f) => ({ ...f, email }))}
          error={errors.email}
        />
        <TextField
          id="password"
          label="Password"
          type="password"
          autoComplete="current-password"
          required
          value={form.password}
          onChange={(password) => setForm((f) => ({ ...f, password }))}
          error={errors.password}
        />
        <PrimaryButton type="submit" disabled={busy} className="w-full">
          {busy ? 'Signing in…' : 'Sign in'}
        </PrimaryButton>
      </form>

      <p className="mt-4 text-center text-sm text-slate-600">
        New here?{' '}
        <Link to="/register" state={location.state} className="font-medium text-brand-700 underline">
          Create an account
        </Link>
      </p>
    </div>
  );
}
