import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import { useAuthStore } from '../lib/auth';

type Suggestion = {
  productKey: string;
  productName: string;
  localityGid2: string;
  localityName: string;
  adminLevel1Name: string;
  countryCode: string;
  countryName: string;
  category: string;
  query: string;
  mapsUrl: string;
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
  const [productFilter, setProductFilter] = useState('');

  const suggestions = useQuery({
    queryKey: ['capture-suggestions'],
    queryFn: async () => (await api.get<Suggestion[]>('/search-jobs/suggestions')).data
  });

  const captures = useQuery({
    queryKey: ['capture-history'],
    queryFn: async () => (await api.get<Capture[]>('/search-jobs', { params: { limit: 30 } })).data,
    refetchInterval: 10_000
  });

  const products = useMemo(() => {
    const m = new Map<string, string>();
    (suggestions.data ?? []).forEach((s) => m.set(s.productKey, s.productName));
    return [...m.entries()];
  }, [suggestions.data]);

  const filtered = useMemo(() => {
    return (suggestions.data ?? []).filter((s) => !productFilter || s.productKey === productFilter);
  }, [suggestions.data, productFilter]);

  function copyToken() {
    if (!token) return toast.error('No hay token');
    navigator.clipboard.writeText(token);
    toast.success('Token copiado al portapapeles');
  }

  return (
    <div className="space-y-4 max-w-6xl">
      <h1 className="text-2xl font-bold">Buscar leads en mis zonas</h1>

      <div className="card p-5 space-y-3">
        <div className="font-semibold">Setup (una sola vez)</div>
        <ol className="text-sm text-slate-700 space-y-2 list-decimal list-inside">
          <li>
            Instalá <a className="text-brand-700 underline" href="https://www.tampermonkey.net/" target="_blank" rel="noreferrer">Tampermonkey</a>
            {' '}(Chrome / Edge / Firefox).
          </li>
          <li>
            Instalá el script de SalesHub: <a className="text-brand-700 underline" href={SCRIPT_URL} target="_blank" rel="noreferrer">saleshub-capture.user.js</a>
            {' '}— Tampermonkey te abre la pantalla de instalación, click "Instalar".
          </li>
          <li>
            Copiá tu token: <button className="btn-secondary text-xs ml-2" onClick={copyToken}>Copiar token</button>
            {' '}(después en Google Maps abrí el panel del script y pegalo en "Config").
          </li>
          <li>
            Logueate a Google con la cuenta que querés usar — el script captura desde TU sesión, no desde el server.
          </li>
        </ol>
      </div>

      <div className="card p-5 space-y-3">
        <div className="font-semibold">Cómo funciona el script en Maps</div>
        <ul className="text-sm text-slate-700 space-y-1 list-disc list-inside">
          <li>Aparece un panelito flotante abajo a la derecha cuando estás en <code>google.com/maps</code>.</li>
          <li><b>+ Este lugar</b>: cuando abrís el detalle de un negocio (ese panel grande con teléfono y horarios), captura ese item.</li>
          <li><b>+ Listado visible</b>: scrollea como quieras y después click acá para mandar todo lo que se ve al buffer.</li>
          <li><b>Subir N</b>: postea el buffer al backend. Te asigna los leads a vos directamente.</li>
          <li>Los links de abajo abren Maps con el contexto (producto + localidad) ya configurado en el script.</li>
        </ul>
      </div>

      <div className="flex items-center justify-between flex-wrap gap-2">
        <div className="text-sm font-semibold">Atajos para empezar</div>
        <div>
          <label className="text-xs text-slate-500">Producto</label>
          <select className="input" value={productFilter} onChange={(e) => setProductFilter(e.target.value)}>
            <option value="">Todos</option>
            {products.map(([key, name]) => (
              <option key={key} value={key}>{name}</option>
            ))}
          </select>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
        <div className="lg:col-span-2">
          {!suggestions.isLoading && filtered.length === 0 && (
            <div className="card p-4 text-sm text-slate-600">
              No hay localidades asignadas todavía. Pedile al admin que te pinte zonas en el mapa.
            </div>
          )}
          <div className="card divide-y divide-slate-100">
            {filtered.map((s, i) => (
              <a
                key={i}
                href={s.mapsUrl}
                target="_blank"
                rel="noreferrer"
                className="flex items-center gap-3 p-3 hover:bg-slate-50 text-sm">
                <span className="text-xs text-slate-400 w-24 truncate">{s.productName}</span>
                <span className="flex-1 truncate">
                  {s.category ? <span className="font-medium">{s.category}</span> : <span className="text-slate-400">(sin categoría)</span>}
                  <span className="text-slate-500"> en {s.localityName}, {s.countryName}</span>
                </span>
                <span className="text-xs text-brand-700">Abrir en Maps →</span>
              </a>
            ))}
          </div>
        </div>

        <div className="card p-4">
          <div className="font-semibold mb-3">Capturas recientes</div>
          <div className="space-y-2">
            {(captures.data ?? []).map((c) => (
              <div key={c.id} className="border-b border-slate-100 pb-2 last:border-0">
                <div className="flex items-center gap-2 text-xs">
                  <span className="px-2 py-0.5 rounded bg-emerald-50 text-emerald-700">
                    {c.leadsCreated} nuevos / {c.rawItems} subidos
                  </span>
                  <span className="text-slate-500">{new Date(c.scheduledAt).toLocaleTimeString()}</span>
                </div>
                <div className="text-sm font-medium truncate" title={c.query}>{c.query}</div>
              </div>
            ))}
            {!captures.isLoading && (captures.data ?? []).length === 0 && (
              <div className="text-xs text-slate-500">Todavía no subiste capturas.</div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
