import { useState, useEffect } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { QRCodeSVG } from 'qrcode.react';
import { api } from '../lib/api';
import toast from 'react-hot-toast';

interface Device {
  id: string; name: string; sellerId?: string; sellerName?: string; tailscaleIp?: string;
  status: string; batteryLevel?: number; lastHeartbeatAt?: string;
}
interface Seller { id: string; displayName: string; email: string; }

export default function Devices() {
  const qc = useQueryClient();
  const [showCreate, setShowCreate] = useState(false);
  const [newName, setNewName] = useState('');
  const [created, setCreated] = useState<{ token: string; qrUrl: string } | null>(null);

  const { data: devices } = useQuery({
    queryKey: ['devices'],
    queryFn: async () => (await api.get<Device[]>('/devices')).data,
    refetchInterval: 10_000
  });
  const { data: sellers } = useQuery({
    queryKey: ['sellers'],
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data
  });

  async function createDevice() {
    if (!newName.trim()) return;
    const { data } = await api.post('/devices', { name: newName });
    setCreated({ token: data.pairingToken, qrUrl: data.qrUrl });
    toast.success('Device creado');
    qc.invalidateQueries({ queryKey: ['devices'] });
  }

  async function assignSeller(deviceId: string, sellerId: string) {
    await api.put(`/devices/${deviceId}/assign`, { sellerId: sellerId || null });
    toast.success('Asignado');
    qc.invalidateQueries({ queryKey: ['devices'] });
  }

  async function regenerateToken(id: string) {
    const { data } = await api.post(`/devices/${id}/regenerate-token`);
    setCreated({ token: data.pairingToken, qrUrl: data.qrUrl });
    setShowCreate(true);
  }

  async function deleteDevice(id: string) {
    if (!confirm('Eliminar este device?')) return;
    await api.delete(`/devices/${id}`);
    qc.invalidateQueries({ queryKey: ['devices'] });
  }

  const isOnline = (d: Device) => d.status === 'Online' && d.lastHeartbeatAt &&
    (Date.now() - new Date(d.lastHeartbeatAt).getTime()) < 60_000;

  return (
    <div className="max-w-4xl mx-auto p-4">
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-xl font-bold">Dispositivos Android</h2>
        <button className="btn-primary text-xs" onClick={() => { setShowCreate(true); setCreated(null); }}>+ Nuevo</button>
      </div>

      {showCreate && (
        <div className="card p-4 mb-4">
          <input className="input" placeholder="Nombre (ej: Moto E14 - Martu)" value={newName}
            onChange={e => setNewName(e.target.value)} />
          <button className="btn-primary mt-2" onClick={createDevice}>Crear</button>
          {created && (
            <div className="mt-3 p-3 bg-emerald-50 rounded flex flex-col items-center">
              <QRCodeSVG value={created.qrUrl} size={180} marginSize={2} className="bg-white rounded" />
              <div className="text-xs text-slate-500 text-center mt-2">
                1. Escaneá el QR con el teléfono → descarga e instala la app
              </div>
              <div className="font-mono text-2xl text-center tracking-widest mt-2">{created.token}</div>
              <div className="text-xs text-slate-500 text-center mt-1">
                2. Abrí la app e ingresá este código (válido 10 min)
              </div>
              <div className="text-xs text-slate-400 text-center mt-1 truncate max-w-full">{created.qrUrl}</div>
            </div>
          )}
        </div>
      )}

      <div className="card divide-y">
        {(devices ?? []).map(d => (
          <div key={d.id} className="p-3 flex items-center justify-between gap-2">
            <div className="flex-1 min-w-0">
              <div className="font-medium">{d.name}</div>
              <div className="text-xs text-slate-500">
                {d.sellerName ? `Vendedor: ${d.sellerName}` : 'Sin asignar'}
                {d.tailscaleIp && ` · ${d.tailscaleIp}`}
              </div>
              <div className="text-xs mt-0.5">
                <span className={isOnline(d) ? 'text-emerald-600' : 'text-slate-400'}>
                  {isOnline(d) ? `● Online ${d.batteryLevel != null ? `🔋${d.batteryLevel}%` : ''}` : `○ ${d.status}`}
                </span>
              </div>
            </div>
            <select className="text-xs border rounded px-1 py-0.5 w-32"
              value={d.sellerId ?? ''}
              onChange={e => assignSeller(d.id, e.target.value)}>
              <option value="">Sin asignar</option>
              {(sellers ?? []).map(s => (
                <option key={s.id} value={s.id}>{s.displayName}</option>
              ))}
            </select>
            <div className="flex gap-1 shrink-0">
              {d.status === 'Pairing' && (
                <button className="btn-secondary text-xs" onClick={() => regenerateToken(d.id)}>Nuevo token</button>
              )}
              <button className="btn-danger text-xs" onClick={() => deleteDevice(d.id)}>Eliminar</button>
            </div>
          </div>
        ))}
        {(!devices || devices.length === 0) && (
          <div className="p-4 text-slate-400 text-center">No hay dispositivos</div>
        )}
      </div>
    </div>
  );
}
