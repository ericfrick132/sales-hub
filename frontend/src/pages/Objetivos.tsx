import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import type { Product, Seller, SellerGoal, SellerGoalMetric, SellerGoalPeriod } from '../lib/types';

const METRICS: SellerGoalMetric[] = ['Contactos', 'Respuestas', 'Demos', 'Cierres'];
const PERIODS: { value: SellerGoalPeriod; label: string }[] = [
  { value: 'Daily', label: 'Diario' },
  { value: 'Weekly', label: 'Semanal' },
];

type Draft = {
  sellerId: string; // '' = todos
  productKey: string; // '' = todas las apps
  metric: SellerGoalMetric;
  period: SellerGoalPeriod;
  target: number;
};

const EMPTY_DRAFT: Draft = { sellerId: '', productKey: '', metric: 'Cierres', period: 'Daily', target: 3 };

export default function Objetivos() {
  const qc = useQueryClient();
  const [draft, setDraft] = useState<Draft>(EMPTY_DRAFT);

  const goalsQ = useQuery({
    queryKey: ['seller-goals'],
    queryFn: async () => (await api.get<SellerGoal[]>('/seller-goals')).data,
  });
  const sellersQ = useQuery({
    queryKey: ['sellers'],
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data,
  });
  const productsQ = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data,
  });

  const sellers = sellersQ.data ?? [];
  const products = productsQ.data ?? [];
  const goals = goalsQ.data ?? [];

  async function create() {
    if (draft.target < 0) return;
    await api.post('/seller-goals', {
      sellerId: draft.sellerId || null,
      productKey: draft.productKey || null,
      metric: draft.metric,
      period: draft.period,
      target: draft.target,
      isActive: true,
    });
    toast.success('Objetivo creado');
    setDraft(EMPTY_DRAFT);
    qc.invalidateQueries({ queryKey: ['seller-goals'] });
  }

  async function patch(id: string, body: Partial<{ target: number; isActive: boolean }>) {
    await api.put(`/seller-goals/${id}`, body);
    qc.invalidateQueries({ queryKey: ['seller-goals'] });
  }

  async function remove(id: string) {
    if (!confirm('¿Eliminar este objetivo?')) return;
    await api.delete(`/seller-goals/${id}`);
    toast.success('Eliminado');
    qc.invalidateQueries({ queryKey: ['seller-goals'] });
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Objetivos de vendedores</h1>
        <p className="text-sm text-slate-500">
          Metas diarias/semanales que ve cada vendedor como barra de progreso. Dejá vendedor o app en
          «Todos» para que aplique de forma global.
        </p>
      </div>

      {/* Alta */}
      <div className="card p-5">
        <h3 className="font-semibold mb-3">Nuevo objetivo</h3>
        <div className="grid grid-cols-1 md:grid-cols-6 gap-3 items-end">
          <Field label="Vendedor">
            <select className="input" value={draft.sellerId}
              onChange={(e) => setDraft({ ...draft, sellerId: e.target.value })}>
              <option value="">Todos</option>
              {sellers.map((s) => <option key={s.id} value={s.id}>{s.displayName}</option>)}
            </select>
          </Field>
          <Field label="App">
            <select className="input" value={draft.productKey}
              onChange={(e) => setDraft({ ...draft, productKey: e.target.value })}>
              <option value="">Todas</option>
              {products.map((p) => <option key={p.id} value={p.productKey}>{p.displayName}</option>)}
            </select>
          </Field>
          <Field label="Métrica">
            <select className="input" value={draft.metric}
              onChange={(e) => setDraft({ ...draft, metric: e.target.value as SellerGoalMetric })}>
              {METRICS.map((m) => <option key={m} value={m}>{m}</option>)}
            </select>
          </Field>
          <Field label="Período">
            <select className="input" value={draft.period}
              onChange={(e) => setDraft({ ...draft, period: e.target.value as SellerGoalPeriod })}>
              {PERIODS.map((p) => <option key={p.value} value={p.value}>{p.label}</option>)}
            </select>
          </Field>
          <Field label="Target">
            <input type="number" min={0} className="input" value={draft.target}
              onChange={(e) => setDraft({ ...draft, target: Math.max(0, +e.target.value) })} />
          </Field>
          <button className="btn-primary" onClick={create}>Agregar</button>
        </div>
      </div>

      {/* Listado */}
      <div className="card overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-slate-500 text-xs uppercase tracking-wide">
            <tr>
              <th className="text-left px-4 py-2">Vendedor</th>
              <th className="text-left px-4 py-2">App</th>
              <th className="text-left px-4 py-2">Métrica</th>
              <th className="text-left px-4 py-2">Período</th>
              <th className="text-left px-4 py-2">Target</th>
              <th className="text-left px-4 py-2">Activo</th>
              <th className="px-4 py-2"></th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {goals.length === 0 && (
              <tr><td colSpan={7} className="px-4 py-6 text-center text-slate-400">Sin objetivos cargados.</td></tr>
            )}
            {goals.map((g) => (
              <tr key={g.id} className={g.isActive ? '' : 'opacity-50'}>
                <td className="px-4 py-2">{g.sellerName ?? <span className="badge bg-slate-100 text-slate-600">Todos</span>}</td>
                <td className="px-4 py-2">{g.productKey ?? <span className="badge bg-slate-100 text-slate-600">Todas</span>}</td>
                <td className="px-4 py-2">{g.metric}</td>
                <td className="px-4 py-2">{g.period === 'Daily' ? 'Diario' : 'Semanal'}</td>
                <td className="px-4 py-2">
                  <input type="number" min={0} defaultValue={g.target}
                    className="input w-20 py-1"
                    onBlur={(e) => { const v = Math.max(0, +e.target.value); if (v !== g.target) patch(g.id, { target: v }); }} />
                </td>
                <td className="px-4 py-2">
                  <button onClick={() => patch(g.id, { isActive: !g.isActive })}
                    className={`badge ${g.isActive ? 'bg-emerald-100 text-emerald-700' : 'bg-slate-100 text-slate-500'}`}>
                    {g.isActive ? 'Sí' : 'No'}
                  </button>
                </td>
                <td className="px-4 py-2 text-right">
                  <button className="btn-danger text-xs px-2 py-1" onClick={() => remove(g.id)}>Eliminar</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="text-xs text-slate-500 mb-1 block">{label}</span>
      {children}
    </label>
  );
}
