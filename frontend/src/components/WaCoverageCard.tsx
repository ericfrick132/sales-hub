import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../lib/api';
import type { Product, Seller } from '../lib/types';
import QrConnectModal from './QrConnectModal';

type AppLine = { productKey: string; instanceName: string; connectedPhoneNumber?: string | null; status: string };

type Row = {
  productKey: string;
  displayName: string;
  owners: Seller[];        // dueños dedicados (whitelist contiene la app); si no hay, catch-alls
  isCatchAll: boolean;
  bestOwner?: Seller;      // el mejor posicionado (conectado+enviando > conectado > resto)
  ownerConnected: boolean;
  appLine?: AppLine;
  appLineConnected: boolean;
};

/**
 * Cobertura de WhatsApp por app: ¿cada app tiene vendedor dueño con línea conectada
 * y/o línea propia (app_<key>)? Si falta, QR al toque desde acá mismo — sin ir a
 * /products ni /sellers.
 */
export default function WaCoverageCard() {
  const qc = useQueryClient();
  const [qr, setQr] = useState<{ title: string; url: string } | null>(null);

  const productsQ = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });
  const sellersQ = useQuery({
    queryKey: ['sellers'],
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data,
    refetchInterval: 20000
  });
  const linesQ = useQuery({
    queryKey: ['app-wa-lines'],
    queryFn: async () => (await api.get<AppLine[]>('/products/whatsapp-lines')).data,
    refetchInterval: 20000
  });

  const sellers = (sellersQ.data ?? []).filter((s) => s.isActive);
  const catchAlls = sellers.filter((s) => !s.verticalsWhitelist || s.verticalsWhitelist.length === 0);

  const rows: Row[] = (productsQ.data ?? [])
    .filter((p) => p.active && p.productKey)
    .map((p) => {
      const dedicated = sellers.filter((s) => s.verticalsWhitelist?.includes(p.productKey));
      const owners = dedicated.length > 0 ? dedicated : catchAlls;
      const rank = (s: Seller) =>
        (s.instanceStatus === 'Connected' ? 2 : s.instanceStatus === 'Connecting' ? 1 : 0) +
        (s.instanceStatus === 'Connected' && s.sendingEnabled ? 1 : 0);
      const bestOwner = [...owners].sort((a, b) => rank(b) - rank(a))[0];
      const appLine = (linesQ.data ?? []).find((l) => l.productKey === p.productKey);
      return {
        productKey: p.productKey,
        displayName: p.displayName,
        owners,
        isCatchAll: dedicated.length === 0,
        bestOwner,
        ownerConnected: bestOwner?.instanceStatus === 'Connected',
        appLine,
        appLineConnected: appLine?.status === 'Connected'
      };
    });

  const okCount = rows.filter((r) => r.ownerConnected || r.appLineConnected).length;

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['app-wa-lines'] });
    qc.invalidateQueries({ queryKey: ['sellers'] });
  };

  if (productsQ.isLoading || sellersQ.isLoading) {
    return <div className="card p-4 text-sm text-slate-500">Cargando cobertura…</div>;
  }

  return (
    <div className="card overflow-x-auto">
      <table className="min-w-full text-sm">
        <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
          <tr>
            <th className="px-3 py-2 text-left">App</th>
            <th className="px-3 py-2 text-left">Estado</th>
            <th className="px-3 py-2 text-left">Vendedor dueño</th>
            <th className="px-3 py-2 text-left">Línea propia de la app</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => {
            const ok = r.ownerConnected || r.appLineConnected;
            const warn = !ok && (r.bestOwner || r.appLine);
            return (
              <tr key={r.productKey} className="border-t border-slate-100">
                <td className="px-3 py-2 font-medium">{r.displayName || r.productKey}</td>
                <td className="px-3 py-2">
                  <span className={`text-[11px] px-2 py-0.5 rounded-full font-medium ${
                    ok ? 'bg-emerald-100 text-emerald-700'
                      : warn ? 'bg-amber-100 text-amber-700'
                      : 'bg-rose-100 text-rose-700'}`}>
                    {ok ? 'con línea' : warn ? 'sin conectar' : 'sin dueño ni línea'}
                  </span>
                </td>
                <td className="px-3 py-2">
                  {r.bestOwner ? (
                    <div className="flex items-center gap-2 flex-wrap">
                      <span>
                        {r.bestOwner.displayName || r.bestOwner.email}
                        {r.isCatchAll && <span className="text-[10px] text-slate-400"> (catch-all)</span>}
                      </span>
                      <span className={`w-2 h-2 rounded-full shrink-0 ${
                        r.ownerConnected ? 'bg-emerald-500'
                          : r.bestOwner.instanceStatus === 'Connecting' ? 'bg-amber-400'
                          : 'bg-rose-400'}`}
                        title={`Línea ${r.bestOwner.evolutionInstance ?? ''}: ${r.bestOwner.instanceStatus ?? 'sin instancia'}`} />
                      {!r.ownerConnected && (
                        <button
                          className="text-[11px] px-2 py-0.5 rounded border border-slate-300 bg-slate-50 hover:bg-slate-100"
                          onClick={() => setQr({
                            title: `WhatsApp de ${r.bestOwner!.displayName || r.bestOwner!.email}`,
                            url: `/sellers/${r.bestOwner!.id}/instance/qr`
                          })}>
                          📱 QR
                        </button>
                      )}
                    </div>
                  ) : (
                    <span className="text-xs text-slate-400">
                      nadie la tiene en whitelist — <Link to="/sellers" className="underline">asignar en /sellers</Link>
                    </span>
                  )}
                </td>
                <td className="px-3 py-2">
                  <div className="flex items-center gap-2 flex-wrap">
                    {r.appLineConnected ? (
                      <span className="text-xs text-emerald-700">📱 {r.appLine?.connectedPhoneNumber ?? 'conectada'}</span>
                    ) : (
                      <>
                        <span className="text-xs text-slate-400">
                          {r.appLine ? `sin conectar (${r.appLine.status})` : 'sin línea propia'}
                        </span>
                        <button
                          className="text-[11px] px-2 py-0.5 rounded border border-slate-300 bg-slate-50 hover:bg-slate-100"
                          onClick={() => setQr({
                            title: `WhatsApp de ${r.displayName || r.productKey}`,
                            url: `/products/${r.productKey}/whatsapp/qr`
                          })}>
                          📱 conectar
                        </button>
                      </>
                    )}
                  </div>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <div className="px-3 py-2 text-[11px] text-slate-400 border-t border-slate-100">
        {okCount}/{rows.length} apps con alguna línea conectada. “Vendedor dueño” = whitelist en /sellers;
        “línea propia” = número exclusivo de la app (instancia app_&lt;key&gt;).
      </div>
      {qr && (
        <QrConnectModal
          title={qr.title}
          fetchUrl={qr.url}
          onClose={() => setQr(null)}
          onConnected={refresh}
        />
      )}
    </div>
  );
}
