import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';

interface Device {
  id: string; name: string; sellerName?: string; sellerId?: string;
  tailscaleIp?: string; status: string; batteryLevel?: number;
  pairingToken?: string;
}

export default function DeviceScreen() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const qc = useQueryClient();
  const [msg, setMsg] = useState('');
  const [sending, setSending] = useState(false);

  const { data: device } = useQuery({
    queryKey: ['device', id],
    queryFn: async () => (await api.get<Device[]>(`/devices`)).data.find(d => d.id === id),
    refetchInterval: 5_000
  });

  const { data: relayState } = useQuery({
    queryKey: ['relay'],
    queryFn: async () => (await api.get<any[]>('/relay')).data ?? [],
    refetchInterval: 5_000
  });

  const relay = relayState?.find((r: any) =>
    device?.tailscaleIp && r.deviceSerial?.includes(device.tailscaleIp));

  const isOnline = device?.status === 'Online';

  async function sendMessage() {
    if (!msg.trim() || !isOnline) return;
    setSending(true);
    try {
      // A través del RelayController, mandamos comando al relay para que envíe
      await api.post(`/relay/${encodeURIComponent(relay?.deviceSerial || device?.tailscaleIp || '')}/command`, {
        action: 'send_now',
        phone: device?.sellerId || '',
        text: msg
      });
      setMsg('');
      toast.success('Mensaje enviado');
    } catch {
      toast.error('Error al enviar');
    }
    setSending(false);
  }

  return (
    <div className="max-w-3xl mx-auto p-4">
      <button className="text-sm text-blue-600 mb-2" onClick={() => navigate('/devices')}>← Volver</button>

      <div className="card p-4">
        <div className="flex items-center justify-between">
          <h2 className="text-lg font-bold">{device?.name ?? 'Dispositivo'}</h2>
          <span className={isOnline ? 'text-emerald-600' : 'text-slate-400'}>
            {isOnline ? '● Online' : '○ Offline'}
            {device?.batteryLevel != null && ` 🔋${device.batteryLevel}%`}
          </span>
        </div>
        <div className="text-xs text-slate-500 mt-1">
          {device?.tailscaleIp && `IP: ${device.tailscaleIp}`}
          {relay && ` · Relay: ${relay.action}`}
        </div>
        {!isOnline && device?.pairingToken && (
          <div className="mt-3 p-2 bg-amber-50 rounded text-sm">
            Token de pairing: <span className="font-mono font-bold">{device.pairingToken}</span>
          </div>
        )}
      </div>

      {/* Área de mensajes */}
      <div className="card mt-3 p-4 min-h-[300px] max-h-[500px] overflow-y-auto">
        <div className="text-xs text-slate-400 text-center mb-4">
          {isOnline ? (relay?.action === 'sending' ? 'Escribiendo...' : relay?.action === 'typing' ? 'Bot tipeando...' : 'Conectado — esperando actividad') : 'Dispositivo offline'}
        </div>
      </div>

      {/* Input de mensaje manual */}
      {isOnline && (
        <div className="mt-3 flex gap-2">
          <input className="input flex-1" placeholder="Escribí un mensaje manual..." value={msg}
            onChange={e => setMsg(e.target.value)}
            onKeyDown={e => e.key === 'Enter' && sendMessage()} />
          <button className="btn-primary" onClick={sendMessage} disabled={sending}>
            {sending ? '...' : 'Enviar'}
          </button>
        </div>
      )}

      {/* Relay state */}
      {relay && (
        <div className="card mt-3 p-3 text-xs text-slate-500">
          <div>Estado relay: <strong>{relay.action}</strong></div>
          {relay.currentPhone && <div>Último: {relay.currentPhone}</div>}
          <div>Enviados hoy: {relay.messagesSentToday}</div>
          <div>En cola: {relay.messagesInQueue}</div>
        </div>
      )}
    </div>
  );
}
