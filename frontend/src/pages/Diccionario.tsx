import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import Switch from '../components/Switch';

type Intent = {
  id: string;
  key: string;
  name: string;
  pattern: string;
  maxWords: number | null;
  action: string;
  reply: string | null;
  note: string | null;
  examples: string[];
  sortOrder: number;
  enabled: boolean;
  autoReply: boolean;
  count: number;
};

type Data = { days: number; total: number; covered: number; other: number; unclassified: number; intents: Intent[] };
type Msg = { leadId: string; text: string; timestamp: string; lead: string };

const pct = (a: number, b: number) => (b > 0 ? `${((100 * a) / b).toFixed(1)}%` : '—');

/**
 * Diccionario de tipos de mensaje del lead: cada mensaje que entra se clasifica por palabras (sin
 * IA) y queda guardado su tipo. Por ahora en modo sombra: mide, no cambia lo que responde el bot.
 */
export default function Diccionario() {
  const qc = useQueryClient();
  const [days, setDays] = useState(30);
  const { data, isLoading } = useQuery({
    queryKey: ['intents', days],
    queryFn: async () => (await api.get<Data>('/intents', { params: { days } })).data,
  });

  const [openKey, setOpenKey] = useState<string | null>(null);
  const msgs = useQuery({
    queryKey: ['intent-msgs', openKey],
    enabled: !!openKey,
    queryFn: async () => (await api.get<Msg[]>(`/intents/${openKey}/messages`)).data,
  });

  const [editing, setEditing] = useState<Intent | null>(null);
  const save = useMutation({
    mutationFn: async (i: Intent) => api.put(`/intents/${i.id}`, i),
    onSuccess: () => {
      toast.success('Guardado. Para reclasificar lo viejo con el cambio, usá "Reclasificar todo".');
      setEditing(null);
      qc.invalidateQueries({ queryKey: ['intents'] });
    },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo guardar'),
  });

  const [test, setTest] = useState({ text: '', previous: '' });
  const [testResult, setTestResult] = useState<{ key: string; normalized: string } | null>(null);
  async function runTest() {
    if (!test.text.trim()) return;
    setTestResult((await api.post('/intents/test', { text: test.text, previous: test.previous || null })).data);
  }

  const [backfilling, setBackfilling] = useState(false);
  async function backfill(all: boolean) {
    setBackfilling(true);
    try {
      let first = true;
      for (;;) {
        const r = (await api.post<{ updated: number; remaining: number }>('/intents/backfill', null, { params: { all: all && first, leads: 400 } })).data;
        first = false;
        if (r.remaining === 0 || r.updated === 0) break;
      }
      toast.success('Listo, mensajes clasificados');
      qc.invalidateQueries({ queryKey: ['intents'] });
    } catch (e: any) {
      toast.error(e?.response?.data?.error ?? 'Falló la clasificación');
    } finally {
      setBackfilling(false);
    }
  }

  async function resetDict() {
    if (!window.confirm('Reemplaza los patrones y respuestas por los del último análisis (se pierden las ediciones a mano). ¿Seguir?')) return;
    const r = (await api.post<{ count: number }>('/intents/reset')).data;
    toast.success(`Diccionario restaurado: ${r.count} tipos. Reclasificando…`);
    await backfill(true);
  }

  if (isLoading || !data) return <div className="text-sm text-slate-400">Cargando…</div>;
  const nameOf = (k: string) => data.intents.find((i) => i.key === k)?.name ?? k;

  return (
    <div className="space-y-6 max-w-6xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Diccionario de respuestas</h1>
        <p className="text-sm text-slate-500 mt-1">
          Cada mensaje que escribe un lead se clasifica por palabras, sin IA, y queda guardado su tipo. Los patrones
          salen del análisis de 2.692 charlas de venta (entrada → qué le contestamos → si siguió la charla). Por ahora
          está en <b>modo sombra</b>: mide, pero el bot sigue respondiendo como siempre.
        </p>
      </div>

      <section className="rounded-xl border bg-white p-4 flex flex-wrap items-center gap-6">
        <div>
          <div className="text-2xl font-bold">{pct(data.covered, data.total)}</div>
          <div className="text-xs text-slate-500">cubierto sin IA (últimos {days} días, {data.total.toLocaleString('es-AR')} mensajes)</div>
        </div>
        <div>
          <div className="text-2xl font-bold text-amber-600">{data.other.toLocaleString('es-AR')}</div>
          <div className="text-xs text-slate-500">
            sin tipo (irían a la IA) ·{' '}
            <button className="underline" onClick={() => setOpenKey('otro')}>ver</button>
          </div>
        </div>
        <div className="flex items-center gap-2 text-sm">
          <span className="text-slate-500">Período</span>
          <select className="input" value={days} onChange={(e) => setDays(Number(e.target.value))}>
            <option value={7}>7 días</option>
            <option value={30}>30 días</option>
            <option value={90}>90 días</option>
            <option value={3650}>todo</option>
          </select>
        </div>
        <div className="ml-auto flex gap-2">
          {data.unclassified > 0 && (
            <button className="btn-secondary" disabled={backfilling} onClick={() => backfill(false)}>
              {backfilling ? 'Clasificando…' : `Clasificar ${data.unclassified.toLocaleString('es-AR')} mensajes viejos`}
            </button>
          )}
          <button className="btn-secondary" disabled={backfilling} onClick={resetDict}>Restaurar diccionario del análisis</button>
          <button className="btn-secondary" disabled={backfilling} onClick={() => backfill(true)}>Reclasificar todo</button>
        </div>
        <div className="w-full text-[11px] text-slate-400">
          Ojo: en la base también hay chats personales y de soporte a clientes, así que la cobertura acá da más baja que en
          las charlas de venta (76%).
        </div>
      </section>

      <section className="rounded-xl border bg-white p-4 space-y-2">
        <div className="font-semibold">Probar un mensaje</div>
        <div className="grid gap-2 sm:grid-cols-[1fr_1fr_auto]">
          <input className="input" placeholder="lo que escribe el lead (ej. cuanto sale?)" value={test.text}
            onChange={(e) => setTest({ ...test, text: e.target.value })} onKeyDown={(e) => e.key === 'Enter' && runTest()} />
          <input className="input" placeholder="nuestro mensaje anterior (opcional)" value={test.previous}
            onChange={(e) => setTest({ ...test, previous: e.target.value })} />
          <button className="btn-primary" onClick={runTest}>Probar</button>
        </div>
        {testResult && (
          <div className="text-sm">
            → <b>{testResult.key === 'otro' ? 'sin tipo (iría a la IA)' : nameOf(testResult.key)}</b>
            <span className="ml-2 text-xs text-slate-400">normalizado: “{testResult.normalized}”</span>
          </div>
        )}
      </section>

      <section className="rounded-xl border bg-white overflow-x-auto">
        <table className="w-full text-sm min-w-[860px]">
          <thead className="text-left text-slate-500 bg-slate-50">
            <tr>
              <th className="p-2 w-8">#</th>
              <th className="p-2">Tipo</th>
              <th className="p-2 text-right">Mensajes</th>
              <th className="p-2">Qué hace</th>
              <th className="p-2">Respuesta sugerida</th>
              <th className="p-2 text-center">Activo</th>
              <th className="p-2"></th>
            </tr>
          </thead>
          <tbody>
            {data.intents.map((i, idx) => (
              <tr key={i.id} className="border-t align-top">
                <td className="p-2 text-slate-400">{idx + 1}</td>
                <td className="p-2">
                  <div className="font-medium">{i.name}</div>
                  <div className="text-[11px] text-slate-400">{i.key}</div>
                </td>
                <td className="p-2 text-right whitespace-nowrap">
                  <button className="underline decoration-dotted" onClick={() => setOpenKey(i.key)}>
                    {i.count.toLocaleString('es-AR')}
                  </button>
                  <div className="text-[11px] text-slate-400">{pct(i.count, data.total)}</div>
                </td>
                <td className="p-2 text-xs text-slate-600 max-w-[220px]">{i.note ?? i.action}</td>
                <td className="p-2 text-xs max-w-[320px]">{i.reply ?? <span className="text-slate-400">según el guion</span>}</td>
                <td className="p-2 text-center">
                  <Switch on={i.enabled} onClick={() => save.mutate({ ...i, enabled: !i.enabled })} title="Usar este tipo" />
                </td>
                <td className="p-2">
                  <button className="text-xs text-slate-500 hover:underline" onClick={() => setEditing(i)}>editar</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      {openKey && (
        <section className="rounded-xl border bg-white p-4 space-y-2">
          <div className="flex items-center justify-between">
            <div className="font-semibold">Últimos mensajes: {openKey === 'otro' ? 'sin tipo' : nameOf(openKey)}</div>
            <button className="text-xs text-slate-500 hover:underline" onClick={() => setOpenKey(null)}>cerrar</button>
          </div>
          {msgs.isLoading ? <div className="text-sm text-slate-400">Cargando…</div> : (
            <ul className="divide-y text-sm">
              {(msgs.data ?? []).map((m, n) => (
                <li key={n} className="py-1.5">
                  <span className="text-slate-400 text-xs mr-2">{new Date(m.timestamp).toLocaleDateString('es-AR')} · {m.lead}</span>
                  {m.text.slice(0, 220)}
                </li>
              ))}
            </ul>
          )}
        </section>
      )}

      {editing && (
        <div className="fixed inset-0 bg-black/30 flex items-center justify-center p-4 z-50" onClick={() => setEditing(null)}>
          <div className="bg-white rounded-xl p-5 w-full max-w-2xl space-y-3" onClick={(e) => e.stopPropagation()}>
            <div className="font-semibold">Editar: {editing.name}</div>
            <label className="block text-sm space-y-1">
              <div className="text-slate-600">Nombre</div>
              <input className="input w-full" value={editing.name} onChange={(e) => setEditing({ ...editing, name: e.target.value })} />
            </label>
            <label className="block text-sm space-y-1">
              <div className="text-slate-600">Patrón (expresión regular sobre el texto normalizado: minúscula, sin tildes, números = N)</div>
              <textarea className="input w-full font-mono text-xs h-28" value={editing.pattern}
                onChange={(e) => setEditing({ ...editing, pattern: e.target.value })} />
            </label>
            <div className="grid grid-cols-3 gap-3 text-sm">
              <label className="space-y-1">
                <div className="text-slate-600">Máx. palabras</div>
                <input type="number" className="input w-full" value={editing.maxWords ?? ''}
                  onChange={(e) => setEditing({ ...editing, maxWords: e.target.value === '' ? null : Number(e.target.value) })} />
              </label>
              <label className="space-y-1">
                <div className="text-slate-600">Orden</div>
                <input type="number" className="input w-full" value={editing.sortOrder}
                  onChange={(e) => setEditing({ ...editing, sortOrder: Number(e.target.value) })} />
              </label>
              <label className="space-y-1">
                <div className="text-slate-600">Acción</div>
                <input className="input w-full" value={editing.action} onChange={(e) => setEditing({ ...editing, action: e.target.value })} />
              </label>
            </div>
            <label className="block text-sm space-y-1">
              <div className="text-slate-600">Respuesta sugerida ({'{vendedor}'}, {'{producto}'}, {'{precio}'}…)</div>
              <textarea className="input w-full h-20" value={editing.reply ?? ''} onChange={(e) => setEditing({ ...editing, reply: e.target.value })} />
            </label>
            <label className="block text-sm space-y-1">
              <div className="text-slate-600">Nota</div>
              <input className="input w-full" value={editing.note ?? ''} onChange={(e) => setEditing({ ...editing, note: e.target.value })} />
            </label>
            {editing.examples.length > 0 && (
              <div className="text-xs text-slate-500">Ejemplos del análisis: {editing.examples.map((e) => `“${e}”`).join(' · ')}</div>
            )}
            <div className="flex justify-end gap-2">
              <button className="btn-secondary" onClick={() => setEditing(null)}>Cancelar</button>
              <button className="btn-primary" disabled={save.isPending} onClick={() => save.mutate(editing)}>Guardar</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
