import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import type { Lead, LeadStatus, Product } from '../lib/types';
import LeadTable from '../components/LeadTable';

const STATUSES: LeadStatus[] = ['Assigned', 'Queued', 'Sent', 'Replied', 'Interested', 'DemoScheduled', 'Closed', 'Lost'];

export default function MyLeads() {
  const [status, setStatus] = useState<LeadStatus | ''>('');
  const [productKey, setProductKey] = useState('');
  const qc = useQueryClient();

  const products = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  const leadsQ = useQuery({
    queryKey: ['my-leads', status, productKey],
    queryFn: async () => {
      const params: Record<string, string> = {};
      if (status) params.status = status;
      if (productKey) params.productKey = productKey;
      const { data } = await api.get<Lead[]>('/leads/mine', { params });
      return data;
    }
  });

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-bold">Mis leads</h1>
      <div className="flex gap-3 items-end">
        <div>
          <label className="text-xs text-slate-500">Estado</label>
          <select className="input" value={status} onChange={(e) => setStatus(e.target.value as LeadStatus)}>
            <option value="">Todos</option>
            {STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        </div>
        <div>
          <label className="text-xs text-slate-500">Producto</label>
          <select className="input" value={productKey} onChange={(e) => setProductKey(e.target.value)}>
            <option value="">Todos</option>
            {(products.data ?? []).map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
          </select>
        </div>
        <button className="btn-secondary" onClick={() => qc.invalidateQueries({ queryKey: ['my-leads'] })}>
          Refrescar
        </button>
      </div>
      {leadsQ.isLoading ? <div>Cargando…</div> : <LeadTable leads={leadsQ.data ?? []} />}
    </div>
  );
}
