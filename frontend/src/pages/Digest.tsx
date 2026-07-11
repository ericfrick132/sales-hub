import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import Switch from '../components/Switch';

type Instance = { instanceName: string; phoneNumber: string | null; label: string | null; status: string };
type Config = {
  enabled: boolean;
  instanceName: string | null;
  phone: string | null;
  sendHour: number;
  lastSentAt: string | null;
  masterPhone: string | null;
  instances: Instance[];
};

/**
 * Resumen diario por WhatsApp: todos los días a la hora elegida (Argentina) el bot te
 * manda un digest con los números del día — leads nuevos por app, respuestas, hot leads
 * sin atender, cierres/altas, posteos vs cupo y líneas caídas.
 */
export default function Digest() {
  const qc = useQueryClient();
  const { data, isLoading } = useQuery({
    queryKey: ['daily-digest'],
    queryFn: async () => (await api.get<Config>('/daily-digest')).data,
  });
  const invalidate = () => qc.invalidateQueries({ queryKey: ['daily-digest'] });

  const [instance, setInstance] = useState<string | null>(null);
  const [phone, setPhone] = useState<string | null>(null);
  const [hour, setHour] = useState<string | null>(null);
  const [preview, setPreview] = useState<string | null>(null);

  const toggle = useMutation({
    mutationFn: async (enabled: boolean) => api.post('/daily-digest/enabled', { enabled }),
    onSuccess: (_r, enabled) => { invalidate(); toast.success(enabled ? 'Resumen activado' : 'Resumen desactivado'); },
    onError: () => toast.error('No se pudo cambiar'),
  });
  const save = useMutation({
    mutationFn: async () =>
      api.post('/daily-digest/config', {
        instanceName: instance ?? data?.instanceName ?? null,
        phone: phone ?? data?.phone ?? null,
        sendHour: parseInt(hour ?? String(data?.sendHour ?? 21), 10),
      }),
    onSuccess: () => { setInstance(null); setPhone(null); setHour(null); invalidate(); toast.success('Config guardada'); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo guardar'),
  });
  const loadPreview = useMutation({
    mutationFn: async () => (await api.get<{ text: string }>('/daily-digest/preview')).data,
    onSuccess: (r) => setPreview(r.text),
    onError: () => toast.error('No se pudo armar el preview'),
  });
  const sendNow = useMutation({
    mutationFn: async () => api.post('/daily-digest/send-now'),
    onSuccess: () => toast.success('Resumen enviado'),
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo enviar'),
  });

  const instances = data?.instances ?? [];
  const noLine = !(instance ?? data?.instanceName);
  const dirty = instance !== null || phone !== null || hour !== null;

  return (
    <div className="space-y-5 max-w-2xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Resumen diario</h1>
        <p className="text-sm text-slate-500">
          Todos los días a la hora elegida te llega por WhatsApp el resumen del día:
          leads nuevos por app, respuestas, hot leads sin atender, cierres, posteos vs cupo y líneas caídas.
        </p>
      </div>

      <div className="card p-4 flex items-center justify-between gap-3">
        <div>
          <div className="text-sm font-medium">Resumen activo</div>
          <div className="text-xs text-slate-500">
            {data?.lastSentAt
              ? `Último envío automático: ${new Date(data.lastSentAt).toLocaleString('es-AR')}`
              : 'Todavía no se mandó ninguno.'}
          </div>
        </div>
        <Switch
          on={!!data?.enabled}
          disabled={isLoading || toggle.isPending}
          onClick={() => toggle.mutate(!data?.enabled)}
        />
      </div>

      <div className="card p-4 space-y-3">
        <div>
          <label className="text-sm font-medium">Línea de WhatsApp (por dónde se manda)</label>
          <select
            className="input mt-1"
            value={instance ?? data?.instanceName ?? ''}
            disabled={isLoading}
            onChange={(e) => setInstance(e.target.value)}
          >
            <option value="">— Elegir línea —</option>
            {instances.map((i) => (
              <option key={i.instanceName} value={i.instanceName}>
                {i.label ?? i.instanceName}{i.phoneNumber ? ` — +${i.phoneNumber}` : ''} ({i.status})
              </option>
            ))}
          </select>
          {data?.enabled && noLine && (
            <p className="text-xs text-amber-600 mt-1">⚠️ El resumen está activo pero sin línea elegida: no se manda nada.</p>
          )}
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-[1fr_140px] gap-2">
          <div>
            <label className="text-xs font-medium text-slate-600">Número destino</label>
            <input
              className="input mt-1"
              placeholder={data?.masterPhone ? `Vacío = maestro (+${data.masterPhone})` : 'Ej. +54 11 6937-0050'}
              value={phone ?? data?.phone ?? ''}
              onChange={(e) => setPhone(e.target.value)}
            />
          </div>
          <div>
            <label className="text-xs font-medium text-slate-600">Hora (AR)</label>
            <input
              className="input mt-1"
              type="number"
              min={0}
              max={23}
              value={hour ?? String(data?.sendHour ?? 21)}
              onChange={(e) => setHour(e.target.value)}
            />
          </div>
        </div>
        <p className="text-xs text-slate-400">
          Si dejás el número vacío, se usa el número maestro de inspiraciones. Si el server estuvo
          caído a esa hora, el resumen sale apenas vuelve (una sola vez por día).
        </p>
        <button className="btn-primary" disabled={!dirty || save.isPending} onClick={() => save.mutate()}>
          Guardar
        </button>
      </div>

      <div className="card p-4 space-y-3">
        <div className="flex items-center gap-2">
          <button className="btn-secondary" disabled={loadPreview.isPending} onClick={() => loadPreview.mutate()}>
            {loadPreview.isPending ? 'Armando…' : 'Ver preview'}
          </button>
          <button className="btn-primary" disabled={sendNow.isPending} onClick={() => sendNow.mutate()}>
            {sendNow.isPending ? 'Enviando…' : 'Enviar ahora'}
          </button>
        </div>
        <p className="text-xs text-slate-400">
          "Enviar ahora" es una prueba: manda el resumen ya mismo y no afecta el envío automático del día.
        </p>
        {preview && (
          <pre className="text-sm bg-slate-50 border border-slate-200 rounded-lg p-3 whitespace-pre-wrap">{preview}</pre>
        )}
      </div>
    </div>
  );
}
