import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import toast from 'react-hot-toast';
import clsx from 'clsx';
import { api } from '../lib/api';
import type { AdSeen, MediaAsset, Pitch, PitchEnrollment, PitchFollowUp, PitchMessage, PitchStep, Product } from '../lib/types';

/**
 * Pitches por anuncio (modelo Smart Setter / GHL): un guion de pasos por creativo. El lead que
 * entra por el anuncio recibe el paso 1; su respuesta avanza al siguiente; si no responde salen
 * los follow-ups. La tabla muestra enrolados / activos / respondieron / convertidos por pitch.
 */
const STATUS_OPTIONS = [
  { value: '', label: '— no tocar —' },
  { value: 'Replied', label: 'Respondió' },
  { value: 'Interested', label: 'Interesado' },
  { value: 'DemoScheduled', label: 'Demo agendada' }
];

const emptyMessage = (): PitchMessage => ({ text: '', mediaAssetId: null, voiceText: null, delaySeconds: 5 });
const emptyFollowUp = (): PitchFollowUp => ({ afterHours: 1, text: '', mediaAssetId: null });
const emptyStep = (): PitchStep => ({ title: '', messages: [emptyMessage()], followUps: [] });

type Draft = Omit<Pitch, 'id' | 'updatedAt' | 'stats'> & { id?: string };

const newDraft = (productKey: string): Draft => ({
  productKey,
  name: '',
  active: true,
  sortOrder: 0,
  adIds: [],
  triggerText: '',
  isDefault: false,
  steps: [emptyStep()],
  autoTagOnReply: 'respondio',
  statusOnReply: 'Interested',
  aiAfterPitch: true,
  replyDelayMinSec: 8,
  replyDelayMaxSec: 40,
  channel: 'WhatsApp',
  autoEnroll: false,
  dailyEnrollCap: 30
});

