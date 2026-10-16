import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';

type Campaign = {
  id: string; productKey: string; status: 'Live' | 'Ended' | 'Error'; couponCode: string;
  discountPercent: number; durationMonths: number; headline: string; subheadline: string; bullets: string[];
  landingUrl?: string; trackedUrl?: string; startsAt: string; endsAt: string; error?: string; attempts: number;
};
type PromoEvent = {
  id: string; slug: string; title: string; description: string; date: string; endDate: string; apps: string[];
  discountPercent?: number | null; durationMonths?: number | null; couponCode?: string | null; effectiveCode: string;
  leadDays: number; enabled: boolean; confidence: string; source?: string; note?: string;
  imageSourceUrl?: string; imageCredit?: string; imageUrl?: string; launchOn: string; targetApps: string[];
  campaigns: Campaign[];
};
type Overview = {
  today: string;
  defaults: { discountPercent: number; durationMonths: number };
  apps: { productKey: string; landingUrl?: string; bioUrl?: string }[];
  events: PromoEvent[];
};

const APP_LABEL: Record<string, string> = {
  gymhero: 'GymHero', turnospro: 'TurnosPro', playcrew: 'PlayCrew', construction: 'ArchiCloud', bunker: 'Bunker', unistock: 'DashFlow',
};
const label = (k: string) => APP_LABEL[k] ?? k;
const fmt = (d: string) => { const [y, m, dd] = d.split('-'); return `${dd}/${m}/${y.slice(2)}`; };

const STATUS_STYLE: Record<Campaign['status'], string> = {
  Live: 'bg-emerald-100 text-emerald-800', Ended: 'bg-slate-100 text-slate-500', Error: 'bg-rose-100 text-rose-700',
};

async function copy(text: string) {
  try { await navigator.clipboard.writeText(text); toast.success('Copiado'); } catch { toast.error('No pude copiar'); }
}

