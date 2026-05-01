import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import maplibregl, { Map as MlMap, MapMouseEvent } from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import { isAdmin, useAuthStore } from '../lib/auth';
import type { Product } from '../lib/types';

// Polígonos LATAM generados por scripts/localities/build.mjs.
// Si no existe, el mapa muestra un mensaje pidiendo correr el seed.
const LOCALITIES_GEOJSON_URL = '/data/localities-latam.geojson';

type SellerWithLocalities = {
  sellerId: string;
  displayName: string;
  color: string;
  localityGid2s: string[];
};

type MapLead = {
  id: string;
  name: string;
  productKey: string;
  city?: string;
  province?: string;
  address?: string;
  whatsappPhone?: string;
  sellerName?: string;
  latitude: number;
  longitude: number;
  status: string;
  sellerId?: string;
};

const PRODUCT_COLORS: Record<string, string> = {
  gymhero: '#1e8dff',
  bookingpro_barber: '#f59e0b',
  bookingpro_salon: '#ec4899',
  bookingpro_aesthetics: '#a855f7',
  unistock: '#10b981',
  playcrew: '#ef4444',
  bunker: '#64748b',
  construction: '#a16207'
};

export default function MapPage() {
  const user = useAuthStore(s => s.user);
  const admin = isAdmin(user);
  const qc = useQueryClient();

  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<MlMap | null>(null);
  const [mapReady, setMapReady] = useState(false);
  const [geojsonMissing, setGeojsonMissing] = useState(false);

  const [productKey, setProductKey] = useState('');
  const [editing, setEditing] = useState(false);
  // Vendedor sobre el que la próxima acción "agregar/quitar" va a aplicarse.
  const [assignTarget, setAssignTarget] = useState<string | null>(null);
  // Multi-select: el usuario click + ctrl/cmd va sumando zonas a este set.
  const [selected, setSelected] = useState<Set<string>>(new Set());

  // Estado canónico (M:N seller↔gid2). Se refresca tras cada PATCH al backend.
  const [assignments, setAssignments] = useState<Record<string, Set<string>>>({});

  const products = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  const sellersQ = useQuery({
    queryKey: ['sellers-with-localities'],
    enabled: admin,
    queryFn: async () => (await api.get<SellerWithLocalities[]>('/sellers/with-localities')).data
  });

  const leadsQ = useQuery({
    queryKey: ['map-leads', productKey],
    queryFn: async () => (await api.get<MapLead[]>('/leads/map', {
      params: { productKey: productKey || undefined, limit: 2000 }
    })).data,
    refetchInterval: 30_000
  });

  useEffect(() => {
    if (!sellersQ.data) return;
    const m: Record<string, Set<string>> = {};
    for (const s of sellersQ.data) m[s.sellerId] = new Set(s.localityGid2s);
    setAssignments(m);
    if (!assignTarget && sellersQ.data.length > 0) setAssignTarget(sellersQ.data[0].sellerId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sellersQ.data]);

  const sellersById = useMemo(() => {
    const m = new Map<string, SellerWithLocalities>();
    (sellersQ.data ?? []).forEach(s => m.set(s.sellerId, s));
    return m;
  }, [sellersQ.data]);

  const ownersByGid2 = useMemo(() => {
    const m = new Map<string, string[]>();
    for (const [sellerId, set] of Object.entries(assignments)) {
      for (const gid2 of set) {
        const arr = m.get(gid2) ?? [];
        arr.push(sellerId);
        m.set(gid2, arr);
      }
    }
    return m;
  }, [assignments]);

  // {gid2 → fillColor} para el view normal o para el view de edición. En edición
  // resaltamos las zonas que tiene el assignTarget para que el admin vea su
  // territorio actual mientras selecciona zonas nuevas.
  const fillExpression = useMemo(() => {
    const stops: unknown[] = ['match', ['get', 'gid2']];
    for (const [gid2, owners] of ownersByGid2) {
      let color = '#cbd5e1';
      if (editing && assignTarget) {
        if (owners.includes(assignTarget)) {
          color = sellersById.get(assignTarget)?.color ?? '#16a34a';
        } else {
          color = '#cbd5e1';
        }
      } else {
        color = sellersById.get(owners[0])?.color ?? '#cbd5e1';
      }
      stops.push(gid2, color);
    }
    stops.push('rgba(0,0,0,0)');
    return stops;
  }, [ownersByGid2, editing, assignTarget, sellersById]);

  // Initialise the map once.
  useEffect(() => {
    if (!containerRef.current || mapRef.current) return;
    const map = new maplibregl.Map({
      container: containerRef.current,
      style: {
        version: 8,
        sources: {
          osm: {
            type: 'raster',
            tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],
            tileSize: 256,
            attribution: '© OpenStreetMap'
          }
        },
        layers: [{ id: 'osm', type: 'raster', source: 'osm' }]
      },
      center: [-63.6, -20],
      zoom: 3,
      attributionControl: { compact: true }
    });
    map.addControl(new maplibregl.NavigationControl({ visualizePitch: false }), 'top-right');
    mapRef.current = map;

    map.on('load', async () => {
      try {
        const res = await fetch(LOCALITIES_GEOJSON_URL);
        if (!res.ok) throw new Error(`status ${res.status}`);
        const gj = await res.json();
        map.addSource('localities', { type: 'geojson', data: gj, generateId: false });
        map.addLayer({
          id: 'localities-fill',
          type: 'fill',
          source: 'localities',
          paint: { 'fill-color': 'rgba(0,0,0,0)', 'fill-opacity': 0.55 }
        });
        map.addLayer({
          id: 'localities-outline',
          type: 'line',
          source: 'localities',
          paint: { 'line-color': '#475569', 'line-width': 0.4, 'line-opacity': 0.6 }
        });
        // Subrayado al pasar el mouse (modo no-edición).
        map.addLayer({
          id: 'localities-hover',
          type: 'line',
          source: 'localities',
          paint: { 'line-color': '#0f172a', 'line-width': 2 },
          filter: ['==', ['get', 'gid2'], '__none__']
        });
        // Borde grueso amarillo para zonas seleccionadas en modo edición.
        map.addLayer({
          id: 'localities-selected',
          type: 'line',
          source: 'localities',
          paint: { 'line-color': '#f59e0b', 'line-width': 3 },
          filter: ['in', ['get', 'gid2'], ['literal', []]]
        });
        setMapReady(true);
      } catch (err) {
        console.warn('No se pudo cargar el GeoJSON de localidades:', err);
        setGeojsonMissing(true);
      }
    });

    return () => { map.remove(); mapRef.current = null; };
  }, []);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapReady || !map.getLayer('localities-fill')) return;
    map.setPaintProperty('localities-fill', 'fill-color', fillExpression as never);
  }, [fillExpression, mapReady]);

  // Update the selected layer filter when the selection changes.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapReady || !map.getLayer('localities-selected')) return;
    map.setFilter('localities-selected', ['in', ['get', 'gid2'], ['literal', [...selected]]]);
  }, [selected, mapReady]);

  // Lead pins (circles) on top of polygons.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapReady) return;
    const data = {
      type: 'FeatureCollection',
      features: (leadsQ.data ?? []).map(l => ({
        type: 'Feature' as const,
        geometry: { type: 'Point' as const, coordinates: [l.longitude, l.latitude] },
        properties: { id: l.id, name: l.name, productKey: l.productKey, color: PRODUCT_COLORS[l.productKey] ?? '#64748b' }
      }))
    };
    if (!map.getSource('leads')) {
      map.addSource('leads', { type: 'geojson', data: data as never });
      map.addLayer({
        id: 'leads-circles',
        type: 'circle',
        source: 'leads',
        paint: {
          'circle-radius': 4,
          'circle-color': ['get', 'color'],
          'circle-stroke-color': '#fff',
          'circle-stroke-width': 1
        }
      });
    } else {
      (map.getSource('leads') as maplibregl.GeoJSONSource).setData(data as never);
    }
  }, [leadsQ.data, mapReady]);

  // Hover popup — sólo en modo NO-edición (en edición confunde con la selección).
  useEffect(() => {
    const m = mapRef.current;
    if (!m || !mapReady || !m.getLayer('localities-fill')) return;
    if (editing) return;
    const popup = new maplibregl.Popup({ closeButton: false, closeOnClick: false, offset: 6 });
    const onMove = (e: MapMouseEvent & { features?: maplibregl.MapGeoJSONFeature[] }) => {
      const f = e.features?.[0];
      if (!f) { m.setFilter('localities-hover', ['==', ['get', 'gid2'], '__none__']); popup.remove(); return; }
      const props = f.properties as { gid2: string; name: string; adm1Name?: string; countryName?: string };
      m.setFilter('localities-hover', ['==', ['get', 'gid2'], props.gid2]);
      const owners = ownersByGid2.get(props.gid2) ?? [];
      const ownerNames = owners.map(id => sellersById.get(id)?.displayName ?? '?').join(', ');
      popup.setLngLat(e.lngLat)
        .setHTML(
          `<div class="text-xs">
            <div class="font-semibold">${escapeHtml(props.name)}</div>
            <div class="text-slate-500">${escapeHtml(props.adm1Name ?? '')}${props.countryName ? `, ${escapeHtml(props.countryName)}` : ''}</div>
            <div class="text-emerald-700 mt-1">${owners.length === 0 ? 'Sin asignar' : `Asignada a: ${escapeHtml(ownerNames)}`}</div>
           </div>`
        )
        .addTo(m);
    };
    const onLeave = () => { m.setFilter('localities-hover', ['==', ['get', 'gid2'], '__none__']); popup.remove(); };
    m.on('mousemove', 'localities-fill', onMove);
    m.on('mouseleave', 'localities-fill', onLeave);
    return () => {
      m.off('mousemove', 'localities-fill', onMove);
      m.off('mouseleave', 'localities-fill', onLeave);
      popup.remove();
    };
  }, [mapReady, editing, ownersByGid2, sellersById]);

  // Click handler en modo edición. Sin modificador: reemplaza la selección.
  // Con Ctrl/Cmd o Shift: toggle (suma/saca).
  useEffect(() => {
    const m = mapRef.current;
    if (!m || !mapReady || !editing) return;
    const onClick = (e: MapMouseEvent & { features?: maplibregl.MapGeoJSONFeature[]; originalEvent: MouseEvent }) => {
      const f = e.features?.[0];
      const gid2 = (f?.properties as { gid2?: string } | undefined)?.gid2;
      if (!gid2) return;
      const additive = e.originalEvent.ctrlKey || e.originalEvent.metaKey || e.originalEvent.shiftKey;
      setSelected(prev => {
        const next = new Set(prev);
        if (additive) {
          if (next.has(gid2)) next.delete(gid2);
          else next.add(gid2);
        } else {
          // Click sin modificador: si ya estaba selccionada sola, deselecciona;
          // si no, queda como única selección.
          if (next.size === 1 && next.has(gid2)) {
            next.clear();
          } else {
            next.clear();
            next.add(gid2);
          }
        }
        return next;
      });
    };
    m.on('click', 'localities-fill', onClick);
    return () => { m.off('click', 'localities-fill', onClick); };
  }, [mapReady, editing]);

  // Cuando salgo de modo edición, limpio la selección.
  useEffect(() => {
    if (!editing) setSelected(new Set());
  }, [editing]);

  async function applySelection(mode: 'add' | 'remove') {
    if (!assignTarget) return toast.error('Elegí un vendedor primero');
    if (selected.size === 0) return toast.error('No hay zonas seleccionadas');
    const current = new Set(assignments[assignTarget] ?? []);
    if (mode === 'add') {
      for (const gid2 of selected) current.add(gid2);
    } else {
      for (const gid2 of selected) current.delete(gid2);
    }
    // Optimistic UI
    setAssignments(prev => ({ ...prev, [assignTarget]: current }));
    try {
      await api.put(`/admin/sellers/${assignTarget}/localities`, {
        localityGid2s: [...current]
      });
      toast.success(mode === 'add'
        ? `+${selected.size} zona${selected.size === 1 ? '' : 's'}`
        : `-${selected.size} zona${selected.size === 1 ? '' : 's'}`);
      setSelected(new Set());
      qc.invalidateQueries({ queryKey: ['sellers-with-localities'] });
    } catch (err) {
      const e = err as { response?: { data?: { error?: string } } };
      toast.error(e?.response?.data?.error ?? 'Error guardando');
      // Revert on failure
      setAssignments(prev => {
        const reverted = new Set(sellersQ.data?.find(s => s.sellerId === assignTarget)?.localityGid2s ?? []);
        return { ...prev, [assignTarget]: reverted };
      });
    }
  }

  const productCounts = useMemo(() => {
    const m = new Map<string, number>();
    (leadsQ.data ?? []).forEach(l => m.set(l.productKey, (m.get(l.productKey) ?? 0) + 1));
    return m;
  }, [leadsQ.data]);

  const targetSeller = assignTarget ? sellersById.get(assignTarget) : null;
  const targetCount = assignTarget ? (assignments[assignTarget]?.size ?? 0) : 0;

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between gap-3 flex-wrap">
        <h1 className="text-2xl font-bold">Mapa de leads y zonas</h1>
        <div className="flex gap-3 items-end">
          <div>
            <label className="text-xs text-slate-500">Producto</label>
            <select className="input" value={productKey} onChange={e => setProductKey(e.target.value)}>
              <option value="">Todos</option>
              {(products.data ?? []).map(p => (
                <option key={p.productKey} value={p.productKey}>{p.displayName}</option>
              ))}
            </select>
          </div>
          {admin && (
            <button
              className={editing ? 'btn-primary' : 'btn-secondary'}
              onClick={() => setEditing(v => !v)}>
              {editing ? 'Listo' : 'Editar zonas'}
            </button>
          )}
        </div>
      </div>

      <div className="flex gap-3" style={{ height: '70vh' }}>
        {admin && (
          <div className="card p-3 overflow-y-auto flex flex-col" style={{ width: 280 }}>
            {editing && (
              <div className="border border-slate-200 rounded-md p-2 mb-3 bg-slate-50">
                <div className="text-xs uppercase tracking-wide text-slate-500 mb-1">Asignar zonas</div>
                <div className="text-xs text-slate-600 mb-2">
                  Click sobre el mapa selecciona una zona. <b>Ctrl/Cmd+click</b> suma o saca zonas
                  de la selección.
                </div>
                <label className="text-xs text-slate-500">Vendedor</label>
                <select
                  className="input w-full text-sm mb-2"
                  value={assignTarget ?? ''}
                  onChange={e => setAssignTarget(e.target.value || null)}>
                  {(sellersQ.data ?? []).map(s => (
                    <option key={s.sellerId} value={s.sellerId}>{s.displayName}</option>
                  ))}
                </select>
                <div className="text-xs text-slate-600 mb-2">
                  Selección actual: <b>{selected.size}</b> zona{selected.size === 1 ? '' : 's'}
                  {targetSeller && (
                    <span className="block text-slate-500">
                      {targetSeller.displayName} hoy tiene {targetCount}.
                    </span>
                  )}
                </div>
                <div className="flex flex-wrap gap-1">
                  <button
                    className="btn-primary text-xs flex-1"
                    disabled={selected.size === 0 || !assignTarget}
                    onClick={() => applySelection('add')}>
                    + Asignar
                  </button>
                  <button
                    className="btn-secondary text-xs flex-1"
                    disabled={selected.size === 0 || !assignTarget}
                    onClick={() => applySelection('remove')}>
                    − Quitar
                  </button>
                  <button
                    className="btn-secondary text-xs"
                    disabled={selected.size === 0}
                    onClick={() => setSelected(new Set())}>
                    Limpiar
                  </button>
                </div>
              </div>
            )}

            <div className="text-xs uppercase tracking-wide text-slate-500 mb-2">Vendedores</div>
            {(sellersQ.data ?? []).map(s => {
              const isTarget = assignTarget === s.sellerId;
              const count = assignments[s.sellerId]?.size ?? 0;
              return (
                <button
                  key={s.sellerId}
                  onClick={() => setAssignTarget(s.sellerId)}
                  className={`w-full flex items-center gap-2 px-2 py-1.5 rounded text-left text-sm ${
                    isTarget && editing ? 'bg-slate-100 ring-1 ring-slate-300' : 'hover:bg-slate-50'
                  }`}>
                  <span className="w-3 h-3 rounded-full flex-shrink-0" style={{ background: s.color }} />
                  <span className="flex-1 truncate">{s.displayName}</span>
                  <span className="text-xs text-slate-500">{count}</span>
                </button>
              );
            })}
            {!sellersQ.isLoading && (sellersQ.data ?? []).length === 0 && (
              <div className="text-xs text-slate-500">Sin vendedores activos.</div>
            )}
          </div>
        )}
        <div className="card overflow-hidden flex-1 relative">
          <div ref={containerRef} className="w-full h-full" />
          {geojsonMissing && (
            <div className="absolute inset-0 grid place-items-center bg-white/80">
              <div className="text-center text-sm text-slate-600 max-w-md p-6">
                <div className="font-semibold text-slate-800 mb-2">Faltan los polígonos de localidades</div>
                Corré <code>node scripts/localities/build.mjs</code> y después <code>node scripts/localities/import.mjs</code> para poblar el dataset.
              </div>
            </div>
          )}
        </div>
      </div>

      <div className="flex flex-wrap gap-2 text-xs">
        {[...productCounts.entries()].map(([k, n]) => (
          <span key={k} className="inline-flex items-center gap-1 bg-white border border-slate-200 rounded-full px-2 py-1">
            <span className="w-3 h-3 rounded-full" style={{ background: PRODUCT_COLORS[k] ?? '#64748b' }} />
            <span>{k}: <strong>{n}</strong></span>
          </span>
        ))}
        {productCounts.size === 0 && !leadsQ.isLoading && (
          <span className="text-slate-500">
            Ningún lead tiene coordenadas todavía. Los próximos leads de Google Maps van a aparecer acá automáticamente.
          </span>
        )}
      </div>

      <div className="text-xs text-slate-400">
        <Link to="/leads" className="hover:underline">Ir a leads →</Link>
      </div>
    </div>
  );
}

function escapeHtml(s: string) {
  return s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!));
}
