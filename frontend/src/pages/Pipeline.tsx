import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import type { LeadSource } from '../lib/types';
import PipelineTriggerModal from '../components/PipelineTriggerModal';

type Run = {
  id: string;
  source: LeadSource;
  actorId: string;
  productKey?: string;
  startedAt: string;
  finishedAt?: string;
  status: string;
  itemsCount: number;
  leadsCreated: number;
  error?: string;
};

const TABS: { key: LeadSource; label: string }[] = [
  { key: 'GooglePlaces', label: 'Places API' },
  { key: 'ApifyGoogleMaps', label: 'Google Maps' },
  { key: 'ApifyInstagram', label: 'Instagram' },
  { key: 'ApifyMetaAdsLibrary', label: 'Meta Ads Library' },
  { key: 'ApifyFacebookPages', label: 'Facebook Posts' }
];

export default function Pipeline() {
  const [tab, setTab] = useState<LeadSource>('GooglePlaces');
  const [modal, setModal] = useState(false);
  const qc = useQueryClient();

  const runs = useQuery({
    queryKey: ['pipeline-runs'],
    queryFn: async () => (await api.get<Run[]>('/pipeline/runs', { params: { limit: 80 } })).data,
    refetchInterval: 15000
  });

  const rows = (runs.data ?? []).filter((r) => r.source === tab);

  async function importCities(country: string) {
    const promise = api.post('/admin/cities/import', { country, minPopulation: 500 });
    return toast.promise(promise, {
      loading: `Importando catálogo de ciudades desde GeoNames (${country})…`,
      success: (r: any) => `✓ ${r.data.inserted} nuevas + ${r.data.updated} actualizadas`,
      error: (e: any) => e.response?.data?.error ?? 'Falló el import'
    });
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <h1 className="text-xl md:text-2xl font-bold">Pipeline</h1>
        <div className="flex gap-2 flex-wrap">
          <button className="btn-secondary text-sm" onClick={() => importCities('AR')}
            title="Descarga el catálogo completo de GeoNames (gratis) para poblar el dropdown de ciudades con lat/lng y población.">
            Importar ciudades AR
          </button>
          <button className="btn-primary text-sm" onClick={() => setModal(true)}>Trigger corrida</button>
        </div>
      </div>

      <div className="flex gap-1 border-b border-slate-200 overflow-x-auto">
        {TABS.map((t) => (
          <button key={t.key} onClick={() => setTab(t.key)}
            className={`px-4 py-2 text-sm border-b-2 whitespace-nowrap ${tab === t.key ? 'border-brand-600 text-brand-700 font-medium' : 'border-transparent text-slate-500 hover:text-slate-700'}`}>
            {t.label}
          </button>
        ))}
      </div>

      <div className="card overflow-x-auto">
        <table className="min-w-full text-sm">
          <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
            <tr>
              <th className="px-3 py-2 text-left">Cuando</th>
              <th className="px-3 py-2 text-left">Producto</th>
              <th className="px-3 py-2 text-left">Estado</th>
              <th className="px-3 py-2 text-right">Items</th>
              <th className="px-3 py-2 text-right">Leads creados</th>
              <th className="px-3 py-2 text-left">Error</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {rows.length === 0 && <tr><td colSpan={6} className="px-3 py-6 text-center text-slate-500">Sin corridas</td></tr>}
            {rows.map((r) => (
              <tr key={r.id}>
                <td className="px-3 py-2">{new Date(r.startedAt).toLocaleString()}</td>
                <td className="px-3 py-2">{r.productKey ?? '—'}</td>
                <td className="px-3 py-2">{r.status}</td>
                <td className="px-3 py-2 text-right">{r.itemsCount}</td>
                <td className="px-3 py-2 text-right font-medium">{r.leadsCreated}</td>
                <td className="px-3 py-2 text-rose-600 text-xs">{r.error ?? ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <PipelineTriggerModal
        open={modal}
        onClose={() => setModal(false)}
        onDone={() => qc.invalidateQueries({ queryKey: ['pipeline-runs'] })}
        defaultSource={tab}
      />
    </div>
  );
}
