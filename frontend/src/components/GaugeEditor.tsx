import { useEffect, useState } from 'react';
import type { Seller, SendMode } from '../lib/types';

type Props = {
  seller: Seller;
  onSave: (patch: Partial<Seller>) => Promise<void>;
};

const PRESETS: Record<SendMode, Partial<Seller>> = {
  Conservative: {
    dailyCap: 25, delayMinSeconds: 90, delayMaxSeconds: 300,
    burstSize: 3, burstPauseMinSeconds: 1800, burstPauseMaxSeconds: 3600,
    skipDayProbabilityPct: 10, warmupDays: 10
  },
  Balanced: {
    dailyCap: 50, delayMinSeconds: 45, delayMaxSeconds: 180,
    burstSize: 4, burstPauseMinSeconds: 900, burstPauseMaxSeconds: 2700,
    skipDayProbabilityPct: 5, warmupDays: 7
  },
  Aggressive: {
    dailyCap: 100, delayMinSeconds: 25, delayMaxSeconds: 90,
    burstSize: 6, burstPauseMinSeconds: 600, burstPauseMaxSeconds: 1800,
    skipDayProbabilityPct: 2, warmupDays: 5
  },
  Custom: {}
};

const MODE_LABEL: Record<SendMode, string> = {
  Conservative: 'Cauteloso',
  Balanced: 'Equilibrado',
  Aggressive: 'Agresivo',
  Custom: 'Personalizado'
};

const MODE_HINT: Record<SendMode, string> = {
  Conservative: 'Pocos mensajes, pausas largas. Ideal para cuentas nuevas o que ya tuvieron un ban.',
  Balanced: 'Volumen razonable con riesgo bajo de ban. Recomendado por default.',
  Aggressive: 'Más volumen, pausas cortas. Solo para cuentas con varias semanas de uso.',
  Custom: 'Vos definís cada parámetro a mano.'
};

