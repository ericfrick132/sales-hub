import { useState, useEffect, useRef, type ReactNode } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import type { OnboardingAppConfig, OnboardingAudioVariant } from '../lib/types';

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
      {isLoading ? (
        <p className="text-sm text-slate-500">Cargando…</p>
      ) : (
        (data ?? []).map((c) => <AppCard key={c.productKey} cfg={c} />)
      )}
    </div>
  );
}
