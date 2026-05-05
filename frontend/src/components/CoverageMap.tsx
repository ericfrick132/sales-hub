import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import maplibregl, { Map as MlMap, MapMouseEvent } from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { api } from '../lib/api';

const LOCALITIES_GEOJSON_URL = '/data/localities-latam.geojson';

type ProductCount = { productKey: string; productName?: string; count: number };
type LastJob = {
  id: string;
  productKey: string;
  productName?: string;
  category?: string;
  query: string;
  leadsCreated: number;
  rawItems: number;
  sellerId: string;
  sellerName?: string;
  capturedAt: string;
};
export type GeoStatsCell = {
  localityGid2: string;
  localityName: string;
  adminLevel1Name: string;
  countryCode: string;
  centroidLat: number;
  centroidLng: number;
  leadsCount: number;
  products: ProductCount[];
  lastJob: LastJob | null;
};

type ProductMin = { productKey: string; displayName: string; categories?: string[] };

type Props = {
  productKey: string;
  onProductChange: (v: string) => void;
  products: ProductMin[];
};

export default function CoverageMap({ productKey, onProductChange, products }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<MlMap | null>(null);
  const [mapReady, setMapReady] = useState(false);
  const [geojsonMissing, setGeojsonMissing] = useState(false);
  const [days, setDays] = useState(90);
  const [category, setCategory] = useState('');

  const stats = useQuery({
    queryKey: ['leads-geo-stats', productKey, category, days],
    queryFn: async () => (await api.get<GeoStatsCell[]>('/leads/geo-stats', {
      params: {
        productKey: productKey || undefined,
        category: category || undefined,
        days
      }
    })).data,
    refetchInterval: 30_000
  });

  const byGid = useMemo(() => {
    const m = new Map<string, GeoStatsCell>();
    (stats.data ?? []).forEach((c) => m.set(c.localityGid2, c));
    return m;
  }, [stats.data]);

  const maxCount = useMemo(() => {
    return (stats.data ?? []).reduce((acc, c) => Math.max(acc, c.leadsCount), 0);
  }, [stats.data]);

  // Producto seleccionado → ofrecemos sus categorías como filtro fino.
  const categoriesForProduct = useMemo(() => {
    if (!productKey) return [] as string[];
    const p = products.find((p) => p.productKey === productKey);
    return p?.categories ?? [];
  }, [productKey, products]);

  // Init map
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
      center: [-63.6, -34.6],
      zoom: 3.5,
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
          paint: { 'fill-color': 'rgba(0,0,0,0)', 'fill-opacity': 0.7 }
        });
        map.addLayer({
          id: 'localities-outline',
          type: 'line',
          source: 'localities',
          paint: { 'line-color': '#475569', 'line-width': 0.3, 'line-opacity': 0.5 }
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
        console.warn('No se pudo cargar GeoJSON localidades:', err);
        setGeojsonMissing(true);
      }
    });

    return () => { map.remove(); mapRef.current = null; };
  }, []);

  // Heat-fill: gradient verde según leadsCount / maxCount.
  const fillExpression = useMemo(() => {
    const stops: unknown[] = ['match', ['get', 'gid2']];
    for (const cell of byGid.values()) {
      const t = maxCount > 0 ? cell.leadsCount / maxCount : 0;
      stops.push(cell.localityGid2, colorForRatio(t));
    }
    stops.push('rgba(0,0,0,0)');
    return stops;
  }, [byGid, maxCount]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapReady || !map.getLayer('localities-fill')) return;
    map.setPaintProperty('localities-fill', 'fill-color', fillExpression as never);
  }, [fillExpression, mapReady]);

  // Hover popup with stats + last job.
  useEffect(() => {
    const m = mapRef.current;
    if (!m || !mapReady || !m.getLayer('localities-fill')) return;
    const popup = new maplibregl.Popup({ closeButton: false, closeOnClick: false, offset: 6, maxWidth: '280px' });
    const onMove = (e: MapMouseEvent & { features?: maplibregl.MapGeoJSONFeature[] }) => {
      const f = e.features?.[0];
      if (!f) { m.setFilter('localities-hover', ['==', ['get', 'gid2'], '__none__']); popup.remove(); return; }
      const props = f.properties as { gid2: string; name: string; adm1Name?: string; countryName?: string };
      m.setFilter('localities-hover', ['==', ['get', 'gid2'], props.gid2]);
      const cell = byGid.get(props.gid2);
      const head = `<div class="font-semibold">${escapeHtml(props.name)}</div>
        <div class="text-slate-500 text-[11px]">${escapeHtml(props.adm1Name ?? '')}${props.countryName ? `, ${escapeHtml(props.countryName)}` : ''}</div>`;
      let body = '';
      if (!cell || cell.leadsCount === 0) {
        body = `<div class="text-slate-500 mt-1">Sin leads cargados todavía.</div>`;
      } else {
        const products = cell.products
          .slice(0, 4)
          .map((p) => `<div class="flex justify-between gap-3"><span>${escapeHtml(p.productName ?? p.productKey)}</span><b>${p.count}</b></div>`)
          .join('');
        const job = cell.lastJob;
        const jobBlock = job
          ? `<div class="mt-2 pt-1 border-t border-slate-200">
              <div class="text-[11px] uppercase tracking-wide text-slate-500">Última búsqueda</div>
              <div class="truncate" title="${escapeHtml(job.query)}">${escapeHtml(job.query)}</div>
              <div class="text-[11px] text-slate-500">
                ${escapeHtml(job.productName ?? job.productKey)}${job.category ? ` · ${escapeHtml(job.category)}` : ''}
                · ${job.leadsCreated}/${job.rawItems}
              </div>
              <div class="text-[11px] text-slate-500">
                por ${escapeHtml(job.sellerName ?? '?')} · ${new Date(job.capturedAt).toLocaleString()}
              </div>
            </div>`
          : '';
        body = `<div class="mt-1">
          <div class="font-semibold text-emerald-700">${cell.leadsCount} leads</div>
          ${products}
          ${jobBlock}
        </div>`;
      }
      popup.setLngLat(e.lngLat).setHTML(`<div class="text-xs">${head}${body}</div>`).addTo(m);
    };
    const onLeave = () => { m.setFilter('localities-hover', ['==', ['get', 'gid2'], '__none__']); popup.remove(); };
    m.on('mousemove', 'localities-fill', onMove);
    m.on('mouseleave', 'localities-fill', onLeave);
    return () => {
      m.off('mousemove', 'localities-fill', onMove);
      m.off('mouseleave', 'localities-fill', onLeave);
      popup.remove();
    };
  }, [mapReady, byGid]);

  const totals = useMemo(() => {
    const cells = stats.data ?? [];
    const leads = cells.reduce((a, c) => a + c.leadsCount, 0);
    return { localities: cells.length, leads };
  }, [stats.data]);

  return (
    <div className="card p-4 space-y-3">
      <div className="flex items-center justify-between flex-wrap gap-2">
        <div>
          <div className="font-semibold">Cobertura del país</div>
          <div className="text-xs text-slate-500">
            Cada zona se pinta más fuerte cuanto más leads cargaste ahí. Pasá el mouse para ver detalles.
          </div>
        </div>
        <div className="flex flex-wrap items-end gap-2">
          <div>
            <label className="text-xs text-slate-500 block">Producto</label>
            <select className="input" value={productKey} onChange={(e) => { onProductChange(e.target.value); setCategory(''); }}>
              <option value="">Todos</option>
              {products.map((p) => (
                <option key={p.productKey} value={p.productKey}>{p.displayName}</option>
              ))}
            </select>
          </div>
          <div>
            <label className="text-xs text-slate-500 block">Categoría</label>
            <select className="input" value={category} onChange={(e) => setCategory(e.target.value)} disabled={categoriesForProduct.length === 0}>
              <option value="">Todas</option>
              {categoriesForProduct.map((c) => (
                <option key={c} value={c}>{c}</option>
              ))}
            </select>
          </div>
          <div>
            <label className="text-xs text-slate-500 block">Período</label>
            <select className="input" value={days} onChange={(e) => setDays(Number(e.target.value))}>
              <option value={7}>7 días</option>
              <option value={30}>30 días</option>
              <option value={90}>90 días</option>
              <option value={365}>1 año</option>
            </select>
          </div>
        </div>
      </div>

      <div className="relative" style={{ height: '420px' }}>
        <div ref={containerRef} className="w-full h-full rounded-md overflow-hidden border border-slate-200" />
        {geojsonMissing && (
          <div className="absolute inset-0 grid place-items-center bg-white/85 rounded-md">
            <div className="text-center text-sm text-slate-600 max-w-md p-6">
              Faltan los polígonos de localidades. Corré los scripts del seed (<code>scripts/localities/build.mjs</code> + <code>import.mjs</code>).
            </div>
          </div>
        )}
        <Legend maxCount={maxCount} />
      </div>

      <div className="flex items-center justify-between flex-wrap gap-2 text-xs text-slate-600">
        <div>
          <b>{totals.leads}</b> leads en <b>{totals.localities}</b> zonas
          {category && <> · categoría <b>{category}</b></>}
          {' '}· últimos {days} días
        </div>
        {stats.isFetching && <span className="text-slate-400">actualizando…</span>}
      </div>
    </div>
  );
}

