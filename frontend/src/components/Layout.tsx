import { NavLink, Outlet } from 'react-router';
import { env } from '../config/env';
import ApiStatusBadge from './ApiStatusBadge';

const navItems = [
  { to: '/', label: 'Home', end: true },
  { to: '/track', label: 'Track parcel', end: false },
  { to: '/branches', label: 'Branches', end: false },
];

export default function Layout() {
  return (
    <div className="flex min-h-screen flex-col">
      <header className="border-b border-slate-200 bg-white">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-3 px-4 py-3">
          <NavLink to="/" className="flex items-center gap-2 font-semibold text-brand-700">
            <img src="/favicon.svg" alt="" className="h-8 w-8" />
            <span className="text-lg">{env.appName}</span>
          </NavLink>
          <nav aria-label="Main" className="flex flex-wrap gap-1">
            {navItems.map((item) => (
              <NavLink
                key={item.to}
                to={item.to}
                end={item.end}
                className={({ isActive }) =>
                  `rounded-md px-3 py-2 text-sm font-medium transition-colors ${
                    isActive ? 'bg-brand-50 text-brand-700' : 'text-slate-600 hover:bg-slate-100 hover:text-slate-900'
                  }`
                }
              >
                {item.label}
              </NavLink>
            ))}
          </nav>
        </div>
      </header>

      <main className="mx-auto w-full max-w-6xl flex-1 px-4 py-8">
        <Outlet />
      </main>

      <footer className="border-t border-slate-200 bg-white">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-2 px-4 py-4 text-sm text-slate-500">
          <span>© {new Date().getFullYear()} {env.appName}. Delivering across all 7 provinces.</span>
          <ApiStatusBadge />
        </div>
      </footer>
    </div>
  );
}
