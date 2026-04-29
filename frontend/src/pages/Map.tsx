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
  const [activeSellerId, setActiveSellerId] = useState<string | null>(null);

  // Local optimistic copy of seller→localities while painting. We sync to the
  // server on mouseup with a debounce so a long drag is one PATCH per seller.
  const [draftAssignments, setDraftAssignments] = useState<Record<string, Set<string>>>({});
  const dirtySellersRef = useRef<Set<string>>(new Set());
  const isPaintingRef = useRef(false);
  const paintModeRef = useRef<'add' | 'remove'>('add');

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

  // Initialise the local draft from the server response so optimistic edits
  // start from the latest state.
  useEffect(() => {
    if (!sellersQ.data) return;
    const m: Record<string, Set<string>> = {};
    for (const s of sellersQ.data) m[s.sellerId] = new Set(s.localityGid2s);
    setDraftAssignments(m);
  }, [sellersQ.data]);

  const sellersById = useMemo(() => {
    const m = new Map<string, SellerWithLocalities>();
    (sellersQ.data ?? []).forEach(s => m.set(s.sellerId, s));
    return m;
  }, [sellersQ.data]);

  // For each gid2, who owns it. Computed on every assignment change.
  const ownersByGid2 = useMemo(() => {
    const m = new Map<string, string[]>();
    for (const [sellerId, set] of Object.entries(draftAssignments)) {
      for (const gid2 of set) {
        const arr = m.get(gid2) ?? [];
        arr.push(sellerId);
        m.set(gid2, arr);
      }
    }
    return m;
  }, [draftAssignments]);

  // Build the {gid2 → fillColor} mapping for the current view+edit state.
  // - Editing + active seller: green if active owns it, hatched-gray if other owns it
  // - Otherwise: color of first owner (or transparent if none)
  const fillExpression = useMemo(() => {
    const stops: unknown[] = ['match', ['get', 'gid2']];
    for (const [gid2, owners] of ownersByGid2) {
      let color = '#cbd5e1';
      if (editing && activeSellerId) {
        if (owners.includes(activeSellerId)) {
          color = sellersById.get(activeSellerId)?.color ?? '#16a34a';
        } else {
          color = '#94a3b8';
        }
      } else {
        color = sellersById.get(owners[0])?.color ?? '#cbd5e1';
      }
      stops.push(gid2, color);
    }
    stops.push('rgba(0,0,0,0)'); // default: transparent
    return stops;
  }, [ownersByGid2, editing, activeSellerId, sellersById]);

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
          paint: {
            'fill-color': 'rgba(0,0,0,0)',
            'fill-opacity': 0.55
          }
        });
        map.addLayer({
          id: 'localities-outline',
          type: 'line',
          source: 'localities',
          paint: {
            'line-color': '#475569',
            'line-width': 0.4,
            'line-opacity': 0.6
          }
        });
        map.addLayer({
          id: 'localities-hover',
          type: 'line',
          source: 'localities',
          paint: { 'line-color': '#0f172a', 'line-width': 2 },
          filter: ['==', ['get', 'gid2'], '__none__']
        });
        setMapReady(true);
      } catch (err) {
        console.warn('No se pudo cargar el GeoJSON de localidades:', err);
        setGeojsonMissing(true);
      }
    });

    return () => { map.remove(); mapRef.current = null; };
  }, []);

  // Update fill color expression whenever assignments / active seller change.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapReady) return;
    if (!map.getLayer('localities-fill')) return;
    map.setPaintProperty('localities-fill', 'fill-color', fillExpression as never);
  }, [fillExpression, mapReady]);

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

  // Hover highlight + tooltip.
  useEffect(() => {
    const m = mapRef.current;
    if (!m || !mapReady || !m.getLayer('localities-fill')) return;
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
  }, [mapReady, ownersByGid2, sellersById]);

  // Drag-paint when editing + an active seller is selected. Toggling on the
  // first cell decides whether the rest of the drag adds or removes.
  useEffect(() => {
    const m = mapRef.current;
    if (!m || !mapReady) return;
    if (!editing || !activeSellerId) return;
    m.dragPan.disable();
    m.getCanvas().style.cursor = 'crosshair';

    const applyToggle = (gid2: string) => {
      setDraftAssignments(prev => {
        const next = { ...prev };
        const set = new Set(next[activeSellerId!] ?? []);
        if (paintModeRef.current === 'add') set.add(gid2);
        else set.delete(gid2);
        next[activeSellerId!] = set;
        return next;
      });
      dirtySellersRef.current.add(activeSellerId!);
    };

    const pickGid2 = (e: MapMouseEvent): string | null => {
      const fs = m.queryRenderedFeatures(e.point, { layers: ['localities-fill'] });
      const f = fs?.[0];
      const id = (f?.properties as { gid2?: string } | undefined)?.gid2;
      return id ?? null;
    };

    const onDown = (e: MapMouseEvent) => {
      const gid2 = pickGid2(e); if (!gid2) return;
      const has = (draftAssignments[activeSellerId!] ?? new Set()).has(gid2);
      paintModeRef.current = has ? 'remove' : 'add';
      isPaintingRef.current = true;
      applyToggle(gid2);
    };
    let lastGid2: string | null = null;
    const onMove = (e: MapMouseEvent) => {
      if (!isPaintingRef.current) return;
      const gid2 = pickGid2(e); if (!gid2 || gid2 === lastGid2) return;
      lastGid2 = gid2;
      applyToggle(gid2);
    };
    const onUp = () => {
      isPaintingRef.current = false;
      lastGid2 = null;
      flushDirty();
    };

    m.on('mousedown', onDown);
    m.on('mousemove', onMove);
    m.on('mouseup', onUp);
    return () => {
      m.dragPan.enable();
      m.getCanvas().style.cursor = '';
      m.off('mousedown', onDown);
      m.off('mousemove', onMove);
      m.off('mouseup', onUp);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mapReady, editing, activeSellerId]);

  async function flushDirty() {
    const ids = [...dirtySellersRef.current];
    dirtySellersRef.current.clear();
    for (const sellerId of ids) {
      const set = draftAssignments[sellerId];
      try {
        await api.put(`/admin/sellers/${sellerId}/localities`, {
          localityGid2s: [...(set ?? [])]
        });
      } catch (err) {
        const e = err as { response?: { data?: { error?: string } } };
        toast.error(e?.response?.data?.error ?? 'Error guardando asignación');
      }
    }
    qc.invalidateQueries({ queryKey: ['sellers-with-localities'] });
  }

  // Counts for the legend.
  const productCounts = useMemo(() => {
    const m = new Map<string, number>();
    (leadsQ.data ?? []).forEach(l => m.set(l.productKey, (m.get(l.productKey) ?? 0) + 1));
    return m;
  }, [leadsQ.data]);

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
          <div className="card p-3 overflow-y-auto" style={{ width: 240 }}>
            <div className="text-xs uppercase tracking-wide text-slate-500 mb-2">Vendedores</div>
            {(sellersQ.data ?? []).map(s => {
              const active = activeSellerId === s.sellerId;
              const count = draftAssignments[s.sellerId]?.size ?? 0;
              return (
                <button
                  key={s.sellerId}
                  onClick={() => setActiveSellerId(active ? null : s.sellerId)}
                  className={`w-full flex items-center gap-2 px-2 py-1.5 rounded text-left text-sm ${
                    active ? 'bg-slate-100 ring-1 ring-slate-300' : 'hover:bg-slate-50'
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
            {editing && (
              <div className="mt-3 text-xs text-slate-500 border-t border-slate-200 pt-2">
                {activeSellerId
                  ? 'Click + arrastrar sobre el mapa para pintar/borrar zonas. Una zona puede tener varios vendedores.'
                  : 'Elegí un vendedor del listado para empezar a pintar.'}
              </div>
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

      {/* Active lead detail link via popup is handled by maplibre. Detail card omitted. */}
      <div className="text-xs text-slate-400">
        <Link to="/leads" className="hover:underline">Ir a leads →</Link>
      </div>
    </div>
  );
}

function escapeHtml(s: string) {
  return s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!));
}
