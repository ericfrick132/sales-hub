import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';

// ── Tipos (espejo del backend) ───────────────────────────────────────────────
interface PostingProfile {
  id: string; productKey: string; enabled: boolean;
  brandColorsJson: string; brandFonts: string; brandVoice: string;
  brandGuidelines: string; targetAudience: string; contentPillars: string[];
  postHours: number[]; postDays: number[]; postsPerDay: number;
}
interface PostingChannel {
  id: string; productKey: string; platform: string; enabled: boolean;
  bufferChannelId: string; format: string; assetKind: string;
  distribution: string; warmrAccount: string; promptTemplate: string;
}
interface SocialPost {
  id: string; productKey: string; platform: string; format: string; assetKind: string;
  status: string; contentPillar: string; concept: string; caption: string;
  hashtags: string[]; assetUrl?: string; thumbnailUrl?: string; target?: string;
  bufferChannelId: string; error?: string;
}

const PLATFORMS = ['Instagram', 'TikTok', 'YouTube', 'Facebook', 'Twitter', 'LinkedIn'];
const FORMATS = ['Story', 'Reel', 'Post', 'Carousel', 'Video'];

// Red del posteo → "service" del canal en Buffer (para filtrar el dropdown de cuentas).
const SERVICE_BY_PLATFORM: Record<string, string> = {
  Instagram: 'instagram', TikTok: 'tiktok', Facebook: 'facebook',
  Twitter: 'twitter', YouTube: 'youtube', LinkedIn: 'linkedin',
};
type BufferChannel = { id: string; name: string; service: string };

function colorOf(json: string, key: string, def: string) {
  try { return JSON.parse(json)[key] ?? def; } catch { return def; }
}

const isVideoAsset = (p: { assetKind: string; assetUrl?: string }) =>
  p.assetKind === 'Video' || (p.assetUrl?.toLowerCase().endsWith('.mp4') ?? false);
const isWarmr = (p: { target?: string }) => p.target === 'Warmr';

