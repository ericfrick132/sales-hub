import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { MapContainer, TileLayer, CircleMarker, Tooltip, Popup } from 'react-leaflet';
import { Link } from 'react-router-dom';
import 'leaflet/dist/leaflet.css';
import { api } from '../lib/api';
import type { Product } from '../lib/types';

type MapLead = {
  id: string;
  name: string;
  productKey: string;
  city?: string;
  province?: string;
  latitude: number;
  longitude: number;
  status: string;
  sellerId?: string;
};

// Palette per product_key (stable colors).
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

export default function MapPage() {
  const [productKey, setProductKey] = useState('');

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

  // Group leads by product_key for legend count.
  const productCounts = useMemo(() => {
    const m = new Map<string, number>();
    (leads.data ?? []).forEach(l => m.set(l.productKey, (m.get(l.productKey) ?? 0) + 1));
    return m;
  }, [leads.data]);

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-2xl font-bold">Mapa de leads</h1>
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

      <div className="card overflow-hidden" style={{ height: '70vh' }}>
        <MapContainer center={[-38.5, -63.6]} zoom={4} style={{ height: '100%', width: '100%' }}>
          <TileLayer
            attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
            url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
          />
          {(leads.data ?? []).map(l => (
            <CircleMarker
              key={l.id}
              center={[l.latitude, l.longitude]}
              radius={6}
              pathOptions={{
                color: COLORS[l.productKey] ?? '#64748b',
                fillColor: COLORS[l.productKey] ?? '#64748b',
                fillOpacity: 0.7,
                weight: 1
              }}>
              <Tooltip>{l.name}</Tooltip>
              <Popup>
                <div className="text-sm">
                  <div className="font-semibold">{l.name}</div>
                  <div className="text-slate-500">{l.productKey}</div>
                  <div>{l.city ?? '?'}, {l.province ?? '?'}</div>
                  <div>Estado: {l.status}</div>
                  <Link to={`/leads/${l.id}`} className="text-brand-600 underline text-xs">Ver detalle</Link>
                </div>
              </Popup>
            </CircleMarker>
          ))}
        </MapContainer>
      </div>
    </div>
  );
}
