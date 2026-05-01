import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { MapContainer, TileLayer, CircleMarker, Tooltip, Popup } from 'react-leaflet';
import { Link } from 'react-router-dom';
import 'leaflet/dist/leaflet.css';
import toast from 'react-hot-toast';
import clsx from 'clsx';
import { api } from '../lib/api';
import { isAdmin, useAuthStore } from '../lib/auth';
import type { Product, Seller } from '../lib/types';

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

type Zone = { country: string; province: string };

const COLORS: Record<string, string> = {
  gymhero: '#1e8dff',
  bookingpro_barber: '#f59e0b',
  bookingpro_salon: '#ec4899',
  bookingpro_aesthetics: '#a855f7',
  unistock: '#10b981',
  playcrew: '#ef4444',
  bunker: '#64748b',
  construction: '#a16207'
};

// Stable color per seller for zone visualization.
const SELLER_PALETTE = [
  '#1e8dff', '#10b981', '#f59e0b', '#ec4899', '#a855f7',
  '#ef4444', '#0ea5e9', '#84cc16', '#f97316', '#6366f1'
];
const colorForSeller = (id: string) => {
  let h = 0;
  for (let i = 0; i < id.length; i++) h = (h * 31 + id.charCodeAt(i)) >>> 0;
  return SELLER_PALETTE[h % SELLER_PALETTE.length];
};

const norm = (s: string) => s.toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '').trim();

export default function MapPage() {
  const user = useAuthStore((s) => s.user);
  const admin = isAdmin(user);
  const [productKey, setProductKey] = useState('');
  const [zonesOpen, setZonesOpen] = useState(false);
  const [colorBy, setColorBy] = useState<'product' | 'seller'>('product');

  const products = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  const leads = useQuery({
    queryKey: ['map-leads', productKey],
    queryFn: async () => (await api.get<MapLead[]>('/leads/map', {
      params: { productKey: productKey || undefined, limit: 2000 }
    })).data,
    refetchInterval: 30_000
  });

  const productCounts = useMemo(() => {
    const m = new Map<string, number>();
    (leads.data ?? []).forEach(l => m.set(l.productKey, (m.get(l.productKey) ?? 0) + 1));
    return m;
  }, [leads.data]);

  const markerColor = (l: MapLead) => {
    if (colorBy === 'seller') {
      return l.sellerId ? colorForSeller(l.sellerId) : '#cbd5e1';
    }
    return COLORS[l.productKey] ?? '#64748b';
  };

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <h1 className="text-xl md:text-2xl font-bold">Mapa de leads</h1>
        <div className="flex gap-2 items-end flex-wrap">
          <div>
            <label className="text-xs text-slate-500">Producto</label>
            <select className="input w-full sm:w-48" value={productKey} onChange={e => setProductKey(e.target.value)}>
              <option value="">Todos</option>
              {(products.data ?? []).map(p => (
                <option key={p.productKey} value={p.productKey}>{p.displayName}</option>
              ))}
            </select>
          </div>
          <div>
            <label className="text-xs text-slate-500">Colorear por</label>
            <select className="input w-full sm:w-36" value={colorBy} onChange={e => setColorBy(e.target.value as 'product' | 'seller')}>
              <option value="product">Producto</option>
              <option value="seller">Vendedor</option>
            </select>
          </div>
          {admin && (
            <button
              type="button"
              onClick={() => setZonesOpen((o) => !o)}
              className={clsx('btn-secondary text-sm', zonesOpen && 'ring-2 ring-brand-300')}>
              {zonesOpen ? '× Cerrar zonas' : 'Asignar zonas'}
            </button>
          )}
        </div>
      </div>

      <div className="flex flex-wrap gap-2 text-xs">
        {[...productCounts.entries()].map(([k, n]) => (
          <span key={k} className="inline-flex items-center gap-1 bg-white border border-slate-200 rounded-full px-2 py-1">
            <span className="w-3 h-3 rounded-full" style={{ background: COLORS[k] ?? '#64748b' }} />
            <span>{k}: <strong>{n}</strong></span>
          </span>
        ))}
        {productCounts.size === 0 && !leads.isLoading && (
          <span className="text-slate-500">
            Ningún lead tiene coordenadas todavía. Los próximos leads de Google Maps Apify van a aparecer acá automáticamente.
          </span>
        )}
      </div>

      {admin && zonesOpen && <ZonesPanel onClose={() => setZonesOpen(false)} />}

      <div className="card overflow-hidden h-[60vh] md:h-[70vh]">
        <MapContainer center={[-38.5, -63.6]} zoom={4} style={{ height: '100%', width: '100%' }}>
          <TileLayer
            attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
            url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
          />
          {(leads.data ?? []).map(l => (
            <CircleMarker
              key={l.id}
              center={[l.latitude, l.longitude]}
              radius={9}
              pathOptions={{
                color: markerColor(l),
                fillColor: markerColor(l),
                fillOpacity: 0.7,
                weight: 1.5
              }}>
              <Tooltip>{l.name}</Tooltip>
              <Popup>
                <div className="text-sm space-y-1 min-w-[220px]">
                  <div className="font-semibold">{l.name}</div>
                  {l.address && <div className="text-slate-600">{l.address}</div>}
                  <div className="text-xs text-slate-500">
                    {l.productKey}{l.sellerName ? ` · ${l.sellerName}` : ''} · {l.status}
                  </div>
                  {l.whatsappPhone && (
                    <a
                      href={`https://wa.me/${l.whatsappPhone}`}
                      target="_blank" rel="noreferrer"
                      className="text-emerald-600 hover:underline text-xs block">
                      WhatsApp: +{l.whatsappPhone}
                    </a>
                  )}
                  <Link to={`/leads/${l.id}`} className="text-brand-600 underline text-xs block">Ver detalle</Link>
                </div>
              </Popup>
            </CircleMarker>
          ))}
        </MapContainer>
      </div>
    </div>
  );
}

