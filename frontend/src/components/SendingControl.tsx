import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import clsx from 'clsx';
import { api } from '../lib/api';

type Props = {
  sellerId: string;
  sendingEnabled: boolean;
  instanceStatus: string | null;
  /** Compact icon-only version for table rows. Default false = full pill with label. */
  compact?: boolean;
  /** Query keys to invalidate after toggling so the parent screen refreshes. */
  invalidate?: string[][];
};

export default function SendingControl({ sellerId, sendingEnabled, instanceStatus, compact, invalidate }: Props) {
  const qc = useQueryClient();
  const [busy, setBusy] = useState(false);
  const connected = instanceStatus === 'Connected';

  const toggle = async () => {
    if (busy) return;
    if (!sendingEnabled && !connected) {
      toast.error('Conectá WhatsApp antes de activar el envío');
      return;
    }
    setBusy(true);
    try {
      await api.post(`/sellers/${sellerId}/sending`, { enabled: !sendingEnabled });
      toast.success(!sendingEnabled ? 'Envío reanudado' : 'Envío pausado');
      (invalidate ?? [['admin-metrics'], ['dashboard-me']]).forEach((k) => qc.invalidateQueries({ queryKey: k }));
    } catch (err: unknown) {
      const e = err as { response?: { data?: { error?: string } } };
      toast.error(e?.response?.data?.error ?? 'No se pudo cambiar el estado');
    } finally {
      setBusy(false);
    }
  };

  if (compact) {
    return (
      <button
        type="button"
        onClick={toggle}
        disabled={busy}
        title={sendingEnabled ? 'Pausar envío' : 'Reanudar envío'}
        className={clsx(
          'inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium transition',
          sendingEnabled ? 'bg-emerald-100 text-emerald-800 hover:bg-emerald-200'
                          : 'bg-slate-100 text-slate-600 hover:bg-slate-200',
          busy && 'opacity-50 cursor-wait'
        )}>
        <Dot active={sendingEnabled} />
        {sendingEnabled ? 'Enviando' : 'Pausado'}
      </button>
    );
  }

  return (
    <div className="card p-4 flex items-center gap-4">
      <div className="flex items-center gap-3">
        <Dot active={sendingEnabled} large />
        <div>
          <div className="text-sm font-semibold">
            {sendingEnabled ? 'Enviando mensajes' : 'Envío pausado'}
          </div>
          <div className="text-xs text-slate-500">
            WhatsApp: <span className="font-medium">{instanceStatus ?? '—'}</span>
            {!connected && !sendingEnabled && ' · Conectá tu WhatsApp para activar'}
          </div>
        </div>
      </div>
      <button
        type="button"
        onClick={toggle}
        disabled={busy}
        className={clsx(
          'ml-auto inline-flex items-center gap-2 rounded-md px-4 py-2 text-sm font-medium transition',
          sendingEnabled ? 'bg-rose-600 text-white hover:bg-rose-700'
                          : 'bg-emerald-600 text-white hover:bg-emerald-700',
          busy && 'opacity-50 cursor-wait'
        )}>
        {sendingEnabled ? <PauseIcon /> : <PlayIcon />}
        {sendingEnabled ? 'Pausar' : 'Reanudar'}
      </button>
    </div>
  );
}

function Dot({ active, large }: { active: boolean; large?: boolean }) {
  return (
    <span className={clsx('relative inline-flex', large ? 'w-3 h-3' : 'w-2 h-2')}>
      <span
        className={clsx(
          'absolute inset-0 rounded-full',
          active ? 'bg-emerald-500' : 'bg-slate-400'
        )}
      />
      {active && (
        <span className="absolute inset-0 rounded-full bg-emerald-500 animate-ping opacity-75" />
      )}
    </span>
  );
}

function PlayIcon() {
  return <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z" /></svg>;
}

function PauseIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor">
      <rect x="6" y="5" width="4" height="14" />
      <rect x="14" y="5" width="4" height="14" />
    </svg>
  );
}
