import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../lib/api';
import toast from 'react-hot-toast';

// ── Tipos (espejo del backend) ───────────────────────────────────────────────
type Competitor = { id: string; handle: string; platform: string; vertical?: string; lastScrapedAt?: string; isActive: boolean };
type InspoPost = {
  id: string; caption?: string; hashtags: string[];
  mediaUrl?: string; thumbnailUrl?: string; postUrl?: string;
  isVideo: boolean; likes: number; commentsCount: number; curated: boolean; curatedAt?: string;
};
type CuratedPost = InspoPost & { source: string; platform: string; vertical?: string };
type Profile = { id: string; productKey: string; brandColorsJson: string };
type Channel = { id: string; platform: string; format: string; assetKind: string; distribution: string };

export default function Inspiracion() {
  const qc = useQueryClient();
  const [tab, setTab] = useState<'cuenta' | 'curados'>('cuenta');
  const [selected, setSelected] = useState<Competitor | null>(null);
  const [productKey, setProductKey] = useState<string | null>(null);
  const [channelId, setChannelId] = useState<string>('');
  const [newHandle, setNewHandle] = useState('');
  const [newVertical, setNewVertical] = useState('');
  const [busy, setBusy] = useState<string | null>(null);

  const profilesQ = useQuery({ queryKey: ['posteos-profiles'], queryFn: async () => (await api.get<Profile[]>('/posteos/profiles')).data });
  const profiles = profilesQ.data ?? [];
  const pk = productKey ?? profiles[0]?.productKey ?? null;

  const channelsQ = useQuery({
    queryKey: ['insp-channels', pk],
    enabled: !!pk,
    queryFn: async () => (await api.get<Channel[]>(`/posteos/posting-channels?productKey=${pk}`)).data,
  });

  const competitorsQ = useQuery({ queryKey: ['competitors'], queryFn: async () => (await api.get<Competitor[]>('/competitors')).data });

  const postsQ = useQuery({
    queryKey: ['insp-posts', selected?.id],
    enabled: !!selected && tab === 'cuenta',
    queryFn: async () => (await api.get<InspoPost[]>(`/competitors/${selected!.id}/posts?limit=60`)).data,
  });

  const curatedQ = useQuery({
    queryKey: ['insp-curated'],
    enabled: tab === 'curados',
    queryFn: async () => (await api.get<CuratedPost[]>('/competitors/inspiration?limit=120')).data,
  });

  async function addCompetitor() {
    if (!newHandle) return;
    await api.post('/competitors', { handle: newHandle.replace(/^@/, ''), platform: 'instagram', vertical: newVertical || null });
    setNewHandle(''); setNewVertical('');
    qc.invalidateQueries({ queryKey: ['competitors'] });
  }

  async function scrape() {
    if (!selected) return;
    const t = toast.loading('Trayendo posteos…');
    try {
      const { data } = await api.post(`/competitors/${selected.id}/scrape`);
      toast.success(`${data.posts} posts nuevos`, { id: t });
      qc.invalidateQueries({ queryKey: ['insp-posts', selected.id] });
    } catch { toast.error('Falló el scrape', { id: t }); }
  }

  async function toggleCurate(p: InspoPost) {
    try {
      await api.post(`/competitors/posts/${p.id}/curate`, { curated: !p.curated });
      qc.invalidateQueries({ queryKey: ['insp-posts', selected?.id] });
      qc.invalidateQueries({ queryKey: ['insp-curated'] });
    } catch { toast.error('Falló'); }
  }

  async function generateInspired(p: InspoPost) {
    if (!pk) { toast.error('Elegí la app destino'); return; }
    setBusy(p.id);
    const t = toast.loading('Generando adaptación con IA…');
    try {
      await api.post('/posteos/generate-from-inspiration', {
        productKey: pk, competitorPostId: p.id, channelId: channelId || null,
      });
      toast.success('Posteo generado (lo ves en Posteos / Calendario)', { id: t });
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló', { id: t }); }
    finally { setBusy(null); }
  }

  const channels = channelsQ.data ?? [];

  return (
    <div className="space-y-4">
      {/* Barra superior: app destino + canal + tabs */}
      <div className="card p-3 flex flex-wrap items-center gap-3">
        <div>
          <h2 className="text-xl font-bold">Inspiración</h2>
          <p className="text-xs text-slate-500">Mirá cuentas que te interesan, marcá lo que te gusta y generá una versión propia.</p>
        </div>
        <div className="flex-1" />
        <div>
          <div className="text-[11px] text-slate-500 mb-0.5">App destino</div>
          <select className="input" value={pk ?? ''} onChange={(e) => { setProductKey(e.target.value); setChannelId(''); }}>
            {profiles.map((p) => <option key={p.id} value={p.productKey}>{p.productKey}</option>)}
          </select>
        </div>
        <div>
          <div className="text-[11px] text-slate-500 mb-0.5">Canal (opcional)</div>
          <select className="input" value={channelId} onChange={(e) => setChannelId(e.target.value)}>
            <option value="">Auto (Claude elige formato)</option>
            {channels.map((c) => <option key={c.id} value={c.id}>{c.platform} · {c.format} · {c.assetKind} · {c.distribution}</option>)}
          </select>
        </div>
      </div>

      <div className="flex gap-2">
        <button className={`text-sm rounded px-3 py-1.5 ${tab === 'cuenta' ? 'bg-brand-600 text-white' : 'bg-white border'}`} onClick={() => setTab('cuenta')}>Por cuenta</button>
        <button className={`text-sm rounded px-3 py-1.5 ${tab === 'curados' ? 'bg-brand-600 text-white' : 'bg-white border'}`} onClick={() => setTab('curados')}>Board curado</button>
      </div>

      {tab === 'cuenta' ? (
        <div className="grid grid-cols-1 md:grid-cols-12 gap-4 md:gap-6">
          {/* Cuentas */}
          <div className="md:col-span-3">
            <div className="card p-3 space-y-2 mb-3">
              <input className="input" placeholder="@handle" value={newHandle} onChange={(e) => setNewHandle(e.target.value)} />
              <input className="input" placeholder="Vertical (ej padel)" value={newVertical} onChange={(e) => setNewVertical(e.target.value)} />
              <button className="btn-primary w-full" onClick={addCompetitor}>Agregar cuenta</button>
            </div>
            <div className="card divide-y divide-slate-100 max-h-[60vh] overflow-y-auto">
              {(competitorsQ.data ?? []).filter((c) => !c.handle.startsWith('__tiktok_')).map((c) => (
                <button key={c.id} onClick={() => setSelected(c)}
                  className={`w-full text-left p-3 hover:bg-slate-50 ${selected?.id === c.id ? 'bg-brand-50' : ''}`}>
                  <div className="font-medium">@{c.handle}</div>
                  <div className="text-xs text-slate-500">{c.vertical ?? '—'} · {c.lastScrapedAt ? new Date(c.lastScrapedAt).toLocaleDateString() : 'sin traer'}</div>
                </button>
              ))}
            </div>
          </div>

          {/* Posts de la cuenta */}
          <div className="md:col-span-9 space-y-3">
            {!selected ? (
              <div className="card p-8 text-center text-slate-500">Elegí una cuenta para ver sus posteos.</div>
            ) : (
              <>
                <div className="flex items-center justify-between">
                  <h3 className="text-lg font-semibold">@{selected.handle} — {postsQ.data?.length ?? 0} posteos</h3>
                  <button className="btn-secondary" onClick={scrape}>Traer posteos</button>
                </div>
                {postsQ.isLoading ? <div className="card p-6 text-slate-500">Cargando…</div> : (
                  <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 gap-3">
                    {(postsQ.data ?? []).map((p) => (
                      <PostCard key={p.id} p={p} busy={busy === p.id} onCurate={() => toggleCurate(p)} onGenerate={() => generateInspired(p)} />
                    ))}
                    {postsQ.data?.length === 0 && <div className="col-span-full text-sm text-slate-500">Sin posteos. Usá "Traer posteos".</div>}
                  </div>
                )}
              </>
            )}
          </div>
        </div>
      ) : (
        <div>
          {curatedQ.isLoading ? <div className="card p-6 text-slate-500">Cargando…</div> : (
            <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-5 gap-3">
              {(curatedQ.data ?? []).map((p) => (
                <PostCard key={p.id} p={p} busy={busy === p.id} source={p.source}
                  onCurate={() => toggleCurate(p)} onGenerate={() => generateInspired(p)} />
              ))}
              {curatedQ.data?.length === 0 && <div className="col-span-full text-sm text-slate-500">Todavía no curaste nada. Andá a "Por cuenta" y marcá con ❤️ lo que te guste.</div>}
            </div>
          )}
        </div>
      )}

      <p className="text-[11px] text-slate-400">Lo generado aparece en <Link to="/posteos" className="text-brand-600">Posteos</Link> y en el <Link to="/calendario" className="text-brand-600">Calendario</Link>.</p>
    </div>
  );
}

function PostCard({ p, busy, source, onCurate, onGenerate }: {
  p: InspoPost; busy: boolean; source?: string; onCurate: () => void; onGenerate: () => void;
}) {
  const img = p.thumbnailUrl || p.mediaUrl;
  return (
    <div className="card overflow-hidden flex flex-col">
      <div className="relative bg-slate-100 aspect-square">
        {img
          ? <img src={img} alt="" className="w-full h-full object-cover" loading="lazy" referrerPolicy="no-referrer" />
          : <div className="w-full h-full flex items-center justify-center text-slate-400 text-xs p-2 text-center">{(p.caption ?? '').slice(0, 80) || 'sin media'}</div>}
        {p.isVideo && <span className="absolute top-1 left-1 text-[10px] bg-black/60 text-white rounded px-1">▶ video</span>}
        <button onClick={onCurate} title={p.curated ? 'Quitar de curados' : 'Marcar como inspiración'}
          className={`absolute top-1 right-1 text-lg leading-none rounded-full w-7 h-7 flex items-center justify-center ${p.curated ? 'bg-rose-500 text-white' : 'bg-white/80 text-slate-600'}`}>
          {p.curated ? '♥' : '♡'}
        </button>
      </div>
      <div className="p-2 text-xs flex-1 flex flex-col">
        {source && <div className="text-[10px] text-slate-400 mb-0.5">@{source.replace(/^@/, '')}</div>}
        <div className="text-slate-600 line-clamp-2">{p.caption?.slice(0, 120) ?? ''}</div>
        <div className="text-[10px] text-slate-400 mt-1">♥ {p.likes} · 💬 {p.commentsCount}</div>
        <div className="flex gap-1 mt-2 items-center">
          <button className="btn-primary text-[11px] flex-1 disabled:opacity-40" disabled={busy} onClick={onGenerate}>
            {busy ? '…' : 'Generar inspirado'}
          </button>
          {p.postUrl && <a href={p.postUrl} target="_blank" rel="noreferrer" className="text-[11px] text-slate-500 px-1" title="Ver original">↗</a>}
        </div>
      </div>
    </div>
  );
}