function ZonesPanel({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient();
  const [query, setQuery] = useState('');
  const [highlight, setHighlight] = useState(0);

  const sellersQ = useQuery({
    queryKey: ['sellers'],
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data
  });

  const zonesQ = useQuery({
    queryKey: ['admin-zones', 'AR'],
    queryFn: async () => (await api.get<Zone[]>('/admin/zones', { params: { country: 'AR' } })).data
  });

  const allProvinces = useMemo(
    () => (zonesQ.data ?? []).map((z) => z.province).filter(Boolean).sort((a, b) => a.localeCompare(b, 'es')),
    [zonesQ.data]
  );

  const sellers = (sellersQ.data ?? []).filter((s) => s.isActive && s.role !== 'Admin');

  // Sellers indexed by region (case-insensitive) so chips can show ownership.
  const ownerOf = useMemo(() => {
    const m = new Map<string, Seller>();
    sellers.forEach((s) => (s.regionsAssigned ?? []).forEach((r) => m.set(norm(r), s)));
    return m;
  }, [sellers]);

  const matches = useMemo(() => {
    const q = norm(query);
    if (!q) return allProvinces.slice(0, 8);
    return allProvinces.filter((p) => norm(p).includes(q)).slice(0, 8);
  }, [query, allProvinces]);

  async function assign(province: string, sellerId: string) {
    const seller = sellers.find((s) => s.id === sellerId);
    if (!seller) return;
    // Remove the province from any other seller (one province → one owner).
    const others = sellers.filter((s) => s.id !== sellerId &&
      (s.regionsAssigned ?? []).some((r) => norm(r) === norm(province)));
    const next = Array.from(new Set([...(seller.regionsAssigned ?? []), province]));
    try {
      await Promise.all([
        api.put(`/sellers/${seller.id}`, { regionsAssigned: next }),
        ...others.map((o) => api.put(`/sellers/${o.id}`, {
          regionsAssigned: (o.regionsAssigned ?? []).filter((r) => norm(r) !== norm(province))
        }))
      ]);
      toast.success(`${province} → ${seller.displayName}`);
      qc.invalidateQueries({ queryKey: ['sellers'] });
      setQuery('');
    } catch (err: any) {
      toast.error(err.response?.data?.error ?? 'No se pudo asignar');
    }
  }

  async function unassign(seller: Seller, region: string) {
    const next = (seller.regionsAssigned ?? []).filter((r) => norm(r) !== norm(region));
    try {
      await api.put(`/sellers/${seller.id}`, { regionsAssigned: next });
      qc.invalidateQueries({ queryKey: ['sellers'] });
    } catch (err: any) {
      toast.error(err.response?.data?.error ?? 'No se pudo quitar');
    }
  }

  const exactMatch = matches.find((p) => norm(p) === norm(query));
  const selected = exactMatch ?? (matches.length === 1 ? matches[0] : matches[highlight] ?? null);

  return (
    <div className="card p-4 space-y-4 border-brand-200">
      <div className="flex items-start justify-between gap-2">
        <div>
          <h2 className="font-semibold">Asignar zonas a vendedores</h2>
          <p className="text-xs text-slate-500">
            Tipeá una provincia y asignala a un vendedor. Una provincia tiene un solo dueño — al
            asignar a otro, se quita del anterior. Vendedores sin zonas reciben los leads que no
            matchean ninguna asignación (catch-all).
          </p>
        </div>
        <button onClick={onClose} className="text-slate-400 hover:text-slate-700 text-xl leading-none -mt-1">×</button>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div className="space-y-2">
          <label className="text-xs text-slate-500">Buscar provincia</label>
          <input
            className="input"
            placeholder="Ej. santa fe, buenos aires…"
            value={query}
            onChange={(e) => { setQuery(e.target.value); setHighlight(0); }}
            onKeyDown={(e) => {
              if (e.key === 'ArrowDown') { setHighlight((h) => Math.min(h + 1, matches.length - 1)); e.preventDefault(); }
              if (e.key === 'ArrowUp') { setHighlight((h) => Math.max(h - 1, 0)); e.preventDefault(); }
            }}
            autoFocus
          />
          {matches.length > 0 && (
            <ul className="border border-slate-200 rounded divide-y divide-slate-100 max-h-56 overflow-y-auto">
              {matches.map((p, i) => {
                const owner = ownerOf.get(norm(p));
                const isSelected = selected === p;
                return (
                  <li
                    key={p}
                    onMouseEnter={() => setHighlight(i)}
                    className={clsx(
                      'flex items-center justify-between gap-2 px-3 py-2 text-sm cursor-default',
                      isSelected && 'bg-brand-50'
                    )}>
                    <span>{p}</span>
                    {owner && (
                      <span
                        className="text-xs px-2 py-0.5 rounded-full text-white"
                        style={{ background: colorForSeller(owner.id) }}
                        title="Asignado actualmente">
                        {owner.displayName}
                      </span>
                    )}
                  </li>
                );
              })}
            </ul>
          )}
          {selected && (
            <div className="border border-slate-200 rounded p-3 space-y-2 bg-slate-50">
              <div className="text-xs text-slate-600">
                Asignar <strong>{selected}</strong> a:
              </div>
              <div className="flex flex-wrap gap-2">
                {sellers.length === 0 ? (
                  <span className="text-xs text-slate-500">No hay vendedores activos</span>
                ) : sellers.map((s) => (
                  <button
                    key={s.id}
                    onClick={() => assign(selected, s.id)}
                    className="text-xs px-2 py-1 rounded-full text-white hover:opacity-90"
                    style={{ background: colorForSeller(s.id) }}>
                    {s.displayName}
                  </button>
                ))}
              </div>
            </div>
          )}
          {!selected && query && (
            <div className="text-xs text-slate-500">Sin coincidencias.</div>
          )}
        </div>

        <div className="space-y-2">
          <div className="text-xs text-slate-500">Vendedores y sus zonas</div>
          {sellersQ.isLoading && <div className="text-sm text-slate-500">Cargando…</div>}
          <ul className="space-y-2">
            {sellers.map((s) => (
              <li key={s.id} className="border border-slate-200 rounded p-2">
                <div className="flex items-center gap-2 mb-1">
                  <span
                    className="inline-block w-3 h-3 rounded-full"
                    style={{ background: colorForSeller(s.id) }} />
                  <span className="text-sm font-medium">{s.displayName}</span>
                  {(!s.regionsAssigned || s.regionsAssigned.length === 0) && (
                    <span className="ml-auto text-[10px] uppercase tracking-wide text-slate-400">
                      catch-all
                    </span>
                  )}
                </div>
                <div className="flex flex-wrap gap-1">
                  {(s.regionsAssigned ?? []).map((r) => (
                    <span
                      key={r}
                      className="inline-flex items-center gap-1 text-xs bg-slate-100 rounded-full pl-2 pr-1 py-0.5">
                      {r}
                      <button
                        onClick={() => unassign(s, r)}
                        className="text-slate-400 hover:text-rose-600 px-1"
                        title="Quitar">
                        ×
                      </button>
                    </span>
                  ))}
                </div>
              </li>
            ))}
          </ul>
        </div>
      </div>
    </div>
  );
}
