import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import { useAuthStore } from '../lib/auth';
import CaptureZonesMap, { ZONE_COLORS, type CaptureZone } from '../components/CaptureZonesMap';
import type { Product, Seller } from '../lib/types';

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

// El producto elegido se recuerda en este navegador: quien captura suele salir con uno solo.
const PRODUCT_KEY_STORAGE = 'saleshub-capture-product';
const readStoredProduct = () => {
  try { return localStorage.getItem(PRODUCT_KEY_STORAGE) ?? ''; } catch { return ''; }
};

export default function SearchLeads() {
  const token = useAuthStore((s) => s.token);
  const [setupOpen, setSetupOpen] = useState(false);
  const [pickedGid2, setPickedGid2] = useState<string | null>(null);
  const [productKey, setProductKey] = useState(readStoredProduct);

  const me = useQuery({
    queryKey: ['seller-me'],
    queryFn: async () => (await api.get<Seller>('/sellers/me')).data,
    staleTime: 5 * 60_000,
  });
  const products = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data,
    staleTime: 5 * 60_000,
  });
  // Los productos que este vendedor puede salir a capturar (whitelist vacía = todos).
  const whitelist = me.data?.verticalsWhitelist ?? [];
  const myProducts = (products.data ?? []).filter((p) =>
    p.active && p.productKey?.trim() && (whitelist.length === 0 || whitelist.includes(p.productKey)));
  // Si lo que quedó guardado ya no es suyo (le cambiaron las apps), vuelve a "Todos".
  const selectedProduct = myProducts.some((p) => p.productKey === productKey) ? productKey : '';

  function chooseProduct(key: string) {
    setProductKey(key);
    try { localStorage.setItem(PRODUCT_KEY_STORAGE, key); } catch { /* sin storage: sólo dura la sesión */ }
  }

  const ready = me.isFetched && products.isFetched;
  const productParam = selectedProduct || undefined;

  // El upload pasa por fuera de React (Tampermonkey), así que se re-consulta cada 15s y al
  // volver a la pestaña para que el mapa y la zona elegida reflejen lo recién capturado.
  const zones = useQuery({
    queryKey: ['capture-zones', selectedProduct],
    enabled: ready,
    queryFn: async () => (await api.get<CaptureZone[]>('/search-jobs/zones', { params: { productKey: productParam } })).data,
    refetchInterval: 15_000,
    refetchOnWindowFocus: true,
  });
  // La mejor sugerencia (nunca capturada primero) elige la zona inicial: un click y a capturar.
  const top = useQuery({
    queryKey: ['capture-next', selectedProduct],
    enabled: ready,
    queryFn: async () => (await api.get<NextCapture[]>('/search-jobs/next', { params: { limit: 1, productKey: productParam } })).data,
    refetchInterval: 15_000,
    refetchOnWindowFocus: true,
  });

  const zoneList = zones.data ?? [];
  const selectedGid2 = pickedGid2 && zoneList.some((z) => z.gid2 === pickedGid2)
    ? pickedGid2
    : top.data?.[0]?.localityGid2 ?? zoneList[0]?.gid2 ?? null;
  const selectedZone = zoneList.find((z) => z.gid2 === selectedGid2);

  const zoneCaptures = useQuery({
    queryKey: ['capture-next', selectedProduct, selectedGid2],
    enabled: ready && !!selectedGid2,
    queryFn: async () => (await api.get<NextCapture[]>('/search-jobs/next', {
      params: { limit: 200, productKey: productParam, gid2: selectedGid2 },
    })).data,
    refetchInterval: 15_000,
    refetchOnWindowFocus: true,
  });
  const pendingZones = zoneList.filter((z) => z.newCount + z.staleCount > 0).length;

  const captures = useQuery({
    queryKey: ['capture-history'],
    queryFn: async () => (await api.get<Capture[]>('/search-jobs', { params: { limit: 10 } })).data,
    refetchInterval: 5_000,
    refetchOnWindowFocus: true
  });

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

      {/* Qué producto sale a capturar: las sugerencias y el link a Maps van con ese producto. */}
      {myProducts.length > 0 && (
        <div className="card p-3 space-y-2">
          <div className="text-xs uppercase tracking-wide text-slate-500 px-1">¿Qué producto vas a capturar?</div>
          <div className="flex flex-wrap gap-1.5">
            {/* Con un solo producto "Todos" es lo mismo: se muestra sólo ese, ya elegido. */}
            {(myProducts.length > 1 ? [{ productKey: '', displayName: 'Todos' }, ...myProducts] : myProducts).map((p) => (
              <button
                key={p.productKey || 'all'}
                type="button"
                onClick={() => chooseProduct(p.productKey)}
                className={`text-sm px-3 py-1.5 rounded-full border transition ${
                  selectedProduct === p.productKey || myProducts.length === 1
                    ? 'bg-brand-600 text-white border-brand-600'
                    : 'bg-white text-slate-700 border-slate-200 hover:border-slate-300'
                }`}>
                {p.displayName}
              </button>
            ))}
          </div>
        </div>
      )}

      {/* Mapa de zonas: click en una localidad para ver qué capturar ahí. */}
      {!ready || zones.isLoading ? (
        <div className="card p-8 text-center text-slate-500">Buscando tus zonas…</div>
      ) : zoneList.length === 0 ? (
        <div className="card p-8 text-center text-sm text-slate-600">
          Todavía no tenés zonas asignadas. Pedile a un admin que te asigne territorio.
        </div>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-5 gap-3">
          <div className="card p-3 md:col-span-3 space-y-2">
            <div className="flex items-center justify-between flex-wrap gap-2 px-1">
              <div className="text-sm font-semibold">Elegí una zona en el mapa</div>
              <div className="text-xs text-slate-500">{pendingZones} de {zoneList.length} con algo para capturar</div>
            </div>
            <div className="h-[340px] md:h-[440px]">
              <CaptureZonesMap zones={zoneList} selected={selectedGid2} onSelect={setPickedGid2} />
            </div>
            <div className="flex flex-wrap gap-3 text-xs text-slate-600 px-1">
              <Legend color={ZONE_COLORS.new} label="Sin capturar" />
              <Legend color={ZONE_COLORS.stale} label="Para refrescar" />
              <Legend color={ZONE_COLORS.done} label="Al día" />
            </div>
          </div>

          <div className="card p-4 md:col-span-2 space-y-3 md:max-h-[520px] md:overflow-y-auto">
            {!selectedZone ? (
              <div className="text-sm text-slate-500">Tocá una zona del mapa.</div>
            ) : (
              <>
                <div>
                  <div className="text-xl font-bold text-slate-900 leading-tight">{selectedZone.name}</div>
                  <div className="text-sm text-slate-500">{selectedZone.adminLevel1Name}</div>
                  <div className="text-xs text-slate-500 mt-1">
                    {selectedZone.newCount} sin capturar · {selectedZone.staleCount} para refrescar · {selectedZone.doneCount} al día
                  </div>
                </div>
                {zoneCaptures.isLoading ? (
                  <div className="text-sm text-slate-500">Cargando…</div>
                ) : (zoneCaptures.data ?? []).length === 0 ? (
                  <div className="text-sm text-slate-600 bg-slate-50 rounded p-3">
                    Esta zona está al día. Elegí otra en el mapa (las azules y ámbar tienen para capturar).
                  </div>
                ) : (
                  <div className="space-y-1.5">
                    {(zoneCaptures.data ?? []).map((c) => (
                      <a
                        key={`${c.productKey}|${c.category}`}
                        href={c.mapsUrl}
                        target="_blank"
                        rel="noreferrer"
                        className="flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 hover:border-brand-300 hover:bg-brand-50/40 transition">
                        <span className="w-2 h-2 rounded-full shrink-0" style={{ background: c.priority === 'new' ? ZONE_COLORS.new : ZONE_COLORS.stale }} />
                        <span className="flex-1 min-w-0">
                          <span className="block text-sm font-medium truncate">
                            {c.category ? cap(c.category) : 'Todo el rubro'}
                          </span>
                          <span className="block text-[11px] text-slate-500 truncate">
                            {!selectedProduct && `${c.productName} · `}
                            {c.priority === 'new'
                              ? 'nunca capturado'
                              : `hace ${daysAgo(c.lastCapturedAt!)} d (${c.leadsLastTime} leads)`}
                          </span>
                        </span>
                        <span className="text-xs font-medium text-brand-700 whitespace-nowrap">Abrir en Maps →</span>
                      </a>
                    ))}
                  </div>
                )}
                <div className="text-xs text-slate-500 border-t border-slate-100 pt-2">
                  Click en cada negocio del listado, después <b>"+ Este lugar"</b> en el panel de SalesHub.
                </div>
              </>
            )}
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

    </div>
  );
}

function Legend({ color, label }: { color: string; label: string }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      <span className="w-3 h-3 rounded-sm" style={{ background: color, opacity: 0.8 }} />
      {label}
    </span>
  );
}

function daysAgo(iso: string): number {
  return Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 86400_000));
}

function cap(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}
