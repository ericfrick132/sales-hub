import { Link, NavLink, Outlet, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { isAdmin, useAuthStore } from '../lib/auth';
import { api } from '../lib/api';
import clsx from 'clsx';

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

  if (!user) return null;
  const admin = isAdmin(user);

  const sellerLinks: { to: string; label: string; badge?: number }[] = [
    { to: '/dashboard', label: 'Mi dashboard' },
    { to: '/leads', label: 'Mis leads' },
    { to: '/conversations', label: 'Conversaciones', badge: unread.data },
    { to: '/pool', label: 'Pool' },
    { to: '/connect', label: 'WhatsApp' }
  ];
  const adminLinks: { to: string; label: string; badge?: number }[] = [
    { to: '/admin', label: 'Panel admin' },
    { to: '/sellers', label: 'Vendedores' },
    { to: '/products', label: 'Aplicaciones' },
    { to: '/pipeline', label: 'Pipeline' },
    { to: '/map', label: 'Mapa' },
    { to: '/competitors', label: 'Competencia' },
    { to: '/trends', label: 'Tendencias' }
  ];

  return (
    <div className="min-h-screen flex">
      <aside className="w-60 bg-slate-900 text-slate-100 flex flex-col">
        <div className="px-6 py-5 border-b border-slate-800">
          <Link to="/" className="text-xl font-bold">SalesHub</Link>
          <div className="text-xs text-slate-400 mt-1">{user.displayName}</div>
        </div>
        <nav className="flex-1 py-4 space-y-1 px-3">
          {(admin ? adminLinks : []).concat(sellerLinks).map((l) => (
            <NavLink
              key={l.to} to={l.to}
              className={({ isActive }) => clsx(
                'flex items-center justify-between rounded-md px-3 py-2 text-sm',
                isActive || pathname.startsWith(l.to)
                  ? 'bg-brand-600 text-white' : 'hover:bg-slate-800 text-slate-300'
              )}>
              <span>{l.label}</span>
              {l.badge !== undefined && l.badge > 0 && (
                <span className="badge bg-rose-500 text-white text-xs">{l.badge}</span>
              )}
            </NavLink>
          ))}
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
