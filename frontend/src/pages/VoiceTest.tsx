import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';

type Instance = { instanceName: string; phoneNumber: string | null; label: string | null; status: string };
type ProductLite = { id: string; productKey: string; displayName: string; active: boolean };

/**
 * Prueba de nota de voz IA: escribís un texto (estilo chat), elegís línea y número,
 * y el sistema lo reescribe como guion (receta de pronunciación aprobada), lo
 * sintetiza con la voz clonada de Eric y lo manda como PTT por WhatsApp.
 * Si elegís una app, el guion usa sus precios/planes/features reales.
 */
export default function VoiceTest() {
  const { data: instances } = useQuery({
    queryKey: ['test-send-instances'],
    queryFn: async () => (await api.get<Instance[]>('/test-send/instances')).data,
  });
  const { data: products } = useQuery({
    queryKey: ['products'],
    queryFn: async () => (await api.get<ProductLite[]>('/products')).data,
  });

  const [instanceName, setInstanceName] = useState('');
  const [phone, setPhone] = useState('');
  const [text, setText] = useState('');
  const [productKey, setProductKey] = useState('');
  const [farewell, setFarewell] = useState(false);
  const [sending, setSending] = useState(false);
  const [lastScript, setLastScript] = useState<string | null>(null);

  const connected = (instances ?? []).filter((i) => i.status === 'Connected');
  const canSend = !!instanceName && phone.replace(/\D/g, '').length >= 8 && text.trim().length > 0 && !sending;

  const send = async () => {
    setSending(true);
    setLastScript(null);
    try {
      const res = await api.post<{ ok: boolean; script: string }>('/test-send/voice-note', {
        instanceName,
        phone: phone.replace(/\D/g, ''),
        chatText: text.trim(),
        farewell,
        productKey: productKey || null,
      });
      setLastScript(res.data.script);
      toast.success('Nota de voz enviada 🎤');
    } catch (e: unknown) {
      const err = e as { response?: { data?: { error?: string; script?: string } } };
      if (err.response?.data?.script) setLastScript(err.response.data.script);
      toast.error(err.response?.data?.error ?? 'Falló el envío');
    } finally {
      setSending(false);
    }
  };

  return (
    <div className="space-y-5 max-w-2xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Nota de voz (prueba)</h1>
        <p className="text-sm text-slate-500">
          Escribí un texto estilo chat, elegí línea y número, y sale como <b>audio con la voz clonada</b>:
          el guion se reescribe solo con la receta (voseo, "shama", lucas, conectores) antes de sintetizar.
          Si elegís una app, usa sus precios y planes reales.
        </p>
      </div>

      <div className="card p-4 space-y-3">
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <div>
            <label className="text-sm font-medium">Línea de WhatsApp</label>
            <select className="input mt-1" value={instanceName} onChange={(e) => setInstanceName(e.target.value)}>
              <option value="">— Elegir línea —</option>
              {(instances ?? []).map((i) => (
                <option key={i.instanceName} value={i.instanceName} disabled={i.status !== 'Connected'}>
                  {i.label ?? i.instanceName} {i.phoneNumber ? `(+${i.phoneNumber})` : ''} — {i.status === 'Connected' ? 'conectada' : i.status}
                </option>
              ))}
            </select>
            {connected.length === 0 && instances && (
              <p className="text-xs text-amber-600 mt-1">No hay líneas conectadas.</p>
            )}
          </div>
          <div>
            <label className="text-sm font-medium">Número destino (con código de país)</label>
            <input className="input mt-1" placeholder="Ej: 541169370050" value={phone}
                   onChange={(e) => setPhone(e.target.value)} />
          </div>
        </div>

        <div>
          <label className="text-sm font-medium">Texto (estilo chat, el guion se arma solo)</label>
          <textarea className="input mt-1 min-h-[96px]"
                    placeholder="Ej: sale 50 mil por mes sin limite de alumnos, tenes 7 dias gratis. si queres te armo la cuenta ahora"
                    value={text} onChange={(e) => setText(e.target.value)} />
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-3 items-end">
          <div>
            <label className="text-sm font-medium">App (opcional — usa sus planes/precios reales)</label>
            <select className="input mt-1" value={productKey} onChange={(e) => setProductKey(e.target.value)}>
              <option value="">— Sin app —</option>
              {(products ?? []).map((p) => (
                <option key={p.productKey} value={p.productKey}>{p.displayName} ({p.productKey})</option>
              ))}
            </select>
          </div>
          <label className="flex items-center gap-2 text-sm cursor-pointer pb-2">
            <input type="checkbox" className="h-4 w-4 accent-emerald-600" checked={farewell}
                   onChange={(e) => setFarewell(e.target.checked)} />
            Es una despedida (cierra con "un abrazo")
          </label>
        </div>

        <div className="flex justify-end">
          <button className="btn-primary" disabled={!canSend} onClick={send}>
            {sending ? 'Generando y enviando…' : '🎤 Generar y enviar'}
          </button>
        </div>
      </div>

      {lastScript && (
        <div className="card p-4">
          <p className="text-[11px] uppercase tracking-widest text-emerald-700 font-bold mb-1">Guion generado</p>
          <p className="text-sm text-slate-700 whitespace-pre-wrap">{lastScript}</p>
        </div>
      )}

      <p className="text-xs text-slate-400">
        No toca leads ni conversaciones — es solo para probar cómo suena. El bot usa este mismo motor
        en los momentos decisivos (flag "Notas de voz IA" en el Centro de control).
      </p>
    </div>
  );
}
