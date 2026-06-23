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

  const connected = sellers.filter((s) => s.instanceStatus === 'Connected');
  const sending = sellers.filter((s) => s.sendingEnabled);
  const disconnected = sellers.filter((s) => s.instanceStatus !== 'Connected');
  const piloto = products.filter((p) => p.autoPilot);
  const selling = sending.length > 0 && piloto.length > 0;

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

  async function toggleAuto(p: Product, patch: { autoPilot?: boolean; autoReengage?: boolean }) {
    const autoPilot = patch.autoPilot ?? p.autoPilot;
    const autoReengage = patch.autoReengage ?? p.autoReengage;
    try {
      await api.post(`/products/${p.id}/automation`, { autoPilot, autoReengage });
      qc.invalidateQueries({ queryKey: ['products-min'] });
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló'); }
  }

  return (
    <div className="card p-4 md:p-5 space-y-4 border-2 border-brand-100">
      <div className="flex items-center justify-between flex-wrap gap-2">
        <h2 className="text-lg font-semibold">Centro de control</h2>
        <span className={`inline-flex items-center gap-1.5 text-sm font-medium rounded-full px-3 py-1 ${
          selling ? 'bg-emerald-100 text-emerald-800' : 'bg-amber-100 text-amber-800'
        }`}>
          <span className={`w-2 h-2 rounded-full ${selling ? 'bg-emerald-500' : 'bg-amber-500'}`} />
          {selling ? 'Vendiendo' : 'No está vendiendo'}
        </span>
      </div>

      {/* 3 chequeos guiados */}
      <div className="grid gap-3 sm:grid-cols-3">
        <StepCard ok title="Motor de captación" desc="Encendido — capturando y procesando leads solo" />
        <StepCard ok={disconnected.length === 0}
          title="WhatsApp conectado"
          desc={`${connected.length}/${sellers.length} vendedores conectados`} />
        <StepCard ok={piloto.length > 0}
          title="Piloto automático"
          desc={`${piloto.length}/${products.length} apps responden solas`} />
      </div>

      {!selling && (
        <div className="text-sm bg-amber-50 text-amber-800 rounded-lg px-3 py-2">
          Para arrancar a vender: conectá el WhatsApp de los vendedores en gris y prendé su <b>envío</b>.
          Las apps ya responden solas si el <b>piloto</b> está en verde.
        </div>
      )}

      {/* Vendedores — envío */}
      <div>
        <h3 className="text-sm font-semibold mb-2">Vendedores — envío</h3>
        <div className="grid gap-2 sm:grid-cols-2">
          {sellers.map((s) => {
            const conn = s.instanceStatus === 'Connected';
            return (
              <div key={s.sellerId} className="flex items-center gap-3 border rounded-lg p-2.5">
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
        <h3 className="text-sm font-semibold mb-2">Aplicaciones — piloto automático</h3>
        <div className="grid gap-2 sm:grid-cols-2">
          {products.map((p) => (
            <div key={p.id} className="flex items-center gap-3 border rounded-lg p-2.5">
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

function StepCard({ ok, title, desc }: { ok: boolean; title: string; desc: string }) {
  return (
    <div className={`rounded-lg p-3 border ${ok ? 'border-emerald-200 bg-emerald-50/50' : 'border-amber-200 bg-amber-50/50'}`}>
      <div className="flex items-center gap-2">
        <span className={`grid place-items-center w-5 h-5 rounded-full text-white text-xs ${ok ? 'bg-emerald-500' : 'bg-amber-500'}`}>
          {ok ? '✓' : '!'}
        </span>
        <span className="font-medium text-sm">{title}</span>
      </div>
      <div className="text-[11px] text-slate-500 mt-1">{desc}</div>
    </div>
  );
}
