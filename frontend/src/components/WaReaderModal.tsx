import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';

/**
 * Leer los chats de un celular y cargarlos como prospectos.
 *
 * Es un escaneo aparte del de la línea: vincula el celular a un lector propio (Baileys nueva),
 * que es lo único que devuelve el TELÉFONO REAL de cada chat — WhatsApp los direcciona por un
 * id opaco (LID) y la Evolution que usamos para el día a día lo descarta. El lector solo lee:
 * no manda mensajes, y al terminar se desvincula solo.
 */
type ReaderState = {
  state: 'starting' | 'qr' | 'syncing' | 'uploading' | 'done' | 'error';
  qrBase64?: string | null;
  chats: number;
  resolved: number;
  batches: number;
  progress?: number | null;
  error?: string | null;
  result?: { chats: number; created: number; alreadyLeads: number; filteredByWords: number; messagesStored: number } | null;
};

const n = (v: number) => v.toLocaleString('es-AR');

export default function WaReaderModal({ lineId, title, onClose, onDone }: {
  lineId: string;
  title: string;
  onClose: () => void;
  onDone?: () => void;
}) {
  const [starting, setStarting] = useState(true);
  const [startError, setStartError] = useState<string | null>(null);

  useEffect(() => {
    api.post(`/phone-lines/${lineId}/wa-read`)
      .catch((e) => setStartError(e.response?.data?.error ?? 'No se pudo arrancar el lector'))
      .finally(() => setStarting(false));
  }, [lineId]);

  const q = useQuery({
    queryKey: ['wa-read', lineId],
    enabled: !starting && !startError,
    // El QR de WhatsApp se renueva cada ~20s y el avance tiene que verse moverse.
    refetchInterval: (query) => (query.state.data?.state === 'done' ? false : 3000),
    queryFn: async () => (await api.get<ReaderState>(`/phone-lines/${lineId}/wa-read`)).data
  });

  const s = q.data;
  useEffect(() => {
    if (s?.state === 'done' && s.result) {
      toast.success(`${n(s.result.created)} prospectos nuevos cargados`);
      onDone?.();
    }
  }, [s?.state]);

  async function cancel() {
    try { await api.delete(`/phone-lines/${lineId}/wa-read`); } catch { /* ya no estaba */ }
    onClose();
  }

  return (
    <div className="fixed inset-0 z-50 bg-black/50 flex items-center justify-center p-4" onClick={onClose}>
      <div className="bg-white rounded-xl p-5 max-w-sm w-full text-center space-y-3" onClick={(e) => e.stopPropagation()}>
        <h3 className="font-semibold">Leer los chats de {title}</h3>

        {startError && <div className="text-sm text-rose-700 py-6">{startError}</div>}

        {!startError && (starting || !s || s.state === 'starting') && (
          <div className="py-10 text-slate-400 text-sm">Generando el QR…</div>
        )}

        {s?.state === 'qr' && (
          <>
            <p className="text-xs text-slate-500">
              En ESE celular: WhatsApp → Ajustes → Dispositivos vinculados → Vincular un dispositivo.
            </p>
            {s.qrBase64
              ? <img src={s.qrBase64} alt="QR" className="mx-auto w-56 h-56" />
              : <div className="py-10 text-slate-400 text-sm">Esperando el QR…</div>}
            <div className="text-[11px] text-slate-400">Se renueva solo. El lector no manda mensajes: solo lee.</div>
          </>
        )}

        {(s?.state === 'syncing' || s?.state === 'uploading') && (
          <div className="py-4 space-y-2">
            <div className="text-sm text-slate-700">
              {s.state === 'uploading' ? 'Cargando los prospectos al CRM…' : 'Trayendo el historial…'}
            </div>
            <div className="h-1.5 rounded-full bg-slate-200 overflow-hidden">
              <div
                className={`h-full bg-emerald-500 transition-all duration-500 ${s.progress == null ? 'animate-pulse w-1/3' : ''}`}
                style={s.progress == null ? undefined : { width: `${Math.min(100, s.progress)}%` }}
              />
            </div>
            <div className="text-xs text-slate-500 tabular-nums">
              {n(s.chats)} chats · {n(s.resolved)} con número
            </div>
            <div className="text-[11px] text-slate-400">
              WhatsApp lo manda en tandas; puede tardar unos minutos. Podés cerrar esta ventana: sigue solo.
            </div>
          </div>
        )}

        {s?.state === 'done' && s.result && (
          <div className="py-4 space-y-1 text-sm">
            <div className="text-emerald-700 font-medium">Listo</div>
            <div>{n(s.result.created)} prospectos nuevos en el CRM</div>
            <div className="text-xs text-slate-500">
              {n(s.result.chats)} chats leídos · {n(s.result.alreadyLeads)} ya eran leads
              {s.result.filteredByWords > 0 && ` · ${n(s.result.filteredByWords)} fuera por el filtro de palabras`}
            </div>
            <div className="text-[11px] text-slate-400">El celular ya quedó desvinculado del lector.</div>
          </div>
        )}

        {s?.state === 'error' && (
          <div className="py-6 text-sm text-rose-700">{s.error ?? 'Algo falló'}</div>
        )}

        <button className="btn-secondary text-sm w-full" onClick={s?.state === 'done' ? onClose : cancel}>
          {s?.state === 'done' ? 'Cerrar' : 'Cancelar'}
        </button>
      </div>
    </div>
  );
}
