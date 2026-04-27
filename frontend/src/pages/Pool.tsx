import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import type { Lead, Product } from '../lib/types';
import LeadTable from '../components/LeadTable';
import { useState } from 'react';

export default function Pool() {
  const [productKey, setProductKey] = useState('');
  const qc = useQueryClient();

  const products = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  const pool = useQuery({
    queryKey: ['pool', productKey],
    queryFn: async () => {
      const params: Record<string, string> = {};
      if (productKey) params.productKey = productKey;
      return (await api.get<Lead[]>('/leads/pool', { params })).data;
    }
  });

  async function claim(leadId: string) {
    try {
      await api.post(`/leads/${leadId}/claim`);
      toast.success('Lead tomado');
      qc.invalidateQueries();
    } catch (err: any) {
      toast.error(err.response?.data?.error ?? 'No se pudo tomar');
    }
  }

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-bold">Pool de leads</h1>
      <p className="text-sm text-slate-500">Leads sin asignar. Tomalos manualmente si querés más volumen.</p>
      <div className="flex gap-3 items-end">
        <div>
          <label className="text-xs text-slate-500">Producto</label>
          <select className="input" value={productKey} onChange={(e) => setProductKey(e.target.value)}>
            <option value="">Todos</option>
            {(products.data ?? []).map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
          </select>
        </div>
      </div>
      {pool.isLoading ? <div>Cargando…</div>
        : <LeadTable leads={pool.data ?? []} onClaim={claim} emptyText="El pool está vacío." />}
    </div>
  );
}
