import { useEffect, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import Switch from '../components/Switch';

type Settings = {
  enabled: boolean;
  perLinePerDay: number;
  minIdleDays: number;
  maxIdleDays: number | null;
  sendWindows: string[];
  sendWeekdays: number[];
  handoffSellerId: string | null;
  senderSellerIds: string[];
  productKeys: string[];
  personalizeWithAi: boolean;
};

type Line = {
  sellerId: string;
  name: string;
  selected: boolean;
  canSend: boolean;
  reason: string;
  deviceCap: number | null;
  sellerDailyCap: number;
  usedToday: number;
  sentToday: number;
  pending: number;
};

type StageRow = { stage: string; label: string; enqueued: number; sent: number; replied: number };

type Status = {
  settings: Settings;
  lines: Line[];
  pool: number;
  totals: { enqueued: number; sent: number; replied: number; excluded: number };
  aiPaused: boolean;
  byStage: StageRow[];
  products: { productKey: string; displayName: string }[];
};

type PreviewItem = {
  leadId: string;
  name: string;
  productKey: string;
  stageLabel: string;
  score: number;
  owed: boolean;
  inboundCount: number;
  idleDays: number;
  lastInbound: string;
  message: string | null;
  ai: boolean;
  excluded: boolean;
  reason: string | null;
};

const pct = (a: number, b: number) => (b > 0 ? `${Math.round((100 * a) / b)}%` : '—');

/**
 * Campaña de remarketing: el bot le escribe a leads que ya nos hablaron y se enfriaron, con un
 * mensaje según dónde quedó la charla, para coordinar una llamada. El tope es por línea y por
 * día para no quemar el número. Los que reciben el mensaje quedan etiquetados "bot-remarketing".
 */
export default function Remarketing() {
  const qc = useQueryClient();
  const { data, isLoading } = useQuery({
    queryKey: ['remarketing'],
    queryFn: async () => (await api.get<Status>('/remarketing')).data,
    refetchInterval: 30_000,
  });

  const [form, setForm] = useState<Settings | null>(null);
  useEffect(() => {
    if (data && !form) setForm(data.settings);
  }, [data, form]);

  const save = useMutation({
    mutationFn: async (s: Settings) => (await api.put<{ ok: boolean; cancelled: number }>('/remarketing', s)).data,
    onSuccess: (r) => {
      toast.success(r.cancelled > 0 ? `Guardado. ${r.cancelled} mensajes sin enviar volvieron a la cola.` : 'Guardado');
      qc.invalidateQueries({ queryKey: ['remarketing'] });
    },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo guardar'),
  });

  const [preview, setPreview] = useState<{ seller: string; items: PreviewItem[] } | null>(null);
  const [loadingPreview, setLoadingPreview] = useState(false);
  async function loadPreview(ai: boolean) {
    setLoadingPreview(true);
    try {
      setPreview((await api.get(`/remarketing/preview`, { params: { ai, take: ai ? 10 : 30 } })).data);
    } catch (e: any) {
      toast.error(e?.response?.data?.error ?? 'No se pudo armar la vista previa');
    } finally {
      setLoadingPreview(false);
    }
  }

  if (isLoading || !data || !form) return <div className="text-sm text-slate-400">Cargando…</div>;

  const set = <K extends keyof Settings>(k: K, v: Settings[K]) => setForm({ ...form, [k]: v });
  const toggleIn = (list: string[], v: string) => (list.includes(v) ? list.filter((x) => x !== v) : [...list, v]);
  const dirty = JSON.stringify(form) !== JSON.stringify(data.settings);
  const selectedLines = data.lines.filter((l) => l.selected);

  return (
    <div className="space-y-6 max-w-5xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Remarketing</h1>
        <p className="text-sm text-slate-500 mt-1">
          El bot le escribe a leads que ya nos hablaron y se enfriaron, con un mensaje según dónde quedó la
          charla, para <b>coordinar una llamada</b>. Primero van a los que les debíamos respuesta, después los
          que más escribieron; la antigüedad desempata. Cada uno recibe un solo mensaje y queda etiquetado{' '}
          <code className="text-xs bg-slate-100 px-1 rounded">bot-remarketing</code>. Si contesta, aparece en el
          CRM con la próxima acción "coordinar la llamada" y se le pasa a la persona elegida con un resumen.
        </p>
      </div>

      {/* Interruptor + medidores */}
      <section className="rounded-xl border bg-white p-4 space-y-4">
        <div className="flex items-center justify-between gap-4">
          <div>
            <div className="font-semibold">Campaña {data.settings.enabled ? 'prendida' : 'apagada'}</div>
            <div className="text-xs text-slate-500">
              {data.pool.toLocaleString('es-AR')} leads en la cola (aprox.) · enviados {data.totals.sent} · contestaron{' '}
              {data.totals.replied} ({pct(data.totals.replied, data.totals.sent)})
              {data.totals.excluded > 0 && <> · la IA descartó {data.totals.excluded} (clientes, proveedores, conocidos)</>}
            </div>
          </div>
          <Switch on={form.enabled} onClick={() => set('enabled', !form.enabled)} title="Prender o apagar la campaña" />
        </div>

        {data.aiPaused && (
          <div className="rounded-lg border border-rose-300 bg-rose-50 px-3 py-2 text-sm text-rose-800">
            La IA no está disponible (la cuenta de Anthropic está sin crédito). Con "personalizar con IA" prendido la
            campaña espera y no manda nada: la IA es la que descarta a clientes y proveedores antes de escribir.
          </div>
        )}

        {selectedLines.length === 0 ? (
          <div className="rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800">
            Elegí abajo por qué líneas sale. Sin líneas no se manda nada.
          </div>
        ) : (
          <div className="grid gap-3 sm:grid-cols-2">
            {selectedLines.map((l) => {
              const cap = data.settings.perLinePerDay;
              const effective = Math.min(cap, l.deviceCap ?? cap, l.sellerDailyCap);
              return (
                <div key={l.sellerId} className="rounded-lg border p-3">
                  <div className="flex items-center justify-between text-sm">
                    <span className="font-medium">{l.name}</span>
                    <span className={l.canSend ? 'text-emerald-600 text-xs' : 'text-rose-600 text-xs'}>
                      {l.canSend ? l.reason : `no manda: ${l.reason}`}
                    </span>
                  </div>
                  <div className="mt-2 h-2.5 rounded-full bg-slate-100 overflow-hidden">
                    <div
                      className="h-full bg-slate-800"
                      style={{ width: `${Math.min(100, (100 * l.sentToday) / Math.max(1, cap))}%` }}
                    />
                  </div>
                  <div className="mt-1.5 text-xs text-slate-500">
                    hoy: {l.sentToday} enviados · {l.pending} esperando · tope {cap}/día
                  </div>
                  {effective < cap && (
                    <div className="mt-1 text-[11px] text-amber-700">
                      Otro tope manda antes:{' '}
                      {l.deviceCap !== null && l.deviceCap < cap && <>el celular acepta {l.deviceCap} chats nuevos por día (se cambia en Dispositivos). </>}
                      {l.sellerDailyCap < cap && <>el vendedor tiene tope diario {l.sellerDailyCap}. </>}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </section>

      {/* Config */}
      <section className="rounded-xl border bg-white p-4 space-y-4">
        <div className="font-semibold">Configuración</div>
        <div className="grid gap-4 sm:grid-cols-3 text-sm">
          <label className="space-y-1">
            <div className="text-slate-600">Mensajes por línea por día</div>
            <input type="number" min={0} max={50} className="input w-full" value={form.perLinePerDay}
              onChange={(e) => set('perLinePerDay', Number(e.target.value))} />
          </label>
          <label className="space-y-1">
            <div className="text-slate-600">Sin actividad hace al menos (días)</div>
            <input type="number" min={1} max={365} className="input w-full" value={form.minIdleDays}
              onChange={(e) => set('minIdleDays', Number(e.target.value))} />
          </label>
          <label className="space-y-1">
            <div className="text-slate-600">Y como máximo (días, vacío = sin tope)</div>
            <input type="number" min={2} className="input w-full" value={form.maxIdleDays ?? ''}
              onChange={(e) => set('maxIdleDays', e.target.value === '' ? null : Number(e.target.value))} />
          </label>
          <label className="space-y-1">
            <div className="text-slate-600">Franjas (hora AR, ej. 9-12, 17-20)</div>
            <input className="input w-full" value={form.sendWindows.join(', ')}
              onChange={(e) => set('sendWindows', e.target.value.split(',').map((x) => x.trim()).filter(Boolean))} />
            <div className="text-[11px] text-slate-400">El primer contacto de 8 a 12 h contesta 36% contra 26% al mediodía y a la tarde.</div>
          </label>
          <div className="space-y-1">
            <div className="text-slate-600">Días</div>
            <div className="flex flex-wrap gap-1.5">
              {['lun', 'mar', 'mié', 'jue', 'vie', 'sáb', 'dom'].map((d, i) => (
                <label key={d} className="flex items-center gap-1 text-xs">
                  <input type="checkbox" checked={form.sendWeekdays.includes(i + 1)}
                    onChange={() => set('sendWeekdays', form.sendWeekdays.includes(i + 1)
                      ? form.sendWeekdays.filter((x) => x !== i + 1)
                      : [...form.sendWeekdays, i + 1].sort())} />
                  {d}
                </label>
              ))}
            </div>
            <div className="text-[11px] text-slate-400">Lunes a jueves ~31% contra ~26% viernes y fin de semana.</div>
          </div>
          <label className="space-y-1">
            <div className="text-slate-600">Si contesta, pasar a</div>
            <select className="input w-full" value={form.handoffSellerId ?? ''}
              onChange={(e) => set('handoffSellerId', e.target.value || null)}>
              <option value="">número maestro</option>
              {data.lines.map((l) => <option key={l.sellerId} value={l.sellerId}>{l.name}</option>)}
            </select>
            <div className="text-[11px] text-slate-400">Le llega el resumen por WhatsApp y el bot se calla en esa charla.</div>
          </label>
          <label className="flex items-center gap-2 pt-6">
            <input type="checkbox" checked={form.personalizeWithAi}
              onChange={(e) => set('personalizeWithAi', e.target.checked)} />
            <span className="text-slate-600">Personalizar cada mensaje con IA según la charla</span>
          </label>
        </div>

        <div className="text-sm">
          <div className="text-slate-600 mb-1">Líneas que mandan (el lead pasa a ese vendedor)</div>
          <div className="flex flex-wrap gap-2">
            {data.lines.map((l) => (
              <label key={l.sellerId}
                className={`flex items-center gap-2 rounded-lg border px-2.5 py-1.5 ${form.senderSellerIds.includes(l.sellerId) ? 'border-slate-800' : ''}`}>
                <input type="checkbox" checked={form.senderSellerIds.includes(l.sellerId)}
                  onChange={() => set('senderSellerIds', toggleIn(form.senderSellerIds, l.sellerId))} />
                <span>{l.name}</span>
                <span className={`text-[11px] ${l.canSend ? 'text-emerald-600' : 'text-slate-400'}`}>
                  {l.reason}
                </span>
              </label>
            ))}
          </div>
        </div>

        <div className="text-sm">
          <div className="text-slate-600 mb-1">Productos (ninguno marcado = todos)</div>
          <div className="flex flex-wrap gap-2">
            {data.products.map((p) => (
              <label key={p.productKey} className="flex items-center gap-2 rounded-lg border px-2.5 py-1.5">
                <input type="checkbox" checked={form.productKeys.includes(p.productKey)}
                  onChange={() => set('productKeys', toggleIn(form.productKeys, p.productKey))} />
                <span>{p.displayName}</span>
              </label>
            ))}
          </div>
        </div>

        <div className="flex items-center gap-3">
          <button className="btn-primary" disabled={!dirty || save.isPending} onClick={() => save.mutate(form)}>
            {save.isPending ? 'Guardando…' : 'Guardar'}
          </button>
          {dirty && <button className="text-sm text-slate-500 hover:underline" onClick={() => setForm(data.settings)}>descartar cambios</button>}
          {form.enabled && !data.settings.enabled && (
            <span className="text-xs text-amber-700">Al guardar, la campaña arranca en el próximo ciclo (≤10 min) si hay línea que pueda mandar.</span>
          )}
        </div>
      </section>

      {/* Métrica por etapa */}
      {data.byStage.length > 0 && (
        <section className="rounded-xl border bg-white p-4">
          <div className="font-semibold mb-2">Respuesta por etapa</div>
          <table className="w-full text-sm">
            <thead className="text-left text-slate-500">
              <tr><th className="py-1">Dónde se había enfriado</th><th>Enviados</th><th>Contestaron</th><th>%</th></tr>
            </thead>
            <tbody>
              {data.byStage.map((s) => (
                <tr key={s.stage} className="border-t">
                  <td className="py-1.5">{s.label}</td><td>{s.sent}</td><td>{s.replied}</td><td>{pct(s.replied, s.sent)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      {/* Vista previa */}
      <section className="rounded-xl border bg-white p-4 space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div>
            <div className="font-semibold">Próximos en la cola</div>
            <div className="text-xs text-slate-500">Con la configuración guardada. No manda nada.</div>
          </div>
          <div className="flex gap-2">
            <button className="btn-secondary" disabled={loadingPreview} onClick={() => loadPreview(false)}>Ver próximos 30</button>
            <button className="btn-secondary" disabled={loadingPreview} onClick={() => loadPreview(true)}>Ver 10 como saldrían (IA)</button>
          </div>
        </div>
        {loadingPreview && <div className="text-sm text-slate-400">Armando…</div>}
        {preview && !loadingPreview && (
          preview.items.length === 0 ? (
            <div className="text-sm text-slate-500">No hay nadie en la cola con esta configuración.</div>
          ) : (
            <div className="space-y-2">
              {preview.items.map((it) => (
                <div key={it.leadId} className="rounded-lg border p-3 text-sm">
                  <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-slate-500">
                    <span className="font-medium text-slate-800 text-sm">{it.name || 'sin nombre'}</span>
                    <span>{it.productKey}</span>
                    <span className="rounded bg-slate-100 px-1.5">{it.stageLabel}</span>
                    {it.owed && <span className="rounded bg-amber-100 text-amber-800 px-1.5">le debíamos respuesta</span>}
                    <span>{it.inboundCount} mensajes suyos</span>
                    <span>hace {it.idleDays} días</span>
                  </div>
                  <div className="mt-1 text-xs text-slate-500">último suyo: “{it.lastInbound.slice(0, 160)}”</div>
                  {it.excluded ? (
                    <div className="mt-2 rounded bg-slate-100 px-2.5 py-2 text-slate-600">
                      No se le escribe: {it.reason}
                    </div>
                  ) : it.message === null ? (
                    <div className="mt-2 rounded bg-rose-50 px-2.5 py-2 text-rose-700">IA no disponible: con la IA prendida, a este no se le escribiría todavía.</div>
                  ) : (
                    <div className="mt-2 rounded bg-emerald-50 px-2.5 py-2 text-slate-800">
                      {it.message}
                      <span className="ml-2 text-[10px] text-slate-400">{it.ai ? 'IA' : 'fijo'}</span>
                    </div>
                  )}
                </div>
              ))}
            </div>
          )
        )}
      </section>
    </div>
  );
}