export default function Pitches() {
  const qc = useQueryClient();
  const [productKey, setProductKey] = useState('');
  const [editing, setEditing] = useState<Draft | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [bulkFor, setBulkFor] = useState<Pitch | null>(null);

  const productsQ = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data,
    staleTime: 5 * 60_000
  });
  const effectiveProduct = productKey || productsQ.data?.[0]?.productKey || '';

  const pitchesQ = useQuery({
    queryKey: ['pitches', effectiveProduct],
    enabled: !!effectiveProduct,
    queryFn: async () => (await api.get<Pitch[]>('/pitches', { params: { productKey: effectiveProduct } })).data,
    refetchInterval: 30_000
  });
  const adsQ = useQuery({
    queryKey: ['pitches-ads', effectiveProduct],
    enabled: !!effectiveProduct,
    queryFn: async () => (await api.get<AdSeen[]>('/pitches/ads', { params: { productKey: effectiveProduct } })).data,
    staleTime: 60_000
  });
  const mediaQ = useQuery({
    queryKey: ['product-media', effectiveProduct],
    enabled: !!effectiveProduct,
    queryFn: async () => (await api.get<MediaAsset[]>(`/products/${effectiveProduct}/media`)).data
  });

  const saveMut = useMutation({
    mutationFn: async (d: Draft) => {
      const body = { ...d, triggerText: d.triggerText || null, autoTagOnReply: d.autoTagOnReply || null, statusOnReply: d.statusOnReply || null };
      return d.id ? (await api.put<Pitch>(`/pitches/${d.id}`, body)).data : (await api.post<Pitch>('/pitches', body)).data;
    },
    onSuccess: () => {
      toast.success('Pitch guardado');
      setEditing(null);
      qc.invalidateQueries({ queryKey: ['pitches'] });
      qc.invalidateQueries({ queryKey: ['pitches-ads'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'No se pudo guardar')
  });
  const toggleMut = useMutation({
    mutationFn: async (p: Pitch) => (await api.put(`/pitches/${p.id}`, { ...p, active: !p.active })).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['pitches'] })
  });
  const dupMut = useMutation({
    mutationFn: async (id: string) => (await api.post<Pitch>(`/pitches/${id}/duplicate`)).data,
    onSuccess: (p) => { toast.success('Copia creada (pausada)'); qc.invalidateQueries({ queryKey: ['pitches'] }); setEditing(toDraft(p)); }
  });
  const delMut = useMutation({
    mutationFn: async (id: string) => (await api.delete(`/pitches/${id}`)).data,
    onSuccess: () => { toast.success('Pitch borrado'); qc.invalidateQueries({ queryKey: ['pitches'] }); }
  });

  const pitches = pitchesQ.data ?? [];
  const totals = useMemo(() => pitches.reduce((a, p) => ({
    enrolled: a.enrolled + p.stats.enrolled, active: a.active + p.stats.active, replied: a.replied + p.stats.replied,
    completed: a.completed + p.stats.completed, gaveUp: a.gaveUp + p.stats.gaveUp, converted: a.converted + p.stats.converted
  }), { enrolled: 0, active: 0, replied: 0, completed: 0, gaveUp: 0, converted: 0 }), [pitches]);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-3">
        <div>
          <h1 className="text-2xl font-bold">Pitches por anuncio</h1>
          <p className="text-sm text-slate-500 max-w-2xl">
            Un guion por creativo. El lead que escribe desde el anuncio recibe el paso 1 al instante; cada respuesta suya
            avanza al siguiente paso; si no contesta, salen los follow-ups. Al terminar, la IA sigue sola o se lo pasás al vendedor.
          </p>
        </div>
        <div className="ml-auto flex items-end gap-2">
          <label className="text-sm">
            <div className="text-[11px] text-slate-500">Producto</div>
            <select className="input" value={effectiveProduct} onChange={(e) => setProductKey(e.target.value)}>
              {(productsQ.data ?? []).map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
            </select>
          </label>
          <button className="btn-primary" onClick={() => setEditing(newDraft(effectiveProduct))} disabled={!effectiveProduct}>+ Nuevo pitch</button>
        </div>
      </div>

      <div className="card overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="text-left text-xs uppercase tracking-wide text-slate-500 bg-slate-50">
            <tr>
              <th className="p-3">Pitch</th>
              <th className="p-3">Estado</th>
              <th className="p-3">Match</th>
              <th className="p-3 text-right">Enrolados</th>
              <th className="p-3 text-right">Activos</th>
              <th className="p-3 text-right">Respondieron</th>
              <th className="p-3 text-right">Terminaron</th>
              <th className="p-3 text-right">Convertidos</th>
              <th className="p-3"></th>
            </tr>
          </thead>
          <tbody>
            {pitchesQ.isLoading && <tr><td className="p-4 text-slate-500" colSpan={9}>Cargando…</td></tr>}
            {!pitchesQ.isLoading && pitches.length === 0 && (
              <tr><td className="p-6 text-slate-500" colSpan={9}>
                Todavía no hay pitches para este producto. Creá uno y marcalo como <b>default</b> para que todos los leads de anuncio lo reciban,
                o asignale los anuncios específicos.
              </td></tr>
            )}
            {pitches.map((p) => (
              <PitchRow key={p.id} p={p} expanded={expanded === p.id}
                onExpand={() => setExpanded(expanded === p.id ? null : p.id)}
                onEdit={() => setEditing(toDraft(p))}
                onBulk={() => setBulkFor(p)}
                onToggle={() => toggleMut.mutate(p)}
                onDup={() => dupMut.mutate(p.id)}
                onDelete={() => { if (confirm(`¿Borrar "${p.name}"? Se pierde el historial de enrolados.`)) delMut.mutate(p.id); }} />
            ))}
          </tbody>
          {pitches.length > 1 && (
            <tfoot className="text-xs font-medium bg-slate-50">
              <tr>
                <td className="p-3" colSpan={3}>Total</td>
                <td className="p-3 text-right">{totals.enrolled}</td>
                <td className="p-3 text-right">{totals.active}</td>
                <td className="p-3 text-right">{totals.replied} <Pct n={totals.replied} d={totals.enrolled} /></td>
                <td className="p-3 text-right">{totals.completed}</td>
                <td className="p-3 text-right">{totals.converted} <Pct n={totals.converted} d={totals.enrolled} /></td>
                <td></td>
              </tr>
            </tfoot>
          )}
        </table>
      </div>

      <AdsSeenCard ads={adsQ.data ?? []} pitches={pitches} loading={adsQ.isLoading} />

      {bulkFor && <BulkEnrollModal pitch={bulkFor} onClose={() => setBulkFor(null)} />}

      {editing && (
        <PitchEditor draft={editing} media={mediaQ.data ?? []} ads={adsQ.data ?? []}
          saving={saveMut.isPending}
          onChange={setEditing}
          onCancel={() => setEditing(null)}
          onSave={() => saveMut.mutate(editing)} />
      )}
    </div>
  );
}

function toDraft(p: Pitch): Draft {
  const { stats: _s, updatedAt: _u, ...rest } = p;
  return { ...rest, steps: JSON.parse(JSON.stringify(p.steps)) };
}

function Pct({ n, d }: { n: number; d: number }) {
  if (!d) return null;
  return <span className="text-slate-400 font-normal">({Math.round((n / d) * 100)}%)</span>;
}

