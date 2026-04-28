import { useState } from 'react';
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

  if (!user) return null;
  const admin = isAdmin(user);

  // Operación: lo que un seller necesita día a día. Admin ve lo mismo más Pool integrado en /leads.
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

  // Configuración + captación: solo admin.
  const adminLinks: NavItem[] = [
    { to: '/pipeline', label: 'Captación' },
    { to: '/sellers', label: 'Vendedores' },
    { to: '/products', label: 'Aplicaciones' }
  ];

  return (
    <div className="min-h-screen flex">
      <aside className="w-60 bg-slate-900 text-slate-100 flex flex-col">
        <div className="px-6 py-5 border-b border-slate-800">
          <Link to="/" className="text-xl font-bold">SalesHub</Link>
          <div className="text-xs text-slate-400 mt-1">{user.displayName}</div>
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
      <main className="flex-1 overflow-y-auto">
        <div className="max-w-7xl mx-auto px-6 py-8">
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
