// Tab "Rubros ARCA" del CRM (solo admin): cada lead con su rubro del nomenclador de ARCA (CLAE F.883),
// que pone solo el backend (ArcaClassifierWorker) desde la categoría de búsqueda, los tipos de Google,
// el nombre o el producto. Acá se ve el resumen por app y rubro y se bajan los CSV para Excel:
// el listado de leads con su rubro y el nomenclador completo.
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';

interface SummaryRow { product: string; code: string | null; activity: string | null; leads: number }
interface Summary { rulesVersion: string; total: number; classified: number; rows: SummaryRow[] }

const APP_NAMES: Record<string, string> = {
  gymhero: 'GymHero', turnospro: 'TurnosPro', playcrew: 'PlayCrew', construction: 'ArchiCloud', unistock: 'DashFlow', bunker: 'Bunker',
};
const appName = (key: string) => APP_NAMES[key] ?? key;

async function download(path: string, params: Record<string, string>, fallbackName: string) {
  const resp = await api.get(path, { params, responseType: 'blob' });
  const header = String(resp.headers['content-disposition'] ?? '');
  const name = /filename="?([^";]+)"?/i.exec(header)?.[1] ?? fallbackName;
  const url = URL.createObjectURL(resp.data as Blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

export default function RubrosArca() {
  const [product, setProduct] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const { data, isLoading } = useQuery({
    queryKey: ['arca-summary'],
    queryFn: async () => (await api.get<Summary>('/arca/summary')).data,
  });

  const products = Array.from(new Set((data?.rows ?? []).map((r) => r.product))).sort();
  const rows = (data?.rows ?? []).filter((r) => !product || r.product === product);
  const shown = rows.reduce((s, r) => s + r.leads, 0);
  const shownClassified = rows.filter((r) => r.code).reduce((s, r) => s + r.leads, 0);

  const run = async (key: string, fn: () => Promise<void>) => {
    setBusy(key);
    try { await fn(); } catch { toast.error('No se pudo bajar el archivo'); } finally { setBusy(null); }
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-3">
        <div>
          <label className="block text-xs text-slate-500 mb-1">App</label>
          <select className="border rounded px-2 py-1.5 text-sm" value={product} onChange={(e) => setProduct(e.target.value)}>
            <option value="">Todas</option>
            {products.map((p) => <option key={p} value={p}>{appName(p)}</option>)}
          </select>
        </div>
        <button
          className="px-3 py-1.5 rounded bg-slate-900 text-white text-sm disabled:opacity-50"
          disabled={busy !== null}
          onClick={() => run('leads', () => download('/arca/leads.csv', product ? { product } : {}, 'leads-rubro-arca.csv'))}
        >
          {busy === 'leads' ? 'Bajando…' : 'Bajar leads con rubro (CSV)'}
        </button>
        <button
          className="px-3 py-1.5 rounded border text-sm disabled:opacity-50"
          disabled={busy !== null}
          onClick={() => run('nomenclador', () => download('/arca/activities.csv', {}, 'nomenclador-arca-clae-f883.csv'))}
        >
          {busy === 'nomenclador' ? 'Bajando…' : 'Bajar nomenclador ARCA (CSV)'}
        </button>
      </div>

      <p className="text-sm text-slate-600">
        {isLoading ? 'Cargando…' : (
          <>
            {shownClassified.toLocaleString('es-AR')} de {shown.toLocaleString('es-AR')} leads con rubro ARCA
            {shown > 0 && ` (${Math.round((shownClassified / shown) * 100)}%)`}. Nomenclador CLAE F.883; reglas {data?.rulesVersion}.
          </>
        )}
      </p>

      <div className="overflow-x-auto border rounded">
        <table className="min-w-full text-sm">
          <thead className="bg-slate-50 text-left text-slate-500">
            <tr>
              <th className="px-3 py-2">App</th>
              <th className="px-3 py-2">Código</th>
              <th className="px-3 py-2">Actividad ARCA</th>
              <th className="px-3 py-2 text-right">Leads</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={`${r.product}-${r.code ?? 'none'}`} className="border-t">
                <td className="px-3 py-1.5 whitespace-nowrap">{appName(r.product)}</td>
                <td className="px-3 py-1.5 font-mono">{r.code ?? '—'}</td>
                <td className="px-3 py-1.5">{r.activity ?? <span className="text-slate-400">Sin rubro deducible</span>}</td>
                <td className="px-3 py-1.5 text-right tabular-nums">{r.leads.toLocaleString('es-AR')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
