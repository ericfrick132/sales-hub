import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import Switch from '../components/Switch';

type Instance = { instanceName: string; phoneNumber: string | null; label: string | null; status: string };
type Phone = { id: string; phone: string; label: string | null };
type Config = { enabled: boolean; instanceName: string | null; instances: Instance[]; phones: Phone[] };

/**
 * Módulo de transcripción de audios (relay personal): cuando un número autorizado
 * manda (o reenvía) una nota de voz a la línea de WhatsApp elegida, se transcribe con
 * Whisper y se responde el texto por WhatsApp. No crea leads ni toca Conversaciones.
 * Doble filtro: tiene que ser de un número de la lista Y llegar a la línea configurada.
 */
export default function Transcripcion() {
  const qc = useQueryClient();
  const { data, isLoading } = useQuery({
    queryKey: ['transcription'],
    queryFn: async () => (await api.get<Config>('/transcription')).data,
  });
  const invalidate = () => qc.invalidateQueries({ queryKey: ['transcription'] });

  const [phone, setPhone] = useState('');
  const [label, setLabel] = useState('');

  const toggle = useMutation({
    mutationFn: async (enabled: boolean) => api.post('/transcription/enabled', { enabled }),
    onSuccess: (_r, enabled) => { invalidate(); toast.success(enabled ? 'Módulo activado' : 'Módulo desactivado'); },
    onError: () => toast.error('No se pudo cambiar'),
  });
  const setInstance = useMutation({
    mutationFn: async (instanceName: string | null) => api.post('/transcription/instance', { instanceName }),
    onSuccess: () => { invalidate(); toast.success('Línea actualizada'); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo guardar la línea'),
  });
  const add = useMutation({
    mutationFn: async () => api.post('/transcription/phones', { phone: phone.trim(), label: label.trim() || null }),
    onSuccess: () => { setPhone(''); setLabel(''); invalidate(); toast.success('Número agregado'); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo agregar'),
  });
  const remove = useMutation({
    mutationFn: async (id: string) => api.delete(`/transcription/phones/${id}`),
    onSuccess: () => { invalidate(); toast.success('Número eliminado'); },
    onError: () => toast.error('No se pudo eliminar'),
  });

  const instances = data?.instances ?? [];
  const phones = data?.phones ?? [];
  const noLine = !data?.instanceName;

  return (
    <div className="space-y-5 max-w-2xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Transcripción de audios</h1>
        <p className="text-sm text-slate-500">
          Reenviá una nota de voz a la línea elegida y te responde el texto transcripto.
          Sólo funciona con audios de los números autorizados que lleguen a esa línea.
        </p>
      </div>

      <div className="card p-4 flex items-center justify-between gap-3">
        <div>
          <div className="text-sm font-medium">Módulo activo</div>
          <div className="text-xs text-slate-500">Si está apagado, los audios siguen el flujo normal.</div>
        </div>
        <Switch
          on={!!data?.enabled}
          disabled={isLoading || toggle.isPending}
          onClick={() => toggle.mutate(!data?.enabled)}
        />
      </div>

      <div className="card p-4 space-y-2">
        <label className="text-sm font-medium">Línea de WhatsApp</label>
        <select
          className="input"
          value={data?.instanceName ?? ''}
          disabled={isLoading || setInstance.isPending}
          onChange={(e) => setInstance.mutate(e.target.value || null)}
        >
          <option value="">— Elegir línea —</option>
          {instances.map((i) => (
            <option key={i.instanceName} value={i.instanceName}>
              {i.label ?? i.instanceName}{i.phoneNumber ? ` — +${i.phoneNumber}` : ''} ({i.status})
            </option>
          ))}
        </select>
        {data?.enabled && noLine && (
          <p className="text-xs text-amber-600">⚠️ El módulo está activo pero no elegiste línea: no va a transcribir nada hasta que selecciones una.</p>
        )}
        <p className="text-xs text-slate-400">El relay sólo escucha en esta línea; los audios a otras líneas no se tocan.</p>
      </div>

      <div className="card p-4 space-y-3">
        <label className="text-sm font-medium">Números autorizados</label>
        <div className="grid grid-cols-1 sm:grid-cols-[1fr_1fr_auto] gap-2">
          <input
            className="input"
            placeholder="Teléfono (ej. +54 11 6937-0050)"
            value={phone}
            onChange={(e) => setPhone(e.target.value)}
          />
          <input
            className="input"
            placeholder="Etiqueta (opcional, ej. Eric)"
            value={label}
            onChange={(e) => setLabel(e.target.value)}
          />
          <button
            className="btn-primary"
            disabled={phone.replace(/\D/g, '').length < 6 || add.isPending}
            onClick={() => add.mutate()}
          >
            Agregar
          </button>
        </div>
        <p className="text-xs text-slate-400">
          Match tolerante (compara por los últimos dígitos): da igual el formato (+, 0, 9, espacios).
        </p>

        {isLoading ? (
          <p className="text-slate-400">Cargando…</p>
        ) : phones.length === 0 ? (
          <p className="text-slate-400 text-sm">Todavía no hay números autorizados.</p>
        ) : (
          <div className="divide-y divide-slate-100 -mx-1">
            {phones.map((p) => (
              <div key={p.id} className="flex items-center justify-between gap-2 px-1 py-2">
                <div>
                  <div className="text-sm font-medium">+{p.phone}</div>
                  {p.label && <div className="text-xs text-slate-500">{p.label}</div>}
                </div>
                <button
                  className="btn-danger text-xs"
                  disabled={remove.isPending}
                  onClick={() => remove.mutate(p.id)}
                >
                  Quitar
                </button>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
