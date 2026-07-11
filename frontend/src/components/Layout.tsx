import { useEffect, useState } from 'react';
import { Link, NavLink, Outlet, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { isAdmin, useAuthStore } from '../lib/auth';
import { api } from '../lib/api';
import clsx from 'clsx';

type NavItem = { to: string; label: string; badge?: number };
type NavGroup = { title?: string; items: NavItem[]; collapsible?: boolean };

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

  const INSIGHT_PATHS = ['/map', '/competitors', '/trends'];
  const [insightsOpen, setInsightsOpen] = useState(INSIGHT_PATHS.some((p) => pathname.startsWith(p)));

  useEffect(() => {
    setDrawerOpen(false);
  }, [pathname]);

  if (!user) return null;
  const admin = isAdmin(user);

  // Navbar agrupado por función (qué hace cada cosa) en vez de "operación + Admin".
  const groups: NavGroup[] = admin
    ? [
        { items: [{ to: '/admin', label: 'Hoy' }] },
        { title: 'Leads', items: [
          { to: '/leads', label: 'Leads' },
          { to: '/conversations', label: 'Conversaciones', badge: unread.data },
        ] },
        { title: 'Captación', items: [
          { to: '/leads/search', label: 'Capturar de Maps' },
          { to: '/pipeline', label: 'Captación' },
        ] },
        { title: 'Canales', items: [
          { to: '/connect', label: 'WhatsApp' },
          { to: '/instagram/accounts', label: 'Cuentas IG' },
          { to: '/instagram/follow', label: 'Auto-follow IG' },
        ] },
        { title: 'Contenido', items: [
          { to: '/inspiracion', label: 'Inspiración' },
          { to: '/posteos', label: 'Posteos' },
          { to: '/calendario', label: 'Calendario' },
          { to: '/warmr', label: 'Cola Warmr' },
          { to: '/seo', label: 'SEO / Contenido' },
          { to: '/audio-analytics', label: 'Audios y estrategias' },
        ] },
        { title: 'Insights', collapsible: true, items: [
          { to: '/map', label: 'Mapa' },
          { to: '/competitors', label: 'Competencia' },
          { to: '/trends', label: 'Tendencias' },
        ] },
        { title: 'Configuración', items: [
          { to: '/sellers', label: 'Vendedores' },
          { to: '/objetivos', label: 'Objetivos' },
          { to: '/products', label: 'Aplicaciones' },
          { to: '/reglas-ia', label: 'Reglas IA' },
          { to: '/onboarding-apps', label: 'Onboarding apps' },
          { to: '/transcripcion', label: 'Transcripción' },
          { to: '/digest', label: 'Resumen diario' },
          { to: '/seguimientos', label: 'Seguimientos' },
          { to: '/voice-test', label: 'Nota de voz (prueba)' },
        ] },
        { title: 'Ayuda', items: [{ to: '/manual', label: 'Manual' }] },
      ]
    : [
        { items: [{ to: '/dashboard', label: 'Hoy' }] },
        { title: 'Leads', items: [
          { to: '/leads', label: 'Mis leads' },
          { to: '/conversations', label: 'Conversaciones', badge: unread.data },
        ] },
        { title: 'Captación', items: [{ to: '/leads/search', label: 'Capturar de Maps' }] },
        { title: 'Canales', items: [{ to: '/connect', label: 'WhatsApp' }] },
        { title: 'Ayuda', items: [{ to: '/manual', label: 'Manual' }] },
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
      <nav className="flex-1 py-3 space-y-1 px-3 overflow-y-auto">
        {groups.map((g, gi) => {
          if (g.collapsible) {
            return (
              <div key={g.title ?? gi} className="pt-2">
                <button
                  onClick={() => setInsightsOpen((o) => !o)}
                  className="flex items-center justify-between rounded-md px-3 py-1.5 w-full hover:bg-slate-800">
                  <span className="text-[10px] uppercase tracking-wider text-slate-500">{g.title}</span>
                  <span className="text-xs text-slate-500">{insightsOpen ? '▾' : '▸'}</span>
                </button>
                {insightsOpen && (
                  <div className="ml-3 border-l border-slate-800 pl-2 space-y-1 mt-1">
                    {g.items.map((l) => <NavRow key={l.to} item={l} pathname={pathname} small />)}
                  </div>
                )}
              </div>
            );
          }
          return (
            <div key={g.title ?? gi} className={gi > 0 ? 'pt-2' : ''}>
              {g.title && (
                <div className="px-3 pt-1 pb-1 text-[10px] uppercase tracking-wider text-slate-500">{g.title}</div>
              )}
              {g.items.map((l) => <NavRow key={l.to} item={l} pathname={pathname} />)}
            </div>
          );
        })}
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
