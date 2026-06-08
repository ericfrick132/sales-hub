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
  bufferChannelId: string; format: string; assetKind: string; promptTemplate: string;
}
interface SocialPost {
  id: string; productKey: string; platform: string; format: string; assetKind: string;
  status: string; contentPillar: string; concept: string; caption: string;
  hashtags: string[]; assetUrl?: string; bufferChannelId: string; error?: string;
}

const PLATFORMS = ['Instagram', 'TikTok', 'YouTube', 'Facebook', 'Twitter', 'LinkedIn'];
const FORMATS = ['Story', 'Reel', 'Post', 'Carousel', 'Video'];

function colorOf(json: string, key: string, def: string) {
  try { return JSON.parse(json)[key] ?? def; } catch { return def; }
}

export default function Posteos() {
  const qc = useQueryClient();
  const [selected, setSelected] = useState<string | null>(null);

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

  async function pushPost(p: SocialPost) {
    const assetUrl = p.assetUrl || window.prompt('URL pública del asset (imagen/video) — ej. export de Canva:') || '';
    if (!assetUrl) return;
    const t = toast.loading('Subiendo a Buffer (draft)…');
    try {
      await api.post(`/posteos/${p.id}/push`, { assetUrl });
      toast.success('Subido a Buffer como draft', { id: t });
      qc.invalidateQueries({ queryKey: ['posteos-posts', productKey] });
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló', { id: t }); }
  }

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
                <div className="text-[11px] text-amber-600 mt-2">⚠️ No pude leer los canales de Buffer (revisá el token). Igual podés pegar el channelId a mano.</div>
              )}
              {bufferQ.data && (
                <div className="text-[11px] text-slate-500 mt-2">Canales Buffer: {bufferQ.data.map((c) => `${c.service}:${c.id.slice(-6)}`).join(' · ') || '—'}</div>
              )}
            </div>

            {/* Frecuencia / horarios */}
            <CadenceEditor profile={profile} onSave={(b) => saveProfile(profile.productKey, b)} />

            {/* Redes */}
            <div className="card p-4">
              <div className="flex items-center justify-between mb-3">
                <h3 className="font-semibold">Redes</h3>
                <div className="flex gap-1 flex-wrap">
                  {PLATFORMS.filter((p) => !usedPlatforms.has(p)).map((p) => (
                    <button key={p} className="btn-secondary text-[11px]" onClick={() => addChannel(p)}>+ {p}</button>
                  ))}
                </div>
              </div>
              <div className="space-y-3">
                {channels.map((c) => <ChannelRow key={c.id} channel={c} onSave={saveChannel} onGenerate={() => generate(c.id)} />)}
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
                      {p.contentPillar && <span className="text-[11px] text-slate-500">{p.contentPillar}</span>}
                    </div>
                    <div className="font-medium mt-1">{p.concept}</div>
                    <div className="text-slate-600 whitespace-pre-wrap mt-1">{p.caption}</div>
                    {p.hashtags?.length > 0 && <div className="text-[11px] text-sky-600 mt-1">{p.hashtags.map((h) => (h.startsWith('#') ? h : '#' + h)).join(' ')}</div>}
                    {p.error && <div className="text-[11px] text-red-600 mt-1">{p.error}</div>}
                    <div className="flex gap-2 mt-2">
                      <button className="btn-primary text-[11px]" onClick={() => pushPost(p)}>Subir a Buffer (draft)</button>
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

function ChannelRow({ channel, onSave, onGenerate }: { channel: PostingChannel; onSave: (c: PostingChannel) => void; onGenerate: () => void }) {
  const [c, setC] = useState(channel);
  const set = <K extends keyof PostingChannel>(k: K, v: PostingChannel[K]) => setC((x) => ({ ...x, [k]: v }));
  return (
    <div className="border rounded-lg p-3">
      <div className="flex items-center gap-3 flex-wrap">
        <span className="font-medium w-24">{c.platform}</span>
        <label className="flex items-center gap-1 text-xs">
          <input type="checkbox" checked={c.enabled} onChange={(e) => set('enabled', e.target.checked)} /> Activa
        </label>
        <select className="input w-32" value={c.format} onChange={(e) => set('format', e.target.value)}>
          {FORMATS.map((f) => <option key={f} value={f}>{f}</option>)}
        </select>
        <select className="input w-32" value={c.assetKind} onChange={(e) => set('assetKind', e.target.value)}>
          <option value="Image">Imagen</option>
          <option value="Video">Video</option>
        </select>
        <input className="input flex-1 min-w-[140px]" placeholder="Buffer channelId" value={c.bufferChannelId}
          onChange={(e) => set('bufferChannelId', e.target.value)} />
      </div>
      <textarea className="input w-full mt-2 text-xs font-mono" rows={4} placeholder="Prompt propio de esta red para esta app…"
        value={c.promptTemplate} onChange={(e) => set('promptTemplate', e.target.value)} />
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
    Rejected: 'bg-slate-200 text-slate-500', Error: 'bg-red-100 text-red-700',
  };
  return <span className={`text-[11px] rounded px-1.5 py-0.5 ${map[status] ?? 'bg-slate-100'}`}>{status}</span>;
}
