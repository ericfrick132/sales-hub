import type { ReactNode } from 'react';
import { Navigate, useLocation, useSearchParams } from 'react-router-dom';
import clsx from 'clsx';

export type Tab = { key: string; label: string; element: ReactNode; hidden?: boolean };

/**
 * Pantalla con tabs. La tab activa vive en la URL (`?view=` arriba, `?sub=` para las
 * sub-tabs) así los links y el back del navegador funcionan. Solo se monta la tab
 * activa: las páginas de adentro son las de siempre y no disparan sus queries hasta
 * que se abren.
 */
export default function TabbedPage({
  title,
  tabs,
  param = 'view',
}: {
  title?: string;
  tabs: Tab[];
  param?: 'view' | 'sub';
}) {
  const [params, setParams] = useSearchParams();
  const visible = tabs.filter((t) => !t.hidden);
  const current = visible.find((t) => t.key === params.get(param)) ?? visible[0];

  function select(key: string) {
    // Cambiar de tab limpia los filtros de la página anterior (y la sub-tab si es la de arriba).
    const sp = new URLSearchParams();
    if (param === 'sub') { const v = params.get('view'); if (v) sp.set('view', v); }
    sp.set(param, key);
    setParams(sp);
  }

  return (
    <div className={param === 'view' ? 'space-y-5' : 'space-y-4'}>
      {title && <h1 className="text-xl md:text-2xl font-bold">{title}</h1>}
      {visible.length > 1 && (
        <div className={clsx('flex gap-1 overflow-x-auto', param === 'view' ? 'border-b border-slate-200' : '')}>
          {visible.map((t) => {
            const active = t.key === current?.key;
            return param === 'view' ? (
              <button
                key={t.key}
                type="button"
                onClick={() => select(t.key)}
                className={clsx(
                  'px-3 md:px-4 py-2 text-sm font-medium whitespace-nowrap border-b-2 -mb-px',
                  active ? 'border-brand-600 text-brand-700' : 'border-transparent text-slate-500 hover:text-slate-800'
                )}>
                {t.label}
              </button>
            ) : (
              <button
                key={t.key}
                type="button"
                onClick={() => select(t.key)}
                className={clsx(
                  'px-3 py-1 text-xs font-medium rounded-full whitespace-nowrap',
                  active ? 'bg-slate-800 text-white' : 'bg-slate-100 text-slate-600 hover:bg-slate-200'
                )}>
                {t.label}
              </button>
            );
          })}
        </div>
      )}
      {/* key: al cambiar de tab se desmonta la anterior (estado limpio). */}
      <div key={current?.key}>{current?.element}</div>
    </div>
  );
}

/** Redirect de una ruta vieja a una tab de un hub, conservando su query string (?lead=, ?source=, …). */
export function GoTab({ to, view, sub }: { to: string; view?: string; sub?: string }) {
  const { search } = useLocation();
  const sp = new URLSearchParams(search);
  if (view) sp.set('view', view);
  if (sub) sp.set('sub', sub);
  const qs = sp.toString();
  return <Navigate to={`${to}${qs ? `?${qs}` : ''}`} replace />;
}
