import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import clsx from 'clsx';
import { api } from '../lib/api';
import Switch from '../components/Switch';

type Settings = {
  productKey: string; displayName: string; enabled: boolean;
  accountUsername?: string | null; defaultAccount?: string | null;
  dailyCap: number; minGapMinutes: number; maxGapMinutes: number; fromHour: number; toHour: number;
  template: string; pausedUntil?: string | null; pausedReason?: string | null;
  sentToday: number; pending: number; repliesToConversations: boolean;
};

const PRESETS = [
  { key: 'cauteloso', label: 'Cauteloso', hint: 'Para arrancar o después de un aviso de Instagram.', dailyCap: 10, minGapMinutes: 6, maxGapMinutes: 15 },
  { key: 'equilibrado', label: 'Equilibrado', hint: 'Cuando la cuenta ya mandó una semana sin avisos.', dailyCap: 20, minGapMinutes: 4, maxGapMinutes: 10 },
];

/** Lo que no va en un mensaje en frío (mismo criterio que WhatsApp): tildes, ¿, emojis y links. */
function copyWarnings(t: string): string[] {
  const w: string[] = [];
  if (/[áéíóúÁÉÍÓÚ]/.test(t)) w.push('tiene tildes');
  if (/[¿¡]/.test(t)) w.push('tiene ¿ o ¡');
  if (/\p{Extended_Pictographic}/u.test(t)) w.push('tiene emojis');
  if (/https?:\/\/|www\.|\.com\b|\.ar\b/i.test(t)) w.push('tiene un link (en el primer DM marca como spam)');
  return w;
}

/**
 * DMs en frío por Instagram: los manda el userscript desde el Chrome donde está logueada la cuenta
 * de cada app. Acá se prende por app y se regula el ritmo; el tope, el horario y las pausas los
 * hace cumplir el backend.
 */
export default function IgDms() {
  const { data, isLoading } = useQuery({
    queryKey: ['ig-dm-settings'],
    queryFn: async () => (await api.get<Settings[]>('/ig-dm/settings')).data,
    refetchInterval: 60_000,
  });

  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-lg font-semibold">DMs por Instagram</h2>
        <p className="text-sm text-slate-500 max-w-3xl">
          Cada app le escribe por DM, desde su propia cuenta, a los leads que tienen @usuario de Instagram y todavía
          no fueron contactados. Los manda la extensión de Tampermonkey (v0.10+) desde el Chrome donde esté logueada esa
          cuenta: dejá una pestaña de Instagram abierta y prendé “DMs SalesHub” en el panel. Si Instagram muestra un
          aviso de bloqueo, la app se frena sola hasta el día siguiente.
        </p>
      </div>
      {isLoading && <div className="text-sm text-slate-500">Cargando…</div>}
      <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
        {(data ?? []).map((s) => <AppCard key={s.productKey} s={s} />)}
      </div>
    </div>
  );
}

