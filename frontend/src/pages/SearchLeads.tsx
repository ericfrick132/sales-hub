import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import { useAuthStore } from '../lib/auth';
import CoverageMap from '../components/CoverageMap';

type NextCapture = {
  productKey: string;
  productName: string;
  localityGid2: string;
  localityName: string;
  adminLevel1Name: string;
  countryName: string;
  category: string;
  query: string;
  mapsUrl: string;
  priority: 'new' | 'stale';
  lastCapturedAt?: string | null;
  leadsLastTime: number;
};

type Capture = {
  id: string;
  productKey: string;
  localityName?: string;
  category?: string;
  query: string;
  status: 'Queued' | 'Running' | 'Done' | 'Failed';
  leadsCreated: number;
  rawItems: number;
  scheduledAt: string;
};

const SCRIPT_URL = '/saleshub-capture.user.js';

export default function SearchLeads() {
  const token = useAuthStore((s) => s.token);
  const [setupOpen, setSetupOpen] = useState(false);
  const [productFilter, setProductFilter] = useState('');

  const next = useQuery({
    queryKey: ['capture-next'],
    queryFn: async () => (await api.get<NextCapture[]>('/search-jobs/next', { params: { limit: 5 } })).data,
    refetchInterval: 30_000
  });

  const captures = useQuery({
    queryKey: ['capture-history'],
    queryFn: async () => (await api.get<Capture[]>('/search-jobs', { params: { limit: 10 } })).data,
    refetchInterval: 10_000
  });

  const top = next.data?.[0];
  const rest = (next.data ?? []).slice(1);

  // Si nunca capturó nada, abrimos setup automáticamente para que arme Tampermonkey.
  const hasAnyCapture = (captures.data ?? []).length > 0;
  const showSetupAuto = !hasAnyCapture && captures.isFetched;

  function copyToken() {
    if (!token) return toast.error('No hay token');
    navigator.clipboard.writeText(token);
    toast.success('Token copiado');
  }

  return (
    <div className="space-y-4 max-w-5xl">
      <div className="flex items-center justify-between flex-wrap gap-2">
        <h1 className="text-2xl font-bold">Buscar leads</h1>
        <button
          className="text-sm text-slate-500 hover:text-slate-700 underline"
          onClick={() => setSetupOpen((v) => !v)}>
          {setupOpen ? 'Ocultar setup' : '¿Cómo se configura?'}
        </button>
      </div>

      {/* CARD GRANDE: la próxima captura. Esto es lo único que el vendedor necesita ver. */}
      {next.isLoading ? (
        <div className="card p-8 text-center text-slate-500">Buscando qué te conviene capturar…</div>
      ) : !top ? (
        <div className="card p-8 text-center space-y-2">
          <div className="text-lg font-semibold">¡Estás al día!</div>
          <div className="text-sm text-slate-600">
            Por ahora no hay zonas tuyas sin capturar. Vuelvo en un rato a buscar nuevas
            sugerencias automáticamente.
          </div>
        </div>
      ) : (
        <a
          href={top.mapsUrl}
          target="_blank"
          rel="noreferrer"
          className="block card p-6 md:p-8 bg-gradient-to-br from-brand-50 to-white border-brand-200 hover:shadow-md transition">
          <div className="flex items-center gap-2 text-xs uppercase tracking-wide text-brand-700 mb-2">
            <span className="bg-brand-600 text-white rounded-full px-2 py-0.5 text-[10px]">
              {top.priority === 'new' ? 'Nueva zona' : 'Refrescar'}
            </span>
            <span className="text-slate-500">{top.productName}</span>
            {top.lastCapturedAt && (
              <span className="text-slate-400">
                · última vez {daysAgo(top.lastCapturedAt)}d ({top.leadsLastTime} leads)
              </span>
            )}
          </div>
          <div className="text-2xl md:text-3xl font-bold text-slate-900">
            {top.category ? <>{cap(top.category)} en {top.localityName}</> : <>{top.localityName}</>}
          </div>
          <div className="text-sm text-slate-500 mt-1">
            {top.adminLevel1Name}, {top.countryName}
          </div>
          <div className="mt-5 flex items-center gap-3">
            <span className="btn-primary text-base px-5 py-2.5">Abrir en Maps →</span>
            <span className="text-xs text-slate-500">
              Click en cada negocio del listado, después <b>"+ Este lugar"</b> en el panel de SalesHub.
            </span>
          </div>
        </a>
      )}

      {/* Otras opciones (chiquitas) — por si la sugerencia no le sirve. */}
      {rest.length > 0 && (
        <div className="card p-3">
          <div className="text-xs uppercase tracking-wide text-slate-500 mb-2 px-1">O probá con</div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-1">
            {rest.map((s, i) => (
              <a
                key={i}
                href={s.mapsUrl}
                target="_blank"
                rel="noreferrer"
                className="flex items-center gap-2 px-2 py-2 rounded hover:bg-slate-50 text-sm">
                <span className="text-[10px] uppercase text-slate-400 w-12">
                  {s.priority === 'new' ? 'Nueva' : 'Refrescar'}
                </span>
                <span className="flex-1 truncate">
                  {s.category ? <b>{cap(s.category)}</b> : <span className="text-slate-400">(sin categoría)</span>}
                  <span className="text-slate-500"> en {s.localityName}</span>
                  <span className="text-xs text-slate-400"> · {s.productName}</span>
                </span>
              </a>
            ))}
          </div>
        </div>
      )}

      {/* Setup: colapsado, salvo que no haya capturado nunca. */}
      {(setupOpen || showSetupAuto) && (
        <div className="card p-5 space-y-4 border-amber-200 bg-amber-50/30">
          <div className="font-semibold">Setup — primera vez (3 minutos)</div>
          <ol className="text-sm text-slate-700 space-y-3 list-decimal list-inside">
            <li>
              <b>Instalá Tampermonkey</b> (extensión gratuita){' '}
              <a className="text-brand-700 underline" href="https://chromewebstore.google.com/detail/tampermonkey/dhdgffkkebhmkfjojejmpbldmpobfkfo" target="_blank" rel="noreferrer">
                Chrome / Brave / Edge
              </a>{' '}·{' '}
              <a className="text-brand-700 underline" href="https://addons.mozilla.org/firefox/addon/tampermonkey/" target="_blank" rel="noreferrer">
                Firefox
              </a>
            </li>
            <li>
              <b>Instalá el script</b>:{' '}
              <a className="text-brand-700 underline font-mono" href={SCRIPT_URL} target="_blank" rel="noreferrer">
                saleshub-capture.user.js
              </a>{' '}— Tampermonkey abre una pantalla con botón verde "Instalar".
            </li>
            <li>
              <b>Pegá tu token</b>:
              <button className="btn-secondary text-xs mx-2" onClick={copyToken}>Copiar token</button>
              después abrí{' '}
              <a className="text-brand-700 underline" href="https://www.google.com/maps" target="_blank" rel="noreferrer">google.com/maps</a>,
              click en <b>Config</b> del panel SalesHub abajo a la derecha, pegá el token y elegí tu producto (ej. <code>gymhero</code>).
            </li>
            <li><b>Logueate a Google Maps</b> con tu cuenta — sin login no se ven los teléfonos.</li>
          </ol>
          <div className="text-xs text-slate-500">
            Listo. Volvé acá, click en la card grande de arriba y arrancá a capturar.
          </div>
        </div>
      )}

      {/* Capturas recientes (feedback de progreso). */}
      <div className="card p-4">
        <div className="font-semibold mb-2">Tu actividad reciente</div>
        <div className="space-y-2">
          {(captures.data ?? []).map((c) => (
            <div key={c.id} className="flex items-center gap-3 border-b border-slate-100 pb-2 last:border-0">
              <span className="text-xs px-2 py-0.5 rounded bg-emerald-50 text-emerald-700 whitespace-nowrap">
                +{c.leadsCreated} nuevos
              </span>
              <div className="flex-1 min-w-0">
                <div className="text-sm font-medium truncate" title={c.query}>{c.query}</div>
                <div className="text-xs text-slate-500">
                  {c.productKey} · {c.rawItems} subidos
                </div>
              </div>
              <div className="text-xs text-slate-400 whitespace-nowrap">
                {new Date(c.scheduledAt).toLocaleString('es-AR', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit' })}
              </div>
            </div>
          ))}
          {!captures.isLoading && (captures.data ?? []).length === 0 && (
            <div className="text-sm text-slate-500">
              Todavía no subiste capturas. Apretá la card grande de arriba para arrancar.
            </div>
          )}
        </div>
      </div>

      <CoverageMap
        productKey={productFilter}
        onProductChange={setProductFilter}
      />
    </div>
  );
}

function daysAgo(iso: string): number {
  return Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 86400_000));
}

function cap(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}