export default function GaugeEditor({ seller, onSave }: Props) {
  const [state, setState] = useState<Seller>(seller);
  const [saving, setSaving] = useState(false);
  useEffect(() => setState(seller), [seller.id]);

  function setMode(mode: SendMode) {
    setState((s) => ({ ...s, ...(PRESETS[mode] as Seller), sendMode: mode }));
  }

  function patch<K extends keyof Seller>(key: K, value: Seller[K]) {
    setState((s) => ({ ...s, [key]: value, sendMode: 'Custom' }));
  }

  async function save() {
    setSaving(true);
    try {
      await onSave(state);
    } finally { setSaving(false); }
  }

  const minsRange = (sMin: number, sMax: number) =>
    `${Math.round(sMin / 60)} a ${Math.round(sMax / 60)} min`;

  return (
    <div className="space-y-5">
      <div>
        <div className="text-sm font-semibold text-slate-700 mb-2">Modo de envío</div>
        <div className="flex gap-2 flex-wrap">
          {(['Conservative', 'Balanced', 'Aggressive', 'Custom'] as SendMode[]).map((m) => (
            <button
              key={m}
              onClick={() => setMode(m)}
              className={`btn ${state.sendMode === m ? 'bg-brand-600 text-white' : 'bg-white border border-slate-300'}`}>
              {MODE_LABEL[m]}
            </button>
          ))}
        </div>
        <p className="text-xs text-slate-500 mt-2">{MODE_HINT[state.sendMode]}</p>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <Field
          label="Máximo de mensajes por día"
          value={`${state.dailyCap} mensajes`}
          hint="El techo que no se supera en un día típico.">
          <input type="range" min={5} max={200} value={state.dailyCap}
            onChange={(e) => patch('dailyCap', +e.target.value)} className="w-full" />
        </Field>

        <Field
          label="Variación diaria"
          value={`± ${state.dailyVariancePct}%`}
          hint="Un día manda 42, otro 58. Evita que el patrón se vea robótico.">
          <input type="range" min={0} max={40} value={state.dailyVariancePct}
            onChange={(e) => patch('dailyVariancePct', +e.target.value)} className="w-full" />
        </Field>

        <Field
          label="Período de calentamiento"
          value={`${state.warmupDays} días`}
          hint="Los primeros días se manda menos volumen para que WhatsApp no banee la línea nueva.">
          <input type="range" min={0} max={21} value={state.warmupDays}
            onChange={(e) => patch('warmupDays', +e.target.value)} className="w-full" />
        </Field>

        <Field
          label="Horario de envío"
          value={`de ${state.activeHoursStart}:00 a ${state.activeHoursEnd}:00`}
          hint="Fuera de esta franja no se manda nada. Nadie contesta mensajes a las 3 a.m.">
          <div className="flex gap-2 items-center">
            <input type="number" min={0} max={23} value={state.activeHoursStart}
              onChange={(e) => patch('activeHoursStart', +e.target.value)} className="input w-20" />
            <span className="text-slate-400">a</span>
            <input type="number" min={0} max={23} value={state.activeHoursEnd}
              onChange={(e) => patch('activeHoursEnd', +e.target.value)} className="input w-20" />
          </div>
        </Field>

        <Field
          label="Pausa entre mensajes"
          value={`${state.delayMinSeconds}s a ${state.delayMaxSeconds}s`}
          hint="Entre cada mensaje, el sistema espera un tiempo aleatorio dentro de este rango.">
          <div className="flex gap-2 items-center">
            <input type="number" value={state.delayMinSeconds}
              onChange={(e) => patch('delayMinSeconds', +e.target.value)} className="input w-24" />
            <span className="text-slate-400">a</span>
            <input type="number" value={state.delayMaxSeconds}
              onChange={(e) => patch('delayMaxSeconds', +e.target.value)} className="input w-24" />
          </div>
        </Field>

        <Field
          label="Tanda y descanso"
          value={`${state.burstSize} mensajes seguidos, descanso ${minsRange(state.burstPauseMinSeconds, state.burstPauseMaxSeconds)}`}
          hint="Manda una tanda y después descansa un rato largo, como alguien que dejó el celular para hacer otra cosa.">
          <div className="flex gap-2 items-center">
            <input type="number" value={state.burstSize}
              onChange={(e) => patch('burstSize', +e.target.value)} className="input w-16" />
            <span className="text-slate-400 text-xs">descanso seg:</span>
            <input type="number" value={state.burstPauseMinSeconds}
              onChange={(e) => patch('burstPauseMinSeconds', +e.target.value)} className="input w-24" />
            <input type="number" value={state.burstPauseMaxSeconds}
              onChange={(e) => patch('burstPauseMaxSeconds', +e.target.value)} className="input w-24" />
          </div>
        </Field>

        <Field
          label="Tiempo simulando 'escribiendo…'"
          value={`${state.preSendTypingMinSeconds}s a ${state.preSendTypingMaxSeconds}s`}
          hint="Antes de mandar cada mensaje, WhatsApp muestra que estás escribiendo durante estos segundos.">
          <div className="flex gap-2 items-center">
            <input type="number" value={state.preSendTypingMinSeconds}
              onChange={(e) => patch('preSendTypingMinSeconds', +e.target.value)} className="input w-20" />
            <span className="text-slate-400">a</span>
            <input type="number" value={state.preSendTypingMaxSeconds}
              onChange={(e) => patch('preSendTypingMaxSeconds', +e.target.value)} className="input w-20" />
          </div>
        </Field>

        <Field
          label="Chance de día libre"
          value={`${state.skipDayProbabilityPct}%`}
          hint="Cada día hay esta probabilidad de tomarse libre (0 mensajes). Humaniza el patrón semanal.">
          <input type="range" min={0} max={30} value={state.skipDayProbabilityPct}
            onChange={(e) => patch('skipDayProbabilityPct', +e.target.value)} className="w-full" />
        </Field>

        <Field
          label="Leer mensajes entrantes antes de mandar"
          value={state.readIncomingFirst ? 'Sí' : 'No'}
          hint="Marca como leídos los mensajes que recibiste antes de mandar uno nuevo. Hace el comportamiento más humano.">
          <label className="inline-flex items-center gap-2 cursor-pointer">
            <input type="checkbox" checked={state.readIncomingFirst}
              onChange={(e) => patch('readIncomingFirst', e.target.checked)} />
            <span className="text-sm">Activado</span>
          </label>
        </Field>

        <Field
          label="Zona horaria del vendedor"
          value={state.timezone}
          hint="Se usa para interpretar el horario de envío.">
          <input value={state.timezone} onChange={(e) => patch('timezone', e.target.value)} className="input" />
        </Field>
      </div>

      <div className="flex justify-end">
        <button className="btn-primary" disabled={saving} onClick={save}>
          {saving ? 'Guardando…' : 'Guardar configuración'}
        </button>
      </div>
    </div>
  );
}

function Field({ label, value, hint, children }: { label: string; value: string; hint: string; children: React.ReactNode }) {
  return (
    <div>
      <div className="flex items-baseline justify-between mb-1 gap-2">
        <span className="text-sm font-medium text-slate-700">{label}</span>
        <span className="text-xs text-slate-500 text-right">{value}</span>
      </div>
      {children}
      <p className="text-xs text-slate-400 mt-1">{hint}</p>
    </div>
  );
}