export default function Promos() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['promos'], queryFn: async () => (await api.get<Overview>('/promos')).data, refetchInterval: 60000 });
  const [app, setApp] = useState('');
  const [showPast, setShowPast] = useState(false);
  const [editing, setEditing] = useState<PromoEvent | 'new' | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const data = q.data;
  const events = useMemo(() => (data?.events ?? [])
    .filter((e) => showPast || e.endDate >= data!.today)
    .filter((e) => !app || e.targetApps.includes(app)), [data, app, showPast]);
  const refresh = () => qc.invalidateQueries({ queryKey: ['promos'] });

  async function act(key: string, fn: () => Promise<unknown>, ok: string) {
    setBusy(key);
    try { await fn(); toast.success(ok); refresh(); }
    catch (e: any) { toast.error(e?.response?.data?.error ?? 'Falló'); }
    finally { setBusy(null); }
  }

  if (q.isLoading) return <div className="p-4 text-slate-500">Cargando…</div>;
  if (!data) return <div className="p-4 text-rose-600">No pude cargar las promos.</div>;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-xl font-bold">Promos por calendario</h2>
          <p className="text-sm text-slate-500">
            {data.events[0]?.leadDays ?? 7} días antes de cada fecha el motor crea el cupón y la landing en cada app y agrega 1 posteo de promo por día por red.
            Por defecto {data.defaults.discountPercent}% off los primeros {data.defaults.durationMonths} meses.
          </p>
        </div>
        <div className="flex gap-2">
          <button className="btn btn-secondary" disabled={busy === 'run'} onClick={() => act('run', () => api.post('/promos/run'), 'Motor corrido')}>Correr motor ahora</button>
          <button className="btn btn-primary" onClick={() => setEditing('new')}>Nueva fecha</button>
        </div>
      </div>

      <div className="card p-3">
        <div className="mb-2 text-sm font-semibold">Link fijo para la bio de cada IG (se carga una sola vez)</div>
        <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
          {data.apps.map((a) => (
            <div key={a.productKey} className="flex items-center justify-between gap-2 rounded border border-slate-200 px-2 py-1.5 text-sm">
              <span className="font-medium">{label(a.productKey)}</span>
              {a.bioUrl
                ? <button className="truncate text-left text-xs text-indigo-600 hover:underline" title={a.bioUrl} onClick={() => copy(a.bioUrl!)}>{a.bioUrl.replace(/^https?:\/\//, '')}</button>
                : <span className="text-xs text-slate-400">aparece al lanzar la 1ra promo</span>}
            </div>
          ))}
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-2 text-sm">
        <select className="input w-auto" value={app} onChange={(e) => setApp(e.target.value)}>
          <option value="">Todas las apps</option>
          {data.apps.map((a) => <option key={a.productKey} value={a.productKey}>{label(a.productKey)}</option>)}
        </select>
        <label className="flex items-center gap-1"><input type="checkbox" checked={showPast} onChange={(e) => setShowPast(e.target.checked)} /> Ver pasadas</label>
        <span className="text-slate-400">{events.length} fechas</span>
      </div>

      <div className="space-y-2">
        {events.map((e) => {
          const pct = e.discountPercent ?? data.defaults.discountPercent;
          const months = e.durationMonths ?? data.defaults.durationMonths;
          const inWindow = e.launchOn <= data.today && e.endDate >= data.today;
          return (
            <div key={e.id} className={`card flex flex-col gap-3 p-3 sm:flex-row ${e.enabled ? '' : 'opacity-50'}`}>
              {e.imageUrl
                ? <img src={e.imageUrl} alt="" className="h-28 w-full shrink-0 rounded object-cover sm:w-44" loading="lazy" />
                : <div className="flex h-28 w-full shrink-0 items-center justify-center rounded bg-slate-100 text-xs text-slate-400 sm:w-44">sin imagen</div>}
              <div className="min-w-0 flex-1 space-y-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-semibold">{e.title}</span>
                  <span className="text-sm text-slate-500">{fmt(e.date)}{e.endDate !== e.date && ` al ${fmt(e.endDate)}`}</span>
                  {e.confidence === 'estimada' && <span className="badge bg-amber-100 text-amber-800">fecha estimada</span>}
                  {!e.enabled && <span className="badge bg-slate-200 text-slate-600">apagada</span>}
                  {inWindow && e.enabled && <span className="badge bg-emerald-100 text-emerald-800">en ventana</span>}
                </div>
                <div className="text-sm text-slate-600">{e.description}</div>
                <div className="flex flex-wrap gap-x-3 text-xs text-slate-500">
                  <span>Código <b className="text-slate-700">{e.effectiveCode}</b></span>
                  <span>{pct}% x {months} {months === 1 ? 'mes' : 'meses'}</span>
                  <span>Arranca {fmt(e.launchOn)}</span>
                  <span>{e.apps.length ? e.apps.map(label).join(', ') : 'Todas las apps'}</span>
                </div>
                {e.campaigns.length > 0 && (
                  <div className="flex flex-wrap gap-1.5 pt-1">
                    {e.campaigns.map((c) => (
                      <span key={c.id} className={`inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-xs ${STATUS_STYLE[c.status]}`} title={c.error ?? c.headline}>
                        {label(c.productKey)} · {c.status}
                        {c.trackedUrl && <>
                          <a className="underline" href={c.trackedUrl} target="_blank" rel="noreferrer">ver</a>
                          <button className="underline" onClick={() => copy(c.trackedUrl!)}>link</button>
                        </>}
                        {c.status === 'Live' && <button className="underline" disabled={busy === c.id} onClick={() => act(c.id, () => api.post(`/promos/campaigns/${c.id}/regenerate`), 'Copy regenerado')}>regenerar</button>}
                        {c.status === 'Live' && <button className="underline" disabled={busy === c.id} onClick={() => { if (window.confirm(`Terminar la promo en ${label(c.productKey)}?`)) act(c.id, () => api.post(`/promos/campaigns/${c.id}/end`), 'Promo terminada'); }}>terminar</button>}
                      </span>
                    ))}
                  </div>
                )}
              </div>
              <div className="flex shrink-0 gap-2 sm:flex-col">
                <button className="btn btn-secondary" onClick={() => setEditing(e)}>Editar</button>
                {e.enabled && e.endDate >= data.today && (
                  <button className="btn btn-secondary" disabled={busy === e.id}
                    onClick={() => { if (window.confirm(`Lanzar YA "${e.title}" en ${e.targetApps.map(label).join(', ')}?`)) act(e.id, () => api.post(`/promos/events/${e.id}/launch`), 'Lanzada'); }}>
                    Lanzar ya
                  </button>
                )}
              </div>
            </div>
          );
        })}
      </div>

      {editing && (
        <EventEditor
          ev={editing === 'new' ? null : editing}
          apps={data.apps.map((a) => a.productKey)}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); refresh(); }}
        />
      )}
    </div>
  );
}

