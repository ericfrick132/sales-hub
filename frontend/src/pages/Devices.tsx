import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';

interface Device {
  id: string;
  name: string;
  sellerName?: string;
  tailscaleIp?: string;
  status: string;
  batteryLevel?: number;
  lastHeartbeatAt?: string;
}

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

  async function createDevice() {
    if (!newName.trim()) return;
    try {
      const { data } = await api.post('/devices', { name: newName });
      setCreated({ token: data.pairingToken, qrUrl: data.qrUrl });
      toast.success('Device creado');
      qc.invalidateQueries({ queryKey: ['devices'] });
    } catch (e: any) {
      toast.error(e.response?.data ?? 'Error al crear device');
    }
  }

  async function regenerateToken(id: string) {
    try {
      const { data } = await api.post(`/devices/${id}/regenerate-token`);
      setCreated({ token: data.pairingToken, qrUrl: data.qrUrl });
      setShowCreate(true);
    } catch { toast.error('Error'); }
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
            <div className="mt-3 p-3 bg-emerald-50 rounded">
              <div className="font-mono text-2xl text-center tracking-widest">{created.token}</div>
              <div className="text-xs text-slate-500 text-center mt-1">Código de pairing (válido 10 min)</div>
              <div className="text-xs text-slate-400 text-center mt-1 truncate">{created.qrUrl}</div>
            </div>
          )}
        </div>
      )}

      <div className="card divide-y">
        {(devices ?? []).map(d => (
          <div key={d.id} className="p-3 flex items-center justify-between">
            <div>
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
            <div className="flex gap-1">
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
