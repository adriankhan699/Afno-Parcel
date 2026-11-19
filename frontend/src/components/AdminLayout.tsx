import { NavLink, Outlet } from 'react-router';
import { useAuth } from '../auth/useAuth';
import { canManageDelivery, canManageStaff, canScan, canViewFinance, canViewNetworkAdmin, canViewNotifications, isFinanceOnly } from '../lib/auth';
import FindByScan from './FindByScan';

const tabClass = ({ isActive }: { isActive: boolean }) =>
  `whitespace-nowrap border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
    isActive ? 'border-brand-600 text-brand-700' : 'border-transparent text-slate-600 hover:border-slate-300 hover:text-slate-900'
  }`;

/**
 * The admin area: a sub-navigation for branches, warehouses, staff, inventory and the scan log, and for the finance pages
 * (payments, reports, cash on delivery). An accountant sees the finance pages and nothing else.
 */
export default function AdminLayout() {
  const auth = useAuth();
  const roles = auth.user?.roles;
  const financeOnly = isFinanceOnly(roles);

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight">{financeOnly ? 'Finance' : 'Network administration'}</h1>
          <p className="mt-1 text-sm text-slate-600">
            {financeOnly
              ? 'Payment transactions, payment reports, and the cash on delivery that agents hand in.'
              : 'Branches, warehouses, staff, delivery agents, parcels waiting to move, payments, notifications, and the scan log.'}
          </p>
        </div>
        <FindByScan className="w-full sm:w-96" />
      </div>

      <nav aria-label="Admin" className="flex flex-wrap gap-1 border-b border-slate-200">
        {canViewNetworkAdmin(roles) && (
          <>
            <NavLink to="/admin/branches" className={tabClass}>
              Branches
            </NavLink>
            <NavLink to="/admin/warehouses" className={tabClass}>
              Warehouses
            </NavLink>
            {canManageStaff(roles) && (
              <NavLink to="/admin/staff" className={tabClass}>
                Staff
              </NavLink>
            )}
            <NavLink to="/admin/inventory" className={tabClass}>
              Waiting parcels
            </NavLink>
            <NavLink to="/admin/scan-log" className={tabClass}>
              Scan log
            </NavLink>
          </>
        )}
        {canManageDelivery(roles) && (
          <>
            <NavLink to="/admin/agents" className={tabClass}>
              Delivery agents
            </NavLink>
            <NavLink to="/admin/delivery" className={tabClass}>
              Delivery
            </NavLink>
            <NavLink to="/admin/agent-performance" className={tabClass}>
              Agent performance
            </NavLink>
          </>
        )}
        {canViewFinance(roles) && (
          <>
            <NavLink to="/admin/payments" className={tabClass}>
              Payments
            </NavLink>
            <NavLink to="/admin/payment-reports" className={tabClass}>
              Payment reports
            </NavLink>
            <NavLink to="/admin/cod-cash" className={tabClass}>
              Cash on delivery
            </NavLink>
          </>
        )}
        {canViewNotifications(roles) && (
          <NavLink to="/admin/notifications" className={tabClass}>
            Notifications
          </NavLink>
        )}
        {canScan(roles) && (
          <NavLink to="/scan" className={`${tabClass({ isActive: false })} ml-auto`}>
            Open scanner →
          </NavLink>
        )}
      </nav>

      <Outlet />
    </div>
  );
}