function EventEditor({ ev, apps, onClose, onSaved }: { ev: PromoEvent | null; apps: string[]; onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState({
    title: ev?.title ?? '', date: ev?.date ?? '', endDate: ev?.endDate ?? '', description: ev?.description ?? '',
    apps: ev?.apps ?? [], discountPercent: ev?.discountPercent?.toString() ?? '', durationMonths: ev?.durationMonths?.toString() ?? '',
    couponCode: ev?.couponCode ?? '', leadDays: (ev?.leadDays ?? 7).toString(), enabled: ev?.enabled ?? true,
    confidence: ev?.confidence ?? 'confirmada', imageSourceUrl: ev?.imageSourceUrl ?? '', imageCredit: ev?.imageCredit ?? '',
    source: ev?.source ?? '', note: ev?.note ?? '',
  });
  const [saving, setSaving] = useState(false);
  const set = (k: keyof typeof f, v: unknown) => setF((p) => ({ ...p, [k]: v }));
  const num = (s: string) => (s.trim() === '' ? null : Number(s));

  async function save() {
    if (!f.title.trim() || !f.date) { toast.error('Falta título o fecha'); return; }
    setSaving(true);
    const body = {
      ...f, endDate: f.endDate || null, discountPercent: num(f.discountPercent), durationMonths: num(f.durationMonths),
      leadDays: num(f.leadDays), couponCode: f.couponCode || null, imageSourceUrl: f.imageSourceUrl || null,
    };
    try {
      if (ev) await api.put(`/promos/events/${ev.id}`, body); else await api.post('/promos/events', body);
      toast.success('Guardado'); onSaved();
    } catch (e: any) { toast.error(e?.response?.data?.error ?? 'No se pudo guardar'); }
    finally { setSaving(false); }
  }
  async function remove() {
    if (!ev || !window.confirm(`Borrar "${ev.title}"? Si tiene promos activas se terminan.`)) return;
    try { await api.delete(`/promos/events/${ev.id}`); toast.success('Borrada'); onSaved(); } catch { toast.error('Falló'); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div className="card max-h-[90vh] w-full max-w-xl space-y-3 overflow-y-auto p-4" onClick={(e) => e.stopPropagation()}>
        <h3 className="text-lg font-bold">{ev ? 'Editar fecha' : 'Nueva fecha'}</h3>
        <div className="grid gap-2 sm:grid-cols-2">
          <label className="sm:col-span-2 text-sm">Título<input className="input" value={f.title} onChange={(e) => set('title', e.target.value)} /></label>
          <label className="text-sm">Fecha<input type="date" className="input" value={f.date} onChange={(e) => set('date', e.target.value)} /></label>
          <label className="text-sm">Hasta (opcional)<input type="date" className="input" value={f.endDate} onChange={(e) => set('endDate', e.target.value)} /></label>
          <label className="sm:col-span-2 text-sm">Descripción<textarea className="input" rows={2} value={f.description} onChange={(e) => set('description', e.target.value)} /></label>
          <label className="text-sm">% off<input className="input" placeholder="default" value={f.discountPercent} onChange={(e) => set('discountPercent', e.target.value)} /></label>
          <label className="text-sm">Meses con descuento<input className="input" placeholder="default" value={f.durationMonths} onChange={(e) => set('durationMonths', e.target.value)} /></label>
          <label className="text-sm">Código (opcional)<input className="input uppercase" placeholder={ev?.effectiveCode ?? 'automático'} value={f.couponCode} onChange={(e) => set('couponCode', e.target.value)} /></label>
          <label className="text-sm">Días antes<input className="input" value={f.leadDays} onChange={(e) => set('leadDays', e.target.value)} /></label>
          <label className="sm:col-span-2 text-sm">URL de la imagen (pública, CC0)<input className="input" value={f.imageSourceUrl} onChange={(e) => set('imageSourceUrl', e.target.value)} /></label>
          <label className="sm:col-span-2 text-sm">Crédito de la imagen<input className="input" value={f.imageCredit} onChange={(e) => set('imageCredit', e.target.value)} /></label>
        </div>
        <div className="text-sm">
          <div className="mb-1">Apps (ninguna = todas)</div>
          <div className="flex flex-wrap gap-2">
            {apps.map((a) => (
              <label key={a} className="flex items-center gap-1">
                <input type="checkbox" checked={f.apps.includes(a)} onChange={(e) => set('apps', e.target.checked ? [...f.apps, a] : f.apps.filter((x) => x !== a))} />
                {label(a)}
              </label>
            ))}
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <label className="flex items-center gap-1"><input type="checkbox" checked={f.enabled} onChange={(e) => set('enabled', e.target.checked)} /> Habilitada</label>
          <select className="input w-auto" value={f.confidence} onChange={(e) => set('confidence', e.target.value)}>
            <option value="confirmada">Fecha confirmada</option>
            <option value="estimada">Fecha estimada</option>
          </select>
        </div>
        <div className="flex justify-between gap-2 pt-2">
          {ev ? <button className="btn btn-danger" onClick={remove}>Borrar</button> : <span />}
          <div className="flex gap-2">
            <button className="btn btn-secondary" onClick={onClose}>Cancelar</button>
            <button className="btn btn-primary" disabled={saving} onClick={save}>Guardar</button>
          </div>
        </div>
      </div>
    </div>
  );
}
