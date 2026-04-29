import { useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
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
  Mega: 11, Big: 9, Medium: 7, Small: 5, Town: 4
};

// Stable palette assigned by seller index (sorted by name).
const PALETTE = [
  '#1e8dff', // azul
  '#ef4444', // rojo
  '#10b981', // verde
  '#f59e0b', // ámbar
  '#a855f7', // violeta
  '#ec4899', // rosa
  '#06b6d4', // cian
  '#84cc16', // lima
  '#a16207', // marrón
  '#64748b'  // pizarra
];

const COLOR_GRAY = '#cbd5e1';

export default function SellerZones() {
  const qc = useQueryClient();
  const [params, setParams] = useSearchParams();

  const sellersQ = useQuery({
    queryKey: ['sellers'],
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data
  });
  const citiesQ = useQuery({
    queryKey: ['cities-map'],
    queryFn: async () => (await api.get<CityPin[]>('/cities/map', { params: { country: 'AR' } })).data
  });

  // Per-seller working copy of regions (lowercase keys).
  const [zones, setZones] = useState<Record<string, Set<string>>>({});
  const [activeSellerId, setActiveSellerId] = useState<string | null>(params.get('seller'));
  const [provinceFilter, setProvinceFilter] = useState<string>('');
  const [saving, setSaving] = useState(false);

  // Hydrate from server when sellers load.
  useEffect(() => {
    if (!sellersQ.data) return;
    const init: Record<string, Set<string>> = {};
    for (const s of sellersQ.data.filter((s) => s.isActive)) {
      init[s.id] = new Set((s.regionsAssigned ?? []).map((r) => r.toLowerCase()));
    }
    setZones(init);
  }, [sellersQ.data?.length]);

  const sellers = useMemo(
    () => (sellersQ.data ?? [])
      .filter((s) => s.isActive)
      .sort((a, b) => a.displayName.localeCompare(b.displayName)),
    [sellersQ.data]
  );

  const colorFor = (sellerId: string) => {
    const idx = sellers.findIndex((s) => s.id === sellerId);
    return idx >= 0 ? PALETTE[idx % PALETTE.length] : COLOR_GRAY;
  };

  const cities = citiesQ.data ?? [];
  const visible = provinceFilter ? cities.filter((c) => c.province === provinceFilter) : cities;
  const provinces = useMemo(() => {
    const s = new Set<string>();
    cities.forEach((c) => s.add(c.province));
    return [...s].sort();
  }, [cities]);

  function getAssignees(c: CityPin): string[] {
    const cityKey = c.city.toLowerCase();
    const provKey = c.province.toLowerCase();
    const out: string[] = [];
    for (const s of sellers) {
      const set = zones[s.id];
      if (!set) continue;
      if (set.has(cityKey) || set.has(provKey)) out.push(s.id);
    }
    return out;
  }

  function isDirty(sellerId: string): boolean {
    const seller = sellers.find((s) => s.id === sellerId);
    if (!seller) return false;
    const orig = new Set((seller.regionsAssigned ?? []).map((r) => r.toLowerCase()));
    const cur = zones[sellerId] ?? new Set();
    if (orig.size !== cur.size) return true;
    for (const k of cur) if (!orig.has(k)) return true;
    for (const k of orig) if (!cur.has(k)) return true;
    return false;
  }

  const dirtySellerIds = sellers.filter((s) => isDirty(s.id)).map((s) => s.id);

  function toggleCity(c: CityPin) {
    if (!activeSellerId) {
      toast.error('Elegí un vendedor en el panel izquierdo para asignar');
      return;
    }
    setZones((prev) => {
      const next = { ...prev };
      const cur = new Set(next[activeSellerId]);
      const k = c.city.toLowerCase();
      if (cur.has(k)) cur.delete(k);
      else cur.add(k);
      next[activeSellerId] = cur;
      return next;
    });
  }

  function toggleProvince(province: string) {
    if (!activeSellerId) {
      toast.error('Elegí un vendedor primero');
      return;
    }
    setZones((prev) => {
      const next = { ...prev };
      const cur = new Set(next[activeSellerId]);
      const k = province.toLowerCase();
      if (cur.has(k)) cur.delete(k);
      else cur.add(k);
      next[activeSellerId] = cur;
      return next;
    });
  }

  async function saveAll() {
    if (dirtySellerIds.length === 0) return;
    const cityByLower = new Map(cities.map((c) => [c.city.toLowerCase(), c.city]));
    const provinceByLower = new Map(cities.map((c) => [c.province.toLowerCase(), c.province]));
    setSaving(true);
    try {
      await Promise.all(dirtySellerIds.map((id) => {
        const regions = [...(zones[id] ?? [])].map(
          (k) => cityByLower.get(k) ?? provinceByLower.get(k) ?? k
        );
        return api.put(`/sellers/${id}`, { regionsAssigned: regions });
      }));
      toast.success(`Guardado (${dirtySellerIds.length} vendedor${dirtySellerIds.length === 1 ? '' : 'es'})`);
      qc.invalidateQueries({ queryKey: ['sellers'] });
    } catch (err) {
      const e = err as { response?: { data?: { error?: string } } };
      toast.error(e?.response?.data?.error ?? 'Falló');
    } finally {
      setSaving(false);
    }
  }

  function discardAll() {
    if (!sellersQ.data) return;
    const init: Record<string, Set<string>> = {};
    for (const s of sellersQ.data.filter((s) => s.isActive)) {
      init[s.id] = new Set((s.regionsAssigned ?? []).map((r) => r.toLowerCase()));
    }
    setZones(init);
  }

  function selectSeller(id: string | null) {
    setActiveSellerId(id);
    if (id) setParams({ seller: id }, { replace: true });
    else setParams({}, { replace: true });
  }

  function citiesAssignedTo(sellerId: string): number {
    const cur = zones[sellerId];
    if (!cur) return 0;
    // Count both direct city tags and cities under province tags.
    let n = 0;
    for (const c of cities) {
      if (cur.has(c.city.toLowerCase()) || cur.has(c.province.toLowerCase())) n++;
    }
    return n;
  }

  if (sellersQ.isLoading || citiesQ.isLoading) return <div>Cargando…</div>;

  const activeSeller = sellers.find((s) => s.id === activeSellerId);

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-2xl font-bold">Zonas por vendedor</h1>
        <div className="flex items-center gap-2">
          {dirtySellerIds.length > 0 && (
            <span className="text-xs text-amber-700">
              {dirtySellerIds.length} cambio{dirtySellerIds.length === 1 ? '' : 's'} sin guardar
            </span>
          )}
          {dirtySellerIds.length > 0 && (
            <button className="btn-secondary" onClick={discardAll} disabled={saving}>
              Descartar
            </button>
          )}
          <button
            className="btn-primary"
            onClick={saveAll}
            disabled={saving || dirtySellerIds.length === 0}>
            {saving ? 'Guardando…' : `Guardar (${dirtySellerIds.length})`}
          </button>
        </div>
      </div>

      <div className="grid grid-cols-12 gap-4">
        {/* Sidebar */}
        <aside className="col-span-12 lg:col-span-3 space-y-3">
          <div className="card p-3">
            <div className="text-xs uppercase tracking-wide text-slate-500 mb-2">Vendedores</div>
            <div className="space-y-1">
              {sellers.map((s) => {
                const cnt = citiesAssignedTo(s.id);
                const dirty = isDirty(s.id);
                const isActive = activeSellerId === s.id;
                return (
                  <button
                    key={s.id}
                    onClick={() => selectSeller(isActive ? null : s.id)}
                    className={`w-full flex items-center justify-between gap-2 px-2 py-1.5 rounded text-sm text-left transition ${
                      isActive ? 'bg-slate-900 text-white' : 'hover:bg-slate-100 text-slate-700'
                    }`}>
                    <span className="flex items-center gap-2 truncate">
                      <span
                        className="w-3 h-3 rounded-full flex-shrink-0 ring-2 ring-white shadow-sm"
                        style={{ background: colorFor(s.id) }} />
                      <span className="truncate">{s.displayName}</span>
                      {dirty && <span className={`text-[10px] ${isActive ? 'text-amber-300' : 'text-amber-600'}`}>•</span>}
                    </span>
                    <span className={`text-xs ${isActive ? 'text-slate-300' : 'text-slate-500'}`}>{cnt}</span>
                  </button>
                );
              })}
            </div>
            <div className="text-[11px] text-slate-500 mt-2 pt-2 border-t border-slate-100">
              {activeSeller
                ? <>Click en el mapa para asignar/quitar ciudades a <strong>{activeSeller.displayName}</strong>.</>
                : <>Elegí un vendedor para empezar a asignar zonas.</>}
            </div>
          </div>

          <div className="card p-3 max-h-[50vh] overflow-y-auto">
            <div className="flex items-center justify-between mb-2">
              <div className="text-xs uppercase tracking-wide text-slate-500">Provincias</div>
              <button
                onClick={() => setProvinceFilter('')}
                className={`text-xs ${provinceFilter === '' ? 'text-brand-700 font-medium' : 'text-slate-500 hover:text-slate-700'}`}>
                Todas
              </button>
            </div>
            <ul className="space-y-1">
              {provinces.map((p) => {
                const provSelected = activeSellerId
                  ? (zones[activeSellerId]?.has(p.toLowerCase()) ?? false)
                  : false;
                return (
                  <li key={p} className="flex items-center justify-between gap-2 text-sm">
                    <button
                      onClick={() => setProvinceFilter(p === provinceFilter ? '' : p)}
                      className={`text-left flex-1 truncate hover:text-slate-900 ${provinceFilter === p ? 'text-brand-700 font-medium' : 'text-slate-700'}`}>
                      {p}
                    </button>
                    <button
                      disabled={!activeSellerId}
                      onClick={() => toggleProvince(p)}
                      title={activeSellerId
                        ? (provSelected ? 'Desasignar provincia entera' : 'Asignar provincia entera al vendedor activo')
                        : 'Elegí un vendedor primero'}
                      className={`text-[10px] px-1.5 py-0.5 rounded border ${
                        provSelected
                          ? 'bg-emerald-50 text-emerald-700 border-emerald-200'
                          : activeSellerId
                            ? 'bg-slate-50 text-slate-500 border-slate-200 hover:bg-slate-100'
                            : 'bg-slate-50 text-slate-300 border-slate-200 cursor-not-allowed'
                      }`}>
                      {provSelected ? '✓' : '+'}
                    </button>
                  </li>
                );
              })}
            </ul>
          </div>
        </aside>

        {/* Map */}
        <div className="col-span-12 lg:col-span-9 card overflow-hidden" style={{ height: '78vh' }}>
          <MapContainer center={[-38.5, -63.6]} zoom={4} style={{ height: '100%', width: '100%' }}>
            <TileLayer
              attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
              url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
            />
            {visible.map((c) => {
              if (c.latitude == null || c.longitude == null) return null;
              const assignees = getAssignees(c);
              const isActiveAssigned = activeSellerId !== null && assignees.includes(activeSellerId);
              const isOtherAssigned = assignees.length > 0 && !isActiveAssigned;

              // Color: active wins, otherwise first assignee, otherwise gray.
              let fillColor: string;
              let opacity: number;
              let weight: number;
              let strokeColor: string;

              if (activeSellerId && isActiveAssigned) {
                fillColor = colorFor(activeSellerId);
                opacity = 0.95;
                weight = 3;
                strokeColor = '#ffffff';
              } else if (activeSellerId && isOtherAssigned) {
                fillColor = colorFor(assignees[0]);
                opacity = 0.35;
                weight = 1;
                strokeColor = fillColor;
              } else if (!activeSellerId && assignees.length > 0) {
                fillColor = colorFor(assignees[0]);
                opacity = 0.85;
                weight = assignees.length > 1 ? 3 : 1;
                strokeColor = assignees.length > 1 ? '#1f2937' : fillColor;
              } else {
                fillColor = COLOR_GRAY;
                opacity = activeSellerId ? 0.5 : 0.4;
                weight = 1;
                strokeColor = fillColor;
              }

              return (
                <CircleMarker
                  key={c.id}
                  center={[c.latitude, c.longitude]}
                  radius={BUCKET_RADIUS[c.bucket] + (isActiveAssigned ? 2 : 0)}
                  eventHandlers={{ click: () => toggleCity(c) }}
                  pathOptions={{
                    color: strokeColor,
                    fillColor,
                    fillOpacity: opacity,
                    weight
                  }}>
                  <Tooltip>
                    <div className="text-xs">
                      <div className="font-semibold">{c.city}</div>
                      <div className="text-slate-500">{c.province} · {c.bucket}</div>
                      {assignees.length > 0 ? (
                        <div className="mt-1">
                          <span className="text-slate-500">Asignada a: </span>
                          {assignees.map((id) => (
                            <span
                              key={id}
                              className="inline-flex items-center gap-1 mr-1">
                              <span className="w-2 h-2 rounded-full" style={{ background: colorFor(id) }} />
                              {sellers.find((s) => s.id === id)?.displayName}
                            </span>
                          ))}
                        </div>
                      ) : (
                        <div className="text-slate-400 mt-1">sin asignar</div>
                      )}
                      {activeSellerId && (
                        <div className="text-slate-400 mt-1">
                          click → {isActiveAssigned ? 'quitar de' : 'asignar a'} <strong>{activeSeller?.displayName}</strong>
                        </div>
                      )}
                    </div>
                  </Tooltip>
                </CircleMarker>
              );
            })}
          </MapContainer>
        </div>
      </div>

      <div className="text-xs text-slate-500">
        ¿Falta una ciudad? Importá el catálogo desde{' '}
        <Link to="/pipeline" className="text-brand-700 hover:underline">Captación → Importar ciudades AR</Link>.
        {' · '}
        Las ciudades grises no están asignadas y van al fallback / Pool. Cuando un círculo tiene
        borde negro, es porque está compartida entre 2+ vendedores (round-robin).
      </div>
    </div>
  );
}
