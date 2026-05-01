import { useEffect, useState } from 'react';
import { Link, NavLink, Outlet, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { isAdmin, useAuthStore } from '../lib/auth';
import { api } from '../lib/api';
import clsx from 'clsx';

type NavItem = { to: string; label: string; badge?: number };

export default function Layout() {
  const user = useAuthStore((s) => s.user);
  const logout = useAuthStore((s) => s.logout);
  const { pathname } = useLocation();
  const [drawerOpen, setDrawerOpen] = useState(false);

  const unread = useQuery({
    queryKey: ['unread-count'],
    enabled: !!user,
    queryFn: async () => (await api.get<{ count: number }>('/conversations/unread-count')).data.count,
    refetchInterval: 20000
  });

  const insightItems: NavItem[] = [
    { to: '/map', label: 'Mapa' },
    { to: '/competitors', label: 'Competencia' },
    { to: '/trends', label: 'Tendencias' }
  ];
  const insightsOpenInitial = insightItems.some((i) => pathname.startsWith(i.to));
  const [insightsOpen, setInsightsOpen] = useState(insightsOpenInitial);

  useEffect(() => {
    setDrawerOpen(false);
  }, [pathname]);

  if (!user) return null;
  const admin = isAdmin(user);

  const operationLinks: NavItem[] = admin
    ? [
        { to: '/admin', label: 'Hoy' },
        { to: '/leads', label: 'Leads' },
        { to: '/conversations', label: 'Conversaciones', badge: unread.data },
        { to: '/connect', label: 'WhatsApp' }
      ]
    : [
        { to: '/dashboard', label: 'Hoy' },
        { to: '/leads', label: 'Mis leads' }
      ];

  const adminLinks: NavItem[] = [
    { to: '/pipeline', label: 'Captación' },
    { to: '/sellers', label: 'Vendedores' },
    { to: '/products', label: 'Aplicaciones' }
  ];

  const sidebar = (
    <aside
      className={clsx(
        'bg-slate-900 text-slate-100 flex flex-col w-64 md:w-60',
        'fixed inset-y-0 left-0 z-40 transform transition-transform duration-200 md:static md:translate-x-0',
        drawerOpen ? 'translate-x-0' : '-translate-x-full'
      )}>
      <div className="px-6 py-5 border-b border-slate-800 flex items-center justify-between">
        <div>
          <Link to="/" className="text-xl font-bold">SalesHub</Link>
          <div className="text-xs text-slate-400 mt-1">{user.displayName}</div>
        </div>
        <button
          type="button"
          onClick={() => setDrawerOpen(false)}
          className="md:hidden text-slate-400 hover:text-white text-xl leading-none"
          aria-label="Cerrar menú">
          ×
        </button>
      </div>
      <nav className="flex-1 py-4 space-y-1 px-3 overflow-y-auto">
        {operationLinks.map((l) => <NavRow key={l.to} item={l} pathname={pathname} />)}

        {admin && (
          <>
            <div className="px-3 pt-4 pb-1 text-[10px] uppercase tracking-wider text-slate-500">
              Admin
            </div>
            {adminLinks.map((l) => <NavRow key={l.to} item={l} pathname={pathname} />)}

            <button
              onClick={() => setInsightsOpen((o) => !o)}
              className={clsx(
                'flex items-center justify-between rounded-md px-3 py-2 text-sm w-full',
                'hover:bg-slate-800 text-slate-300'
              )}>
              <span>Insights</span>
              <span className="text-xs text-slate-500">{insightsOpen ? '▾' : '▸'}</span>
            </button>
            {insightsOpen && (
              <div className="ml-3 border-l border-slate-800 pl-2 space-y-1">
                {insightItems.map((l) => <NavRow key={l.to} item={l} pathname={pathname} small />)}
              </div>
            )}
          </>
        )}
      </nav>
      <button
        onClick={logout}
        className="m-3 mt-auto text-xs text-slate-400 hover:text-white border border-slate-800 rounded px-3 py-2">
        Salir
      </button>
    </aside>
  );

  return (
    <div className="min-h-screen md:flex">
      <header className="md:hidden sticky top-0 z-30 bg-slate-900 text-slate-100 flex items-center justify-between px-4 py-3 border-b border-slate-800">
        <button
          type="button"
          onClick={() => setDrawerOpen(true)}
          className="p-2 -ml-2 text-slate-200 hover:text-white"
          aria-label="Abrir menú">
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <line x1="3" y1="6" x2="21" y2="6" />
            <line x1="3" y1="12" x2="21" y2="12" />
            <line x1="3" y1="18" x2="21" y2="18" />
          </svg>
        </button>
        <Link to="/" className="text-base font-bold">SalesHub</Link>
        <div className="w-8" />
      </header>

      {drawerOpen && (
        <div
          onClick={() => setDrawerOpen(false)}
          className="md:hidden fixed inset-0 z-30 bg-black/50"
          aria-hidden />
      )}

      {sidebar}

      <main className="flex-1 overflow-y-auto">
        <div className="max-w-7xl mx-auto w-full px-4 py-4 md:px-6 md:py-8">
          <Outlet />
        </div>
      </main>
    </div>
  );
}

function NavRow({ item, pathname, small }: { item: NavItem; pathname: string; small?: boolean }) {
  return (
    <NavLink
      to={item.to}
      className={({ isActive }) => clsx(
        'flex items-center justify-between rounded-md px-3 py-2',
        small ? 'text-xs' : 'text-sm',
        isActive || pathname.startsWith(item.to)
          ? 'bg-brand-600 text-white' : 'hover:bg-slate-800 text-slate-300'
      )}>
      <span>{item.label}</span>
      {item.badge !== undefined && item.badge > 0 && (
        <span className="badge bg-rose-500 text-white text-xs">{item.badge}</span>
      )}
    </NavLink>
  );
}
