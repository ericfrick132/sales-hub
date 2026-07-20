import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';

/**
 * Modal genérico para conectar una línea de WhatsApp escaneando un QR.
 * `fetchUrl` es cualquier endpoint que devuelva { qrBase64?, status } y que al
 * pegarle (re)genere la instancia + QR (products/{key}/whatsapp/qr o
 * sellers/{id}/instance/qr). Se refresca solo cada 15s (el QR expira ~20s) y
 * al detectar la conexión avisa y se cierra.
 */
export default function QrConnectModal({
  title,
  fetchUrl,
  onClose,
  onConnected
}: {
  title: string;
  fetchUrl: string;
  onClose: () => void;
  onConnected?: () => void;
}) {
  const qrQ = useQuery({
    queryKey: ['qr-connect', fetchUrl],
    refetchInterval: 15000,
    queryFn: async () => (await api.get<{ qrBase64?: string; status: string }>(fetchUrl)).data
  });

  const connected = qrQ.data?.status === 'open' || qrQ.data?.status === 'connected';
  useEffect(() => {
    if (connected) {
      toast.success(`${title}: WhatsApp conectado ✅`);
      onConnected?.();
      onClose();
    }
  }, [connected]);

  return (
    <div className="fixed inset-0 z-50 bg-black/50 flex items-center justify-center p-4" onClick={onClose}>
      <div className="bg-white rounded-xl p-5 max-w-sm w-full text-center space-y-3" onClick={(e) => e.stopPropagation()}>
        <h3 className="font-semibold">{title}</h3>
        <p className="text-xs text-slate-500">
          En el celular con el número para esta línea: WhatsApp → Dispositivos vinculados → Vincular dispositivo → escaneá.
        </p>
        {qrQ.isLoading ? (
          <div className="py-10 text-slate-400 text-sm">Generando QR…</div>
        ) : qrQ.data?.qrBase64 ? (
          <img
            src={qrQ.data.qrBase64.startsWith('data:') ? qrQ.data.qrBase64 : `data:image/png;base64,${qrQ.data.qrBase64}`}
            alt="QR" className="mx-auto w-56 h-56" />
        ) : (
          <div className="py-10 text-slate-400 text-sm">
            {connected ? 'Ya está conectada ✅' : 'Sin QR (probá de nuevo en unos segundos)'}
          </div>
        )}
        <div className="text-[11px] text-slate-400">Estado: {qrQ.data?.status ?? '…'} — el QR se renueva solo cada 15s</div>
        <button className="btn-secondary text-sm w-full" onClick={onClose}>Cerrar</button>
      </div>
    </div>
  );
}