function AppCard({ s }: { s: Settings }) {
  const qc = useQueryClient();
  const [d, setD] = useState(s);
  const [saving, setSaving] = useState(false);
  useEffect(() => { setD(s); }, [s]);
  const dirty = JSON.stringify(d) !== JSON.stringify(s);
  const set = <K extends keyof Settings>(k: K, v: Settings[K]) => setD((x) => ({ ...x, [k]: v }));
  const preset = PRESETS.find((p) => p.dailyCap === d.dailyCap && p.minGapMinutes === d.minGapMinutes && p.maxGapMinutes === d.maxGapMinutes);
  const account = d.accountUsername || d.defaultAccount;
  const warnings = copyWarnings(d.template);
  const preview = d.template.replace(/\{negocio\}/g, 'Gimnasio Olimpo').replace(/\{app\}/g, s.displayName);

  async function save(patch: Partial<Settings> = {}, clearPause = false) {
    const next = { ...d, ...patch };
    setSaving(true);
    try {
      await api.put(`/ig-dm/settings/${s.productKey}`, {
        enabled: next.enabled, accountUsername: next.accountUsername || null,
        dailyCap: next.dailyCap, minGapMinutes: next.minGapMinutes, maxGapMinutes: next.maxGapMinutes,
        fromHour: next.fromHour, toHour: next.toHour, template: next.template, clearPause,
      });
      toast.success('Guardado');
      qc.invalidateQueries({ queryKey: ['ig-dm-settings'] });
    } catch (e: any) {
      toast.error(e?.response?.data?.error ?? 'No se pudo guardar');
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="card p-4 space-y-3">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="font-semibold">{s.displayName}</div>
          <div className="text-xs text-slate-500">
            {account ? `desde @${account}` : 'sin cuenta de Instagram'}
            {s.repliesToConversations ? ' · las respuestas entran a Conversaciones' : ' · las respuestas se ven en la app de Instagram'}
          </div>
        </div>
        <Switch on={d.enabled} disabled={saving} title={d.enabled ? 'Apagar' : 'Prender'}
          onClick={() => { set('enabled', !d.enabled); save({ enabled: !d.enabled }); }} />
      </div>

      {/* Medidor del día */}
      <div>
        <div className="flex justify-between text-xs text-slate-500 mb-1">
          <span>Hoy: <b className="text-slate-800 tabular-nums">{s.sentToday}</b> de {s.dailyCap}</span>
          <span>{s.pending.toLocaleString('es-AR')} leads con Instagram en cola</span>
        </div>
        <div className="h-2 rounded-full bg-slate-100 overflow-hidden">
          <div className="h-full bg-pink-500" style={{ width: `${s.dailyCap ? Math.min(100, (s.sentToday / s.dailyCap) * 100) : 0}%` }} />
        </div>
      </div>

      {s.pausedUntil && (
        <div className="text-xs bg-amber-50 border border-amber-200 text-amber-800 rounded p-2 flex items-center gap-2">
          <span className="flex-1">
            Frenado hasta {new Date(s.pausedUntil).toLocaleString('es-AR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })}
            {s.pausedReason ? `: ${s.pausedReason}` : ''}
          </span>
          <button type="button" className="underline shrink-0" disabled={saving} onClick={() => save({}, true)}>reanudar ya</button>
        </div>
      )}

      {/* Ritmo */}
      <div className="flex flex-wrap gap-1.5">
        {PRESETS.map((p) => (
          <button key={p.key} type="button" title={p.hint}
            onClick={() => setD((x) => ({ ...x, dailyCap: p.dailyCap, minGapMinutes: p.minGapMinutes, maxGapMinutes: p.maxGapMinutes }))}
            className={clsx('text-xs px-2.5 py-1 rounded-full border',
              preset?.key === p.key ? 'bg-slate-800 text-white border-slate-800' : 'bg-white text-slate-600 border-slate-200 hover:bg-slate-50')}>
            {p.label}
          </button>
        ))}
        <span className={clsx('text-xs px-2.5 py-1 rounded-full border',
          !preset ? 'bg-slate-800 text-white border-slate-800' : 'bg-white text-slate-400 border-slate-200')}>
          Personalizado
        </span>
      </div>
      <div className="grid grid-cols-2 sm:grid-cols-5 gap-2 text-xs">
        <NumField label="Tope por día" value={d.dailyCap} min={0} max={100} onChange={(v) => set('dailyCap', v)} />
        <NumField label="Pausa mín. (min)" value={d.minGapMinutes} min={1} max={240} onChange={(v) => set('minGapMinutes', v)} />
        <NumField label="Pausa máx. (min)" value={d.maxGapMinutes} min={1} max={240} onChange={(v) => set('maxGapMinutes', v)} />
        <NumField label="Desde (h)" value={d.fromHour} min={0} max={23} onChange={(v) => set('fromHour', v)} />
        <NumField label="Hasta (h)" value={d.toHour} min={1} max={24} onChange={(v) => set('toHour', v)} />
      </div>

      <label className="block text-xs">
        <span className="text-slate-500">Cuenta que manda (sin @)</span>
        <input className="input text-sm mt-0.5" placeholder={d.defaultAccount ?? 'ej. gymhero.fitness'}
          value={d.accountUsername ?? ''} onChange={(e) => set('accountUsername', e.target.value)} />
      </label>

      <label className="block text-xs">
        <span className="text-slate-500">Texto del DM · variables: <code>{'{negocio}'}</code> <code>{'{app}'}</code></span>
        <textarea className="input text-sm mt-0.5 min-h-[88px]" value={d.template}
          placeholder={`hola, como va? soy eric de ${s.displayName}. vi {negocio} y queria contarte algo que les puede servir, te paso info?`}
          onChange={(e) => set('template', e.target.value)} />
      </label>
      {d.template.trim() && (
        <div className="text-xs space-y-1">
          <div className="bg-slate-50 border border-slate-200 rounded p-2 whitespace-pre-wrap text-slate-700">{preview}</div>
          {warnings.length > 0 && <div className="text-amber-700">Ojo: {warnings.join(', ')}.</div>}
        </div>
      )}

      <div className="flex justify-end">
        <button type="button" className="btn-primary text-sm" disabled={!dirty || saving} onClick={() => save()}>
          {saving ? 'Guardando…' : 'Guardar'}
        </button>
      </div>
    </section>
  );
}

function NumField({ label, value, min, max, onChange }: { label: string; value: number; min: number; max: number; onChange: (v: number) => void }) {
  return (
    <label className="block">
      <span className="text-slate-500">{label}</span>
      <input type="number" className="input text-sm mt-0.5 tabular-nums" min={min} max={max} value={value}
        onChange={(e) => onChange(Number(e.target.value))} />
    </label>
  );
}
