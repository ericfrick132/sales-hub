import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';

type Ad = {
  adId: string; name: string; status: string; productKey: string | null; rubro: string | null; angle: string | null;
  businessRef: string | null; prefillText: string | null; title: string | null; body: string | null;
  destinationPhone: string | null; adCreatedAt: string | null;
  lineInstance: string | null; lineStatus: string | null; lineListenOnly: boolean; leads: number;
};
type Resp = { configured: boolean; lastSyncAt: string | null; ads: Ad[] };

const ANGLE_LABEL: Record<string, string> = { 'bot-whatsapp': 'bot WhatsApp', 'antes-despues': 'antes y después' };

/** Estado de la línea a la que apunta el anuncio: si no está conectada, esos leads no llegan al bot. */
function LineBadge({ ad }: { ad: Ad }) {
  if (!ad.destinationPhone) return <span className="text-slate-400">sin número</span>;
  if (!ad.lineInstance)
    return <span className="text-red-600 font-medium">+{ad.destinationPhone} · no está cargado en Líneas</span>;
  const ok = ad.lineStatus === 'Connected' && !ad.lineListenOnly;
  return (
    <span className={ok ? 'text-emerald-700' : 'text-red-600 font-medium'}>
      +{ad.destinationPhone} · {ad.lineInstance}
      {ad.lineStatus !== 'Connected' && ' · desconectada'}
      {ad.lineListenOnly && ' · solo escucha'}
    </span>
  );
}

/**
 * Anuncios de Meta sincronizados cada hora: qué mensaje deja escrito cada uno y con qué rubro y
 * ángulo lo lee el bot. El lead se reconoce por ese mensaje (Meta casi nunca manda el id del anuncio).
 */
export default function Anuncios() {
  const qc = useQueryClient();
  const [onlyActive, setOnlyActive] = useState(true);
  const { data, isLoading } = useQuery({
    queryKey: ['meta-ads'],
    queryFn: async () => (await api.get<Resp>('/meta-ads')).data,
  });
  const sync = useMutation({
    mutationFn: async () => (await api.post<{ synced: number }>('/meta-ads/sync')).data,
    onSuccess: (r) => { toast.success(`${r.synced} anuncios sincronizados`); qc.invalidateQueries({ queryKey: ['meta-ads'] }); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo sincronizar'),
  });

  const groups = useMemo(() => {
    const ads = (data?.ads ?? []).filter((a) => !onlyActive || a.status === 'ACTIVE');
    const by = new Map<string, Ad[]>();
    for (const a of ads) {
      const k = a.productKey ?? 'sin app';
      by.set(k, [...(by.get(k) ?? []), a]);
    }
    return [...by.entries()];
  }, [data, onlyActive]);

  if (isLoading) return <div className="text-slate-500">Cargando…</div>;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <div className="text-sm text-slate-600 flex-1 min-w-[16rem]">
          El bot reconoce de qué anuncio viene cada lead por el mensaje que le deja escrito, y con eso sabe el
          rubro (no lo vuelve a preguntar) y el ángulo (qué pitch le cuenta). Se sincroniza solo cada hora.
          {data?.lastSyncAt && <> Última: {new Date(data.lastSyncAt).toLocaleString('es-AR')}.</>}
        </div>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={onlyActive} onChange={(e) => setOnlyActive(e.target.checked)} />
          solo activos
        </label>
        <button className="btn-primary" disabled={sync.isPending || !data?.configured} onClick={() => sync.mutate()}>
          {sync.isPending ? 'Sincronizando…' : 'Sincronizar ahora'}
        </button>
      </div>
      {!data?.configured && (
        <div className="card text-sm text-red-700">Falta el token de Meta (Meta:AdsAccessToken) en el servidor.</div>
      )}
      {groups.length === 0 && <div className="card text-sm text-slate-500">No hay anuncios para mostrar.</div>}
      {groups.map(([product, ads]) => (
        <div key={product} className="card space-y-2">
          <div className="font-semibold">{product} <span className="text-slate-400 font-normal">· {ads.length}</span></div>
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="text-left text-slate-500">
                  <th className="py-1 pr-3">Rubro</th>
                  <th className="py-1 pr-3">Ángulo</th>
                  <th className="py-1 pr-3">Mensaje que deja escrito</th>
                  <th className="py-1 pr-3">Línea</th>
                  <th className="py-1 pr-3 text-right">Leads</th>
                </tr>
              </thead>
              <tbody>
                {ads.map((a) => (
                  <tr key={a.adId} className="border-t border-slate-100 align-top">
                    <td className="py-1.5 pr-3 whitespace-nowrap">
                      {a.rubro ?? '—'}
                      {a.status !== 'ACTIVE' && <div className="text-xs text-slate-400">{a.status.toLowerCase()}</div>}
                    </td>
                    <td className="py-1.5 pr-3 whitespace-nowrap">{a.angle ? ANGLE_LABEL[a.angle] ?? a.angle : '—'}</td>
                    <td className="py-1.5 pr-3 min-w-[16rem]">
                      <div>{a.prefillText ?? <span className="text-slate-400">sin mensaje pre-armado</span>}</div>
                      {a.businessRef && <div className="text-xs text-slate-500">pregunta: como se llama {a.businessRef}?</div>}
                    </td>
                    <td className="py-1.5 pr-3 text-xs"><LineBadge ad={a} /></td>
                    <td className="py-1.5 pr-3 text-right">{a.leads}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      ))}
    </div>
  );
}
