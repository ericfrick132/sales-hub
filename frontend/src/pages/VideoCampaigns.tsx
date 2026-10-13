import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import clsx from 'clsx';
import { Link } from 'react-router-dom';
import { api } from '../lib/api';
import type { Product } from '../lib/types';

type Campaign = {
  id: string; name: string; productKey: string; instanceName: string;
  messageBefore: string; mediaAssetId: string; videoName?: string | null; messageAfter: string;
  perDay: number; windowFromHour: number; windowToHour: number; weekdays: number[];
  status: 'Draft' | 'Active' | 'Paused' | 'Done'; createdAt: string; startedAt?: string | null;
  total: number; pending: number; sent: number; failed: number; skipped: number; replied: number;
};
type Recipient = { leadId: string; name: string; phone?: string | null; status: string; sentAt?: string | null; repliedAt?: string | null; error?: string | null };
type AudienceItem = { leadId: string; name: string; phone?: string | null; source: string; firstMessage?: string | null; lastActivity?: string | null; ourMessages: number; suggested: boolean; whyNot?: string | null };
type Media = { id: string; fileName: string; mimeType: string; sizeBytes: number; createdAt: string };
type SendLine = { instanceName: string; label: string; phone?: string | null; productKey?: string | null };
type Preview = { name?: string | null; before: string[]; after: string[] };

const STATUS: Record<Campaign['status'], { label: string; cls: string }> = {
  Draft: { label: 'borrador', cls: 'bg-slate-100 text-slate-600' },
  Active: { label: 'enviando', cls: 'bg-emerald-100 text-emerald-700' },
  Paused: { label: 'pausada', cls: 'bg-amber-100 text-amber-700' },
  Done: { label: 'terminada', cls: 'bg-sky-100 text-sky-700' },
};
const DAYS = ['lun', 'mar', 'mie', 'jue', 'vie', 'sab', 'dom'];
const MB = 1024 * 1024;

