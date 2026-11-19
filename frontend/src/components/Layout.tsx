import { NavLink, Outlet, useNavigate } from 'react-router';
import { useAuth } from '../auth/useAuth';
import { env } from '../config/env';
import { AGENT_HOME, canBook, canManageOwnNotifications, canScan, canViewAdmin, isDeliveryAgent, isFinanceOnly } from '../lib/auth';
import { logout } from '../lib/session';
import ApiStatusBadge from './ApiStatusBadge';

interface NavItem {
  to: string;
  label: string;
  end?: boolean;
}

const navClass = ({ isActive }: { isActive: boolean }) =>
  `rounded-md px-3 py-2 text-sm font-medium transition-colors ${
    isActive ? 'bg-brand-50 text-brand-700' : 'text-slate-600 hover:bg-slate-100 hover:text-slate-900'
  }`;

export default function Layout() {
  const auth = useAuth();
  const navigate = useNavigate();
  const roles = auth.user?.roles;

  const navItems: NavItem[] = [{ to: '/', label: 'Home', end: true }];
  const agent = auth.isAuthenticated && isDeliveryAgent(roles);
  if (agent) {
    // A delivery agent works in the agent app: no booking, parcel list, scanner or admin links.
    navItems.push({ to: AGENT_HOME, label: 'My deliveries' });
  }
  if (!agent && (!auth.isAuthenticated || canBook(roles))) {
    navItems.push({ to: '/book', label: 'Book a parcel' });
  }
  if (auth.isAuthenticated && !agent) {
    navItems.push({ to: '/parcels', label: auth.isStaff ? 'Parcels' : 'My parcels' });
  }
  if (auth.isAuthenticated && canScan(roles)) {
    navItems.push({ to: '/scan', label: 'Scan' });
  }
  if (auth.isAuthenticated && canViewAdmin(roles)) {
    navItems.push({ to: '/admin', label: isFinanceOnly(roles) ? 'Finance' : 'Admin' });
  }
  navItems.push({ to: '/track', label: 'Track parcel' }, { to: '/branches', label: 'Branches' });

  async function handleSignOut() {
    await logout();
    navigate('/');
  }

  return (
    <div className="flex min-h-screen flex-col">
      {/* Everything except the page content is hidden when printing (receipts and labels). */}
      <header className="border-b border-slate-200 bg-white print:hidden">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-3 px-4 py-3">
          <NavLink to="/" className="flex items-center gap-2 font-semibold text-brand-700">
            <img src="/favicon.svg" alt="" className="h-8 w-8" />
            <span className="text-lg">{env.appName}</span>
          </NavLink>
          <nav aria-label="Main" className="flex flex-wrap items-center gap-1">
            {navItems.map((item) => (
              <NavLink key={item.to} to={item.to} end={item.end} className={navClass}>
                {item.label}
              </NavLink>
            ))}
            <span aria-hidden className="mx-1 hidden h-5 w-px bg-slate-200 sm:inline-block" />
            {auth.status === 'authenticated' && auth.user && (
              <>
                <span className="px-2 text-sm text-slate-600" title={auth.user.email}>
                  {auth.user.fullName}
                </span>
                {canManageOwnNotifications(roles) && (
                  <NavLink to="/account/notifications" className={navClass}>
                    Parcel updates
                  </NavLink>
                )}
                <button
                  type="button"
                  onClick={() => void handleSignOut()}
                  className="rounded-md px-3 py-2 text-sm font-medium text-slate-600 hover:bg-slate-100 hover:text-slate-900"
                >
                  Sign out
                </button>
              </>
            )}
            {auth.status === 'anonymous' && (
              <>
                <NavLink to="/login" className={navClass}>
                  Sign in
                </NavLink>
                <NavLink
                  to="/register"
                  className="rounded-md bg-brand-600 px-3 py-2 text-sm font-semibold text-white hover:bg-brand-700"
                >
                  Register
                </NavLink>
              </>
            )}
          </nav>
        </div>
      </header>

      <main className="mx-auto w-full max-w-6xl flex-1 px-4 py-8 print:max-w-none print:p-0">
        <Outlet />
      </main>

      <footer className="border-t border-slate-200 bg-white print:hidden">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-2 px-4 py-4 text-sm text-slate-500">
          <span>© {new Date().getFullYear()} {env.appName}. Delivering across all 7 provinces.</span>
          <ApiStatusBadge />
        </div>
      </footer>
    </div>
  );
}
