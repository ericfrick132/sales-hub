import { useEffect, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import type { Product } from '../lib/types';

type ConvRow = {
  id: string; name: string; productKey: string; seller: string | null;
  inbound: number; botActs: number; feedback: number; lastAt: string | null;
};
type Feedback = { verdict: 'bien' | 'mal'; correctKey: string | null; correctAction: string | null; betterReply: string | null; note: string | null };
type Msg = {
  id: string; inbound: boolean; text: string; timestamp: string;
  intentKey: string | null; intentConfident: boolean | null; intentAction: string | null;
  intentSimulatedReply: string | null; intentWhy: string | null; feedback: Feedback | null;
};
type Conv = { lead: { id: string; name: string; productKey: string; seller: string | null }; messages: Msg[] };
type IntentDef = { key: string; name: string };
type Summary = {
  total: number; good: number; bad: number;
  byType: { intentKey: string; intentAction: string; good: number; bad: number }[];
};

const ACTION: Record<string, string> = {
  respuesta: 'respondería',
  sin_respuesta: 'no respondería',
  ia_humano: 'lo pasa a una persona / IA',
};

/**
 * Conversaciones reales con lo que habría hecho el bot en cada mensaje del lead (diccionario, sin
 * IA; nada se manda) y la opinión de una persona sobre cada decisión, que alimenta el backtest.
 */
export default function Simulacion() {
  const qc = useQueryClient();
  const [product, setProduct] = useState('');
  const [filter, setFilter] = useState('bot');
  const [q, setQ] = useState('');
  const [skip, setSkip] = useState(0);
  const [selected, setSelected] = useState<string | null>(null);

  const products = useQuery({ queryKey: ['products'], queryFn: async () => (await api.get<Product[]>('/products')).data });
  const intents = useQuery({ queryKey: ['intents-defs'], queryFn: async () => (await api.get<{ intents: IntentDef[] }>('/intents', { params: { days: 7 } })).data.intents });
  const summary = useQuery({ queryKey: ['sim-summary'], queryFn: async () => (await api.get<Summary>('/simulation/feedback/summary')).data });
  const list = useQuery({
    queryKey: ['sim-convs', product, filter, q, skip],
    queryFn: async () => (await api.get<{ total: number; rows: ConvRow[] }>('/simulation/conversations', {
      params: { product: product || undefined, filter, q: q || undefined, skip, take: 40 },
    })).data,
  });
  const conv = useQuery({
    queryKey: ['sim-conv', selected],
    enabled: !!selected,
    queryFn: async () => (await api.get<Conv>(`/simulation/conversations/${selected}`)).data,
  });

  useEffect(() => { setSkip(0); }, [product, filter, q]);
  const nameOf = (k: string | null) => (k && intents.data?.find((i) => i.key === k)?.name) || k || '—';

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Simulación del bot</h1>
        <p className="text-sm text-slate-500 mt-1">
          Conversaciones reales. Debajo de cada mensaje del lead, lo que habría hecho el bot con el diccionario (no se mandó
          nada) y por qué; el mensaje real que siguió está en el chat. Marcá si estuvo bien o mal y qué habrías respondido vos:
          eso entra al backtest y mejora las respuestas.
        </p>
      </div>

      {summary.data && summary.data.total > 0 && (
        <div className="rounded-xl border bg-white p-3 text-sm flex flex-wrap gap-4">
          <span>Opiniones: <b>{summary.data.total}</b></span>
          <span className="text-emerald-700">bien {summary.data.good}</span>
          <span className="text-rose-700">mal {summary.data.bad}</span>
          <span className="text-slate-500">
            {summary.data.byType.slice(0, 6).map((t) => `${nameOf(t.intentKey)} (${ACTION[t.intentAction] ?? t.intentAction}): ${t.good}✓ ${t.bad}✗`).join(' · ')}
          </span>
        </div>
      )}

      <div className="grid gap-4 lg:grid-cols-[340px_1fr]">
        {/* Lista */}
        <div className="rounded-xl border bg-white p-3 space-y-2 lg:max-h-[80vh] lg:overflow-y-auto">
          <input className="input w-full" placeholder="buscar por nombre" value={q} onChange={(e) => setQ(e.target.value)} />
          <div className="flex gap-2">
            <select className="input flex-1" value={product} onChange={(e) => setProduct(e.target.value)}>
              <option value="">todas las apps</option>
              {(products.data ?? []).map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
            </select>
            <select className="input flex-1" value={filter} onChange={(e) => setFilter(e.target.value)}>
              <option value="bot">donde el bot actúa</option>
              <option value="pendientes">sin opinión todavía</option>
              <option value="con_feedback">con opinión</option>
              <option value="todas">todas</option>
            </select>
          </div>
          <div className="text-xs text-slate-500">{list.data?.total ?? '…'} conversaciones</div>
          {(list.data?.rows ?? []).map((c) => (
            <button key={c.id} onClick={() => setSelected(c.id)}
              className={`w-full text-left rounded-lg border px-2.5 py-2 text-sm ${selected === c.id ? 'border-slate-800 bg-slate-50' : ''}`}>
              <div className="font-medium truncate">{c.name || 'sin nombre'}</div>
              <div className="text-[11px] text-slate-500">
                {c.productKey} {c.seller ? `· ${c.seller}` : ''} · {c.inbound} mensajes · bot actúa en {c.botActs}
                {c.feedback > 0 && <span className="text-emerald-700"> · {c.feedback} opiniones</span>}
              </div>
            </button>
          ))}
          <div className="flex justify-between text-xs">
            <button className="text-slate-500 hover:underline disabled:opacity-40" disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - 40))}>← anteriores</button>
            <button className="text-slate-500 hover:underline disabled:opacity-40" disabled={(list.data?.rows.length ?? 0) < 40} onClick={() => setSkip(skip + 40)}>siguientes →</button>
          </div>
        </div>

        {/* Chat */}
        <div className="rounded-xl border bg-white p-3 lg:max-h-[80vh] lg:overflow-y-auto">
          {!selected ? (
            <div className="text-sm text-slate-400 p-6 text-center">Elegí una conversación de la lista.</div>
          ) : conv.isLoading || !conv.data ? (
            <div className="text-sm text-slate-400">Cargando…</div>
          ) : (
            <div className="space-y-2">
              <div className="text-sm font-semibold">{conv.data.lead.name} <span className="font-normal text-slate-500">· {conv.data.lead.productKey}{conv.data.lead.seller ? ` · línea: ${conv.data.lead.seller}` : ''}</span></div>
              {conv.data.messages.map((m) => (
                <div key={m.id}>
                  <div className={`flex ${m.inbound ? 'justify-start' : 'justify-end'}`}>
                    <div className={`max-w-[80%] rounded-2xl px-3 py-1.5 text-sm whitespace-pre-wrap ${m.inbound ? 'bg-slate-100' : 'bg-emerald-100'}`}>
                      {m.text}
                      <div className="text-[10px] text-slate-400 mt-0.5">{new Date(m.timestamp).toLocaleString('es-AR')}</div>
                    </div>
                  </div>
                  {m.inbound && m.intentAction && (
                    <BotDecision m={m} intents={intents.data ?? []} nameOf={nameOf}
                      onSaved={() => { qc.invalidateQueries({ queryKey: ['sim-conv', selected] }); qc.invalidateQueries({ queryKey: ['sim-summary'] }); qc.invalidateQueries({ queryKey: ['sim-convs'] }); }} />
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function BotDecision({ m, intents, nameOf, onSaved }: {
  m: Msg; intents: IntentDef[]; nameOf: (k: string | null) => string; onSaved: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [correctKey, setCorrectKey] = useState(m.feedback?.correctKey ?? '');
  const [correctAction, setCorrectAction] = useState(m.feedback?.correctAction ?? '');
  const [betterReply, setBetterReply] = useState(m.feedback?.betterReply ?? '');
  const [note, setNote] = useState(m.feedback?.note ?? '');

  const save = useMutation({
    mutationFn: async (verdict: 'bien' | 'mal') => api.put(`/simulation/feedback/${m.id}`, {
      verdict,
      correctKey: verdict === 'mal' ? correctKey || null : null,
      correctAction: verdict === 'mal' ? correctAction || null : null,
      betterReply: betterReply || null,
      note: note || null,
    }),
    onSuccess: () => { toast.success('Guardado'); setOpen(false); onSaved(); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo guardar'),
  });

  const acts = m.intentAction === 'respuesta' || m.intentAction === 'sin_respuesta';
  const fb = m.feedback;
  return (
    <div className={`ml-2 my-1 max-w-[85%] rounded-lg border-l-4 px-3 py-2 text-xs ${acts ? 'border-emerald-500 bg-emerald-50' : 'border-slate-300 bg-slate-50'}`}>
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
        <b>bot:</b>
        <span>{ACTION[m.intentAction!] ?? m.intentAction}</span>
        <span className="rounded bg-white px-1.5 border">{nameOf(m.intentKey)}</span>
        {m.intentConfident === false && <span className="text-slate-400">no es seguro</span>}
        {m.intentWhy && <span className="text-slate-500">· {m.intentWhy}</span>}
        {fb && <span className={fb.verdict === 'bien' ? 'text-emerald-700 font-medium' : 'text-rose-700 font-medium'}>· vos: {fb.verdict}</span>}
      </div>
      {m.intentAction === 'respuesta' && m.intentSimulatedReply && (
        <div className="mt-1 text-sm text-slate-800">“{m.intentSimulatedReply}”</div>
      )}
      {fb?.betterReply && <div className="mt-1 text-slate-600">vos habrías dicho: “{fb.betterReply}”</div>}

      <div className="mt-1.5 flex flex-wrap gap-2">
        <button className="rounded border border-emerald-600 text-emerald-700 px-2 py-0.5 hover:bg-emerald-100" disabled={save.isPending} onClick={() => save.mutate('bien')}>bien</button>
        <button className="rounded border border-rose-600 text-rose-700 px-2 py-0.5 hover:bg-rose-100" onClick={() => setOpen(!open)}>mal / mejorar</button>
      </div>

      {open && (
        <div className="mt-2 space-y-2 rounded-lg bg-white border p-2">
          <div className="grid gap-2 sm:grid-cols-2">
            <label className="space-y-0.5">
              <div className="text-slate-500">Tipo correcto</div>
              <select className="input w-full" value={correctKey} onChange={(e) => setCorrectKey(e.target.value)}>
                <option value="">(el tipo estaba bien)</option>
                {intents.map((i) => <option key={i.key} value={i.key}>{i.name}</option>)}
                <option value="otro">Ninguno (lo tiene que ver una persona)</option>
              </select>
            </label>
            <label className="space-y-0.5">
              <div className="text-slate-500">Qué debería haber hecho</div>
              <select className="input w-full" value={correctAction} onChange={(e) => setCorrectAction(e.target.value)}>
                <option value="">—</option>
                <option value="respuesta">responder</option>
                <option value="sin_respuesta">no responder</option>
                <option value="ia_humano">pasarlo a una persona</option>
              </select>
            </label>
          </div>
          <label className="block space-y-0.5">
            <div className="text-slate-500">Lo que habrías respondido vos</div>
            <textarea className="input w-full h-16" value={betterReply} onChange={(e) => setBetterReply(e.target.value)} />
          </label>
          <input className="input w-full" placeholder="nota (opcional)" value={note} onChange={(e) => setNote(e.target.value)} />
          <div className="flex gap-2">
            <button className="btn-primary" disabled={save.isPending} onClick={() => save.mutate('mal')}>Guardar como mal</button>
            <button className="btn-secondary" disabled={save.isPending} onClick={() => save.mutate('bien')}>Guardar como bien (con mi respuesta)</button>
          </div>
        </div>
      )}
    </div>
  );
}