function fmtDate(s?: string | null) {
  return s ? new Date(s).toLocaleString('es-AR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : '';
}

export default function VideoCampaigns() {
  const [creating, setCreating] = useState(false);
  const list = useQuery({
    queryKey: ['video-campaigns'],
    queryFn: async () => (await api.get<Campaign[]>('/video-campaigns')).data,
    refetchInterval: 30000,
  });

  return (
    <div className="space-y-4">
      <div className="card p-4 flex items-start justify-between gap-3">
        <div>
          <h2 className="font-semibold text-lg">Campaña con video</h2>
          <p className="text-sm text-slate-500 max-w-2xl">
            Un mensaje y un video a leads que nos escribieron primero y despues no contestaron. Sale de a uno, en la
            franja que elijas, con tope diario. Cada lead la recibe una sola vez; si contesta, pasa a la persona de la
            app con copia a vos.
          </p>
        </div>
        {!creating && <button className="btn-primary shrink-0" onClick={() => setCreating(true)}>+ Nueva campaña</button>}
      </div>

      {creating && <NewCampaign onDone={() => setCreating(false)} />}

      {list.isLoading && <div className="text-sm text-slate-500">Cargando…</div>}
      {list.data?.length === 0 && !creating && <div className="text-sm text-slate-500">Todavia no hay campañas.</div>}
      {(list.data ?? []).map((c) => <CampaignCard key={c.id} c={c} />)}
    </div>
  );
}

function CampaignCard({ c }: { c: Campaign }) {
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const [testPhone, setTestPhone] = useState('');
  const refresh = () => qc.invalidateQueries({ queryKey: ['video-campaigns'] });

  const recipients = useQuery({
    queryKey: ['video-campaign-recipients', c.id],
    enabled: open,
    queryFn: async () => (await api.get<Recipient[]>(`/video-campaigns/${c.id}/recipients`)).data,
  });
  const action = useMutation({
    mutationFn: async (what: 'activate' | 'pause' | 'delete') =>
      what === 'delete' ? api.delete(`/video-campaigns/${c.id}`) : api.post(`/video-campaigns/${c.id}/${what}`),
    onSuccess: refresh,
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'No se pudo'),
  });
  const test = useMutation({
    mutationFn: async () => api.post(`/video-campaigns/${c.id}/test`, { phone: testPhone }),
    onSuccess: () => toast.success('Prueba enviada'),
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'No se pudo mandar la prueba'),
  });

  const pct = c.sent ? Math.round((c.replied / c.sent) * 100) : 0;
  return (
    <div className="card p-4 space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <div className="font-semibold">{c.name}</div>
        <span className={clsx('text-[11px] rounded px-1.5 py-0.5', STATUS[c.status].cls)}>{STATUS[c.status].label}</span>
        <span className="text-xs text-slate-500">{c.productKey} · {c.instanceName} · {c.videoName}</span>
        <span className="text-xs text-slate-500">
          · {c.perDay}/dia de {c.windowFromHour} a {c.windowToHour} h ({c.weekdays.map((d) => DAYS[d - 1]).join(', ')})
        </span>
        <div className="ml-auto flex gap-2">
          {(c.status === 'Draft' || c.status === 'Paused') && (
            <button className="btn-primary text-sm" disabled={action.isPending} onClick={() => action.mutate('activate')}>
              {c.status === 'Draft' ? 'Empezar a mandar' : 'Seguir'}
            </button>
          )}
          {c.status === 'Active' && (
            <button className="btn-secondary text-sm" disabled={action.isPending} onClick={() => action.mutate('pause')}>Pausar</button>
          )}
          {c.status === 'Draft' && (
            <button className="btn-secondary text-sm" disabled={action.isPending} onClick={() => action.mutate('delete')}>Borrar</button>
          )}
        </div>
      </div>
      <div className="grid grid-cols-3 sm:grid-cols-6 gap-2 text-center">
        {[
          ['leads', c.total], ['pendientes', c.pending], ['enviados', c.sent],
          ['contestaron', `${c.replied}${c.sent ? ` (${pct}%)` : ''}`], ['salteados', c.skipped], ['fallaron', c.failed],
        ].map(([l, v]) => (
          <div key={l as string} className="rounded bg-slate-50 py-2">
            <div className="text-lg font-semibold">{v}</div>
            <div className="text-[11px] text-slate-500">{l}</div>
          </div>
        ))}
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <input className="input text-sm w-48" placeholder="Tu numero para la prueba" value={testPhone} onChange={(e) => setTestPhone(e.target.value)} />
        <button className="btn-secondary text-sm" disabled={test.isPending || testPhone.replace(/\D/g, '').length < 10} onClick={() => test.mutate()}>
          {test.isPending ? 'Mandando…' : 'Mandarme una prueba'}
        </button>
        <button className="text-sm underline text-slate-600 ml-auto" onClick={() => setOpen(!open)}>
          {open ? 'Ocultar leads' : 'Ver leads'}
        </button>
      </div>
      {open && (
        <div className="max-h-80 overflow-y-auto border border-slate-100 rounded">
          <table className="w-full text-sm">
            <tbody>
              {(recipients.data ?? []).map((r) => (
                <tr key={r.leadId} className="border-b border-slate-50">
                  <td className="p-2"><Link className="hover:underline" to={`/conversations?lead=${r.leadId}`}>{r.name}</Link></td>
                  <td className="p-2 text-slate-500 tabular-nums">{r.phone}</td>
                  <td className="p-2">
                    {r.repliedAt ? <span className="text-emerald-700 font-medium">contesto {fmtDate(r.repliedAt)}</span>
                      : r.status === 'Sent' ? <span className="text-slate-600">enviado {fmtDate(r.sentAt)}</span>
                      : r.status === 'Pending' ? <span className="text-slate-400">pendiente</span>
                      : <span className="text-rose-600" title={r.error ?? ''}>{r.status === 'Skipped' ? 'salteado' : 'fallo'}: {r.error}</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function NewCampaign({ onDone }: { onDone: () => void }) {
  const qc = useQueryClient();
  const fileRef = useRef<HTMLInputElement>(null);
  const products = useQuery({ queryKey: ['products-min'], queryFn: async () => (await api.get<Product[]>('/products')).data });
  const lines = useQuery({ queryKey: ['conv-send-lines'], queryFn: async () => (await api.get<SendLine[]>('/conversations/lines')).data });

  const [productKey, setProductKey] = useState('');
  const [instanceName, setInstanceName] = useState('');
  const [name, setName] = useState('');
  const [mediaId, setMediaId] = useState('');
  const [before, setBefore] = useState('');
  const [after, setAfter] = useState('podemos armar una demo hoy si queres con el equipo, en que horario podes?');
  const [perDay, setPerDay] = useState(20);
  const [from, setFrom] = useState(9);
  const [to, setTo] = useState(12);
  const [weekdays, setWeekdays] = useState<number[]>([1, 2, 3, 4]);
  const [minIdleDays, setMinIdleDays] = useState(7);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [uploading, setUploading] = useState(false);
  const [preview, setPreview] = useState<Preview[] | null>(null);

  const product = products.data?.find((p) => p.productKey === productKey);

  // Al elegir la app: su línea, un nombre y un texto de base con variantes.
  useEffect(() => {
    if (!product) return;
    const line = lines.data?.find((l) => l.productKey === productKey) ?? lines.data?.[0];
    if (line) setInstanceName(line.instanceName);
    const app = product.displayName.toLowerCase();
    setName(`${product.displayName} · escribieron primero · ${new Date().toLocaleDateString('es-AR')}`);
    setBefore(`{hola|buenas|que tal} {nombre}, {como estas?|como va?|todo bien?} soy mateo de ${app}\n\n{hace un tiempo|hace unos meses} nos escribiste por ${app}, {te paso|te muestro} como es arrancar`);
    setMediaId('');
    setPreview(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [productKey, lines.data]);

  const media = useQuery({
    queryKey: ['product-media', productKey],
    enabled: !!productKey,
    queryFn: async () => (await api.get<Media[]>(`/products/${productKey}/media`)).data,
  });
  const videos = (media.data ?? []).filter((m) => m.mimeType.startsWith('video/') || m.mimeType.startsWith('image/'));

  const audience = useQuery({
    queryKey: ['video-campaign-audience', productKey, minIdleDays],
    enabled: !!productKey,
    queryFn: async () => (await api.get<AudienceItem[]>('/video-campaigns/audience', { params: { productKey, minIdleDays } })).data,
  });
  useEffect(() => {
    setSelected(new Set((audience.data ?? []).filter((a) => a.suggested).map((a) => a.leadId)));
  }, [audience.data]);

  const selectedNames = useMemo(
    () => (audience.data ?? []).filter((a) => selected.has(a.leadId)).slice(0, 3).map((a) => a.name),
    [audience.data, selected]);

  async function upload(f?: File) {
    if (!f || !productKey) return;
    if (f.size > 16 * MB) { toast.error('WhatsApp acepta hasta 16 MB'); return; }
    setUploading(true);
    try {
      const fd = new FormData();
      fd.append('file', f);
      const m = (await api.post<Media>(`/products/${productKey}/media`, fd)).data;
      await qc.invalidateQueries({ queryKey: ['product-media', productKey] });
      setMediaId(m.id);
    } catch (e: any) {
      toast.error(e.response?.data?.error ?? 'No se pudo subir');
    } finally {
      setUploading(false);
    }
  }

  async function showPreview() {
    try {
      setPreview((await api.post<Preview[]>('/video-campaigns/preview', {
        messageBefore: before, messageAfter: after, names: selectedNames.length ? selectedNames : [null],
      })).data);
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'No se pudo'); }
  }

  const save = useMutation({
    mutationFn: async () => (await api.post<{ id: string; recipients: number }>('/video-campaigns', {
      name, productKey, instanceName, messageBefore: before, mediaAssetId: mediaId, messageAfter: after,
      perDay, windowFromHour: from, windowToHour: to, weekdays, leadIds: [...selected],
    })).data,
    onSuccess: (r) => {
      toast.success(`Campaña guardada con ${r.recipients} leads. Mandate una prueba y despues dale "Empezar a mandar".`);
      qc.invalidateQueries({ queryKey: ['video-campaigns'] });
      onDone();
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'No se pudo guardar'),
  });

  const days = selected.size ? Math.ceil(selected.size / Math.max(1, perDay)) : 0;
  const toggle = (id: string) => setSelected((s) => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n; });

  return (
    <div className="card p-4 space-y-4">
      <div className="flex items-center justify-between">
        <h3 className="font-semibold">Nueva campaña</h3>
        <button className="text-sm text-slate-500 underline" onClick={onDone}>Cancelar</button>
      </div>

      <div className="grid sm:grid-cols-3 gap-3">
        <label className="text-sm block">
          <div className="text-slate-500 mb-1">App</div>
          <select className="input w-full" value={productKey} onChange={(e) => setProductKey(e.target.value)}>
            <option value="">Elegi…</option>
            {(products.data ?? []).filter((p) => p.displayName).map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
          </select>
        </label>
        <label className="text-sm block">
          <div className="text-slate-500 mb-1">Sale por la linea</div>
          <select className="input w-full" value={instanceName} onChange={(e) => setInstanceName(e.target.value)}>
            {(lines.data ?? []).map((l) => <option key={l.instanceName} value={l.instanceName}>{l.label}{l.productKey ? ` (${l.productKey})` : ''}</option>)}
          </select>
        </label>
        <label className="text-sm block">
          <div className="text-slate-500 mb-1">Nombre de la campaña</div>
          <input className="input w-full" value={name} onChange={(e) => setName(e.target.value)} />
        </label>
      </div>

      {productKey && (
        <>
          <div className="grid lg:grid-cols-2 gap-4">
            <div className="space-y-3">
              <label className="text-sm block">
                <div className="text-slate-500 mb-1">Mensaje antes del video</div>
                <textarea className="input w-full min-h-[110px] font-mono text-[13px]" value={before} onChange={(e) => { setBefore(e.target.value); setPreview(null); }} />
              </label>
              <label className="text-sm block">
                <div className="text-slate-500 mb-1">Mensaje despues del video (opcional)</div>
                <textarea className="input w-full min-h-[60px] font-mono text-[13px]" value={after} onChange={(e) => { setAfter(e.target.value); setPreview(null); }} />
              </label>
              <div className="text-xs text-slate-500 space-y-0.5">
                <div><code>{'{nombre}'}</code> = nombre de pila del lead (si no tiene uno usable, se saca).</div>
                <div><code>{'{hola|buenas|que tal}'}</code> = a cada lead le toca una variante al azar, asi no le llega lo mismo a todos.</div>
                <div>Una linea en blanco = otro mensaje. Las tildes, los ¿ y los emojis se sacan solos.</div>
              </div>
              <button className="btn-secondary text-sm" onClick={showPreview} disabled={!before.trim()}>Ver como queda</button>
              {preview && (
                <div className="space-y-2">
                  {preview.map((p, i) => (
                    <div key={i} className="rounded bg-[#efeae2] p-2 space-y-1">
                      <div className="text-[11px] text-slate-500">{p.name ?? 'sin nombre'}</div>
                      {p.before.map((b, j) => <div key={j} className="bg-[#d9fdd3] rounded px-2 py-1 text-sm w-fit max-w-[90%] ml-auto">{b}</div>)}
                      <div className="bg-[#d9fdd3] rounded px-2 py-1 text-sm w-fit ml-auto text-slate-500">[video]</div>
                      {p.after.map((b, j) => <div key={j} className="bg-[#d9fdd3] rounded px-2 py-1 text-sm w-fit max-w-[90%] ml-auto">{b}</div>)}
                    </div>
                  ))}
                </div>
              )}
            </div>

            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <div className="text-sm text-slate-500">Video</div>
                <input ref={fileRef} type="file" accept="video/*,image/*" className="hidden" onChange={(e) => { upload(e.target.files?.[0]); e.target.value = ''; }} />
                <button className="btn-secondary text-sm" disabled={uploading} onClick={() => fileRef.current?.click()}>
                  {uploading ? 'Subiendo…' : 'Subir video'}
                </button>
              </div>
              {videos.length === 0 && !media.isLoading && <div className="text-xs text-slate-500">Todavia no hay videos de esta app. Subi uno (hasta 16 MB).</div>}
              <div className="grid grid-cols-2 sm:grid-cols-3 gap-2 max-h-[420px] overflow-y-auto">
                {videos.map((v) => (
                  <button key={v.id} type="button" onClick={() => setMediaId(v.id)}
                    className={clsx('rounded border p-1 text-left', mediaId === v.id ? 'border-brand-600 ring-2 ring-brand-200' : 'border-slate-200')}>
                    {v.mimeType.startsWith('video/')
                      ? <video src={`${api.defaults.baseURL}/media/${v.id}`} className="w-full aspect-[9/16] object-cover rounded bg-black" controls preload="metadata" />
                      : <img src={`${api.defaults.baseURL}/media/${v.id}`} className="w-full aspect-[9/16] object-cover rounded" />}
                    <div className="text-[11px] truncate mt-1">{v.fileName}</div>
                    <div className={clsx('text-[10px]', v.sizeBytes > 16 * MB ? 'text-rose-600' : 'text-slate-400')}>
                      {(v.sizeBytes / MB).toFixed(1)} MB{v.sizeBytes > 16 * MB ? ' · muy pesado para WhatsApp' : ''}
                    </div>
                  </button>
                ))}
              </div>
            </div>
          </div>

          <div className="flex flex-wrap items-end gap-3 text-sm">
            <label><div className="text-slate-500 mb-1">Por dia</div>
              <input type="number" min={1} max={40} className="input w-20" value={perDay} onChange={(e) => setPerDay(Number(e.target.value))} /></label>
            <label><div className="text-slate-500 mb-1">Desde (h)</div>
              <input type="number" min={7} max={21} className="input w-20" value={from} onChange={(e) => setFrom(Number(e.target.value))} /></label>
            <label><div className="text-slate-500 mb-1">Hasta (h)</div>
              <input type="number" min={8} max={22} className="input w-20" value={to} onChange={(e) => setTo(Number(e.target.value))} /></label>
            <div>
              <div className="text-slate-500 mb-1">Dias</div>
              <div className="flex gap-1">
                {DAYS.map((d, i) => (
                  <button key={d} type="button"
                    onClick={() => setWeekdays((w) => w.includes(i + 1) ? w.filter((x) => x !== i + 1) : [...w, i + 1].sort())}
                    className={clsx('px-2 py-1 rounded border text-xs', weekdays.includes(i + 1) ? 'bg-slate-800 text-white border-slate-800' : 'bg-white border-slate-200')}>
                    {d}
                  </button>
                ))}
              </div>
            </div>
            <div className="text-xs text-slate-500">Hora Argentina. 9 a 12 de lunes a jueves es la franja que mas contesta.</div>
          </div>

          <div className="space-y-2">
            <div className="flex flex-wrap items-center gap-3 text-sm">
              <div className="font-medium">A quien le llega</div>
              <label className="text-xs text-slate-500 flex items-center gap-1">
                quietos hace mas de
                <input type="number" min={0} className="input w-16 text-xs py-0.5" value={minIdleDays} onChange={(e) => setMinIdleDays(Number(e.target.value))} />
                dias
              </label>
              <span className="text-xs text-slate-500">
                {selected.size} de {audience.data?.length ?? 0} elegidos{days ? ` · ${days} dia${days > 1 ? 's' : ''} de envio` : ''}
              </span>
              <button className="text-xs underline text-slate-600" onClick={() => setSelected(new Set((audience.data ?? []).map((a) => a.leadId)))}>todos</button>
              <button className="text-xs underline text-slate-600" onClick={() => setSelected(new Set())}>ninguno</button>
            </div>
            <div className="text-xs text-slate-500">
              Nos escribieron ellos primero (anuncio o consulta) y despues no contestaron nada. Los que parecen basura vienen destildados.
            </div>
            {audience.isLoading && <div className="text-sm text-slate-500">Buscando…</div>}
            <div className="max-h-96 overflow-y-auto border border-slate-100 rounded">
              <table className="w-full text-sm">
                <tbody>
                  {(audience.data ?? []).map((a) => (
                    <tr key={a.leadId} className={clsx('border-b border-slate-50 cursor-pointer hover:bg-slate-50', !selected.has(a.leadId) && 'text-slate-400')} onClick={() => toggle(a.leadId)}>
                      <td className="p-2 w-6"><input type="checkbox" checked={selected.has(a.leadId)} readOnly /></td>
                      <td className="p-2 whitespace-nowrap">{a.name}</td>
                      <td className="p-2 text-xs tabular-nums whitespace-nowrap">{a.phone}</td>
                      <td className="p-2 text-xs">
                        "{a.firstMessage}"
                        {a.whyNot && <span className="ml-1 text-[10px] bg-rose-50 text-rose-600 border border-rose-200 rounded px-1">{a.whyNot}</span>}
                      </td>
                      <td className="p-2 text-xs whitespace-nowrap">{fmtDate(a.lastActivity)}</td>
                      <td className="p-2 text-xs whitespace-nowrap" title="Mensajes nuestros que ya recibio">{a.ourMessages} msj</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>

          <div className="flex justify-end gap-2">
            <button className="btn-primary" disabled={save.isPending || !mediaId || !before.trim() || !instanceName || selected.size === 0 || !name.trim()}
              onClick={() => save.mutate()}>
              {save.isPending ? 'Guardando…' : `Guardar campaña (${selected.size} leads)`}
            </button>
          </div>
        </>
      )}
    </div>
  );
}
