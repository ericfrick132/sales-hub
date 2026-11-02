import { useState, useEffect, useRef, type ReactNode } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import type { AnglePitch, OnboardingAppConfig, OnboardingAudioVariant } from '../lib/types';

const ANGLES = [
  { key: '', label: 'por defecto (sin anuncio)' },
  { key: 'bot-whatsapp', label: 'anuncio "bot WhatsApp"' },
  { key: 'antes-despues', label: 'anuncio "antes y después"' },
];

/** Pitch al terminar la pre-calificación, uno por ángulo del anuncio. */
function AnglePitchesEditor({ value, onChange }: { value: AnglePitch[]; onChange: (v: AnglePitch[]) => void }) {
  const get = (angle: string) => value.find((p) => p.angle === angle);
  const set = (angle: string, text: string) => {
    const rest = value.filter((p) => p.angle !== angle);
    onChange(text.trim() ? [...rest, { angle, text, mediaAssetIds: get(angle)?.mediaAssetIds ?? [] }] : rest);
  };
  const extra = value.filter((p) => !ANGLES.some((a) => a.key === p.angle));
  return (
    <div className="space-y-2 sm:col-span-3">
      <div className="text-slate-600">
        Pitch al terminar las preguntas, según el anuncio del que vino el lead. Sale justo antes del pase a la persona.
        Admite {'{a|b}'} y [NUEVO_MENSAJE].
      </div>
      {[...ANGLES, ...extra.map((p) => ({ key: p.angle, label: p.angle }))].map((a) => (
        <label key={a.key} className="block space-y-0.5">
          <div className="text-xs font-medium text-slate-500">{a.label}</div>
          <textarea className="input w-full h-20" value={get(a.key)?.text ?? ''}
            onChange={(e) => set(a.key, e.target.value)} />
        </label>
      ))}
    </div>
  );
}

