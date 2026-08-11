import { useState, useEffect } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { QRCodeSVG } from 'qrcode.react';
import { api } from '../lib/api';
import toast from 'react-hot-toast';

interface Device {
  id: string; name: string; sellerId?: string; sellerName?: string; tailscaleIp?: string;
  status: string; batteryLevel?: number; appVersion?: string; lastHeartbeatAt?: string;
}
interface Seller { id: string; displayName: string; email: string; }

/** "hace 3 min" / "hace 2 h" / "hace 4 d" — para saber de cuándo es el último latido. */
function hace(iso?: string): string {
  if (!iso) return 'nunca';
  const mins = Math.floor((Date.now() - new Date(iso).getTime()) / 60_000);
  if (mins < 1) return 'recién';
  if (mins < 60) return `hace ${mins} min`;
  if (mins < 60 * 24) return `hace ${Math.floor(mins / 60)} h`;
  return `hace ${Math.floor(mins / 1440)} d`;
}

export default function Devices() {
  const qc = useQueryClient();
  const [showCreate, setShowCreate] = useState(false);
  const [newName, setNewName] = useState('');
  const [created, setCreated] = useState<{ token: string; qrUrl: string } | null>(null);
  /** Panel de re-pairing abierto para un device ya existente (token nuevo + QR). */
  const [pairing, setPairing] = useState<{ deviceId: string; token: string; qrUrl: string } | null>(null);

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

  /**
   * Reconectar un celu que se cayó: token nuevo de 6 dígitos + QR, sin borrar el device
   * (conserva su vendedor y su historial). El celu vuelve solo si la app sigue viva; esto
   * es para cuando hay que reengancharlo a mano desde el teléfono.
   */
  async function reconnect(id: string) {
    try {
      const { data } = await api.post(`/devices/${id}/regenerate-token`);
      setPairing({ deviceId: id, token: data.pairingToken, qrUrl: data.qrUrl });
      qc.invalidateQueries({ queryKey: ['devices'] });
    } catch {
      toast.error('No se pudo generar el código');
    }
  }

  async function deleteDevice(id: string) {
    if (!confirm('Eliminar este device?')) return;
    await api.delete(`/devices/${id}`);
    qc.invalidateQueries({ queryKey: ['devices'] });
  }

  // 2 min de gracia: el latido puede venir del WebSocket (cada 20s) o del poll de envío
  // (cada 30s, más lo que tarde un envío en curso). Con 60s parpadeaba mientras mandaba.
  const isOnline = (d: Device) => d.status === 'Online' && d.lastHeartbeatAt &&
    (Date.now() - new Date(d.lastHeartbeatAt).getTime()) < 120_000;

  const [testingId, setTestingId] = useState<string | null>(null);

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
          {created && <PairingPanel token={created.token} qrUrl={created.qrUrl} withApk />}
        </div>
      )}

      <div className="card divide-y">
        {(devices ?? []).map(d => (
          <div key={d.id} className="p-3">
            <div className="flex items-center justify-between gap-2">
              <div className="flex-1 min-w-0">
                <div className="font-medium">{d.name}</div>
                <div className="text-xs text-slate-500">
                  {d.sellerName ? `Vendedor: ${d.sellerName}` : 'Sin asignar'}
                  {d.tailscaleIp && ` · ${d.tailscaleIp}`}
                </div>
                <div className="text-xs mt-0.5">
                  <span className={isOnline(d) ? 'text-emerald-600' : 'text-slate-400'}>
                    {isOnline(d) ? `● Online ${d.batteryLevel != null ? `🔋${d.batteryLevel}%` : ''}` : `○ ${d.status}`}{d.appVersion ? ` · v${d.appVersion}` : ''}
                    {!isOnline(d) && ` · último latido ${hace(d.lastHeartbeatAt)}`}
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
                <button className="btn-secondary text-xs"
                  title="El celu recorre los chats de WhatsApp y reporta lo que respondieron los leads (para recuperar charlas viejas)"
                  onClick={async () => {
                    try {
                      const { data } = await api.post(`/devices/${d.id}/sweep-chats`);
                      toast.success(data.message ?? 'Pedido enviado', { duration: 6000 });
                    } catch {
                      toast.error('No se pudo pedir el barrido');
                    }
                  }}>
                  Levantar chats
                </button>
                <button className="btn-secondary text-xs"
                  onClick={() => setTestingId(testingId === d.id ? null : d.id)}>
                  Probar envío
                </button>
                <button className={isOnline(d) ? 'btn-secondary text-xs' : 'btn-primary text-xs'}
                  title="Genera un código nuevo para volver a enganchar este celu, sin perder el vendedor asignado"
                  onClick={() => pairing?.deviceId === d.id ? setPairing(null) : reconnect(d.id)}>
                  {d.status === 'Pairing' ? 'Nuevo código' : 'Reconectar'}
                </button>
                <button className="btn-danger text-xs" onClick={() => deleteDevice(d.id)}>Eliminar</button>
              </div>
            </div>
            {pairing?.deviceId === d.id && <PairingPanel token={pairing.token} qrUrl={pairing.qrUrl} />}
            {testingId === d.id && <TestSendPanel deviceId={d.id} />}
          </div>
        ))}
        {(!devices || devices.length === 0) && (
          <div className="p-4 text-slate-400 text-center">No hay dispositivos</div>
        )}
      </div>
    </div>
  );
}

