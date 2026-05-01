import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import { isAdmin, useAuthStore } from '../lib/auth';
import type { LeadSource, LeadStatus, Product } from '../lib/types';

type Outcome = 'inserted' | 'duplicate' | 'closed' | 'error' | 'skipped';

interface ResultItem {
  name: string;
  phone?: string;
  address?: string;
  rating?: number;
  totalReviews?: number;
  outcome: Outcome;
  reason?: string;
  leadId?: string;
}

interface BulkResult {
  parsed: number;
  inserted: number;
  duplicates: number;
  closed: number;
  errors: number;
  items: ResultItem[];
}

const SOURCE_OPTIONS: { value: LeadSource; label: string }[] = [
  { value: 'ManualMaps', label: 'Google Maps (manual paste)' },
  { value: 'ManualInstagram', label: 'Instagram' },
  { value: 'ManualWhatsApp', label: 'WhatsApp' },
  { value: 'ManualWeb', label: 'Web' }
];

const STATUS_OPTIONS: { value: LeadStatus; label: string }[] = [
  { value: 'New', label: 'Nuevo (no contactado)' },
  { value: 'Sent', label: 'Contactado (ya hablé)' },
  { value: 'Interested', label: 'Interesado' }
];

const OUTCOME_LABEL: Record<Outcome, string> = {
  inserted: 'Insertado',
  duplicate: 'Duplicado',
  closed: 'Cerrado permanente',
  error: 'Error',
  skipped: 'Saltado'
};

const OUTCOME_CLASS: Record<Outcome, string> = {
  inserted: 'bg-emerald-50 text-emerald-700 border-emerald-200',
  duplicate: 'bg-slate-100 text-slate-600 border-slate-200',
  closed: 'bg-amber-50 text-amber-700 border-amber-200',
  error: 'bg-rose-50 text-rose-700 border-rose-200',
  skipped: 'bg-slate-50 text-slate-500 border-slate-200'
};

const STORAGE_KEY = 'saleshub.bulk-import.defaults';
function loadDefaults(): { productKey?: string; source?: LeadSource; status?: LeadStatus; city?: string } {
  try { return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}'); }
  catch { return {}; }
}

