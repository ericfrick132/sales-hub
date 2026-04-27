import { useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import type { LeadSource, Product } from '../lib/types';

type Props = {
  open: boolean;
  onClose: () => void;
  onDone: () => void;
  defaultSource?: LeadSource;
};

const SOURCE_OPTS: { value: LeadSource; label: string }[] = [
  { value: 'ApifyGoogleMaps', label: 'Google Maps' },
  { value: 'ApifyInstagram', label: 'Instagram' },
  { value: 'ApifyMetaAdsLibrary', label: 'Meta Ads Library' },
  { value: 'ApifyFacebookPages', label: 'Facebook Posts' },
  { value: 'GooglePlaces', label: 'Google Places (API)' }
];

const BUCKETS = ['Mega', 'Big', 'Medium', 'Small', 'Town'] as const;
type Bucket = typeof BUCKETS[number];

type City = {
  id: string; country: string; province: string; city: string;
  bucket: Bucket;
  lastScrapedForProduct: string | null;
  daysSinceLastScrape: number;
  leadsFromCityForProduct: number;
  cooldownActive: boolean;
  lastResultsCount: number | null;
};

type Suggested = {
  id: string; country: string; province: string; city: string;
  bucket: Bucket; score: number; reason: string;
};

export default function PipelineTriggerModal({ open, onClose, onDone, defaultSource = 'ApifyGoogleMaps' }: Props) {
  const [productKey, setProductKey] = useState('');
  const [source, setSource] = useState<LeadSource>(defaultSource);
  const [category, setCategory] = useState('');
  const [max, setMax] = useState(20);
  const [auto, setAuto] = useState(true);

  // Location mode: 'auto' lets the backend pick via algorithm, 'pick' uses dropdowns, 'batch' runs N cities.
  const [mode, setMode] = useState<'auto' | 'pick' | 'batch'>('auto');
  const [country, setCountry] = useState('AR');
  const [province, setProvince] = useState('');
  const [city, setCity] = useState('');
  const [selectedBuckets, setSelectedBuckets] = useState<Set<Bucket>>(new Set(['Mega', 'Big', 'Medium']));
  const [batchCount, setBatchCount] = useState(3);

  const products = useQuery({
    queryKey: ['products-min'],
    enabled: open,
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  useEffect(() => {
    if (products.data && !productKey && products.data.length > 0) {
      setProductKey(products.data[0].productKey);
    }
  }, [products.data]);

  useEffect(() => { setSource(defaultSource); }, [defaultSource]);

  const cities = useQuery({
    queryKey: ['cities', country, productKey, source],
    enabled: open && mode === 'pick' && !!productKey,
    queryFn: async () => (await api.get<City[]>('/cities', {
      params: { country, productKey, source }
    })).data
  });

  const suggested = useQuery({
    queryKey: ['cities-suggested', productKey, source, [...selectedBuckets].join(',')],
    enabled: open && !!productKey,
    queryFn: async () => (await api.get<Suggested[]>('/cities/suggested', {
      params: {
        productKey, source,
        limit: 8,
        bucketsCsv: [...selectedBuckets].join(',')
      }
    })).data
  });

  const provinces = useMemo(() => {
    if (!cities.data) return [];
    const bySet = new Set<string>();
    cities.data.forEach(c => bySet.add(c.province));
    return [...bySet].sort();
  }, [cities.data]);

  const citiesInProvince = useMemo(() => {
    if (!cities.data || !province) return [];
    return cities.data
      .filter(c => c.province === province)
      .filter(c => selectedBuckets.has(c.bucket))
      .sort((a, b) => {
        const order = { Mega: 5, Big: 4, Medium: 3, Small: 2, Town: 1 };
        return order[b.bucket] - order[a.bucket] || a.city.localeCompare(b.city);
      });
  }, [cities.data, province, selectedBuckets]);

  function toggleBucket(b: Bucket) {
    setSelectedBuckets(prev => {
      const next = new Set(prev);
      if (next.has(b)) next.delete(b);
      else next.add(b);
      return next;
    });
  }

  function pickSuggested(s: Suggested) {
    setMode('pick');
    setCountry(s.country);
    setProvince(s.province);
    setCity(s.city);
  }

  async function fireRun(cityName: string | null, provinceName: string | null) {
    const promise = api.post('/pipeline/run', {
      productKey, sources: [source],
      city: cityName, province: provinceName,
      category: category || null,
      maxPerSource: max, autoQueue: auto
    });
    return toast.promise(promise, {
      loading: `Corriendo ${source}${cityName ? ` en ${cityName}` : ''}…`,
      success: (res: any) => `✓ ${res.data.leadsCreated} leads creados${cityName ? ` (${cityName})` : ''}`,
      error: (err: any) => err.response?.data?.error ?? 'La corrida falló'
    });
  }

  function run() {
    if (mode === 'batch' && suggested.data) {
      const targets = suggested.data.slice(0, batchCount);
      if (targets.length === 0) {
        toast.error('No hay ciudades sugeridas con esos filtros');
        return;
      }
      onClose();
      (async () => {
        for (let i = 0; i < targets.length; i++) {
          const t = targets[i];
          try { await fireRun(t.city, t.province); }
          catch { /* toast shown */ }
          // 45s pause between runs so Apify memory budget doesn't spike.
          if (i < targets.length - 1) await new Promise(r => setTimeout(r, 45_000));
        }
        onDone();
      })();
      return;
    }

    const chosenCity = mode === 'pick' ? city || null : null;
    const chosenProvince = mode === 'pick' ? province || null : null;
    if (mode === 'pick' && !chosenCity) {
      toast.error('Elegí una ciudad o cambiá a modo Auto');
      return;
    }
    onClose();
    setTimeout(onDone, 250);
    fireRun(chosenCity, chosenProvince).then(() => onDone()).catch(() => {});
  }

  if (!open) return null;
  const topSugg = suggested.data ?? [];

  return (
    <div className="fixed inset-0 bg-black/40 grid place-items-center z-50 p-4 overflow-y-auto">
      <div className="card p-6 w-full max-w-xl space-y-4 my-8">
        <h3 className="text-lg font-semibold">Trigger pipeline Apify</h3>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="text-xs text-slate-500">Producto</label>
            <select value={productKey} onChange={e => setProductKey(e.target.value)} className="input">
              {(products.data ?? []).map(p => (
                <option key={p.productKey} value={p.productKey}>{p.displayName}</option>
              ))}
            </select>
          </div>
          <div>
            <label className="text-xs text-slate-500">Fuente</label>
            <select value={source} onChange={e => setSource(e.target.value as LeadSource)} className="input">
              {SOURCE_OPTS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </div>
        </div>

        {/* Mode selector */}
        <div className="border border-slate-200 rounded-md p-3 space-y-3">
          <div className="flex gap-2 text-sm">
            {(['auto', 'pick', 'batch'] as const).map(m => (
              <button key={m} onClick={() => setMode(m)}
                className={`btn ${mode === m ? 'bg-brand-600 text-white' : 'bg-white border border-slate-300'}`}>
                {m === 'auto' ? 'Auto (algoritmo elige)' : m === 'pick' ? 'Elegir ciudad' : 'Próximas N en cola'}
              </button>
            ))}
          </div>

          {/* Bucket filter — affects both suggested + dropdown */}
          <div>
            <div className="text-xs text-slate-500 mb-1">Filtrar por tamaño de ciudad</div>
            <div className="flex flex-wrap gap-1">
              {BUCKETS.map(b => (
                <button key={b} onClick={() => toggleBucket(b)}
                  className={`badge px-2 py-0.5 text-xs cursor-pointer ${
                    selectedBuckets.has(b)
                      ? 'bg-brand-100 text-brand-700 border border-brand-200'
                      : 'bg-slate-100 text-slate-500'
                  }`}>
                  {b}
                </button>
              ))}
            </div>
          </div>

          {mode === 'pick' && (
            <div className="grid grid-cols-3 gap-2">
              <div>
                <label className="text-xs text-slate-500">País</label>
                <select value={country} onChange={e => { setCountry(e.target.value); setProvince(''); setCity(''); }}
                  className="input">
                  <option value="AR">AR</option>
                  <option value="MX">MX</option>
                  <option value="CO">CO</option>
                </select>
              </div>
              <div>
                <label className="text-xs text-slate-500">Provincia</label>
                <select value={province} onChange={e => { setProvince(e.target.value); setCity(''); }}
                  className="input" disabled={provinces.length === 0}>
                  <option value="">—</option>
                  {provinces.map(p => <option key={p} value={p}>{p}</option>)}
                </select>
              </div>
              <div>
                <label className="text-xs text-slate-500">Ciudad</label>
                <select value={city} onChange={e => setCity(e.target.value)}
                  className="input" disabled={!province}>
                  <option value="">—</option>
                  {citiesInProvince.map(c => (
                    <option key={c.id} value={c.city} disabled={c.cooldownActive}>
                      {c.city} [{c.bucket.toLowerCase()}]
                      {c.cooldownActive
                        ? ` · cooldown (${c.daysSinceLastScrape}d)`
                        : c.daysSinceLastScrape >= 0
                          ? ` · scrapeada hace ${c.daysSinceLastScrape}d${c.lastResultsCount !== null ? ` (${c.lastResultsCount} leads)` : ''}`
                          : ' · nunca scrapeada'}
                    </option>
                  ))}
                </select>
              </div>
            </div>
          )}

          {mode === 'batch' && (
            <div>
              <label className="text-xs text-slate-500">Cantidad de ciudades</label>
              <input type="number" min={1} max={10} className="input w-24"
                value={batchCount} onChange={e => setBatchCount(+e.target.value)} />
              <div className="text-xs text-slate-500 mt-1">
                Las próximas {batchCount} ciudades sugeridas, con pausa de 45s entre runs.
              </div>
            </div>
          )}

          {/* Suggested chips (always visible as quick-pick) */}
          {topSugg.length > 0 && (
            <div>
              <div className="text-xs text-slate-500 mb-1">
                Sugeridas para <strong>{productKey}</strong> (click para usar):
              </div>
              <div className="flex flex-wrap gap-1">
                {topSugg.slice(0, mode === 'batch' ? batchCount : 8).map((s, i) => (
                  <button key={s.id} onClick={() => pickSuggested(s)}
                    className={`text-xs px-2 py-1 rounded border ${
                      mode === 'pick' && city === s.city
                        ? 'bg-brand-600 text-white border-brand-600'
                        : mode === 'batch' && i < batchCount
                          ? 'bg-brand-50 text-brand-800 border-brand-300'
                          : 'bg-white text-slate-700 border-slate-300 hover:bg-slate-50'
                    }`}
                    title={s.reason}>
                    {s.city} <span className="text-slate-400">· {s.reason}</span>
                  </button>
                ))}
              </div>
            </div>
          )}
          {suggested.isLoading && <div className="text-xs text-slate-400">Cargando sugeridas…</div>}
          {!suggested.isLoading && topSugg.length === 0 && (
            <div className="text-xs text-amber-600">
              No hay ciudades sugeridas con estos filtros. Bajá el cooldown o agregá más ciudades.
            </div>
          )}
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="text-xs text-slate-500">Categoría (opcional)</label>
            <input className="input" value={category} onChange={e => setCategory(e.target.value)}
              placeholder="Todas las del producto" />
          </div>
          <div>
            <label className="text-xs text-slate-500">Max leads por corrida</label>
            <input type="number" className="input" value={max} onChange={e => setMax(+e.target.value)} />
          </div>
        </div>

        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={auto} onChange={e => setAuto(e.target.checked)} />
          Auto-encolar mensajes para envío humanizado
        </label>

        <p className="text-xs text-slate-400">
          Los runs se disparan en background. El modal se cierra y te llegan toasts con el progreso.
          Mientras corre, el run aparece en la tabla abajo con estado "running".
        </p>

        <div className="flex justify-end gap-2">
          <button className="btn-secondary" onClick={onClose}>Cancelar</button>
          <button className="btn-primary" onClick={run} disabled={!productKey}>
            {mode === 'batch' ? `Trigger × ${batchCount}` : 'Trigger'}
          </button>
        </div>
      </div>
    </div>
  );
}