function colorForRatio(t: number): string {
  if (t <= 0) return '#e2e8f0';
  // Verde claro → verde oscuro.
  const stops: Array<[number, string]> = [
    [0.0, '#dcfce7'],
    [0.25, '#86efac'],
    [0.5, '#22c55e'],
    [0.75, '#15803d'],
    [1.0, '#14532d']
  ];
  for (let i = 1; i < stops.length; i++) {
    const [tA, cA] = stops[i - 1];
    const [tB, cB] = stops[i];
    if (t <= tB) return mix(cA, cB, (t - tA) / (tB - tA));
  }
  return stops[stops.length - 1][1];
}

function mix(a: string, b: string, t: number): string {
  const pa = parseHex(a); const pb = parseHex(b);
  const r = Math.round(pa[0] + (pb[0] - pa[0]) * t);
  const g = Math.round(pa[1] + (pb[1] - pa[1]) * t);
  const bl = Math.round(pa[2] + (pb[2] - pa[2]) * t);
  return `rgb(${r}, ${g}, ${bl})`;
}

function parseHex(c: string): [number, number, number] {
  const m = c.replace('#', '');
  return [parseInt(m.slice(0, 2), 16), parseInt(m.slice(2, 4), 16), parseInt(m.slice(4, 6), 16)];
}

function Legend({ maxCount }: { maxCount: number }) {
  if (maxCount <= 0) return null;
  return (
    <div className="absolute bottom-2 left-2 bg-white/90 border border-slate-200 rounded-md px-2 py-1 text-[10px] text-slate-700 shadow-sm">
      <div className="mb-1 uppercase tracking-wide text-slate-500">Leads por zona</div>
      <div className="flex items-center gap-1">
        <span>0</span>
        <div className="h-2 w-32 rounded-sm" style={{
          background: 'linear-gradient(to right, #dcfce7, #86efac, #22c55e, #15803d, #14532d)'
        }} />
        <span>{maxCount}</span>
      </div>
    </div>
  );
}

function escapeHtml(s: string) {
  return s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!));
}
