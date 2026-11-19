import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { canBook, canScan } from '../lib/auth';
import { useAuth } from './useAuth';

interface RequireAuthProps {
  children: ReactNode;
  /** Only users who can create bookings (customers and counter staff) may enter. */
  needsBooking?: boolean;
  /** Only users who can scan parcels at a warehouse may enter. */
  needsScan?: boolean;
}

export default function RequireAuth({ children, needsBooking = false, needsScan = false }: RequireAuthProps) {
  const auth = useAuth();
  const location = useLocation();

  if (auth.status === 'loading') {
    return <p className="py-10 text-center text-sm text-slate-500">Checking your session…</p>;
  }

  if (!auth.isAuthenticated) {
    const from = `${location.pathname}${location.search}`;
    return <Navigate to="/login" state={{ from }} replace />;
  }

  if (needsBooking && !canBook(auth.user?.roles)) {
    return (
      <div role="alert" className="mx-auto max-w-xl rounded-lg bg-amber-50 p-4 text-sm text-amber-800">
        Your account role cannot create bookings. Ask a branch manager if you need access.
      </div>
    );
  }

  if (needsScan && !canScan(auth.user?.roles)) {
    return (
      <div role="alert" className="mx-auto max-w-xl rounded-lg bg-amber-50 p-4 text-sm text-amber-800">
        Your account role cannot scan parcels. Ask a branch manager if you need access.
      </div>
    );
  }

  return <>{children}</>;
}
