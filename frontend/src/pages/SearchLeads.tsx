import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import { useAuthStore } from '../lib/auth';
import CoverageMap from '../components/CoverageMap';
import type { Product } from '../lib/types';

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

  const productsQ = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
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

      <div className="card p-5 space-y-4">
        <div className="font-semibold text-base">Setup — primera vez</div>

        <div>
          <div className="font-medium text-sm mb-1">1. Instalá Tampermonkey en tu browser</div>
          <div className="text-sm text-slate-600 mb-1">
            Es una extensión gratuita y oficial. Permite que un script chiquito de SalesHub
            se ejecute dentro de Google Maps cuando vos lo navegás (sin esto, no hay forma
            de capturar datos del DOM de Google).
          </div>
          <div className="text-sm">
            • <b>Chrome / Brave / Edge / Arc</b>:{' '}
            <a className="text-brand-700 underline"
               href="https://chromewebstore.google.com/detail/tampermonkey/dhdgffkkebhmkfjojejmpbldmpobfkfo"
               target="_blank" rel="noreferrer">Chrome Web Store</a>{' '}→ "Añadir a Chrome".
            <br />
            • <b>Firefox</b>:{' '}
            <a className="text-brand-700 underline"
               href="https://addons.mozilla.org/firefox/addon/tampermonkey/"
               target="_blank" rel="noreferrer">addons.mozilla.org</a>{' '}→ "Add to Firefox".
          </div>
          <div className="text-xs text-slate-500 mt-1">
            Cuando termina ves un ícono nuevo arriba a la derecha del browser (cuadrado negro con tres puntos).
          </div>
        </div>

        <div>
          <div className="font-medium text-sm mb-1">2. Instalá el script de SalesHub</div>
          <div className="text-sm text-slate-600">
            Click acá: <a className="text-brand-700 underline font-mono" href={SCRIPT_URL} target="_blank" rel="noreferrer">saleshub-capture.user.js</a>
            {' '}— Tampermonkey detecta el archivo y abre solo una pantalla con el código y
            un botón verde grande <b>"Instalar"</b>. Click ahí, listo.
          </div>
          <div className="text-xs text-slate-500 mt-1">
            Si ves código sin colorear (texto plano), Tampermonkey no se está activando — verificá que la
            extensión esté instalada y habilitada en el browser.
          </div>
        </div>

        <div>
          <div className="font-medium text-sm mb-1">3. Pegá tu token en el script</div>
          <div className="text-sm text-slate-600 mb-2">
            El token es un password temporal (vale 7 días) que el script usa para mandar los leads a tu cuenta.
          </div>
          <ol className="text-sm text-slate-700 list-decimal list-inside space-y-1">
            <li>
              <button className="btn-secondary text-xs mx-1" onClick={copyToken}>Copiar token</button>
              {' '}(se copia al portapapeles).
            </li>
            <li>Abrí <a className="text-brand-700 underline" href="https://www.google.com/maps" target="_blank" rel="noreferrer">google.com/maps</a> en una pestaña.</li>
            <li>Mirá <b>abajo a la derecha</b> de la pantalla: aparece un panel "SalesHub · captura" con un texto rojo "Falta configurar token + producto".</li>
            <li>Click el botón <b>"Config"</b> del panel. Te van a aparecer 3 ventanitas en orden:
              <ul className="list-disc list-inside ml-4 mt-1 text-slate-600">
                <li><i>URL del backend</i>: dejá lo que ya está (https://sales.efcloud.tech) → OK.</li>
                <li><i>JWT</i>: pegá con Ctrl/Cmd+V el token que copiaste → OK.</li>
                <li><i>productKey</i>: escribí el de la app que vas a buscar (ej. <code>gymhero</code>) → OK.</li>
              </ul>
            </li>
          </ol>
          <div className="text-xs text-slate-500 mt-1">
            Cuando termina, el texto rojo desaparece. El panel queda listo.
          </div>
        </div>

        <div>
          <div className="font-medium text-sm mb-1">4. Logueate a Google</div>
          <div className="text-sm text-slate-600">
            En Google Maps, arriba a la derecha, asegurate de estar con tu cuenta. Cuando estás
            logueado Google muestra los teléfonos de los negocios sin captcha — esa es la diferencia
            con scrapear desde un servidor.
          </div>
        </div>
      </div>

      <div className="card p-5 space-y-3">
        <div className="font-semibold">Cómo lo usás todos los días</div>
        <ol className="text-sm text-slate-700 space-y-2 list-decimal list-inside">
          <li>
            Click un atajo de los de abajo (ej. "yoga en Caballito, Argentina") — abre Google Maps
            con el contexto pre-configurado en el script.
          </li>
          <li>
            Click un negocio del listado lateral → se abre el panel grande de detalle (con teléfono y horarios).
          </li>
          <li>
            En el panel de SalesHub (abajo a la derecha) click <b>"+ Este lugar"</b>. El contador
            del buffer sube en 1. Repetí con cada negocio que te interese.
          </li>
          <li>
            Cuando junten 10-50, click <b>"Subir N"</b>. El backend dedupea (no carga teléfonos repetidos)
            y te crea los leads asignados a vos.
          </li>
        </ol>
        <div className="text-xs text-slate-500">
          Si el listado está abierto y querés capturar todo de una sola vez, el botón <b>"+ Listado visible"</b> agarra los items que ves
          (rinde menos teléfonos porque Maps no los muestra todos en el listado — para teléfono confiable, usá "+ Este lugar").
        </div>
      </div>

      <CoverageMap
        productKey={productFilter}
        onProductChange={setProductFilter}
        products={(productsQ.data ?? []).map((p) => ({
          productKey: p.productKey,
          displayName: p.displayName,
          categories: p.categories
        }))}
      />

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