function PitchRow({ p, expanded, onExpand, onEdit, onBulk, onToggle, onDup, onDelete }: {
  p: Pitch; expanded: boolean; onExpand: () => void; onEdit: () => void; onBulk: () => void; onToggle: () => void; onDup: () => void; onDelete: () => void;
}) {
  const isIg = p.channel === 'Instagram';
  const match = isIg ? (
    <span>{p.autoEnroll ? `auto-enrola hasta ${p.dailyEnrollCap}/día` : 'enrolamiento manual (Bulk)'}</span>
  ) : [
    p.isDefault ? 'default del producto' : null,
    p.adIds.length ? `${p.adIds.length} anuncio${p.adIds.length > 1 ? 's' : ''}` : null,
    p.triggerText ? `texto "${p.triggerText}"` : null
  ].filter(Boolean).join(' · ') || <span className="text-rose-600">sin match (no enrola a nadie)</span>;
  return (
    <>
      <tr className="border-t border-slate-100 hover:bg-slate-50">
        <td className="p-3">
          <button className="font-medium text-left hover:underline" onClick={onExpand}>{p.name}</button>
          <div className="text-xs text-slate-500">
            <span className={clsx('inline-block text-[10px] px-1 rounded mr-1 border', isIg ? 'bg-pink-50 text-pink-700 border-pink-200' : 'bg-emerald-50 text-emerald-700 border-emerald-200')}>
              {isIg ? 'Instagram · outbound' : 'WhatsApp · anuncio'}
            </span>
            {p.steps.length} paso{p.steps.length !== 1 ? 's' : ''} · {p.steps.reduce((a, s) => a + s.followUps.length, 0)} follow-ups ·{' '}
            {p.aiAfterPitch ? 'IA después del pitch' : 'handoff humano al terminar'}
          </div>
        </td>
        <td className="p-3">
          <span className={clsx('text-[10px] px-1.5 py-0.5 rounded font-medium', p.active ? 'bg-emerald-100 text-emerald-700' : 'bg-slate-100 text-slate-500')}>
            {p.active ? 'activo' : 'pausado'}
          </span>
        </td>
        <td className="p-3 text-xs text-slate-600">{match}</td>
        <td className="p-3 text-right font-mono">{p.stats.enrolled}</td>
        <td className="p-3 text-right font-mono">{p.stats.active}</td>
        <td className="p-3 text-right font-mono">{p.stats.replied} <Pct n={p.stats.replied} d={p.stats.enrolled} /></td>
        <td className="p-3 text-right font-mono">{p.stats.completed}</td>
        <td className="p-3 text-right font-mono">{p.stats.converted} <Pct n={p.stats.converted} d={p.stats.enrolled} /></td>
        <td className="p-3 text-right whitespace-nowrap">
          <button className="text-xs text-brand-700 hover:underline mr-2" onClick={onEdit}>Editar</button>
          {isIg && <button className="text-xs text-pink-700 hover:underline mr-2" onClick={onBulk}>Enrolar leads</button>}
          <button className="text-xs text-slate-600 hover:underline mr-2" onClick={onToggle}>{p.active ? 'Pausar' : 'Activar'}</button>
          <button className="text-xs text-slate-600 hover:underline mr-2" onClick={onDup}>Duplicar</button>
          <button className="text-xs text-rose-600 hover:underline" onClick={onDelete}>Borrar</button>
        </td>
      </tr>
      {expanded && (
        <tr className="bg-slate-50/60">
          <td colSpan={9} className="p-3"><Enrollments pitchId={p.id} /></td>
        </tr>
      )}
    </>
  );
}

