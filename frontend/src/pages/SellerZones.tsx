import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { MapContainer, TileLayer, CircleMarker, Tooltip } from 'react-leaflet';
import 'leaflet/dist/leaflet.css';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import type { Seller } from '../lib/types';

type Bucket = 'Mega' | 'Big' | 'Medium' | 'Small' | 'Town';
interface CityPin {
  id: string;
  country: string;
  province: string;
  city: string;
  bucket: Bucket;
  latitude: number | null;
  longitude: number | null;
}

const BUCKET_RADIUS: Record<Bucket, number> = {
  Mega: 11,
  Big: 9,
  Medium: 7,
  Small: 5,
  Town: 4
};

const COLOR_ON = '#10b981';   // verde — asignado
const COLOR_OFF = '#94a3b8';  // gris — no asignado

export default function SellerZones() {
  const { id } = useParams<{ id: string }>();
  const nav = useNavigate();
  const qc = useQueryClient();

  const sellerQ = useQuery({
    queryKey: ['seller-detail', id],
    enabled: !!id,
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data.find((s) => s.id === id) ?? null
  });

  const citiesQ = useQuery({
    queryKey: ['cities-map'],
    queryFn: async () => (await api.get<CityPin[]>('/cities/map', { params: { country: 'AR' } })).data
  });

  // Local state for selection. Sync to seller's regionsAssigned when it loads.
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [filterProvince, setFilterProvince] = useState<string>('');
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (sellerQ.data) {
      setSelected(new Set((sellerQ.data.regionsAssigned ?? []).map((r) => r.toLowerCase())));
    }
  }, [sellerQ.data?.id]);

  const cities = citiesQ.data ?? [];
  const provinces = useMemo(() => {
    const s = new Set<string>();
    cities.forEach((c) => s.add(c.province));
    return [...s].sort();
  }, [cities]);

  const initial = useMemo(
    () => new Set((sellerQ.data?.regionsAssigned ?? []).map((r) => r.toLowerCase())),
    [sellerQ.data?.regionsAssigned]
  );
  const dirty =
    selected.size !== initial.size ||
    [...selected].some((s) => !initial.has(s)) ||
    [...initial].some((s) => !selected.has(s));

  function isSelected(c: CityPin) {
    return selected.has(c.city.toLowerCase()) || selected.has(c.province.toLowerCase());
  }

  function toggleCity(c: CityPin) {
    setSelected((prev) => {
      const next = new Set(prev);
      const k = c.city.toLowerCase();
      if (next.has(k)) next.delete(k);
      else next.add(k);
      return next;
    });
  }

  function toggleProvince(province: string) {
    const k = province.toLowerCase();
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(k)) next.delete(k);
      else next.add(k);
      return next;
    });
  }

  async function save() {
    if (!sellerQ.data) return;
    setSaving(true);
    try {
      // Preserve original casing for known cities/provinces; case-insensitive matched.
      const cityByLower = new Map<string, string>();
      cities.forEach((c) => cityByLower.set(c.city.toLowerCase(), c.city));
      const provinceByLower = new Map<string, string>();
      cities.forEach((c) => provinceByLower.set(c.province.toLowerCase(), c.province));
      const regions = [...selected].map((k) => cityByLower.get(k) ?? provinceByLower.get(k) ?? k);

      await api.put(`/sellers/${sellerQ.data.id}`, { regionsAssigned: regions });
      toast.success('Zonas guardadas');
      qc.invalidateQueries({ queryKey: ['sellers'] });
      qc.invalidateQueries({ queryKey: ['seller-detail'] });
    } catch (err) {
      const e = err as { response?: { data?: { error?: string } } };
      toast.error(e?.response?.data?.error ?? 'Falló');
    } finally {
      setSaving(false);
    }
  }

  function reset() {
    setSelected(new Set((sellerQ.data?.regionsAssigned ?? []).map((r) => r.toLowerCase())));
  }

  if (sellerQ.isLoading) return <div>Cargando…</div>;
  if (!sellerQ.data) return <div className="card p-6">Vendedor no encontrado.</div>;

  const visibleCities = filterProvince
    ? cities.filter((c) => c.province === filterProvince)
    : cities;

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <button className="btn-secondary" onClick={() => nav(-1)}>← Volver</button>
        <h1 className="text-2xl font-bold">Zonas — {sellerQ.data.displayName}</h1>
      </div>

      <div className="grid grid-cols-12 gap-4">
        {/* Sidebar */}
        <aside className="col-span-12 lg:col-span-4 space-y-3">
          <div className="card p-4 space-y-2">
            <div className="text-xs uppercase tracking-wide text-slate-500">Acciones</div>
            <div className="flex gap-2">
              <button
                className="btn-primary flex-1"
                disabled={!dirty || saving}
                onClick={save}>
                {saving ? 'Guardando…' : `Guardar ${selected.size > 0 ? `(${selected.size})` : ''}`}
              </button>
              {dirty && (
                <button className="btn-secondary" onClick={reset}>Descartar</button>
              )}
            </div>
            {dirty && <div className="text-xs text-amber-600">Hay cambios sin guardar</div>}
            <div className="text-[11px] text-slate-500 pt-1 border-t border-slate-100">
              Click en un punto del mapa para asignar/desasignar la ciudad. Click en una provincia
              de la lista para asignarla entera.
            </div>
          </div>

          <div className="card p-4 space-y-2 max-h-[55vh] overflow-y-auto">
            <div className="flex items-center justify-between">
              <div className="text-xs uppercase tracking-wide text-slate-500">Provincias</div>
              <button
                onClick={() => setFilterProvince('')}
                className={`text-xs ${filterProvince === '' ? 'text-brand-700 font-medium' : 'text-slate-500 hover:text-slate-700'}`}>
                Todas
              </button>
            </div>
            {provinces.map((p) => {
              const provSelected = selected.has(p.toLowerCase());
              return (
                <div key={p} className="flex items-center justify-between gap-2 text-sm">
                  <button
                    onClick={() => setFilterProvince(p === filterProvince ? '' : p)}
                    className={`text-left flex-1 truncate hover:text-slate-900 ${filterProvince === p ? 'text-brand-700 font-medium' : 'text-slate-700'}`}>
                    {p}
                  </button>
                  <button
                    onClick={() => toggleProvince(p)}
                    title={provSelected ? 'Desasignar provincia entera' : 'Asignar provincia entera'}
                    className={`text-xs px-2 py-0.5 rounded border ${
                      provSelected
                        ? 'bg-emerald-50 text-emerald-700 border-emerald-200'
                        : 'bg-slate-50 text-slate-500 border-slate-200 hover:bg-slate-100'
                    }`}>
                    {provSelected ? '✓ asignada' : '+ provincia'}
                  </button>
                </div>
              );
            })}
          </div>

          {selected.size > 0 && (
            <div className="card p-4 space-y-1">
              <div className="text-xs uppercase tracking-wide text-slate-500 mb-2">
                Asignadas ({selected.size})
              </div>
              <div className="flex flex-wrap gap-1">
                {[...selected].sort().map((s) => (
                  <span
                    key={s}
                    className="inline-flex items-center gap-1 text-xs bg-emerald-50 text-emerald-700 border border-emerald-200 rounded-full px-2 py-0.5">
                    {s}
                    <button
                      onClick={() => setSelected((prev) => { const n = new Set(prev); n.delete(s); return n; })}
                      className="hover:text-emerald-900">×</button>
                  </span>
                ))}
              </div>
            </div>
          )}
        </aside>

        {/* Map */}
        <div className="col-span-12 lg:col-span-8 card overflow-hidden" style={{ height: '75vh' }}>
          <MapContainer center={[-38.5, -63.6]} zoom={4} style={{ height: '100%', width: '100%' }}>
            <TileLayer
              attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
              url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
            />
            {visibleCities.map((c) => {
              if (c.latitude == null || c.longitude == null) return null;
              const on = isSelected(c);
              return (
                <CircleMarker
                  key={c.id}
                  center={[c.latitude, c.longitude]}
                  radius={BUCKET_RADIUS[c.bucket]}
                  eventHandlers={{ click: () => toggleCity(c) }}
                  pathOptions={{
                    color: on ? COLOR_ON : COLOR_OFF,
                    fillColor: on ? COLOR_ON : COLOR_OFF,
                    fillOpacity: on ? 0.85 : 0.45,
                    weight: on ? 2 : 1
                  }}>
                  <Tooltip>
                    <div className="text-xs">
                      <div className="font-semibold">{c.city}</div>
                      <div className="text-slate-500">{c.province} · {c.bucket}</div>
                      <div className="text-slate-400">click para {on ? 'desasignar' : 'asignar'}</div>
                    </div>
                  </Tooltip>
                </CircleMarker>
              );
            })}
          </MapContainer>
        </div>
      </div>

      <div className="text-xs text-slate-500">
        ¿Falta alguna ciudad? Importá el catálogo desde Captación → "Importar ciudades AR".
        {' '}
        <Link to="/pipeline" className="text-brand-700 hover:underline">Ir a Captación</Link>
      </div>
    </div>
  );
}