/** Grabador del audio del pitch: graba del micrófono, sube y lista las variantes (el motor rota). */
function AudioSection({ productKey, usePitchAudio, onToggle }: {
  productKey: string; usePitchAudio: boolean; onToggle: (v: boolean) => void;
}) {
  const qc = useQueryClient();
  const { data: audios } = useQuery({
    queryKey: ['onb-audios', productKey],
    queryFn: async () => (await api.get<OnboardingAudioVariant[]>(`/onboarding-configs/${productKey}/audios`)).data,
  });
  const [recording, setRecording] = useState(false);
  const recRef = useRef<MediaRecorder | null>(null);
  const chunksRef = useRef<Blob[]>([]);
  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ['onb-audios', productKey] });
    qc.invalidateQueries({ queryKey: ['onboarding-configs'] });
  };

  const upload = useMutation({
    mutationFn: async (blob: Blob) => {
      const fd = new FormData();
      fd.append('file', blob, 'pitch.webm');
      return api.post(`/onboarding-configs/${productKey}/audios`, fd, { headers: { 'Content-Type': 'multipart/form-data' } });
    },
    onSuccess: () => { invalidate(); toast.success('Audio guardado'); },
    onError: () => toast.error('No se pudo subir el audio'),
  });
  const del = useMutation({
    mutationFn: async (id: string) => api.delete(`/onboarding-configs/${productKey}/audios/${id}`),
    onSuccess: invalidate,
  });

  const start = async () => {
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const rec = new MediaRecorder(stream);
      chunksRef.current = [];
      rec.ondataavailable = (e) => { if (e.data.size) chunksRef.current.push(e.data); };
      rec.onstop = () => {
        upload.mutate(new Blob(chunksRef.current, { type: 'audio/webm' }));
        stream.getTracks().forEach((t) => t.stop());
      };
      rec.start();
      recRef.current = rec;
      setRecording(true);
    } catch {
      toast.error('No pude acceder al micrófono');
    }
  };
  const stop = () => { recRef.current?.stop(); setRecording(false); };

  const play = async (id: string) => {
    const resp = await api.get(`/onboarding-configs/${productKey}/audios/${id}/play`, { responseType: 'blob' });
    new Audio(URL.createObjectURL(resp.data as Blob)).play();
  };

  const list = audios ?? [];
  return (
    <div className="border-t pt-3 space-y-2">
      <label className="flex items-center gap-2 text-sm cursor-pointer">
        <input type="checkbox" checked={usePitchAudio} onChange={(e) => onToggle(e.target.checked)} />
        <span className="font-medium">🎙 Mandar un audio en el pitch</span>
        <span className="text-slate-400">({list.length} variante{list.length === 1 ? '' : 's'})</span>
      </label>
      <p className="text-xs text-slate-500">
        Grabá varias tomas del pitch en tu voz — el motor rota al azar para que Meta no fichee el mismo archivo.
      </p>
      <div className="flex items-center gap-2">
        {recording ? (
          <button type="button" className="btn-danger" onClick={stop}>⏹ Detener</button>
        ) : (
          <button type="button" className="btn-secondary" onClick={start} disabled={upload.isPending}>🎙 Grabar toma</button>
        )}
        {upload.isPending && <span className="text-xs text-slate-500">subiendo…</span>}
      </div>
      {list.length > 0 && (
        <div className="space-y-1">
          {list.map((a, i) => (
            <div key={a.id} className="flex items-center gap-2 text-sm">
              <span className="text-slate-500 w-8">#{i + 1}</span>
              <button type="button" className="btn-secondary py-1 px-2" onClick={() => play(a.id)}>▶</button>
              <span className="text-slate-400">{Math.max(1, Math.round(a.durationMs / 1000))}s</span>
              <button type="button" className="btn-danger py-1 px-2 ml-auto" onClick={() => del.mutate(a.id)}>✕</button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * CRUD del onboarding de ads POR APP (multi-app). Cuando un lead de anuncio dice "activar [App]",
 * sales-hub corre estas preguntas y al final provisiona la cuenta. El motor es genérico; acá se
 * define la config de cada aplicación.
 */
function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <label className="text-sm font-medium">{label}</label>
      <div className="mt-1">{children}</div>
    </div>
  );
}

function AppCard({ cfg }: { cfg: OnboardingAppConfig }) {
  const qc = useQueryClient();
  const [form, setForm] = useState(cfg);
  const gcal = useQuery({ queryKey: ['google-calendar-status'], queryFn: fetchGcalStatus });
  const sellers = useQuery({
    queryKey: ['sellers-list'],
    queryFn: async () => (await api.get<{ id: string; displayName: string; isActive: boolean }[]>('/sellers')).data,
  });
  useEffect(() => setForm(cfg), [cfg]);

  const save = useMutation({
    mutationFn: async () => api.put(`/onboarding-configs/${form.productKey}`, form),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['onboarding-configs'] });
      toast.success(`${form.displayName} guardado`);
    },
    onError: () => toast.error('No se pudo guardar'),
  });

  const setQ = (i: number, v: string) =>
    setForm((f) => ({ ...f, questions: f.questions.map((q, j) => (j === i ? v : q)) }));
  const addQ = () => setForm((f) => ({ ...f, questions: [...f.questions, ''] }));
  const delQ = (i: number) =>
    setForm((f) => ({ ...f, questions: f.questions.filter((_, j) => j !== i) }));

  // ?? []: hasta que el backend con los campos de reenganche esté deployado, el GET no los trae.
  const setRQ = (i: number, v: string) =>
    setForm((f) => ({ ...f, reengageQuestions: (f.reengageQuestions ?? []).map((q, j) => (j === i ? v : q)) }));
  const addRQ = () => setForm((f) => ({ ...f, reengageQuestions: [...(f.reengageQuestions ?? []), ''] }));
  const delRQ = (i: number) =>
    setForm((f) => ({ ...f, reengageQuestions: (f.reengageQuestions ?? []).filter((_, j) => j !== i) }));

  return (
    <div className="card p-4 space-y-3">
      <div className="flex items-center justify-between">
        <h2 className="font-semibold">
          {form.displayName} <span className="text-xs text-slate-400">/{form.productKey}</span>
        </h2>
        <label className="flex items-center gap-2 text-sm cursor-pointer">
          <input
            type="checkbox"
            checked={form.enabled}
            onChange={(e) => setForm((f) => ({ ...f, enabled: e.target.checked }))}
          />
          <span className={form.enabled ? 'text-green-600 font-medium' : 'text-slate-400'}>
            {form.enabled ? 'Activo' : 'Inactivo'}
          </span>
        </label>
      </div>

      <div className="rounded-lg border border-indigo-200 bg-indigo-50/50 p-3 space-y-2 text-sm">
        <label className="flex items-center gap-2 cursor-pointer">
          <input type="checkbox" checked={!!form.handoffEnabled}
            onChange={(e) => setForm((f) => ({ ...f, handoffEnabled: e.target.checked }))} />
          <span className="font-medium">Pre-calificar y pasar a una persona</span>
        </label>
        <div className="text-xs text-slate-500">
          El bot hace las primeras preguntas y le pasa el lead a la persona, que sigue en el mismo chat (misma línea).
          Si antes el lead pregunta algo, pide precio o llamada, o se sale del guion, el pase es inmediato. El bot se
          calla en esa charla, el lead queda etiquetado <code>para-mateo</code> y a nombre de esta persona, y le llega un
          aviso a su WhatsApp personal con qué chat abrir (tiene que tener su número cargado en Vendedores). En las
          líneas por app, todo número nuevo pasa por estas preguntas y el bot contesta por la misma línea.
        </div>
        {form.handoffEnabled && (
          <div className="grid gap-2 sm:grid-cols-3">
            <label className="space-y-0.5">
              <div className="text-slate-600">Pasar a</div>
              <select className="input w-full" value={form.handoffSellerId ?? ''}
                onChange={(e) => setForm((f) => ({ ...f, handoffSellerId: e.target.value || null }))}>
                <option value="">elegí…</option>
                {(sellers.data ?? []).filter((s) => s.isActive).map((s) => <option key={s.id} value={s.id}>{s.displayName}</option>)}
              </select>
            </label>
            <label className="space-y-0.5">
              <div className="text-slate-600">Después de cuántas preguntas</div>
              <input type="number" min={1} max={10} className="input w-full" value={form.handoffAfterQuestions ?? 2}
                onChange={(e) => setForm((f) => ({ ...f, handoffAfterQuestions: Number(e.target.value) }))} />
            </label>
            <label className="space-y-0.5">
              <div className="text-slate-600">El bot se presenta como ({'{seller}'})</div>
              <input className="input w-full" placeholder="mateo" value={form.presentAs ?? ''}
                onChange={(e) => setForm((f) => ({ ...f, presentAs: e.target.value }))} />
            </label>
            <div className="space-y-0.5 sm:col-span-3">
              <div className="text-slate-600">
                Demo: después del pitch le propone verlo en una videollamada y, si dice que sí, le ofrece hasta 2 horarios
                libres de hoy o mañana del Google Calendar, le pide el mail y le manda la invitación con Meet. Recién ahí pasa.
              </div>
              <div className="flex flex-wrap gap-2 items-center">
                <select className="input" value={form.demoEventTypeUri === 'google' ? 'google' : ''}
                  onChange={(e) => setForm((f) => ({ ...f, demoEventTypeUri: e.target.value }))}>
                  <option value="">sin agenda (manda el link de abajo)</option>
                  <option value="google">agendar en Google Calendar</option>
                </select>
                {form.demoEventTypeUri === 'google' && gcal.data && !gcal.data.connected && (
                  <span className="text-xs text-amber-700">Google Calendar sin conectar (arriba de todo): mientras tanto manda el link</span>
                )}
              </div>
            </div>
            {form.demoEventTypeUri === 'google' && (
              <label className="space-y-0.5 sm:col-span-3">
                <div className="text-slate-600">
                  Si el anuncio no tiene pitch por ángulo: antes de los horarios el bot reacciona a su última respuesta ("uf, a mano es donde mas se escapan las cosas") y
                  después dice esto. Los horarios van recién si contesta que sí. Admite {'{a|b}'} y [NUEVO_MENSAJE].
                </div>
                <textarea className="input w-full h-20" value={form.demoPitch ?? ''}
                  placeholder="gymhero les cobra la cuota solo por mercado pago y te avisa quien se atraso, asi no perseguis a nadie[NUEVO_MENSAJE]si queres te lo muestro andando con tus datos, son 15 minutos por videollamada. te sirve?"
                  onChange={(e) => setForm((f) => ({ ...f, demoPitch: e.target.value }))} />
              </label>
            )}
            <label className="space-y-0.5 sm:col-span-3">
              <div className="text-slate-600">
                1ª pregunta cuando el anuncio ya dijo el rubro (ver Marketing → Anuncios). {'{negocio}'} = "la barberia",
                "el estudio de tatuajes"… Vacío = la 1ª pregunta de siempre.
              </div>
              <input className="input w-full" value={form.knownBusinessQuestion ?? ''}
                placeholder="comentame, como se llama {negocio}?"
                onChange={(e) => setForm((f) => ({ ...f, knownBusinessQuestion: e.target.value }))} />
            </label>
            <label className="space-y-0.5 sm:col-span-3">
              <div className="text-slate-600">
                Línea extra del saludo cuando el anuncio dijo qué tiene. {'{tiene}'} = "una barberia", "un box",
                "canchas de futbol". Vacío = saludo solo.
              </div>
              <input className="input w-full" value={form.knownBusinessGreeting ?? ''}
                placeholder="vi que tenes {tiene}, buenisimo"
                onChange={(e) => setForm((f) => ({ ...f, knownBusinessGreeting: e.target.value }))} />
            </label>
            <AnglePitchesEditor value={form.anglePitches ?? []}
              onChange={(v) => setForm((f) => ({ ...f, anglePitches: v }))} />
            <label className="space-y-0.5 sm:col-span-3">
              <div className="text-slate-600">
                Precios: si el pitch termina en "queres que te pase los precios?" y el lead dice que sí, el bot manda esto
                y la pregunta de abajo; recién con esa respuesta pasa a la persona. Vacío = pasa justo después del pitch.
              </div>
              <textarea className="input w-full h-20" value={form.pricesMessage ?? ''}
                placeholder="el mensual es $50.000[NUEVO_MENSAJE]y si lo tomas por mas tiempo baja hasta $37.500[NUEVO_MENSAJE]probas 7 dias gratis, se activa con tu tarjeta en mercado pago"
                onChange={(e) => setForm((f) => ({ ...f, pricesMessage: e.target.value }))} />
            </label>
            <label className="space-y-0.5 sm:col-span-3">
              <div className="text-slate-600">
                Pregunta antes de la demo ("te paso el link?"): el link va si dice que sí, y si no contesta va igual con el
                seguimiento. Vacío = el bot manda la demo directo después del pitch y sigue con la pregunta del cierre.
              </div>
              <input className="input w-full" value={form.demoOffer ?? ''}
                placeholder="te interesa ver una demo?"
                onChange={(e) => setForm((f) => ({ ...f, demoOffer: e.target.value }))} />
            </label>
            <label className="space-y-0.5 sm:col-span-3">
              <div className="text-slate-600">Demo: lo que manda después del pitch (admite [NUEVO_MENSAJE]). Vacío = sin demo.</div>
              <input className="input w-full" value={form.demoLink ?? ''}
                placeholder="mira, aca lo podes ver andando[NUEVO_MENSAJE]https://turnos-pro.com/demo-app"
                onChange={(e) => setForm((f) => ({ ...f, demoLink: e.target.value }))} />
            </label>
            {[0, 1].map((i) => (
              <label key={i} className="space-y-0.5 sm:col-span-3">
                <div className="text-slate-600">
                  {i === 0
                    ? 'Seguimiento si no contesta (a las 3 h): re-saludo como si nada, con un gancho. Si contesta, sigue la persona. Admite {nombre} y {a|b}. Vacío = sin seguimiento.'
                    : '2º seguimiento (24 h después del 1º). Vacío = solo uno.'}
                </div>
                <textarea className="input w-full h-16" value={form.followUpMessages?.[i] ?? ''}
                  placeholder={i === 0 ? 'hola {nombre}! como va?[NUEVO_MENSAJE]este mes tenemos una promo para los que arrancan, queres que te cuente?' : 'buenas {nombre}! la promo de este mes sigue en pie, si queres te cuento como es'}
                  onChange={(e) => setForm((f) => {
                    const arr = [...(f.followUpMessages ?? [])];
                    while (arr.length < 2) arr.push('');
                    arr[i] = e.target.value;
                    return { ...f, followUpMessages: arr };
                  })} />
              </label>
            ))}
            <label className="space-y-0.5 sm:col-span-3">
              <div className="text-slate-600">Pregunta del cierre (después de los precios o de la demo)</div>
              <input className="input w-full" value={form.startQuestion ?? ''}
                placeholder="para cuando tenias pensado comenzar?"
                onChange={(e) => setForm((f) => ({ ...f, startQuestion: e.target.value }))} />
            </label>
            <label className="space-y-0.5 sm:col-span-3">
              <div className="text-slate-600">Lo que dice el bot antes de que siga la persona si no hay pitch para el ángulo (admite {'{a|b}'}; vacío = no dice nada)</div>
              <textarea className="input w-full h-16" value={form.handoffMessage ?? ''}
                placeholder="{perfecto|genial}, con eso ya tengo lo que necesito. te lo dejo armado con tus datos asi lo ves andando, {te llamo 10 minutos|hacemos una llamada corta} o preferis que lo veamos por aca?"
                onChange={(e) => setForm((f) => ({ ...f, handoffMessage: e.target.value }))} />
            </label>
          </div>
        )}
      </div>

      <div className="flex items-center gap-2 text-sm">
        <span className="font-medium">Modo:</span>
        <select className="input w-auto py-1" value={form.selfServe ? 'self' : 'assisted'}
          onChange={(e) => setForm((f) => ({ ...f, selfServe: e.target.value === 'self' }))}>
          <option value="self">Autoservicio (crea la cuenta)</option>
          <option value="assisted">Venta asistida (deriva a demo)</option>
        </select>
      </div>

      <Field label="Intro (saludo inicial)">
        <textarea className="input min-h-[60px]" value={form.intro}
          onChange={(e) => setForm((f) => ({ ...f, intro: e.target.value }))} />
      </Field>

      <div>
        <label className="text-sm font-medium">Preguntas (la 1ª es el nombre del negocio)</label>
        <div className="space-y-2 mt-1">
          {form.questions.map((q, i) => (
            <div key={i} className="flex gap-2">
              <textarea className="input min-h-[44px] flex-1" value={q}
                onChange={(e) => setQ(i, e.target.value)} />
              <button type="button" className="btn-danger px-2 self-start" onClick={() => delQ(i)}>✕</button>
            </div>
          ))}
          <button type="button" className="btn-secondary text-sm" onClick={addQ}>+ pregunta</button>
        </div>
      </div>

      <div className="border rounded-lg p-3 space-y-2 bg-slate-50 dark:bg-slate-800/40">
        <p className="text-sm font-semibold">🔁 Reenganche (leads que crearon cuenta y no siguieron)</p>
        <p className="text-xs text-slate-500">
          El opener ya se presentó y prometió los precios — acá el bot NO se re-presenta: reacciona,
          cumple la promesa y sigue con estas preguntas (suelen ser menos: el opener ya calificó).
          Soporta <code>{'{seller}'}</code>, <code>{'{price}'}</code>, <code>{'{checkout_url}'}</code>.
          Vacío = usa el intro/preguntas de arriba (se re-presenta).
        </p>
        <Field label="Reacción inicial (cumple la promesa: precios/link, sin re-presentarse)">
          <textarea className="input min-h-[60px]" value={form.reengageIntro ?? ''}
            onChange={(e) => setForm((f) => ({ ...f, reengageIntro: e.target.value }))} />
        </Field>
        <div>
          <label className="text-sm font-medium">Preguntas del reenganche (la 1ª es el nombre del negocio)</label>
          <div className="space-y-2 mt-1">
            {(form.reengageQuestions ?? []).map((q, i) => (
              <div key={i} className="flex gap-2">
                <textarea className="input min-h-[44px] flex-1" value={q}
                  onChange={(e) => setRQ(i, e.target.value)} />
                <button type="button" className="btn-danger px-2 self-start" onClick={() => delRQ(i)}>✕</button>
              </div>
            ))}
            <button type="button" className="btn-secondary text-sm" onClick={addRQ}>+ pregunta</button>
          </div>
        </div>
        <Field label="Adjuntos con la reacción (IDs de media del producto, separados por coma — video demo, imagen de precios)">
          <input className="input" value={(form.reengageMediaAssetIds ?? []).join(', ')}
            placeholder="vacío = solo texto"
            onChange={(e) => setForm((f) => ({
              ...f,
              reengageMediaAssetIds: e.target.value.split(',').map((s) => s.trim()).filter(Boolean),
            }))} />
        </Field>
      </div>

      {form.selfServe ? (
        <>
          <Field label="Pedido del mail (antes de crear la cuenta)">
            <textarea className="input min-h-[44px]" value={form.emailPrompt}
              onChange={(e) => setForm((f) => ({ ...f, emailPrompt: e.target.value }))} />
          </Field>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <Field label="Endpoint de provisión (bot-register)">
              <input className="input" value={form.provisionUrl}
                onChange={(e) => setForm((f) => ({ ...f, provisionUrl: e.target.value }))} />
            </Field>
            <Field label="Campo del nombre en el body (ej. gymName)">
              <input className="input" value={form.provisionNameField}
                onChange={(e) => setForm((f) => ({ ...f, provisionNameField: e.target.value }))} />
            </Field>
          </div>

          <Field label="Mensaje de éxito (usá {accessUrl} para el link)">
            <textarea className="input min-h-[90px]" value={form.successMessage}
              onChange={(e) => setForm((f) => ({ ...f, successMessage: e.target.value }))} />
          </Field>
        </>
      ) : (
        <Field label="Cierre (pitch + handoff a demo, sin mail)">
          <textarea className="input min-h-[90px]" value={form.closingMessage}
            onChange={(e) => setForm((f) => ({ ...f, closingMessage: e.target.value }))} />
        </Field>
      )}

      <Field label="Espera antes de responder (random entre min y max — 0 = instantáneo)">
        <div className="flex items-center gap-2">
          <input type="number" min={0} className="input w-24" value={form.replyDelayMinSec}
            onChange={(e) => setForm((f) => ({ ...f, replyDelayMinSec: Math.max(0, parseInt(e.target.value) || 0) }))} />
          <span className="text-slate-400">a</span>
          <input type="number" min={0} className="input w-24" value={form.replyDelayMaxSec}
            onChange={(e) => setForm((f) => ({ ...f, replyDelayMaxSec: Math.max(0, parseInt(e.target.value) || 0) }))} />
          <span className="text-xs text-slate-500">seg · ej. 120 a 3600 = 2 min a 1 hora</span>
        </div>
      </Field>

      <AudioSection productKey={form.productKey} usePitchAudio={form.usePitchAudio}
        onToggle={(v) => setForm((f) => ({ ...f, usePitchAudio: v }))} />

      <div className="flex justify-end">
        <button className="btn-primary" disabled={save.isPending} onClick={() => save.mutate()}>
          {save.isPending ? 'Guardando…' : 'Guardar'}
        </button>
      </div>
    </div>
  );
}

type GcalStatus = {
  hasClient: boolean; connected: boolean; email: string | null; redirectUri: string;
  startHour: number; endHour: number; days: string; slotMinutes: number; minNoticeMinutes: number;
};
async function fetchGcalStatus() {
  return (await api.get<GcalStatus>('/google-calendar/status')).data;
}

/** Google Calendar donde el bot agenda las demos (uno solo para todas las apps). */
function GoogleCalendarPanel() {
  const qc = useQueryClient();
  const status = useQuery({ queryKey: ['google-calendar-status'], queryFn: fetchGcalStatus });
  const [clientId, setClientId] = useState('');
  const [clientSecret, setClientSecret] = useState('');
  const [hours, setHours] = useState<{ startHour: number; endHour: number; slotMinutes: number; minNoticeMinutes: number } | null>(null);
  const [preview, setPreview] = useState<string | null>(null);
  const s = status.data;
  useEffect(() => {
    if (s && !hours) setHours({ startHour: s.startHour, endHour: s.endHour, slotMinutes: s.slotMinutes, minNoticeMinutes: s.minNoticeMinutes });
  }, [s, hours]);

  async function saveSettings(body: Record<string, unknown>) {
    try {
      await api.put('/google-calendar/settings', body);
      qc.invalidateQueries({ queryKey: ['google-calendar-status'] });
      toast.success('Guardado');
    } catch { toast.error('No se pudo guardar'); }
  }
  async function connect() {
    try {
      const { url } = (await api.post<{ url: string }>('/google-calendar/connect')).data;
      window.open(url, '_blank');
      toast('Aceptá en la pestaña de Google y después tocá "actualizar"');
    } catch { toast.error('Falta el cliente OAuth'); }
  }
  async function previewSlots() {
    try {
      const r = (await api.get<{ free: number; offer: string[] }>('/google-calendar/preview')).data;
      setPreview(r.offer.length ? `ofreceria: ${r.offer.join(' o ')} (${r.free} huecos libres hoy y mañana)` : 'no hay horarios libres hoy ni mañana: pasaria a la persona');
    } catch { setPreview('no pude leer el Google Calendar'); }
  }

  if (!s) return null;
  return (
    <div className="card p-4 space-y-2 text-sm">
      <div className="flex flex-wrap items-center gap-2 justify-between">
        <h2 className="font-semibold">Google Calendar de las demos</h2>
        <span className={s.connected ? 'text-green-700 text-xs' : 'text-amber-700 text-xs'}>
          {s.connected ? `conectado${s.email ? ` (${s.email})` : ''}` : 'sin conectar'}
        </span>
      </div>
      {!s.hasClient && (
        <div className="space-y-1">
          <div className="text-xs text-slate-500">
            Cliente OAuth de Google Cloud (tipo "Aplicación web", URI de redirección <code>{s.redirectUri}</code>).
          </div>
          <div className="grid gap-2 sm:grid-cols-2">
            <input className="input w-full" placeholder="Client ID" value={clientId} onChange={(e) => setClientId(e.target.value)} />
            <input className="input w-full" placeholder="Client secret" type="password" value={clientSecret} onChange={(e) => setClientSecret(e.target.value)} />
          </div>
          <button type="button" className="btn-secondary" disabled={!clientId || !clientSecret}
            onClick={() => saveSettings({ clientId, clientSecret })}>Guardar cliente</button>
        </div>
      )}
      {s.hasClient && (
        <div className="flex flex-wrap gap-2 items-center">
          <button type="button" className="btn-primary" onClick={connect}>{s.connected ? 'Reconectar' : 'Conectar Google Calendar'}</button>
          <button type="button" className="text-xs text-slate-500 hover:underline" onClick={() => status.refetch()}>actualizar</button>
          {s.connected && <button type="button" className="text-xs text-slate-500 hover:underline" onClick={previewSlots}>ver qué horarios ofrecería ahora</button>}
        </div>
      )}
      {preview && <div className="text-xs text-slate-600">{preview}</div>}
      {hours && (
        <div className="flex flex-wrap gap-2 items-end text-xs">
          <label>de <input type="number" min={0} max={23} className="input w-16" value={hours.startHour}
            onChange={(e) => setHours({ ...hours, startHour: Number(e.target.value) })} /></label>
          <label>a <input type="number" min={1} max={24} className="input w-16" value={hours.endHour}
            onChange={(e) => setHours({ ...hours, endHour: Number(e.target.value) })} /> h</label>
          <label>demo de <input type="number" min={10} max={180} className="input w-16" value={hours.slotMinutes}
            onChange={(e) => setHours({ ...hours, slotMinutes: Number(e.target.value) })} /> min</label>
          <label>margen <input type="number" min={0} max={1440} className="input w-20" value={hours.minNoticeMinutes}
            onChange={(e) => setHours({ ...hours, minNoticeMinutes: Number(e.target.value) })} /> min</label>
          <span className="text-slate-500">lun a vie, solo hoy o mañana, hasta 2 horarios</span>
          <button type="button" className="btn-secondary" onClick={() => saveSettings(hours)}>Guardar franja</button>
        </div>
      )}
    </div>
  );
}

export default function OnboardingApps() {
  const { data, isLoading } = useQuery({
    queryKey: ['onboarding-configs'],
    queryFn: async () => (await api.get<OnboardingAppConfig[]>('/onboarding-configs')).data,
  });

  return (
    <div className="space-y-5 max-w-3xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Onboarding de apps</h1>
        <p className="text-sm text-slate-500">
          Bot de alta de ads por aplicación. Cuando un lead de anuncio dice “activar [App]”, sales-hub
          corre estas preguntas y al final crea la cuenta. Prendé <b>Activo</b> para que la app use el bot.
          Usá <code>[NUEVO_MENSAJE]</code> para dividir en varios mensajes y <code>{'{accessUrl}'}</code>{' '}
          para el link de acceso en el mensaje de éxito.
        </p>
      </div>
      <GoogleCalendarPanel />
      {isLoading ? (
        <p className="text-sm text-slate-500">Cargando…</p>
      ) : (
        (data ?? []).map((c) => <AppCard key={c.productKey} cfg={c} />)
      )}
    </div>
  );
}
