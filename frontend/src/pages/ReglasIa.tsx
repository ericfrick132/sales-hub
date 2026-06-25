import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import type { AiRule } from '../lib/types';

/**
 * CRUD de reglas duras de la IA de ventas. Se inyectan al final del system prompt
 * (con prioridad sobre el playbook de cada app) y aplican a todas las apps.
 */
export default function ReglasIa() {
  const qc = useQueryClient();
  const { data, isLoading } = useQuery({
    queryKey: ['ai-rules'],
    queryFn: async () => (await api.get<AiRule[]>('/ai-rules')).data,
  });
  const [text, setText] = useState('');
  const invalidate = () => qc.invalidateQueries({ queryKey: ['ai-rules'] });

  const create = useMutation({
    mutationFn: async (t: string) =>
      api.post('/ai-rules', { text: t, productKey: null, isActive: true, sortOrder: data?.length ?? 0 }),
    onSuccess: () => { setText(''); invalidate(); toast.success('Regla agregada'); },
    onError: () => toast.error('No se pudo agregar'),
  });
  const update = useMutation({
    mutationFn: async (r: AiRule) =>
      api.put(`/ai-rules/${r.id}`, { text: r.text, productKey: r.productKey, isActive: r.isActive, sortOrder: r.sortOrder }),
    onSuccess: invalidate,
    onError: () => toast.error('No se pudo guardar'),
  });
  const remove = useMutation({
    mutationFn: async (id: string) => api.delete(`/ai-rules/${id}`),
    onSuccess: () => { invalidate(); toast.success('Regla borrada'); },
    onError: () => toast.error('No se pudo borrar'),
  });

  const rules = data ?? [];

  return (
    <div className="space-y-5 max-w-3xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Reglas de la IA</h1>
        <p className="text-sm text-slate-500">
          Reglas duras que la IA de ventas DEBE cumplir en cada respuesta. Se inyectan al final del
          prompt, con prioridad sobre el playbook de cada app. Aplican a todas las apps.
        </p>
      </div>

      <div className="card p-4 space-y-3">
        <label className="text-sm font-medium">Nueva regla</label>
        <textarea
          className="input min-h-[72px]"
          placeholder="Ej: cuando el lead confirme que quiere comprar, mandale el link de checkout y nada mas"
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        <div className="flex justify-end">
          <button
            className="btn-primary"
            disabled={!text.trim() || create.isPending}
            onClick={() => create.mutate(text.trim())}
          >
            Agregar regla
          </button>
        </div>
      </div>

      {isLoading ? (
        <p className="text-slate-400">Cargando…</p>
      ) : rules.length === 0 ? (
        <p className="text-slate-400 text-sm">
          Todavía no hay reglas. La IA usa solo el estilo base + el playbook de cada app.
        </p>
      ) : (
        <div className="space-y-2">
          {rules.map((r) => (
            <RuleRow key={r.id} rule={r} onSave={(x) => update.mutate(x)} onDelete={() => remove.mutate(r.id)} />
          ))}
        </div>
      )}
    </div>
  );
}

function RuleRow({ rule, onSave, onDelete }: { rule: AiRule; onSave: (r: AiRule) => void; onDelete: () => void }) {
  const [text, setText] = useState(rule.text);
  const dirty = text.trim() !== rule.text;
  return (
    <div className={`card p-3 ${rule.isActive ? '' : 'opacity-60'}`}>
      <textarea className="input min-h-[56px] mb-2" value={text} onChange={(e) => setText(e.target.value)} />
      <div className="flex items-center justify-between gap-2">
        <label className="flex items-center gap-2 text-sm text-slate-600">
          <input type="checkbox" checked={rule.isActive} onChange={(e) => onSave({ ...rule, isActive: e.target.checked })} />
          Activa
        </label>
        <div className="flex gap-2">
          {dirty && (
            <button className="btn-secondary text-xs" onClick={() => onSave({ ...rule, text: text.trim() })}>
              Guardar
            </button>
          )}
          <button className="btn-danger text-xs" onClick={onDelete}>Borrar</button>
        </div>
      </div>
    </div>
  );
}
