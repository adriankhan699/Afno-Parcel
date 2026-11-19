import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { accessDeniedMessage, type AccessRequirements } from '../lib/auth';
import { useAuth } from './useAuth';

interface RequireAuthProps extends AccessRequirements {
  children: ReactNode;
  /** Only users who can create bookings (customers and counter staff) may enter. */
  needsBooking?: boolean;
  /** Only users who can scan parcels at a warehouse may enter. */
  needsScan?: boolean;
  /** Only staff who can see the admin area (branches, warehouses, inventory, scan log) may enter. */
  needsAdmin?: boolean;
  /** Only delivery agents may enter (the mobile agent app). */
  needsAgent?: boolean;
  /** Only super admins and branch managers may enter (delivery agents, assignments and performance). */
  needsDeliveryManagement?: boolean;
  /** Accountants, branch managers and super admins may enter (payments, reports, cash and settlements). */
  needsFinance?: boolean;
  /** The network pages of the admin area: everyone who may open the admin area except an accountant-only login. */
  needsNetworkView?: boolean;
  /** Super admins and branch managers may enter (messages, templates and opt-outs; a branch manager only reads). */
  needsNotifications?: boolean;
  /** Only customer accounts may enter (their own SMS and WhatsApp choices). */
  needsCustomer?: boolean;
}

export default function RequireAuth({
  children,
  needsBooking = false,
  needsScan = false,
  needsAdmin = false,
  needsAgent = false,
  needsDeliveryManagement = false,
  needsFinance = false,
  needsNetworkView = false,
  needsNotifications = false,
  needsCustomer = false,
}: RequireAuthProps) {
  const auth = useAuth();
  const location = useLocation();

  if (auth.status === 'loading') {
    return <p className="py-10 text-center text-sm text-slate-500">Checking your session…</p>;
  }

  if (!auth.isAuthenticated) {
    const from = `${location.pathname}${location.search}`;
    return <Navigate to="/login" state={{ from }} replace />;
  }

  const denied = accessDeniedMessage(auth.user?.roles, {
    needsBooking,
    needsScan,
    needsAdmin,
    needsAgent,
    needsDeliveryManagement,
    needsFinance,
    needsNetworkView,
    needsNotifications,
    needsCustomer,
  });
  if (denied) {
    return (
      <div role="alert" className="mx-auto max-w-xl rounded-lg bg-amber-50 p-4 text-sm text-amber-800">
        {denied}
      </div>
    );
  }

  return <>{children}</>;
}
