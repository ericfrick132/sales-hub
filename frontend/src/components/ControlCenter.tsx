import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import Switch from './Switch';
import type { Product } from '../lib/types';

type SellerLite = {
  sellerId: string;
  displayName: string;
  instanceStatus: string | null;
  sendingEnabled: boolean;
};

// Centro de control de la venta: todos los switches en un solo lugar (arriba de "Hoy"),
// con un estado guiado de qué falta prender para que la máquina venda sola.
export default function ControlCenter({ sellers }: { sellers: SellerLite[] }) {
  const qc = useQueryClient();
  const productsQ = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data,
  });
  const products = productsQ.data ?? [];

  async function toggleSending(s: SellerLite) {
    if (!s.sendingEnabled && s.instanceStatus !== 'Connected') {
      toast.error('Conectá WhatsApp antes de activar el envío');
      return;
    }
    try {
      await api.post(`/sellers/${s.sellerId}/sending`, { enabled: !s.sendingEnabled });
      toast.success(!s.sendingEnabled ? 'Envío activado' : 'Envío pausado');
      qc.invalidateQueries({ queryKey: ['admin-metrics'] });
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'No se pudo cambiar el envío'); }
  }

  const flagsQ = useQuery({
    queryKey: ['runner-flags'],
    queryFn: async () => (await api.get<{ key: string; label: string; enabled: boolean }[]>('/flags')).data,
  });

  async function toggleFlag(key: string, enabled: boolean) {
    try {
      await api.post(`/flags/${key}`, { enabled });
      toast.success(enabled ? 'Runner encendido' : 'Runner apagado');
      qc.invalidateQueries({ queryKey: ['runner-flags'] });
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló'); }
  }

  async function toggleAuto(p: Product, patch: { autoPilot?: boolean; autoReengage?: boolean }) {
    const autoPilot = patch.autoPilot ?? p.autoPilot;
    const autoReengage = patch.autoReengage ?? p.autoReengage;
    try {
      await api.post(`/products/${p.id}/automation`, { autoPilot, autoReengage });
      qc.invalidateQueries({ queryKey: ['products-min'] });
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló'); }
  }

  return (
    <div className="space-y-5">
      {/* Motores automáticos (workers) */}
      <div>
        <h3 className="text-sm font-semibold">Motores automáticos</h3>
        <p className="text-xs text-slate-400 mb-2">Procesos que corren solos en el servidor: generación de posteos, captación de leads, Instagram y artículos SEO.</p>
        <div className="grid gap-2 sm:grid-cols-3">
          {(flagsQ.data ?? []).map((f) => (
            <div key={f.key} className="flex items-center gap-3 border rounded-lg p-2.5 bg-white">
              <div className="flex-1 min-w-0">
                <div className="font-medium text-sm truncate">{f.label}</div>
                <div className="text-[11px] text-slate-400">{f.enabled ? 'corriendo' : 'apagado'}</div>
              </div>
              <Switch on={f.enabled} onClick={() => toggleFlag(f.key, !f.enabled)} title={`Prender/apagar ${f.label}`} />
            </div>
          ))}
        </div>
      </div>

      {/* Envío de WhatsApp por vendedor */}
      <div>
        <h3 className="text-sm font-semibold">Envío de WhatsApp por vendedor</h3>
        <p className="text-xs text-slate-400 mb-2">Prende o pausa el envío automático de mensajes de cada vendedor. Necesita el WhatsApp conectado.</p>
        <div className="grid gap-2 sm:grid-cols-2">
          {sellers.map((s) => {
            const conn = s.instanceStatus === 'Connected';
            return (
              <div key={s.sellerId} className="flex items-center gap-3 border rounded-lg p-2.5 bg-white">
                <div className="flex-1 min-w-0">
                  <div className="font-medium text-sm truncate">{s.displayName}</div>
                  <div className="text-[11px]">
                    {conn
                      ? <span className="text-emerald-600">● WhatsApp conectado</span>
                      : <Link to={`/admin/sellers/${s.sellerId}`} className="text-amber-600 underline">○ Conectar WhatsApp →</Link>}
                  </div>
                </div>
                <Switch on={s.sendingEnabled} onClick={() => toggleSending(s)}
                  title={conn ? 'Prender/apagar el envío de este vendedor' : 'Conectá WhatsApp para poder enviar'} />
              </div>
            );
          })}
        </div>
      </div>

      {/* Apps — piloto / re-enganche */}
      <div>
        <h3 className="text-sm font-semibold">Respuesta automática por app (piloto)</h3>
        <p className="text-xs text-slate-400 mb-2">Con el <b>piloto</b> en verde, el bot de esa app responde y vende solo. <b>Re-eng.</b> le vuelve a escribir a los leads que quedaron dormidos.</p>
        <div className="grid gap-2 sm:grid-cols-2">
          {products.map((p) => (
            <div key={p.id} className="flex items-center gap-3 border rounded-lg p-2.5 bg-white">
              <div className="flex-1 min-w-0">
                <div className="font-medium text-sm truncate">{p.displayName}</div>
                <div className="text-[11px] text-slate-400">{p.active ? 'activa' : 'pausada'}</div>
              </div>
              <div className="flex items-center gap-3 shrink-0">
                <div className="flex flex-col items-center gap-0.5">
                  <Switch on={p.autoPilot} onClick={() => toggleAuto(p, { autoPilot: !p.autoPilot })}
                    title="Piloto automático: el bot responde y vende solo" />
                  <span className="text-[9px] text-slate-400">piloto</span>
                </div>
                <div className="flex flex-col items-center gap-0.5">
                  <Switch on={p.autoReengage} disabled={!p.autoPilot}
                    onClick={() => toggleAuto(p, { autoReengage: !p.autoReengage })}
                    title={p.autoPilot ? 'Re-enganche a leads dormidos' : 'Necesita Piloto ON'} />
                  <span className="text-[9px] text-slate-400">re-eng.</span>
                </div>
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
