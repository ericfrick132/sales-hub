import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';

type Profile = { id: string; productKey: string };
type SocialPost = {
  id: string; productKey: string; platform: string; format: string; assetKind: string;
  status: string; concept: string; caption: string; hashtags: string[];
  assetUrl?: string; thumbnailUrl?: string; warmrAccount?: string; scheduledAt?: string;
};

const isVideo = (p: SocialPost) => p.assetKind === 'Video' || (p.assetUrl?.toLowerCase().endsWith('.mp4') ?? false);
const fullCaption = (p: SocialPost) =>
  p.hashtags?.length ? `${p.caption}\n\n${p.hashtags.map((h) => (h.startsWith('#') ? h : '#' + h)).join(' ')}` : p.caption;

export default function WarmrQueue() {
  const qc = useQueryClient();
  const [pk, setPk] = useState<string>('');

  const infoQ = useQuery({ queryKey: ['warmr-info'], queryFn: async () => (await api.get<{ mode: string; isConfigured: boolean }>('/posteos/warmr/info')).data });
  const profilesQ = useQuery({ queryKey: ['posteos-profiles'], queryFn: async () => (await api.get<Profile[]>('/posteos/profiles')).data });
  const queueQ = useQuery({
    queryKey: ['warmr-queue', pk],
    queryFn: async () => (await api.get<SocialPost[]>(`/posteos/warmr/queue${pk ? `?productKey=${pk}` : ''}`)).data,
    refetchInterval: 30000,
  });

  async function markUploaded(p: SocialPost) {
    try {
      await api.post(`/posteos/${p.id}/warmr-uploaded`);
      toast.success('Marcado como subido');
      qc.invalidateQueries({ queryKey: ['warmr-queue', pk] });
    } catch { toast.error('Falló'); }
  }
  async function remove(p: SocialPost) {
    if (!window.confirm('¿Sacar de la cola y borrar?')) return;
    try { await api.delete(`/posteos/${p.id}`); qc.invalidateQueries({ queryKey: ['warmr-queue', pk] }); }
    catch { toast.error('Falló'); }
  }
  async function copyCaption(p: SocialPost) {
    try { await navigator.clipboard.writeText(fullCaption(p)); toast.success('Caption copiado'); }
    catch { toast.error('No pude copiar'); }
  }

  const posts = queueQ.data ?? [];

  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-xl font-bold">Cola de Warmr</h2>
        <p className="text-sm text-slate-500">Posteos listos para subir a Warmr Cloud Drop. Warmr no tiene API → este es el último paso manual: descargás el clip, copiás el caption y lo arrastrás a Cloud Drop.</p>
      </div>

      {infoQ.data && (
        <div className={`card p-3 text-sm ${infoQ.data.mode === 'handoff' ? 'border-violet-200 bg-violet-50' : 'border-emerald-200 bg-emerald-50'}`}>
          Modo: <b>{infoQ.data.mode === 'handoff' ? 'Handoff (subida manual)' : infoQ.data.mode}</b>.{' '}
          {infoQ.data.mode === 'handoff' && 'Abrí app.warmr.so → Cloud Drop, subí el clip, pegá el caption y elegí la cuenta + slot.'}
        </div>
      )}

      <div className="flex items-center gap-2">
        <select className="input w-56" value={pk} onChange={(e) => setPk(e.target.value)}>
          <option value="">Todas las apps</option>
          {(profilesQ.data ?? []).map((p) => <option key={p.id} value={p.productKey}>{p.productKey}</option>)}
        </select>
        <span className="text-sm text-slate-500">{posts.length} en cola</span>
      </div>

      {posts.length === 0 ? (
        <div className="card p-8 text-center text-slate-500">Nada en la cola. Mandá posteos a Warmr desde Posteos o Inspiración (canales con distribución “Warmr”).</div>
      ) : (
        <div className="space-y-3">
          {posts.map((p) => (
            <div key={p.id} className="card p-3 flex flex-col sm:flex-row gap-3">
              <div className="sm:w-44 shrink-0">
                {p.assetUrl
                  ? (isVideo(p)
                      ? <video src={p.assetUrl} controls className="w-full rounded-lg border" />
                      : <img src={p.assetUrl} alt="" className="w-full rounded-lg border" />)
                  : <div className="w-full aspect-square rounded-lg border bg-slate-100 flex items-center justify-center text-xs text-slate-400">sin asset</div>}
              </div>
              <div className="flex-1 text-sm">
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="text-[11px] font-mono bg-slate-100 rounded px-1.5 py-0.5">{p.productKey} · {p.platform} · {p.format} · {p.assetKind}</span>
                  {p.warmrAccount && <span className="text-[11px] bg-violet-100 text-violet-700 rounded px-1.5 py-0.5">→ {p.warmrAccount}</span>}
                  {p.scheduledAt && <span className="text-[11px] text-slate-500">slot {new Date(p.scheduledAt).toLocaleString()}</span>}
                </div>
                <div className="font-medium mt-1">{p.concept}</div>
                <div className="text-slate-600 whitespace-pre-wrap mt-1 text-xs max-h-24 overflow-y-auto">{fullCaption(p)}</div>
                <div className="flex gap-2 mt-2 flex-wrap items-center">
                  {p.assetUrl && <a className="btn-secondary text-[11px]" href={p.assetUrl} download target="_blank" rel="noreferrer">⬇ Descargar clip</a>}
                  <button className="btn-secondary text-[11px]" onClick={() => copyCaption(p)}>Copiar caption</button>
                  <button className="btn-primary text-[11px]" onClick={() => markUploaded(p)}>✓ Marcar como subido</button>
                  <button className="text-[11px] text-red-600" onClick={() => remove(p)}>Borrar</button>
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