function Enrollments({ pitchId }: { pitchId: string }) {
  const qc = useQueryClient();
  const q = useQuery({
    queryKey: ['pitch-enrollments', pitchId],
    queryFn: async () => (await api.get<PitchEnrollment[]>(`/pitches/${pitchId}/enrollments`)).data,
    refetchInterval: 20_000
  });
  const stop = useMutation({
    mutationFn: async (leadId: string) => (await api.post(`/pitches/enrollments/${leadId}/stop`)).data,
    onSuccess: () => { toast.success('Lead sacado del pitch'); qc.invalidateQueries({ queryKey: ['pitch-enrollments', pitchId] }); qc.invalidateQueries({ queryKey: ['pitches'] }); }
  });
  if (q.isLoading) return <div className="text-xs text-slate-500">Cargando enrolados…</div>;
  const rows = q.data ?? [];
  if (rows.length === 0) return <div className="text-xs text-slate-500">Nadie enrolado todavía.</div>;
  return (
    <table className="w-full text-xs">
      <thead className="text-slate-500"><tr>
        <th className="text-left p-1">Lead</th><th className="text-left p-1">Anuncio</th><th className="text-left p-1">Estado</th>
        <th className="text-right p-1">Paso</th><th className="text-right p-1">Follow-ups</th><th className="text-right p-1">Respuestas</th>
        <th className="text-left p-1">Pitch</th><th className="text-left p-1">Enrolado</th><th></th>
      </tr></thead>
      <tbody>
        {rows.map((r) => {
          const state = r.completedAt ? 'terminó' : r.gaveUpAt ? 'sin respuesta' : r.nextStepDueAt ? 'próximo paso en cola' : 'esperando respuesta';
          return (
            <tr key={r.leadId} className="border-t border-slate-100">
              <td className="p-1"><Link className="text-brand-700 hover:underline" to={`/conversations?lead=${r.leadId}`}>{r.leadName}</Link> <span className="text-slate-400">{r.phone}</span></td>
              <td className="p-1 text-slate-500 truncate max-w-[220px]">{r.adTitle ?? '—'}</td>
              <td className="p-1">{r.status}</td>
              <td className="p-1 text-right font-mono">{Math.max(0, r.stepIndex)}</td>
              <td className="p-1 text-right font-mono">{r.followupsSent}</td>
              <td className="p-1 text-right font-mono">{r.replies}</td>
              <td className="p-1">{state}</td>
              <td className="p-1 text-slate-500">{new Date(r.enrolledAt).toLocaleString('es-AR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })}</td>
              <td className="p-1 text-right">
                {!r.completedAt && !r.gaveUpAt && (
                  <button className="text-rose-600 hover:underline" onClick={() => stop.mutate(r.leadId)}>Sacar</button>
                )}
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

function AdsSeenCard({ ads, pitches, loading }: { ads: AdSeen[]; pitches: Pitch[]; loading: boolean }) {
  const qc = useQueryClient();
  const assign = useMutation({
    mutationFn: async ({ ad, pitchId }: { ad: AdSeen; pitchId: string }) => {
      // Sacarlo del pitch que lo tenía y ponerlo en el nuevo (los ids son exclusivos).
      const prev = pitches.find((p) => p.adIds.includes(ad.adId));
      if (prev && prev.id !== pitchId) await api.put(`/pitches/${prev.id}`, { ...prev, adIds: prev.adIds.filter((a) => a !== ad.adId) });
      if (pitchId) {
        const next = pitches.find((p) => p.id === pitchId)!;
        if (!next.adIds.includes(ad.adId)) await api.put(`/pitches/${next.id}`, { ...next, adIds: [...next.adIds, ad.adId] });
      }
    },
    onSuccess: () => { toast.success('Anuncio asignado'); qc.invalidateQueries({ queryKey: ['pitches'] }); qc.invalidateQueries({ queryKey: ['pitches-ads'] }); },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'No se pudo asignar')
  });
  return (
    <div className="card p-4 space-y-2">
      <div>
        <h2 className="font-semibold">Anuncios vistos (últimos 90 días)</h2>
        <p className="text-xs text-slate-500">
          Cada chat que entra desde un click-to-WhatsApp trae el id del anuncio (externalAdReply). Acá ves cuántos leads trajo cada
          creativo y a qué pitch está asignado. Si un anuncio no aparece, es que todavía no escribió nadie desde él.
        </p>
      </div>
      {loading && <div className="text-xs text-slate-500">Cargando…</div>}
      {!loading && ads.length === 0 && <div className="text-xs text-slate-500">Ningún lead con atribución de anuncio todavía.</div>}
      {ads.length > 0 && (
        <table className="w-full text-sm">
          <thead className="text-left text-xs uppercase tracking-wide text-slate-500"><tr>
            <th className="p-2">Anuncio</th><th className="p-2">Id</th><th className="p-2 text-right">Leads</th><th className="p-2 text-right">Respondieron</th>
            <th className="p-2 text-right">Ganados</th><th className="p-2">Último</th><th className="p-2">Pitch</th>
          </tr></thead>
          <tbody>
            {ads.map((a) => (
              <tr key={a.adId} className="border-t border-slate-100">
                <td className="p-2 truncate max-w-[320px]" title={a.title ?? ''}>{a.title ?? <span className="text-slate-400">(sin título)</span>}</td>
                <td className="p-2 font-mono text-xs text-slate-500">{a.adId}</td>
                <td className="p-2 text-right font-mono">{a.leads}</td>
                <td className="p-2 text-right font-mono">{a.replied}</td>
                <td className="p-2 text-right font-mono">{a.closed}</td>
                <td className="p-2 text-xs text-slate-500">{new Date(a.lastSeen).toLocaleDateString('es-AR')}</td>
                <td className="p-2">
                  <select className="input text-xs py-1" value={a.pitch?.id ?? ''}
                    onChange={(e) => assign.mutate({ ad: a, pitchId: e.target.value })}>
                    <option value="">— default —</option>
                    {pitches.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                  </select>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

function BulkEnrollModal({ pitch, onClose }: { pitch: Pitch; onClose: () => void }) {
  const qc = useQueryClient();
  const [limit, setLimit] = useState(30);
  const [statuses, setStatuses] = useState<string[]>(['New', 'Assigned']);
  const [city, setCity] = useState('');
  const preview = useQuery({
    queryKey: ['pitch-enroll-preview', pitch.id, statuses.join(','), city],
    queryFn: async () => (await api.get<{ eligible: number; activeAccounts: number }>(`/pitches/${pitch.id}/enroll-preview`, { params: { statuses: statuses.join(','), city: city || undefined } })).data
  });
  const enroll = useMutation({
    mutationFn: async () => (await api.post<{ enrolled: number }>(`/pitches/${pitch.id}/enroll-bulk`, { limit, statuses, city: city || null })).data,
    onSuccess: (r) => { toast.success(`${r.enrolled} leads enrolados — los DMs salen de a uno por la cola de Instagram`); qc.invalidateQueries({ queryKey: ['pitches'] }); qc.invalidateQueries({ queryKey: ['pitch-enrollments', pitch.id] }); onClose(); },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'No se pudo enrolar')
  });
  const toggle = (st: string) => setStatuses((s) => (s.includes(st) ? s.filter((x) => x !== st) : [...s, st]));
  return (
    <div className="fixed inset-0 bg-black/40 grid place-items-center z-50 p-4">
      <div className="card p-5 w-full max-w-lg space-y-3">
        <h3 className="text-lg font-semibold">Enrolar leads en "{pitch.name}"</h3>
        <p className="text-xs text-slate-500">
          Toma leads de <b>{pitch.productKey}</b> con handle de Instagram que nunca recibieron un mensaje, mejor score primero.
          El paso 1 sale como DM por la cola de Instagram (cap diario por cuenta, 1 DM por tick).
        </p>
        <div className="text-sm">
          <div className="text-slate-500 mb-1">Estados a incluir</div>
          <div className="flex gap-2 flex-wrap">
            {['New', 'Assigned', 'Sent', 'Replied'].map((st) => (
              <label key={st} className="flex items-center gap-1 text-xs border rounded px-2 py-1">
                <input type="checkbox" checked={statuses.includes(st)} onChange={() => toggle(st)} /> {st}
              </label>
            ))}
          </div>
        </div>
        <div className="grid grid-cols-2 gap-2 text-sm">
          <label><div className="text-slate-500 mb-1">Cantidad</div><input type="number" className="input" value={limit} onChange={(e) => setLimit(Number(e.target.value))} /></label>
          <label><div className="text-slate-500 mb-1">Ciudad (opcional)</div><input className="input" value={city} onChange={(e) => setCity(e.target.value)} placeholder="ej. Córdoba" /></label>
        </div>
        <div className="text-xs text-slate-600">
          {preview.isLoading ? 'Calculando…' : <>Elegibles: <b>{preview.data?.eligible ?? 0}</b> · Cuentas de IG logueadas: <b>{preview.data?.activeAccounts ?? 0}</b>
            {(preview.data?.activeAccounts ?? 0) === 0 && <span className="text-rose-600"> — sin cuentas logueadas los DMs quedan en cola.</span>}</>}
        </div>
        <div className="flex justify-end gap-2">
          <button className="btn-secondary" onClick={onClose}>Cancelar</button>
          <button className="btn-primary" disabled={enroll.isPending || !pitch.active} onClick={() => enroll.mutate()} title={!pitch.active ? 'Activá el pitch primero' : ''}>
            {enroll.isPending ? 'Enrolando…' : `Enrolar hasta ${limit}`}
          </button>
        </div>
      </div>
    </div>
  );
}

function PitchEditor({ draft, media, ads, saving, onChange, onCancel, onSave }: {
  draft: Draft; media: MediaAsset[]; ads: AdSeen[]; saving: boolean;
  onChange: (d: Draft) => void; onCancel: () => void; onSave: () => void;
}) {
  const set = (patch: Partial<Draft>) => onChange({ ...draft, ...patch });
  const setStep = (i: number, s: PitchStep) => set({ steps: draft.steps.map((x, k) => (k === i ? s : x)) });
  const [newAdId, setNewAdId] = useState('');
  const mediaLabel = (id?: string | null) => media.find((m) => m.id === id)?.fileName ?? '';

  return (
    <div className="fixed inset-0 bg-black/40 z-50 overflow-y-auto p-4">
      <div className="card max-w-4xl mx-auto my-6 p-5 space-y-5">
        <div className="flex items-start justify-between gap-3">
          <div>
            <h3 className="text-lg font-semibold">{draft.id ? 'Editar pitch' : 'Nuevo pitch'} <span className="text-slate-400 font-normal">· {draft.productKey}</span></h3>
            <p className="text-xs text-slate-500">Placeholders disponibles en los textos: {'{name}'} {'{seller}'} {'{price}'} {'{checkout_url}'}. Spin-text {'{hola|buenas}'}.</p>
          </div>
          <button className="text-slate-500 hover:text-slate-700" onClick={onCancel}>✕</button>
        </div>

        {/* Ajustes */}
        <div className="grid md:grid-cols-2 gap-3">
          <label className="text-sm block md:col-span-2">
            <div className="text-slate-500 mb-1">Nombre</div>
            <input className="input" placeholder='AD 1 — placa "¿cuántos turnos perdiste?"' value={draft.name} onChange={(e) => set({ name: e.target.value })} />
          </label>
          <div className="text-sm md:col-span-2 flex flex-wrap gap-2 items-center">
            <div className="text-slate-500">Canal</div>
            {(['WhatsApp', 'Instagram'] as const).map((c) => (
              <button key={c} type="button" onClick={() => set({ channel: c })}
                className={clsx('px-3 py-1 rounded border text-sm', draft.channel === c ? 'bg-slate-800 text-white border-slate-800' : 'bg-white border-slate-200 hover:bg-slate-50')}>
                {c === 'WhatsApp' ? '💬 WhatsApp — se dispara cuando el lead escribe desde el anuncio' : '📸 Instagram — outbound: el paso 1 es el primer DM'}
              </button>
            ))}
          </div>
          {draft.channel === 'Instagram' && (
            <div className="md:col-span-2 bg-pink-50 border border-pink-200 rounded p-3 text-sm space-y-2">
              <div className="text-xs text-pink-800">
                Los DMs salen por la cola de Instagram del hub con <b>tus cuentas</b> (Cuentas IG), respetando el cap diario por cuenta y el pacing anti-bloqueo.
                Instagram es <b>solo texto</b>: los adjuntos y audios de los mensajes se ignoran (si un mensaje solo tiene nota de voz, se manda su guion como texto).
                Enrolás leads con el botón <b>Enrolar leads</b> (Bulk) o dejás que se enrolen solos:
              </div>
              <label className="flex items-center gap-2">
                <input type="checkbox" checked={draft.autoEnroll} onChange={(e) => set({ autoEnroll: e.target.checked })} />
                Auto-enrolar leads del producto con handle de IG que nunca fueron contactados
              </label>
              <label className="flex items-center gap-2">
                <span>Tope de enrolados nuevos por día</span>
                <input type="number" className="input w-24" value={draft.dailyEnrollCap} onChange={(e) => set({ dailyEnrollCap: Number(e.target.value) })} />
              </label>
            </div>
          )}
          {draft.channel === 'WhatsApp' && (<>
          <label className="text-sm block">
            <div className="text-slate-500 mb-1">Texto prellenado del anuncio (match por contenido)</div>
            <input className="input" placeholder="ej. Vengo del anuncio de TurnosPro" value={draft.triggerText ?? ''} onChange={(e) => set({ triggerText: e.target.value })} />
          </label>
          <div className="text-sm">
            <div className="text-slate-500 mb-1">Anuncios asignados (id de Meta)</div>
            <div className="flex flex-wrap gap-1 mb-1">
              {draft.adIds.map((id) => {
                const seen = ads.find((a) => a.adId === id);
                return (
                  <span key={id} className="text-xs bg-brand-50 border border-brand-200 text-brand-700 rounded px-1.5 py-0.5 flex items-center gap-1" title={seen?.title ?? ''}>
                    {seen?.title ? seen.title.slice(0, 28) : id}
                    <button type="button" onClick={() => set({ adIds: draft.adIds.filter((a) => a !== id) })}>×</button>
                  </span>
                );
              })}
            </div>
            <div className="flex gap-1">
              <select className="input text-xs py-1 flex-1" value={newAdId} onChange={(e) => setNewAdId(e.target.value)}>
                <option value="">— elegir de los vistos —</option>
                {ads.filter((a) => !draft.adIds.includes(a.adId)).map((a) => <option key={a.adId} value={a.adId}>{(a.title ?? a.adId).slice(0, 50)} · {a.leads} leads</option>)}
              </select>
              <input className="input text-xs py-1 w-40" placeholder="o pegá un id" value={newAdId} onChange={(e) => setNewAdId(e.target.value)} />
              <button type="button" className="btn-secondary text-xs" onClick={() => { if (newAdId.trim()) { set({ adIds: [...draft.adIds, newAdId.trim()] }); setNewAdId(''); } }}>+</button>
            </div>
          </div>
          <label className="text-sm flex items-center gap-2">
            <input type="checkbox" checked={draft.isDefault} onChange={(e) => set({ isDefault: e.target.checked })} />
            Pitch <b>default</b> del producto (cualquier lead de anuncio sin match cae acá)
          </label>
          </>)}
          <label className="text-sm flex items-center gap-2">
            <input type="checkbox" checked={draft.active} onChange={(e) => set({ active: e.target.checked })} /> Activo
          </label>
          <label className="text-sm block">
            <div className="text-slate-500 mb-1">Tag al responder</div>
            <input className="input" value={draft.autoTagOnReply ?? ''} onChange={(e) => set({ autoTagOnReply: e.target.value })} placeholder="respondio" />
          </label>
          <label className="text-sm block">
            <div className="text-slate-500 mb-1">Etapa del CRM al responder</div>
            <select className="input" value={draft.statusOnReply ?? ''} onChange={(e) => set({ statusOnReply: e.target.value })}>
              {STATUS_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </label>
          <label className="text-sm flex items-center gap-2 md:col-span-2">
            <input type="checkbox" checked={draft.aiAfterPitch} onChange={(e) => set({ aiAfterPitch: e.target.checked })} />
            <span><b>IA después del pitch</b> — terminado el guion la IA sigue la charla sola. Apagado = handoff humano (el bot queda pausado y el chat aparece para el vendedor).</span>
          </label>
          <label className="text-sm block">
            <div className="text-slate-500 mb-1">Espera humana antes de cada paso (seg, mín–máx)</div>
            <div className="flex gap-2">
              <input type="number" className="input w-24" value={draft.replyDelayMinSec} onChange={(e) => set({ replyDelayMinSec: Number(e.target.value) })} />
              <input type="number" className="input w-24" value={draft.replyDelayMaxSec} onChange={(e) => set({ replyDelayMaxSec: Number(e.target.value) })} />
            </div>
          </label>
        </div>

        {/* Pasos */}
        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <h4 className="font-semibold">Pasos <span className="text-xs text-slate-500 font-normal">— cada paso es un grupo de mensajes que salen juntos; la respuesta del lead avanza al siguiente</span></h4>
            <button type="button" className="btn-secondary text-xs" onClick={() => set({ steps: [...draft.steps, emptyStep()] })}>+ Paso</button>
          </div>
          {draft.steps.map((s, i) => (
            <div key={i} className="border border-slate-200 rounded-lg p-3 space-y-3 bg-slate-50/50">
              <div className="flex items-center gap-2">
                <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">Paso {i + 1}{i === draft.steps.length - 1 ? ' (último)' : ''}</span>
                <input className="input text-sm py-1 flex-1" placeholder="Título (opcional)" value={s.title ?? ''} onChange={(e) => setStep(i, { ...s, title: e.target.value })} />
                <button type="button" className="text-xs text-slate-500" disabled={i === 0} onClick={() => { const st = [...draft.steps]; [st[i - 1], st[i]] = [st[i], st[i - 1]]; set({ steps: st }); }}>↑</button>
                <button type="button" className="text-xs text-slate-500" disabled={i === draft.steps.length - 1} onClick={() => { const st = [...draft.steps]; [st[i + 1], st[i]] = [st[i], st[i + 1]]; set({ steps: st }); }}>↓</button>
                <button type="button" className="text-xs text-rose-600" disabled={draft.steps.length === 1} onClick={() => set({ steps: draft.steps.filter((_, k) => k !== i) })}>Borrar paso</button>
              </div>

              {s.messages.map((m, j) => (
                <div key={j} className="bg-white border border-slate-200 rounded p-2 space-y-2">
                  <div className="flex items-center gap-2 text-xs text-slate-500">
                    <span>Msg {j + 1}</span>
                    <button type="button" className="ml-auto text-rose-600" disabled={s.messages.length === 1}
                      onClick={() => setStep(i, { ...s, messages: s.messages.filter((_, k) => k !== j) })}>quitar</button>
                  </div>
                  <textarea className="input min-h-[64px] text-sm" placeholder="Texto del mensaje (vacío si es solo audio/adjunto)"
                    value={m.text} onChange={(e) => setStep(i, { ...s, messages: s.messages.map((x, k) => (k === j ? { ...x, text: e.target.value } : x)) })} />
                  <div className="grid md:grid-cols-3 gap-2 text-xs">
                    <label>
                      <div className="text-slate-500 mb-0.5">Adjunto / audio grabado</div>
                      <select className="input text-xs py-1" value={m.mediaAssetId ?? ''}
                        onChange={(e) => setStep(i, { ...s, messages: s.messages.map((x, k) => (k === j ? { ...x, mediaAssetId: e.target.value || null } : x)) })}>
                        <option value="">— ninguno —</option>
                        {media.map((a) => <option key={a.id} value={a.id}>{a.mimeType.startsWith('audio') ? '🎤 ' : a.mimeType.startsWith('video') ? '🎬 ' : '🖼 '}{a.fileName}</option>)}
                      </select>
                      {media.length === 0 && <div className="text-[11px] text-slate-400 mt-0.5">Subí archivos en <Link className="underline" to="/products">Aplicaciones → media</Link>.</div>}
                    </label>
                    <label>
                      <div className="text-slate-500 mb-0.5">Nota de voz generada (tu voz clonada)</div>
                      <input className="input text-xs py-1" placeholder="hola {name}, te cuento en 30 seg…" value={m.voiceText ?? ''}
                        onChange={(e) => setStep(i, { ...s, messages: s.messages.map((x, k) => (k === j ? { ...x, voiceText: e.target.value || null } : x)) })} />
                    </label>
                    <label>
                      <div className="text-slate-500 mb-0.5">Delay antes del próximo msg (seg)</div>
                      <input type="number" className="input text-xs py-1" value={m.delaySeconds}
                        onChange={(e) => setStep(i, { ...s, messages: s.messages.map((x, k) => (k === j ? { ...x, delaySeconds: Number(e.target.value) } : x)) })} />
                    </label>
                  </div>
                  {m.mediaAssetId && <div className="text-[11px] text-slate-400">Adjunto: {mediaLabel(m.mediaAssetId)}</div>}
                </div>
              ))}
              <button type="button" className="btn-secondary text-xs" onClick={() => setStep(i, { ...s, messages: [...s.messages, emptyMessage()] })}>+ Mensaje</button>

              <div className="border-t border-slate-200 pt-2 space-y-2">
                <div className="flex items-center justify-between">
                  <span className="text-xs font-semibold text-slate-600">Follow-ups si no responde <span className="font-normal text-slate-400">({s.followUps.length} configurados)</span></span>
                  <button type="button" className="btn-secondary text-xs" onClick={() => setStep(i, { ...s, followUps: [...s.followUps, emptyFollowUp()] })}>+ Follow-up</button>
                </div>
                {s.followUps.map((f, j) => (
                  <div key={j} className="bg-white border border-amber-200 rounded p-2 grid md:grid-cols-[110px_1fr_200px_auto] gap-2 items-start text-xs">
                    <label>
                      <div className="text-slate-500 mb-0.5">Horas después</div>
                      <input type="number" step="0.25" className="input text-xs py-1" value={f.afterHours}
                        onChange={(e) => setStep(i, { ...s, followUps: s.followUps.map((x, k) => (k === j ? { ...x, afterHours: Number(e.target.value) } : x)) })} />
                    </label>
                    <label>
                      <div className="text-slate-500 mb-0.5">Texto</div>
                      <input className="input text-xs py-1" placeholder="¿Seguís ahí? te cuento en 1 minuto cómo funciona" value={f.text}
                        onChange={(e) => setStep(i, { ...s, followUps: s.followUps.map((x, k) => (k === j ? { ...x, text: e.target.value } : x)) })} />
                    </label>
                    <label>
                      <div className="text-slate-500 mb-0.5">Adjunto (opcional)</div>
                      <select className="input text-xs py-1" value={f.mediaAssetId ?? ''}
                        onChange={(e) => setStep(i, { ...s, followUps: s.followUps.map((x, k) => (k === j ? { ...x, mediaAssetId: e.target.value || null } : x)) })}>
                        <option value="">— ninguno —</option>
                        {media.map((a) => <option key={a.id} value={a.id}>{a.fileName}</option>)}
                      </select>
                    </label>
                    <button type="button" className="text-rose-600 mt-5" onClick={() => setStep(i, { ...s, followUps: s.followUps.filter((_, k) => k !== j) })}>quitar</button>
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>

        <div className="flex justify-end gap-2 sticky bottom-0 bg-white pt-2">
          <button className="btn-secondary" onClick={onCancel} disabled={saving}>Cancelar</button>
          <button className="btn-primary" onClick={onSave} disabled={saving || !draft.name.trim()}>{saving ? 'Guardando…' : 'Guardar pitch'}</button>
        </div>
      </div>
    </div>
  );
}