export default function Posteos() {
  const qc = useQueryClient();
  const [selected, setSelected] = useState<string | null>(null);
  const [preview, setPreview] = useState<SocialPost | null>(null);
  const [busyAsset, setBusyAsset] = useState<string | null>(null);

  const profilesQ = useQuery({
    queryKey: ['posteos-profiles'],
    queryFn: async () => (await api.get<PostingProfile[]>('/posteos/profiles')).data,
  });

  const profiles = profilesQ.data ?? [];
  const productKey = selected ?? profiles[0]?.productKey ?? null;
  const profile = profiles.find((p) => p.productKey === productKey) ?? null;

  const channelsQ = useQuery({
    queryKey: ['posteos-channels', productKey],
    queryFn: async () => (await api.get<PostingChannel[]>(`/posteos/posting-channels?productKey=${productKey}`)).data,
    enabled: !!productKey,
  });

  const postsQ = useQuery({
    queryKey: ['posteos-posts', productKey],
    queryFn: async () => (await api.get<SocialPost[]>(`/posteos?productKey=${productKey}&take=30`)).data,
    enabled: !!productKey,
  });

  const bufferQ = useQuery({
    queryKey: ['posteos-buffer-channels'],
    queryFn: async () => (await api.get<{ id: string; name: string; service: string }[]>('/posteos/channels')).data,
    retry: false,
  });

  async function saveProfile(pk: string, body: Partial<PostingProfile>) {
    try {
      await api.put(`/posteos/profiles/${pk}`, body);
      toast.success('Frecuencia guardada');
      qc.invalidateQueries({ queryKey: ['posteos-profiles'] });
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló'); }
  }

  async function saveChannel(c: PostingChannel) {
    try {
      await api.put(`/posteos/posting-channels/${c.id}`, {
        enabled: c.enabled, bufferChannelId: c.bufferChannelId,
        format: c.format, assetKind: c.assetKind, promptTemplate: c.promptTemplate,
        distribution: c.distribution, warmrAccount: c.warmrAccount,
      });
      toast.success('Canal guardado');
      qc.invalidateQueries({ queryKey: ['posteos-channels', productKey] });
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló'); }
  }

  async function addChannel(platform: string) {
    try {
      await api.post('/posteos/posting-channels', { productKey, platform, format: 'Post', assetKind: 'Image' });
      qc.invalidateQueries({ queryKey: ['posteos-channels', productKey] });
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló'); }
  }

  async function generate(channelId: string) {
    const t = toast.loading('Generando con IA…');
    try {
      await api.post(`/posteos/posting-channels/${channelId}/generate`);
      toast.success('Posteo generado', { id: t });
      qc.invalidateQueries({ queryKey: ['posteos-posts', productKey] });
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló', { id: t }); }
  }

  // Genera el asset con IA (imagen o video; lo guarda en la DB y deja el assetUrl listo). Devuelve el post actualizado.
  async function generateAsset(p: SocialPost): Promise<SocialPost | null> {
    setBusyAsset(p.id);
    const kind = p.assetKind === 'Video' ? 'video' : 'imagen';
    const t = toast.loading(`Generando ${kind} con IA…${p.assetKind === 'Video' ? ' (puede tardar un minuto)' : ''}`);
    try {
      const { data } = await api.post<SocialPost>(`/posteos/${p.id}/generate-asset`);
      toast.success(`${kind === 'video' ? 'Video' : 'Imagen'} generado`, { id: t });
      qc.invalidateQueries({ queryKey: ['posteos-posts', productKey] });
      return data;
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló', { id: t }); return null; }
    finally { setBusyAsset(null); }
  }

  // Genera y abre el modal de preview (cumple "previa preview" antes de subir).
  async function generateAndPreview(p: SocialPost) {
    const updated = await generateAsset(p);
    if (updated?.assetUrl) setPreview(updated);
  }

  async function pushPost(p: SocialPost) {
    if (!p.assetUrl) { toast.error('Generá el asset primero'); return; }
    const t = toast.loading('Subiendo a Buffer (draft)…');
    try {
      await api.post(`/posteos/${p.id}/push`, { assetUrl: p.assetUrl });
      toast.success('Subido a Buffer como draft', { id: t });
      setPreview(null);
      qc.invalidateQueries({ queryKey: ['posteos-posts', productKey] });
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló', { id: t }); }
  }

  // Warmr no tiene API → manda el posteo a la cola de handoff (subida manual a Cloud Drop).
  async function dispatchWarmr(p: SocialPost) {
    if (!p.assetUrl) { toast.error('Generá el asset primero'); return; }
    const t = toast.loading('Enviando a la cola de Warmr…');
    try {
      await api.post(`/posteos/${p.id}/dispatch-warmr`, {});
      toast.success('En la cola de Warmr — subí el clip a Cloud Drop', { id: t });
      setPreview(null);
      qc.invalidateQueries({ queryKey: ['posteos-posts', productKey] });
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló', { id: t }); }
  }

  // Distribuye según el destino del posteo (Warmr o Buffer).
  const distribute = (p: SocialPost) => (isWarmr(p) ? dispatchWarmr(p) : pushPost(p));

  async function rejectPost(id: string) {
    try { await api.post(`/posteos/${id}/reject`); qc.invalidateQueries({ queryKey: ['posteos-posts', productKey] }); }
    catch { toast.error('Falló'); }
  }
  async function deletePost(id: string) {
    if (!window.confirm('¿Borrar este posteo?')) return;
    try { await api.delete(`/posteos/${id}`); qc.invalidateQueries({ queryKey: ['posteos-posts', productKey] }); }
    catch { toast.error('Falló'); }
  }

  const channels = channelsQ.data ?? [];
  const usedPlatforms = new Set(channels.map((c) => c.platform));
  const posts = postsQ.data ?? [];

  return (
    <div className="grid grid-cols-1 md:grid-cols-12 gap-4 md:gap-6">
      {/* Apps */}
      <div className="md:col-span-3">
        <h2 className="text-xl font-bold mb-2">Posteos</h2>
        <p className="text-xs text-slate-500 mb-3">Contenido automático por app y por red. Cada red con su formato y su prompt propio.</p>
        <div className="card divide-y divide-slate-100">
          {profiles.map((p) => (
            <button key={p.id} onClick={() => setSelected(p.productKey)}
              className={`w-full text-left p-3 hover:bg-slate-50 flex items-center gap-2 ${productKey === p.productKey ? 'bg-brand-50' : ''}`}>
              <span className="w-3 h-3 rounded-full border" style={{ background: colorOf(p.brandColorsJson, 'primary', '#ccc') }} />
              <div>
                <div className="font-medium capitalize">{p.productKey}</div>
                <div className="text-xs text-slate-500">{p.enabled ? 'activo' : 'pausado'}</div>
              </div>
            </button>
          ))}
        </div>
      </div>

      {/* Detalle */}
      <div className="md:col-span-9 space-y-4">
        {!profile ? <div className="card p-6 text-slate-500">Elegí una app.</div> : (
          <>
            {/* Marca */}
            <div className="card p-4">
              <div className="flex items-center justify-between">
                <h3 className="font-semibold capitalize">{profile.productKey} — marca</h3>
                <div className="flex gap-1">
                  {['primary', 'background', 'accent'].map((k) => (
                    <span key={k} className="w-6 h-6 rounded border" title={k}
                      style={{ background: colorOf(profile.brandColorsJson, k, '#fff') }} />
                  ))}
                </div>
              </div>
              <div className="text-xs text-slate-600 mt-2"><b>Audiencia:</b> {profile.targetAudience}</div>
              <div className="text-xs text-slate-600 mt-1"><b>Tono:</b> {profile.brandVoice}</div>
              <div className="flex flex-wrap gap-1 mt-2">
                {profile.contentPillars.map((p, i) => (
                  <span key={i} className="text-[11px] bg-slate-100 rounded px-2 py-0.5">{p}</span>
                ))}
              </div>
              {bufferQ.isError && (
                <div className="text-[11px] text-amber-600 mt-2">⚠️ No pude leer tus canales de Buffer (revisá el token). Podés pegar el channelId a mano abajo.</div>
              )}
            </div>

            {/* Frecuencia / horarios */}
            <CadenceEditor profile={profile} onSave={(b) => saveProfile(profile.productKey, b)} />

            {/* Redes */}
            <div className="card p-4">
              <div className="flex items-center justify-between mb-1">
                <h3 className="font-semibold">Redes</h3>
                <div className="flex gap-1 flex-wrap">
                  {PLATFORMS.filter((p) => !usedPlatforms.has(p)).map((p) => (
                    <button key={p} className="btn-secondary text-[11px]" onClick={() => addChannel(p)}>+ {p}</button>
                  ))}
                </div>
              </div>
              <p className="text-xs text-slate-500 mb-3">
                Una red postea sola solo si: <b>la app está activa</b> (arriba) <b>+</b> la red está <b>Activa</b> <b>+</b> tiene una <b>cuenta destino</b> elegida.
              </p>
              <div className="space-y-3">
                {channels.map((c) => <ChannelRow key={c.id} channel={c} bufferChannels={bufferQ.data} onSave={saveChannel} onGenerate={() => generate(c.id)} />)}
                {channels.length === 0 && <div className="text-sm text-slate-500">Sin redes. Agregá una arriba.</div>}
              </div>
            </div>

            {/* Posteos generados */}
            <div className="card p-4">
              <h3 className="font-semibold mb-3">Posteos generados ({posts.length})</h3>
              <div className="space-y-2">
                {posts.map((p) => (
                  <div key={p.id} className="border rounded-lg p-3 text-sm">
                    <div className="flex items-center gap-2 flex-wrap">
                      <span className="text-[11px] font-mono bg-slate-100 rounded px-1.5 py-0.5">{p.platform} · {p.format} · {p.assetKind}</span>
                      <StatusChip status={p.status} />
                      <span className={`text-[11px] rounded px-1.5 py-0.5 ${isWarmr(p) ? 'bg-violet-100 text-violet-700' : 'bg-slate-100 text-slate-500'}`}>{isWarmr(p) ? '→ Warmr' : '→ Buffer'}</span>
                      {p.contentPillar && <span className="text-[11px] text-slate-500">{p.contentPillar}</span>}
                    </div>
                    <div className="font-medium mt-1">{p.concept}</div>
                    <div className="text-slate-600 whitespace-pre-wrap mt-1">{p.caption}</div>
                    {p.hashtags?.length > 0 && <div className="text-[11px] text-sky-600 mt-1">{p.hashtags.map((h) => (h.startsWith('#') ? h : '#' + h)).join(' ')}</div>}
                    {p.error && <div className="text-[11px] text-red-600 mt-1">{p.error}</div>}
                    {p.assetUrl && (
                      isVideoAsset(p)
                        ? <video src={p.assetUrl} controls className="mt-2 w-40 rounded-lg border" />
                        : <button type="button" onClick={() => setPreview(p)} className="mt-2 block" title="Ver imagen">
                            <img src={p.assetUrl} alt="preview" className="w-28 h-28 object-cover rounded-lg border hover:opacity-90" />
                          </button>
                    )}
                    <div className="flex gap-2 mt-2 flex-wrap items-center">
                      <button className="btn-secondary text-[11px]" disabled={busyAsset === p.id}
                        onClick={() => generateAndPreview(p)}>
                        {busyAsset === p.id ? 'Generando…' : p.assetUrl ? `Regenerar ${p.assetKind === 'Video' ? 'video' : 'imagen'}` : `Generar ${p.assetKind === 'Video' ? 'video' : 'imagen'}`}
                      </button>
                      <button className="btn-primary text-[11px] disabled:opacity-40 disabled:cursor-not-allowed"
                        disabled={!p.assetUrl} title={!p.assetUrl ? 'Generá el asset primero' : ''}
                        onClick={() => distribute(p)}>{isWarmr(p) ? 'Enviar a cola Warmr' : 'Subir a Buffer (draft)'}</button>
                      <button className="btn-secondary text-[11px]" onClick={() => rejectPost(p.id)}>Rechazar</button>
                      <button className="text-[11px] text-red-600" onClick={() => deletePost(p.id)}>Borrar</button>
                    </div>
                  </div>
                ))}
                {posts.length === 0 && <div className="text-sm text-slate-500">Todavía no generaste posteos. Usá "Generar" en una red.</div>}
              </div>
            </div>
          </>
        )}
      </div>

      {preview && (
        <PreviewModal
          post={preview}
          busy={busyAsset === preview.id}
          onClose={() => setPreview(null)}
          onDistribute={() => distribute(preview)}
          onRegenerate={() => generateAndPreview(preview)}
        />
      )}
    </div>
  );
}

function PreviewModal({ post, busy, onClose, onDistribute, onRegenerate }: {
  post: SocialPost; busy: boolean; onClose: () => void; onDistribute: () => void; onRegenerate: () => void;
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4" onClick={onClose}>
      <div className="bg-white rounded-2xl max-w-md w-full p-4 shadow-xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between mb-2">
          <h3 className="font-semibold">Vista previa</h3>
          <button className="text-slate-400 hover:text-slate-600 text-xl leading-none" onClick={onClose}>×</button>
        </div>
        {post.assetUrl
          ? (isVideoAsset(post)
              ? <video src={post.assetUrl} controls className="w-full rounded-lg border" />
              : <img src={post.assetUrl} alt="preview" className="w-full rounded-lg border" />)
          : <div className="text-sm text-slate-500 py-10 text-center">Sin asset.</div>}
        <div className="text-xs text-slate-600 mt-2 whitespace-pre-wrap">{post.caption}</div>
        <div className="flex gap-2 mt-3">
          <button className="btn-primary text-sm flex-1 disabled:opacity-40" disabled={!post.assetUrl || busy} onClick={onDistribute}>
            {isWarmr(post) ? 'Enviar a cola Warmr' : 'Subir a Buffer (draft)'}
          </button>
          <button className="btn-secondary text-sm disabled:opacity-40" disabled={busy} onClick={onRegenerate}>
            {busy ? 'Generando…' : 'Regenerar'}
          </button>
        </div>
      </div>
    </div>
  );
}

const DOW = ['Dom', 'Lun', 'Mar', 'Mié', 'Jue', 'Vie', 'Sáb']; // índice = DayOfWeek (0=Domingo)

function CadenceEditor({ profile, onSave }: { profile: PostingProfile; onSave: (b: Partial<PostingProfile>) => void }) {
  const [enabled, setEnabled] = useState(profile.enabled);
  const [hours, setHours] = useState((profile.postHours ?? []).join(', '));
  const [days, setDays] = useState<number[]>(profile.postDays ?? []);
  const [perDay, setPerDay] = useState(profile.postsPerDay ?? 1);

  const toggleDay = (d: number) => setDays((xs) => xs.includes(d) ? xs.filter((x) => x !== d) : [...xs, d].sort());

  function save() {
    const postHours = hours.split(',').map((s) => parseInt(s.trim(), 10)).filter((n) => !isNaN(n) && n >= 0 && n <= 23);
    onSave({ enabled, postHours, postDays: days, postsPerDay: Math.max(1, Number(perDay) || 1) });
  }
  return (
    <div className="card p-4">
      <h3 className="font-semibold mb-1">Frecuencia / horarios</h3>
      <p className="text-xs text-slate-500 mb-3">Cada cuánto postea esta app. El worker revisa cada hora: en los días y horarios elegidos genera <b>{perDay || 1}</b> posteo(s) por red activa.</p>
      <div className="mb-3">
        <div className="text-xs text-slate-500 mb-1">Días (vacío = todos los días)</div>
        <div className="flex gap-1 flex-wrap">
          {DOW.map((label, d) => (
            <button key={d} type="button" onClick={() => toggleDay(d)}
              className={`text-xs rounded px-2.5 py-1 border ${days.includes(d) ? 'bg-slate-800 text-white border-slate-800' : 'bg-white text-slate-600'}`}>
              {label}
            </button>
          ))}
        </div>
      </div>
      <div className="flex items-end gap-3 flex-wrap">
        <label className="flex items-center gap-1 text-sm">
          <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Posteo automático activo
        </label>
        <div>
          <div className="text-xs text-slate-500 mb-0.5">Horarios (0-23, coma)</div>
          <input className="input w-40" value={hours} onChange={(e) => setHours(e.target.value)} placeholder="10, 18" />
        </div>
        <div>
          <div className="text-xs text-slate-500 mb-0.5">Posteos por horario</div>
          <input className="input w-24" type="number" min={1} value={perDay} onChange={(e) => setPerDay(Number(e.target.value))} />
        </div>
        <button className="btn-primary text-sm" onClick={save}>Guardar</button>
      </div>
      {!enabled && <div className="text-[11px] text-amber-600 mt-2">Está pausado: no postea solo. Igual podés "Generar ahora" a mano por red.</div>}
    </div>
  );
}

function ChannelRow({ channel, bufferChannels, onSave, onGenerate }:
  { channel: PostingChannel; bufferChannels?: BufferChannel[]; onSave: (c: PostingChannel) => void; onGenerate: () => void }) {
  const [c, setC] = useState(channel);
  const [showPrompt, setShowPrompt] = useState(false);
  const set = <K extends keyof PostingChannel>(k: K, v: PostingChannel[K]) => setC((x) => ({ ...x, [k]: v }));

  const isWarmrDist = c.distribution === 'Warmr';
  // Cuentas de Buffer que matchean esta red (un canal de IG solo puede ir a una cuenta de IG).
  const svc = SERVICE_BY_PLATFORM[c.platform];
  const matching = (bufferChannels ?? []).filter((b) => !svc || b.service === svc);
  const dest = bufferChannels?.find((b) => b.id === c.bufferChannelId);
  const hasDest = isWarmrDist ? !!c.warmrAccount.trim() : !!c.bufferChannelId.trim();

  // Estado claro de la red.
  const state = !c.enabled
    ? { label: 'Pausada', cls: 'bg-slate-100 text-slate-500' }
    : !hasDest
      ? { label: 'Falta destino', cls: 'bg-amber-100 text-amber-700' }
      : { label: 'Lista', cls: 'bg-emerald-100 text-emerald-700' };

  return (
    <div className="border rounded-lg p-3">
      {/* Línea 1: red + Activa + estado + a dónde va */}
      <div className="flex items-center gap-3 flex-wrap">
        <span className="font-medium w-20">{c.platform}</span>
        <label className="flex items-center gap-1 text-xs">
          <input type="checkbox" checked={c.enabled} onChange={(e) => set('enabled', e.target.checked)} /> Activa
        </label>
        <span className={`text-[11px] rounded px-1.5 py-0.5 ${state.cls}`}>{state.label}</span>
        <span className="text-[11px] text-slate-400 ml-auto">
          → {isWarmrDist ? `Warmr ${c.warmrAccount || '(sin cuenta)'}` : (dest ? `${dest.name}` : 'Buffer (sin cuenta)')}
        </span>
      </div>

      {/* Línea 2: formato · asset · distribución · cuenta destino */}
      <div className="flex items-center gap-2 flex-wrap mt-2">
        <select className="input w-28" value={c.format} onChange={(e) => set('format', e.target.value)} title="Formato">
          {FORMATS.map((f) => <option key={f} value={f}>{f}</option>)}
        </select>
        <select className="input w-28" value={c.assetKind} onChange={(e) => set('assetKind', e.target.value)} title="Imagen o video">
          <option value="Image">Imagen</option>
          <option value="Video">Video</option>
        </select>
        <select className="input w-36" value={c.distribution} onChange={(e) => set('distribution', e.target.value)} title="Por dónde se distribuye">
          <option value="Buffer">→ Buffer (API)</option>
          <option value="Warmr">→ Warmr (device)</option>
        </select>
        {isWarmrDist ? (
          <input className="input flex-1 min-w-[160px]" placeholder="Cuenta Warmr (@handle)" value={c.warmrAccount}
            onChange={(e) => set('warmrAccount', e.target.value)} />
        ) : matching.length > 0 ? (
          <select className="input flex-1 min-w-[180px]" value={c.bufferChannelId} onChange={(e) => set('bufferChannelId', e.target.value)} title="Cuenta de Buffer destino">
            <option value="">— elegí cuenta —</option>
            {matching.map((b) => <option key={b.id} value={b.id}>{b.name} · {b.service}</option>)}
          </select>
        ) : (
          <input className="input flex-1 min-w-[160px]" placeholder={`Sin cuenta de ${c.platform} en Buffer — pegá el channelId`}
            value={c.bufferChannelId} onChange={(e) => set('bufferChannelId', e.target.value)} />
        )}
      </div>

      {/* Prompt propio (colapsable, no más muro de texto) */}
      <button type="button" onClick={() => setShowPrompt((s) => !s)} className="text-xs text-slate-500 hover:text-slate-700 mt-2">
        {showPrompt ? '▾' : '▸'} Prompt propio de esta red
      </button>
      {showPrompt && (
        <textarea className="input w-full mt-1 text-xs font-mono" rows={5} placeholder="Instrucciones propias de esta red para esta app…"
          value={c.promptTemplate} onChange={(e) => set('promptTemplate', e.target.value)} />
      )}

      <div className="flex gap-2 mt-2">
        <button className="btn-primary text-xs" onClick={() => onSave(c)}>Guardar</button>
        <button className="btn-secondary text-xs" onClick={onGenerate}>Generar ahora</button>
      </div>
    </div>
  );
}

function StatusChip({ status }: { status: string }) {
  const map: Record<string, string> = {
    Idea: 'bg-slate-100 text-slate-600', DraftReady: 'bg-sky-100 text-sky-700',
    GeneratingAsset: 'bg-amber-100 text-amber-700', PushedToBuffer: 'bg-emerald-100 text-emerald-700',
    Scheduled: 'bg-emerald-100 text-emerald-700', Posted: 'bg-emerald-200 text-emerald-800',
    ReadyForWarmr: 'bg-violet-100 text-violet-700', WarmrUploaded: 'bg-emerald-200 text-emerald-800',
    Rejected: 'bg-slate-200 text-slate-500', Error: 'bg-red-100 text-red-700',
  };
  return <span className={`text-[11px] rounded px-1.5 py-0.5 ${map[status] ?? 'bg-slate-100'}`}>{status}</span>;
}
