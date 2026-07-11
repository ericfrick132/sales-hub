import { Fragment, useState } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import Switch from '../components/Switch';
import type { Product } from '../lib/types';

// ── Tipos (espejo de SupportController) ──────────────────────────────────────

type CaseStatus = 'Open' | 'Escalated' | 'Resolved';

type SupportCase = {
  id: string;
  productKey: string;
  status: CaseStatus;
  botTurns: number;
  summary: string;
  openedAt: string;
  lastBotReplyAt?: string | null;
  escalatedAt?: string | null;
  resolvedAt?: string | null;
  leadId?: string | null;
  leadName?: string | null;
  leadPhone?: string | null;
};

type Faq = {
  id: string;
  productKey: string;
  question: string;
  keywords: string[];
  answer: string;
  enabled: boolean;
  source: 'manual' | 'promoted';
  timesUsed: number;
  createdAt: string;
  updatedAt: string;
};

type KnowledgeItem = { id: string; productKey: string; sourceVersion: string; uploadedAt: string; length: number };
type KnowledgeDetail = { id: string; productKey: string; content: string; sourceVersion: string; uploadedAt: string };

type ToneProfile = { id: string; scope: string; content: string; updatedAt: string };
type ToneResp = { profiles: ToneProfile[]; defaultTone: string };

const TABS = [
  { key: 'casos', label: 'Casos' },
  { key: 'faqs', label: 'FAQs' },
  { key: 'conocimiento', label: 'Conocimiento' },
  { key: 'tono', label: 'Tono' },
] as const;
type TabKey = (typeof TABS)[number]['key'];

function fmtDate(x?: string | null) {
  return x
    ? new Date(x).toLocaleString('es-AR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })
    : '—';
}

function splitKeywords(s: string): string[] {
  return s.split(',').map((k) => k.trim()).filter(Boolean);
}

function truncate(s: string, n: number) {
  return s.length > n ? s.slice(0, n) + '…' : s;
}

/**
 * Admin del bot de SOPORTE de WhatsApp: casos abiertos/escalados, FAQs curadas
 * por producto (capa 1), base de conocimiento que sube cada app (capa 2,
 * read-only acá) y el TONO que rige todo lo que escribe la IA.
 */
export default function Soporte() {
  const [tab, setTab] = useState<TabKey>('casos');

  const productsQ = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data,
    staleTime: 5 * 60_000,
  });
  const products = productsQ.data ?? [];
  const productName = (key: string) => products.find((p) => p.productKey === key)?.displayName ?? key;

  return (
    <div className="space-y-5 max-w-5xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Soporte (bot de WhatsApp)</h1>
        <p className="text-sm text-slate-500">
          El bot atiende los problemas de los clientes: primero prueba con las FAQs curadas, después con la
          base de conocimiento de cada app, y si no puede lo escala a un humano. Acá administrás todo eso.
        </p>
      </div>

      <div className="flex gap-1 flex-wrap">
        {TABS.map((t) => (
          <button key={t.key} onClick={() => setTab(t.key)}
            className={`text-sm px-3 py-1.5 rounded ${tab === t.key ? 'bg-brand-600 text-white' : 'bg-slate-100 text-slate-600 hover:bg-slate-200'}`}>
            {t.label}
          </button>
        ))}
      </div>

      {tab === 'casos' && <CasosTab productName={productName} />}
      {tab === 'faqs' && <FaqsTab products={products} productName={productName} />}
      {tab === 'conocimiento' && <ConocimientoTab productName={productName} />}
      {tab === 'tono' && <TonoTab products={products} productName={productName} />}
    </div>
  );
}

// ── Casos ────────────────────────────────────────────────────────────────────

const CASE_FILTERS = [
  { key: '', label: 'Pendientes' },
  { key: 'Open', label: 'Abiertos' },
  { key: 'Escalated', label: 'Escalados' },
  { key: 'Resolved', label: 'Resueltos' },
] as const;

const CASE_BADGE: Record<CaseStatus, { label: string; cls: string }> = {
  Open: { label: 'abierto', cls: 'bg-amber-100 text-amber-700' },
  Escalated: { label: 'escalado', cls: 'bg-rose-100 text-rose-700' },
  Resolved: { label: 'resuelto', cls: 'bg-emerald-100 text-emerald-700' },
};

