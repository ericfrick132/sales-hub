import { useState, useEffect, type ReactNode } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import type { OnboardingAppConfig } from '../lib/types';

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