/**
 * Código de pairing + QR. En alta (`withApk`) el QR sirve para bajar el APK; al reconectar
 * un celu que ya tiene la app, lo único que hace falta es el código de 6 dígitos.
 */
function PairingPanel({ token, qrUrl, withApk = false }: { token: string; qrUrl: string; withApk?: boolean }) {
  return (
    <div className="mt-3 p-3 bg-emerald-50 rounded flex flex-col items-center">
      <QRCodeSVG value={qrUrl} size={180} marginSize={2} className="bg-white rounded" />
      <div className="text-xs text-slate-500 text-center mt-2">
        {withApk
          ? '1. Escaneá el QR con el teléfono → descarga e instala la app'
          : '1. Si la app no está instalada, escaneá el QR para bajarla'}
      </div>
      <div className="font-mono text-2xl text-center tracking-widest mt-2">{token}</div>
      <div className="text-xs text-slate-500 text-center mt-1">
        2. Abrí la app en el celu, ingresá este código y tocá Conectar (válido 10 min)
      </div>
      <div className="text-xs text-slate-400 text-center mt-1 truncate max-w-full">{qrUrl}</div>
    </div>
  );
}

interface TestStatus {
  state: 'none' | 'queued' | 'sending' | 'sent' | 'failed';
  error?: string | null;
  phone?: string;
}

/** Traduce los motivos que reporta el APK a algo que se entienda de un vistazo. */
function friendlyError(error?: string | null): string {
  if (!error) return 'sin detalle';
  if (error === 'no_whatsapp') return 'el número no tiene WhatsApp';
  if (error === 'invalid_number') return 'número inválido para WhatsApp';
  if (error.startsWith('humano_en_el_celu')) return 'alguien agarró el teléfono: se cancel\u00f3 y vuelve a la cola';
  if (error === 'no_se_tipeo_nada') return 'no lleg\u00f3 a escribir el mensaje';
  if (error === 'no_pude_limpiar_el_cajon') return 'no pudo limpiar el borrador de WhatsApp';
  if (error.startsWith('input_alterado')) return 'alguien estaba usando el teléfono: no se envió, se reintenta más tarde';
  if (error === 'chat_no_abrio') return 'WhatsApp no abrió la conversación (reintenta solo)';
  if (error === 'send_button_not_found') return 'no encontró el botón de enviar (pantalla bloqueada o WhatsApp no abrió)';
  if (error === 'send_not_confirmed') return 'tipeó el mensaje pero no salió';
  if (error.startsWith('open:')) return `no pudo abrir WhatsApp por adb — ${error.slice(5).trim()}`;
  return error;
}

/**
 * "Enviar YA" de prueba: el celu manda este texto en su próximo poll (≤30s),
 * salteando cola, caps y pacing. Para verificar el fierro end-to-end.
 */
function TestSendPanel({ deviceId }: { deviceId: string }) {
  const [phone, setPhone] = useState('');
  const [text, setText] = useState('prueba saleshub bridge');
  const [status, setStatus] = useState<TestStatus | null>(null);
  const [polling, setPolling] = useState(false);

  useEffect(() => {
    if (!polling) return;
    const iv = setInterval(async () => {
      const { data } = await api.get<TestStatus>(`/devices/${deviceId}/test-send`);
      setStatus(data);
      if (data.state === 'sent' || data.state === 'failed') setPolling(false);
    }, 3000);
    return () => clearInterval(iv);
  }, [polling, deviceId]);

  async function fire() {
    try {
      await api.post(`/devices/${deviceId}/test-send`, { phone, text });
      setStatus({ state: 'queued' });
      setPolling(true);
      toast.success('Encolado: el celu lo manda en su próximo poll (menos de 30s)');
    } catch (e: any) {
      toast.error(e.response?.data?.error ?? 'No se pudo encolar la prueba');
    }
  }

  const statusLine = status && status.state !== 'none' && (
    status.state === 'sent' ? <span className="text-emerald-600">✅ Enviado</span>
    : status.state === 'failed' ? <span className="text-red-600">❌ Falló: {friendlyError(status.error)}</span>
    : <span className="text-amber-600">⏳ {status.state === 'queued' ? 'Esperando el poll del celu…' : 'El celu lo está mandando…'}</span>
  );

  return (
    <div className="mt-2 p-3 bg-slate-50 rounded space-y-2">
      <div className="text-xs text-slate-500">
        Manda YA (salteando cola y pacing) desde este celu. Número completo con código de país, sin +.
      </div>
      <div className="flex flex-wrap gap-2">
        <input className="input text-sm flex-1 min-w-40" placeholder="549341xxxxxxx"
          value={phone} onChange={e => setPhone(e.target.value)} />
        <input className="input text-sm flex-[2] min-w-52" placeholder="Texto"
          value={text} onChange={e => setText(e.target.value)} />
        <button className="btn-primary text-xs" onClick={fire} disabled={polling}>
          Enviar YA
        </button>
      </div>
      {statusLine && <div className="text-sm">{statusLine}</div>}
    </div>
  );
}