function CasosTab({ productName }: { productName: (k: string) => string }) {
  const qc = useQueryClient();
  const [filter, setFilter] = useState<string>('');

  const casesQ = useQuery({
    queryKey: ['support-cases', filter],
    queryFn: async () => (await api.get<SupportCase[]>('/support/cases', {
      params: { status: filter || undefined },
    })).data,
    refetchInterval: 60000,
  });

  const patch = useMutation({
    mutationFn: async (v: { id: string; status: 'Resolved' | 'Open' }) =>
      api.patch(`/support/cases/${v.id}`, { status: v.status }),
    onSuccess: (_r, v) => {
      qc.invalidateQueries({ queryKey: ['support-cases'] });
      toast.success(v.status === 'Resolved' ? 'Caso resuelto' : 'Caso reabierto');
    },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo actualizar el caso'),
  });

  const cases = casesQ.data ?? [];

  return (
    <div className="card p-4">
      <div className="flex items-center justify-between gap-2 flex-wrap mb-3">
        <h2 className="text-lg font-semibold">Casos de soporte</h2>
        <div className="flex gap-1">
          {CASE_FILTERS.map((f) => (
            <button key={f.key} onClick={() => setFilter(f.key)}
              className={`text-xs px-2 py-1 rounded ${filter === f.key ? 'bg-brand-600 text-white' : 'bg-slate-100 text-slate-600 hover:bg-slate-200'}`}>
              {f.label}
            </button>
          ))}
        </div>
      </div>

      {casesQ.isLoading ? (
        <p className="text-slate-400 text-sm">Cargando…</p>
      ) : cases.length === 0 ? (
        <p className="text-slate-400 text-sm">
          {filter === 'Resolved'
            ? 'Todavía no hay casos resueltos.'
            : 'No hay casos que necesiten atención. El bot está al día.'}
        </p>
      ) : (
        <div className="overflow-x-auto -mx-1">
          <table className="min-w-full text-sm">
            <thead className="text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-2 py-1 text-left">App</th>
                <th className="px-2 py-1 text-left">Lead</th>
                <th className="px-2 py-1 text-left">Problema</th>
                <th className="px-2 py-1 text-left">Estado</th>
                <th className="px-2 py-1 text-right">Turnos bot</th>
                <th className="px-2 py-1 text-left">Fechas</th>
                <th className="px-2 py-1" />
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {cases.map((c) => (
                <tr key={c.id} className="hover:bg-slate-50 align-top">
                  <td className="px-2 py-2 font-medium whitespace-nowrap">{productName(c.productKey)}</td>
                  <td className="px-2 py-2">
                    {c.leadId ? (
                      <Link to={`/leads/${c.leadId}`} className="text-brand-600 hover:underline font-medium">
                        {c.leadName ?? 'ver lead'}
                      </Link>
                    ) : (
                      <span>{c.leadName ?? '—'}</span>
                    )}
                    {c.leadPhone && <div className="text-xs text-slate-500">{c.leadPhone}</div>}
                  </td>
                  <td className="px-2 py-2 text-slate-600 max-w-[280px]">
                    <span title={c.summary}>{c.summary ? truncate(c.summary, 160) : '—'}</span>
                  </td>
                  <td className="px-2 py-2">
                    <span className={`badge ${CASE_BADGE[c.status].cls}`}>{CASE_BADGE[c.status].label}</span>
                  </td>
                  <td className="px-2 py-2 text-right tabular-nums">{c.botTurns}</td>
                  <td className="px-2 py-2 text-xs text-slate-500 whitespace-nowrap">
                    <div>abrió {fmtDate(c.openedAt)}</div>
                    {c.escalatedAt && <div className="text-rose-600">escaló {fmtDate(c.escalatedAt)}</div>}
                    {c.resolvedAt
                      ? <div className="text-emerald-600">resuelto {fmtDate(c.resolvedAt)}</div>
                      : c.lastBotReplyAt && <div>últ. bot {fmtDate(c.lastBotReplyAt)}</div>}
                  </td>
                  <td className="px-2 py-2 text-right">
                    {c.status === 'Resolved' ? (
                      <button className="btn-secondary text-xs px-2 py-1" disabled={patch.isPending}
                        onClick={() => patch.mutate({ id: c.id, status: 'Open' })}>
                        Reabrir
                      </button>
                    ) : (
                      <button className="btn-primary text-xs px-2 py-1" disabled={patch.isPending}
                        onClick={() => patch.mutate({ id: c.id, status: 'Resolved' })}>
                        Resolver
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="text-[11px] text-slate-400 mt-2">
        <b>Escalado</b> = el bot no pudo (o no debió) seguir y espera un humano — entrale por la conversación
        del lead. Se refresca solo cada 60s.
      </p>
    </div>
  );
}

// ── FAQs ─────────────────────────────────────────────────────────────────────

function FaqsTab({ products, productName }: { products: Product[]; productName: (k: string) => string }) {
  const qc = useQueryClient();
  const [productFilter, setProductFilter] = useState('');

  const faqsQ = useQuery({
    queryKey: ['support-faqs', productFilter],
    queryFn: async () => (await api.get<Faq[]>('/support/faqs', {
      params: { productKey: productFilter || undefined },
    })).data,
  });

  const invalidate = () => qc.invalidateQueries({ queryKey: ['support-faqs'] });
  const faqs = faqsQ.data ?? [];

  return (
    <div className="space-y-4">
      <div className="card p-4">
        <div className="flex items-center gap-2 flex-wrap">
          <h2 className="text-lg font-semibold flex-1">FAQs curadas</h2>
          <select className="input w-auto" value={productFilter} onChange={(e) => setProductFilter(e.target.value)}>
            <option value="">Todas las apps</option>
            {products.map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
          </select>
        </div>
        <p className="text-xs text-slate-400 mt-2">
          Primera capa del bot: si la consulta del cliente matchea una FAQ habilitada, responde esto tal cual.
          Las <span className="badge bg-violet-100 text-violet-700">promovidas</span> salen de respuestas reales
          aprobadas en conversaciones.
        </p>
      </div>

      <NewFaqForm products={products} onCreated={invalidate} />

      {faqsQ.isLoading ? (
        <p className="text-slate-400 text-sm">Cargando…</p>
      ) : faqs.length === 0 ? (
        <p className="text-slate-400 text-sm">
          Todavía no hay FAQs{productFilter ? ' para esta app' : ''}. Creá una arriba o promové respuestas
          desde las conversaciones.
        </p>
      ) : (
        <div className="space-y-3">
          {faqs.map((f) => (
            <FaqCard key={f.id} faq={f} productName={productName(f.productKey)} onChanged={invalidate} />
          ))}
        </div>
      )}
    </div>
  );
}

function NewFaqForm({ products, onCreated }: { products: Product[]; onCreated: () => void }) {
  const [productKey, setProductKey] = useState('');
  const [question, setQuestion] = useState('');
  const [keywordsStr, setKeywordsStr] = useState('');
  const [answer, setAnswer] = useState('');

  const create = useMutation({
    mutationFn: async () => api.put('/support/faqs', {
      productKey,
      question: question.trim(),
      keywords: splitKeywords(keywordsStr),
      answer: answer.trim(),
      enabled: true,
    }),
    onSuccess: () => {
      setQuestion(''); setKeywordsStr(''); setAnswer('');
      onCreated(); toast.success('FAQ creada');
    },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo crear'),
  });

  return (
    <div className="card p-4 space-y-2">
      <label className="text-sm font-medium">Nueva FAQ</label>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
        <select className="input" value={productKey} onChange={(e) => setProductKey(e.target.value)}>
          <option value="">— App —</option>
          {products.map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
        </select>
        <input className="input" placeholder="Keywords separadas por coma (ej. factura, pago, mercadopago)"
          value={keywordsStr} onChange={(e) => setKeywordsStr(e.target.value)} />
      </div>
      <input className="input" placeholder="Pregunta (como la haría el cliente)"
        value={question} onChange={(e) => setQuestion(e.target.value)} />
      <textarea className="input min-h-[72px]" placeholder="Respuesta (el bot la manda tal cual)"
        value={answer} onChange={(e) => setAnswer(e.target.value)} />
      <div className="flex justify-end">
        <button className="btn-primary"
          disabled={!productKey || !question.trim() || !answer.trim() || create.isPending}
          onClick={() => create.mutate()}>
          Crear FAQ
        </button>
      </div>
    </div>
  );
}

function FaqCard({ faq, productName, onChanged }: { faq: Faq; productName: string; onChanged: () => void }) {
  const [editing, setEditing] = useState(false);
  const [question, setQuestion] = useState(faq.question);
  const [keywordsStr, setKeywordsStr] = useState(faq.keywords.join(', '));
  const [answer, setAnswer] = useState(faq.answer);
  const [enabled, setEnabled] = useState(faq.enabled);

  const save = useMutation({
    mutationFn: async (enabledOverride: boolean | undefined) => api.put('/support/faqs', {
      id: faq.id,
      productKey: faq.productKey,
      question: question.trim(),
      keywords: splitKeywords(keywordsStr),
      answer: answer.trim(),
      enabled: enabledOverride ?? enabled,
    }),
    onSuccess: () => { setEditing(false); onChanged(); toast.success('Guardada'); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo guardar'),
  });
  const remove = useMutation({
    mutationFn: async () => api.delete(`/support/faqs/${faq.id}`),
    onSuccess: () => { onChanged(); toast.success('FAQ borrada'); },
    onError: () => toast.error('No se pudo borrar'),
  });

  const keywords = splitKeywords(keywordsStr);

  return (
    <div className={`card p-4 space-y-2 ${enabled ? '' : 'opacity-60'}`}>
      <div className="flex items-start justify-between gap-2 flex-wrap">
        <div className="min-w-0 flex-1">
          <div className="text-xs text-slate-400">{productName}</div>
          {editing ? (
            <input className="input mt-1" value={question} onChange={(e) => setQuestion(e.target.value)} />
          ) : (
            <div className="font-semibold">{question}</div>
          )}
        </div>
        <div className="flex items-center gap-2 shrink-0">
          {faq.source === 'promoted' && (
            <span className="badge bg-violet-100 text-violet-700"
              title="Salió de una respuesta real aprobada en una conversación">
              promovida
            </span>
          )}
          <span className="text-xs text-slate-400 tabular-nums" title="Cuántas veces la usó el bot">
            usada {faq.timesUsed} {faq.timesUsed === 1 ? 'vez' : 'veces'}
          </span>
          <Switch on={enabled}
            title={enabled ? 'Habilitada (el bot la usa)' : 'Deshabilitada (el bot no la usa)'}
            onClick={() => { const next = !enabled; setEnabled(next); save.mutate(next); }} />
        </div>
      </div>

      {!editing && keywords.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {keywords.map((k) => <span key={k} className="badge bg-slate-100 text-slate-600">{k}</span>)}
        </div>
      )}

      {editing ? (
        <>
          <input className="input" placeholder="Keywords separadas por coma"
            value={keywordsStr} onChange={(e) => setKeywordsStr(e.target.value)} />
          <textarea className="input min-h-[72px]" value={answer} onChange={(e) => setAnswer(e.target.value)} />
          <div className="flex justify-between gap-2">
            <button className="btn-danger text-xs"
              onClick={() => { if (window.confirm('¿Borrar esta FAQ? El bot deja de usarla.')) remove.mutate(); }}>
              Borrar
            </button>
            <div className="flex gap-2">
              <button className="btn-secondary text-xs"
                onClick={() => {
                  setEditing(false);
                  setQuestion(faq.question);
                  setKeywordsStr(faq.keywords.join(', '));
                  setAnswer(faq.answer);
                }}>
                Cancelar
              </button>
              <button className="btn-primary text-xs"
                disabled={save.isPending || !question.trim() || !answer.trim()}
                onClick={() => save.mutate(undefined)}>
                Guardar
              </button>
            </div>
          </div>
        </>
      ) : (
        <>
          <p className="text-sm text-slate-600 whitespace-pre-wrap">{answer}</p>
          <div className="flex justify-end">
            <button className="btn-secondary text-xs px-2 py-1" onClick={() => setEditing(true)}>Editar</button>
          </div>
        </>
      )}
    </div>
  );
}

// ── Conocimiento (read-only) ─────────────────────────────────────────────────

function ConocimientoTab({ productName }: { productName: (k: string) => string }) {
  const [expanded, setExpanded] = useState<string | null>(null);

  const listQ = useQuery({
    queryKey: ['support-knowledge'],
    queryFn: async () => (await api.get<KnowledgeItem[]>('/support/knowledge')).data,
  });

  const items = listQ.data ?? [];

  return (
    <div className="card p-4">
      <h2 className="text-lg font-semibold mb-1">Base de conocimiento por app</h2>
      <p className="text-xs text-slate-500 mb-3">
        Solo lectura: se sube desde el deploy de cada app
        (<code className="bg-slate-100 px-1 rounded">scripts/generate-support-kb</code>). El bot la usa como
        segunda capa cuando ninguna FAQ matchea.
      </p>
      {listQ.isLoading ? (
        <p className="text-slate-400 text-sm">Cargando…</p>
      ) : items.length === 0 ? (
        <p className="text-slate-400 text-sm">
          Ninguna app subió conocimiento todavía. Aparece solo con el próximo deploy de cada app.
        </p>
      ) : (
        <div className="overflow-x-auto -mx-1">
          <table className="min-w-full text-sm">
            <thead className="text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-2 py-1 text-left">App</th>
                <th className="px-2 py-1 text-left">Versión</th>
                <th className="px-2 py-1 text-left">Subida</th>
                <th className="px-2 py-1 text-right">Tamaño</th>
                <th className="px-2 py-1" />
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {items.map((k) => (
                <Fragment key={k.id}>
                  <tr className="hover:bg-slate-50">
                    <td className="px-2 py-1.5 font-medium">{productName(k.productKey)}</td>
                    <td className="px-2 py-1.5 text-slate-600">{k.sourceVersion || '—'}</td>
                    <td className="px-2 py-1.5 text-slate-600">{fmtDate(k.uploadedAt)}</td>
                    <td className="px-2 py-1.5 text-right tabular-nums">
                      {k.length.toLocaleString('es-AR')} caracteres
                    </td>
                    <td className="px-2 py-1.5 text-right">
                      <button className="btn-secondary text-xs px-2 py-1"
                        onClick={() => setExpanded(expanded === k.productKey ? null : k.productKey)}>
                        {expanded === k.productKey ? 'Ocultar' : 'Ver contenido'}
                      </button>
                    </td>
                  </tr>
                  {expanded === k.productKey && (
                    <tr>
                      <td colSpan={5} className="px-2 py-2">
                        <KnowledgeContent productKey={k.productKey} />
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function KnowledgeContent({ productKey }: { productKey: string }) {
  const q = useQuery({
    queryKey: ['support-knowledge', productKey],
    queryFn: async () => (await api.get<KnowledgeDetail>(`/support/knowledge/${productKey}`)).data,
  });
  if (q.isLoading) return <p className="text-slate-400 text-sm">Cargando…</p>;
  if (!q.data) return <p className="text-slate-400 text-sm">No se pudo cargar el contenido.</p>;
  return (
    <pre className="whitespace-pre-wrap text-xs bg-slate-50 border border-slate-200 rounded p-3 max-h-96 overflow-y-auto">
      {q.data.content}
    </pre>
  );
}

// ── Tono ─────────────────────────────────────────────────────────────────────

function TonoTab({ products, productName }: { products: Product[]; productName: (k: string) => string }) {
  const q = useQuery({
    queryKey: ['support-tone'],
    queryFn: async () => (await api.get<ToneResp>('/support/tone')).data,
  });
  if (q.isLoading) return <p className="text-slate-400 text-sm">Cargando…</p>;
  if (!q.data) return null;
  return <ToneEditor data={q.data} products={products} productName={productName} />;
}

function ToneEditor({ data, products, productName }: {
  data: ToneResp;
  products: Product[];
  productName: (k: string) => string;
}) {
  const qc = useQueryClient();
  const invalidate = () => qc.invalidateQueries({ queryKey: ['support-tone'] });

  const globalProfile = data.profiles.find((p) => p.scope === 'global');
  const [globalText, setGlobalText] = useState(globalProfile?.content ?? data.defaultTone);

  const saveGlobal = useMutation({
    mutationFn: async () => api.put('/support/tone', { scope: 'global', content: globalText.trim() }),
    onSuccess: () => { invalidate(); toast.success('Tono global guardado'); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo guardar'),
  });
  const resetGlobal = useMutation({
    mutationFn: async () => api.delete('/support/tone/global'),
    onSuccess: () => { setGlobalText(data.defaultTone); invalidate(); toast.success('Vuelve el tono default'); },
    onError: () => toast.error('No se pudo restaurar'),
  });

  const overrides = data.profiles.filter((p) => p.scope !== 'global');
  const productsWithoutOverride = products.filter((p) => !overrides.some((o) => o.scope === p.productKey));

  const [newScope, setNewScope] = useState('');
  const [newContent, setNewContent] = useState('');
  const saveNew = useMutation({
    mutationFn: async () => api.put('/support/tone', { scope: newScope, content: newContent.trim() }),
    onSuccess: () => { setNewScope(''); setNewContent(''); invalidate(); toast.success('Override guardado'); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo guardar'),
  });

  return (
    <div className="space-y-4">
      <div className="card p-4 space-y-2">
        <div className="flex items-center justify-between gap-2 flex-wrap">
          <h2 className="text-lg font-semibold">Tono global de la IA</h2>
          {globalProfile
            ? <span className="badge bg-brand-100 text-brand-700">personalizado</span>
            : <span className="badge bg-slate-100 text-slate-600">default (sin guardar)</span>}
        </div>
        <p className="text-xs text-amber-700 bg-amber-50 border border-amber-200 rounded px-3 py-2">
          Ojo: este tono rige TODO lo que escribe la IA (venta, nudges, soporte). Cambiarlo le cambia la voz
          en todos lados.
        </p>
        <textarea className="input min-h-[220px] font-mono text-xs" value={globalText}
          onChange={(e) => setGlobalText(e.target.value)} placeholder={data.defaultTone} />
        <div className="flex justify-between gap-2">
          {globalProfile ? (
            <button className="btn-secondary text-xs" disabled={resetGlobal.isPending}
              onClick={() => {
                if (window.confirm('¿Borrar el tono personalizado y volver al default embebido?')) resetGlobal.mutate();
              }}>
              Restaurar default
            </button>
          ) : <span />}
          <button className="btn-primary" disabled={saveGlobal.isPending || !globalText.trim()}
            onClick={() => saveGlobal.mutate()}>
            Guardar
          </button>
        </div>
      </div>

      <div className="card p-4 space-y-3">
        <h2 className="text-lg font-semibold">Overrides por app</h2>
        <p className="text-xs text-slate-500">
          Si una app necesita una voz distinta, pisás el global solo para ella. Sin override, usa el global.
        </p>
        {overrides.length === 0 && (
          <p className="text-slate-400 text-sm">Ninguna app tiene override — todas usan el tono global.</p>
        )}
        {overrides.map((o) => (
          <ToneOverrideCard key={o.id} profile={o} productName={productName(o.scope)} onChanged={invalidate} />
        ))}

        <div className="rounded border border-slate-200 p-3 space-y-2">
          <label className="text-sm font-medium">Nuevo override</label>
          <select className="input" value={newScope} onChange={(e) => setNewScope(e.target.value)}>
            <option value="">— App —</option>
            {productsWithoutOverride.map((p) => (
              <option key={p.productKey} value={p.productKey}>{p.displayName}</option>
            ))}
          </select>
          <textarea className="input min-h-[96px]" placeholder="Tono para esta app (reemplaza al global)"
            value={newContent} onChange={(e) => setNewContent(e.target.value)} />
          <div className="flex justify-end">
            <button className="btn-primary text-xs" disabled={!newScope || !newContent.trim() || saveNew.isPending}
              onClick={() => saveNew.mutate()}>
              Guardar override
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function ToneOverrideCard({ profile, productName, onChanged }: {
  profile: ToneProfile;
  productName: string;
  onChanged: () => void;
}) {
  const [content, setContent] = useState(profile.content);
  const save = useMutation({
    mutationFn: async () => api.put('/support/tone', { scope: profile.scope, content: content.trim() }),
    onSuccess: () => { onChanged(); toast.success('Override guardado'); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo guardar'),
  });
  const remove = useMutation({
    mutationFn: async () => api.delete(`/support/tone/${profile.scope}`),
    onSuccess: () => { onChanged(); toast.success('Override borrado — vuelve al global'); },
    onError: () => toast.error('No se pudo borrar'),
  });
  return (
    <div className="rounded border border-slate-200 p-3 space-y-2">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <div className="font-medium text-sm">
          {productName} <span className="text-slate-400 font-normal">· {profile.scope}</span>
        </div>
        <span className="text-xs text-slate-400">editado {fmtDate(profile.updatedAt)}</span>
      </div>
      <textarea className="input min-h-[96px]" value={content} onChange={(e) => setContent(e.target.value)} />
      <div className="flex justify-between gap-2">
        <button className="btn-danger text-xs"
          onClick={() => {
            if (window.confirm('¿Borrar el override? Esta app vuelve al tono global.')) remove.mutate();
          }}>
          Borrar override
        </button>
        <button className="btn-primary text-xs" disabled={save.isPending || !content.trim()}
          onClick={() => save.mutate()}>
          Guardar
        </button>
      </div>
    </div>
  );
}
