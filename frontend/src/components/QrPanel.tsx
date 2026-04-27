import { useEffect, useState } from 'react';
import { api } from '../lib/api';
import toast from 'react-hot-toast';

type Props = { sellerId: string; currentStatus?: string | null };

export default function QrPanel({ sellerId, currentStatus }: Props) {
  const [qr, setQr] = useState<string | null>(null);
  const [status, setStatus] = useState<string>(currentStatus ?? 'Unknown');
  const [loading, setLoading] = useState(false);

  async function refresh() {
    setLoading(true);
    try {
      const { data } = await api.get(`/sellers/${sellerId}/instance/qr`);
      setQr(data.qrBase64 ?? null);
      setStatus(data.status);
    } catch (err: any) {
      toast.error(err.response?.data?.error ?? 'Error al generar QR');
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    refresh();
    const int = setInterval(refresh, 8000);
    return () => clearInterval(int);
  }, [sellerId]);

  async function logout() {
    if (!confirm('¿Cerrar sesión de WhatsApp? Tendrás que volver a escanear.')) return;
    await api.post(`/sellers/${sellerId}/instance/logout`);
    setQr(null);
    setStatus('Disconnected');
  }

  return (
    <div className="card p-6 space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <div className="text-sm text-slate-500">Estado WhatsApp</div>
          <div className="text-lg font-semibold">{status}</div>
        </div>
        <button className="btn-secondary" onClick={refresh} disabled={loading}>
          {loading ? '...' : 'Refrescar'}
        </button>
      </div>

      {status !== 'Connected' && status !== 'open' ? (
        qr ? (
          <div className="flex flex-col items-center gap-2">
            <img src={qr.startsWith('data:') ? qr : `data:image/png;base64,${qr}`}
                 alt="QR WhatsApp" className="w-64 h-64 bg-white rounded" />
            <p className="text-xs text-slate-500">
              Abrí WhatsApp en tu celular → Configuración → Dispositivos vinculados → Vincular dispositivo
            </p>
          </div>
        ) : (
          <div className="text-slate-500 text-sm">Generando QR…</div>
        )
      ) : (
        <div className="text-sm text-emerald-700 bg-emerald-50 border border-emerald-200 rounded-md p-3">
          WhatsApp conectado. Ya podés activar el envío automático desde el switch "Comenzar envíos".
        </div>
      )}

      {(status === 'Connected' || status === 'open') && (
        <button className="btn-danger w-full" onClick={logout}>Desvincular WhatsApp</button>
      )}
    </div>
  );
}