export default function LeadsImport() {
  const user = useAuthStore((s) => s.user);
  const admin = isAdmin(user);
  const nav = useNavigate();
  const defaults = loadDefaults();

  const [rawText, setRawText] = useState('');
  const [productKey, setProductKey] = useState(defaults.productKey ?? '');
  const [source, setSource] = useState<LeadSource>(defaults.source ?? 'ManualMaps');
  const [status, setStatus] = useState<LeadStatus>(defaults.status ?? 'New');
  const [city, setCity] = useState(defaults.city ?? '');
  const [assignToCaller, setAssignToCaller] = useState(true);
  const [enrichWithPlaces, setEnrichWithPlaces] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [result, setResult] = useState<BulkResult | null>(null);

  const products = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  const activeProducts = (products.data ?? []).filter((p) => p.active);

  useEffect(() => {
    if (!productKey && activeProducts.length > 0) {
      setProductKey(activeProducts[0].productKey);
    }
  }, [productKey, activeProducts.length]);

  async function submit() {
    if (!rawText.trim()) return toast.error('Pegá el texto del listado de Google Maps');
    if (!productKey) return toast.error('Elegí un producto');

    setSubmitting(true);
    setResult(null);
    try {
      const { data } = await api.post<BulkResult>('/leads/bulk-import', {
        rawText,
        productKey,
        source,
        status,
        city: city.trim() || null,
        assignToCaller,
        enrichWithPlacesApi: enrichWithPlaces
      }, { timeout: 600_000 });
      setResult(data);
      localStorage.setItem(STORAGE_KEY, JSON.stringify({ productKey, source, status, city }));
      toast.success(`${data.inserted} insertados · ${data.duplicates} duplicados · ${data.closed} cerrados · ${data.errors} errores`);
    } catch (err) {
      const e = err as { response?: { data?: { error?: string } } };
      toast.error(e?.response?.data?.error ?? 'Falló el import');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="space-y-5 max-w-5xl">
      <div className="flex items-center gap-3">
        <button className="btn-secondary" onClick={() => nav(-1)}>← Volver</button>
        <h1 className="text-2xl font-bold">Importar leads desde Google Maps</h1>
      </div>

      <div className="card p-5 space-y-3">
        <p className="text-sm text-slate-600">
          Pegá <strong>todo el listado</strong> que sale en Google Maps después de buscar
          (ej. <em>"crossfit recoleta"</em>). El parser extrae nombre, dirección, teléfono y rating
          de cada item. Después intenta insertar y te avisa cuántos eran duplicados.
        </p>

        <div className="grid grid-cols-1 md:grid-cols-4 gap-3">
          <div>
            <label className="text-xs text-slate-500">Producto *</label>
            <select className="input w-full" value={productKey} onChange={(e) => setProductKey(e.target.value)}>
              <option value="">— Elegir —</option>
              {activeProducts.map((p) => (
                <option key={p.productKey} value={p.productKey}>{p.displayName}</option>
              ))}
            </select>
          </div>
          <div>
            <label className="text-xs text-slate-500">Origen</label>
            <select className="input w-full" value={source} onChange={(e) => setSource(e.target.value as LeadSource)}>
              {SOURCE_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </div>
          <div>
            <label className="text-xs text-slate-500">Estado inicial</label>
            <select className="input w-full" value={status} onChange={(e) => setStatus(e.target.value as LeadStatus)}>
              {STATUS_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </div>
          <div>
            <label className="text-xs text-slate-500">Ciudad (opcional)</label>
            <input
              className="input w-full"
              value={city}
              onChange={(e) => setCity(e.target.value)}
              placeholder="ej. CABA, Rosario" />
          </div>
        </div>

        <div className="space-y-2 border border-slate-200 rounded p-3 bg-slate-50">
          <label className="flex items-start gap-2 text-sm">
            <input
              type="checkbox"
              checked={enrichWithPlaces}
              onChange={(e) => setEnrichWithPlaces(e.target.checked)}
              className="mt-0.5" />
            <span>
              <strong>Enriquecer con Google Places</strong> (recomendado)
              <span className="block text-xs text-slate-500">
                Busca cada lead por nombre + dirección y completa teléfono, website y coordenadas.
                Necesario porque el listado de Maps no trae teléfono. Cuesta ~$0.04/lead — dentro del free tier ($200/mes).
                Si lo desactivás, solo se importan los leads que ya traen teléfono visible en el listado.
              </span>
            </span>
          </label>
          {admin && (
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={assignToCaller} onChange={(e) => setAssignToCaller(e.target.checked)} />
              Asignarme estos leads (si lo destildás, quedan en el Pool)
            </label>
          )}
        </div>

        <div>
          <label className="text-xs text-slate-500 mb-1 block">
            Pegá el texto del listado:
          </label>
          <textarea
            className="input w-full font-mono text-xs"
            rows={14}
            value={rawText}
            onChange={(e) => setRawText(e.target.value)}
            placeholder="ON FIT Barrio Norte&#10;4,8(1084)&#10;Centro de gimnasia · 2844 Avenida Santa Fe&#10;Abierto · Cierra a las 11 p. m. · 011 2780-9274&#10;..." />
          <div className="text-[11px] text-slate-400 mt-1">
            {rawText.length.toLocaleString()} caracteres · {rawText.split('\n').length} líneas
          </div>
        </div>

        <div className="flex items-center gap-2 justify-end">
          {submitting && enrichWithPlaces && (
            <span className="text-xs text-slate-500">
              Enriqueciendo con Places… (~1s por lead)
            </span>
          )}
          <button className="btn-secondary" disabled={submitting} onClick={() => { setRawText(''); setResult(null); }}>
            Limpiar
          </button>
          <button className="btn-primary" disabled={submitting || !rawText.trim() || !productKey} onClick={submit}>
            {submitting ? 'Procesando…' : 'Procesar e importar'}
          </button>
        </div>
      </div>

      {result && (
        <div className="space-y-3">
          <div className="grid grid-cols-2 md:grid-cols-5 gap-3">
            <Stat label="Detectados" value={result.parsed} cls="bg-slate-50 text-slate-700" />
            <Stat label="Insertados" value={result.inserted} cls="bg-emerald-50 text-emerald-700" />
            <Stat label="Duplicados" value={result.duplicates} cls="bg-slate-50 text-slate-600" />
            <Stat label="Cerrados" value={result.closed} cls="bg-amber-50 text-amber-700" />
            <Stat label="Errores" value={result.errors} cls="bg-rose-50 text-rose-700" />
          </div>

          <div className="card overflow-x-auto">
            <table className="min-w-full text-sm">
              <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
                <tr>
                  <th className="px-3 py-2 text-left">Estado</th>
                  <th className="px-3 py-2 text-left">Negocio</th>
                  <th className="px-3 py-2 text-left">WhatsApp</th>
                  <th className="px-3 py-2 text-left">Rating</th>
                  <th className="px-3 py-2 text-left">Dirección</th>
                  <th className="px-3 py-2 text-left">Detalle</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.items.map((it, idx) => (
                  <tr key={idx} className="hover:bg-slate-50">
                    <td className="px-3 py-2">
                      <span className={`inline-flex items-center rounded border px-2 py-0.5 text-xs ${OUTCOME_CLASS[it.outcome]}`}>
                        {OUTCOME_LABEL[it.outcome]}
                      </span>
                    </td>
                    <td className="px-3 py-2 font-medium">
                      {it.leadId ? (
                        <Link to={`/leads/${it.leadId}`} className="text-brand-700 hover:underline">{it.name}</Link>
                      ) : it.name}
                    </td>
                    <td className="px-3 py-2 text-slate-600">{it.phone ?? '—'}</td>
                    <td className="px-3 py-2 text-slate-600">
                      {it.rating !== undefined && it.rating !== null
                        ? `${it.rating} (${it.totalReviews ?? 0})`
                        : '—'}
                    </td>
                    <td className="px-3 py-2 text-slate-600 truncate max-w-xs">{it.address ?? '—'}</td>
                    <td className="px-3 py-2 text-slate-500 text-xs">{it.reason ?? ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="text-sm text-slate-500">
            <Link to="/leads" className="text-brand-700 hover:underline">Ir a Mis leads →</Link>
          </div>
        </div>
      )}
    </div>
  );
}

function Stat({ label, value, cls }: { label: string; value: number; cls: string }) {
  return (
    <div className={`card p-3 ${cls}`}>
      <div className="text-[11px] uppercase tracking-wide opacity-70">{label}</div>
      <div className="text-2xl font-bold">{value}</div>
    </div>
  );
}
