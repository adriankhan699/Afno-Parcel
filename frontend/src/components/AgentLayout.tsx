import { NavLink, Outlet, useNavigate } from 'react-router';
import { useAuth } from '../auth/useAuth';
import { env } from '../config/env';
import { logout } from '../lib/session';

const tabClass = ({ isActive }: { isActive: boolean }) =>
  `flex min-h-14 flex-1 flex-col items-center justify-center gap-0.5 text-sm font-semibold ${isActive ? 'text-brand-700' : 'text-slate-500'}`;

function Icon({ children }: { children: React.ReactNode }) {
  return (
    <svg aria-hidden viewBox="0 0 24 24" className="h-6 w-6" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      {children}
    </svg>
  );
}

/**
 * The delivery agent's own, phone-sized layout: a slim header and a bottom navigation (Today, Cash, History, Me). There is no link
 * to the customer pages, the scanner or the admin area here. The app never uses the device's location.
 */
export default function AgentLayout() {
  const auth = useAuth();
  const navigate = useNavigate();

  async function handleSignOut() {
    await logout();
    navigate('/');
  }

  return (
    <div className="flex min-h-screen flex-col bg-slate-50">
      <header className="sticky top-0 z-10 border-b border-slate-200 bg-white">
        <div className="mx-auto flex max-w-xl items-center justify-between gap-3 px-4 py-2">
          <div className="flex min-w-0 items-center gap-2 font-semibold text-brand-700">
            <img src="/favicon.svg" alt="" className="h-8 w-8 shrink-0" />
            <span className="truncate text-base">{env.appName} · Agent</span>
          </div>
          <button type="button" onClick={() => void handleSignOut()} className="min-h-11 shrink-0 rounded-lg px-3 text-sm font-semibold text-slate-600 hover:bg-slate-100">
            Sign out{auth.user ? <span className="sr-only"> {auth.user.fullName}</span> : null}
          </button>
        </div>
      </header>

      <main className="mx-auto w-full max-w-xl flex-1 px-4 pb-44 pt-4">
        <Outlet />
      </main>

      <nav aria-label="Agent" className="fixed inset-x-0 bottom-0 z-20 border-t border-slate-200 bg-white pb-[env(safe-area-inset-bottom)]">
        <div className="mx-auto flex max-w-xl">
          <NavLink to="/agent" end className={tabClass}>
            <Icon>
              <path d="M3 7l9-4 9 4-9 4-9-4z" />
              <path d="M3 7v10l9 4 9-4V7" />
            </Icon>
            Today
          </NavLink>
          <NavLink to="/agent/cash" className={tabClass}>
            <Icon>
              <rect x="3" y="6" width="18" height="12" rx="2" />
              <circle cx="12" cy="12" r="2.5" />
              <path d="M6.5 9.5v5M17.5 9.5v5" />
            </Icon>
            Cash
          </NavLink>
          <NavLink to="/agent/history" className={tabClass}>
            <Icon>
              <circle cx="12" cy="12" r="9" />
              <path d="M12 7v5l3 2" />
            </Icon>
            History
          </NavLink>
          <NavLink to="/agent/me" className={tabClass}>
            <Icon>
              <circle cx="12" cy="8" r="4" />
              <path d="M4 21c0-4 4-6 8-6s8 2 8 6" />
            </Icon>
            Me
          </NavLink>
        </div>
      </nav>
    </div>
  );
}
