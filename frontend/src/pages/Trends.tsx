import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';

const VERTICALS = ['gymhero', 'bookingpro_barber', 'bookingpro_salon', 'playcrew', 'bunker', 'unistock', 'construction'];

export default function Trends() {
  const [vertical, setVertical] = useState<string>(VERTICALS[0]);
  const [tab, setTab] = useState<'hashtags' | 'posts' | 'tiktok'>('hashtags');

  const hashtags = useQuery({
    queryKey: ['trends-hashtags', vertical],
    enabled: tab === 'hashtags',
    queryFn: async () => (await api.get<{ hashtag: string; count: number }[]>('/trends/hashtags', { params: { vertical, days: 14 } })).data
  });
  const posts = useQuery({
    queryKey: ['trends-posts', vertical],
    enabled: tab === 'posts' || tab === 'tiktok',
    queryFn: async () => (await api.get<any[]>('/trends/top-posts', { params: { vertical, days: 14, limit: 40 } })).data
  });

  async function triggerTikTok() {
    const h = prompt('Hashtag TikTok a scrapear (sin #):');
    if (!h) return;
    try {
      const { data } = await api.post('/admin/trends/tiktok', { hashtag: h, vertical, maxResults: 30 });
      toast.success(`TikTok: ${data.saved} posts`);
    } catch { toast.error('Falló'); }
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <h1 className="text-xl md:text-2xl font-bold">Tendencias</h1>
        <div className="flex items-center gap-2 flex-wrap">
          <select className="input w-full sm:w-56" value={vertical} onChange={(e) => setVertical(e.target.value)}>
            {VERTICALS.map((v) => <option key={v} value={v}>{v}</option>)}
          </select>
          <button className="btn-secondary text-xs" onClick={triggerTikTok}>+ Scrapear TikTok</button>
        </div>
      </div>

      <div className="flex gap-1 border-b border-slate-200 overflow-x-auto">
        {(['hashtags', 'posts', 'tiktok'] as const).map((t) => (
          <button key={t} onClick={() => setTab(t)}
            className={`px-4 py-2 text-sm border-b-2 whitespace-nowrap ${tab === t ? 'border-brand-600 text-brand-700 font-medium' : 'border-transparent text-slate-500'}`}>
            {t === 'hashtags' ? 'Top hashtags IG' : t === 'posts' ? 'Top posts IG' : 'TikTok'}
          </button>
        ))}
      </div>

      {tab === 'hashtags' && (
        <div className="card p-5">
          {hashtags.isLoading ? 'Cargando…' : (
            <ul className="grid grid-cols-2 sm:grid-cols-3 gap-2 text-sm">
              {(hashtags.data ?? []).map((h) => (
                <li key={h.hashtag} className="flex justify-between border-b border-slate-100 py-1">
                  <span>#{h.hashtag}</span>
                  <span className="text-slate-500">{h.count}</span>
                </li>
              ))}
              {hashtags.data?.length === 0 && <li className="col-span-2 sm:col-span-3 text-slate-500">Sin datos aún. Agregá competidores en /competitors para alimentar esto.</li>}
            </ul>
          )}
        </div>
      )}
      {(tab === 'posts' || tab === 'tiktok') && (
        <div className="card divide-y divide-slate-100">
          {posts.isLoading ? <div className="p-5">Cargando…</div> : (posts.data ?? []).map((p: any, i) => (
            <div key={i} className="p-3 text-sm flex gap-3">
              <div className="w-24 text-xs text-slate-500 shrink-0">
                @{p.handle ?? '?'}<br />
                ♥ {p.likes ?? 0} · 💬 {p.commentsCount ?? 0}
              </div>
              <div className="flex-1">
                <div>{(p.caption ?? '').slice(0, 240)}</div>
                {p.postUrl && <a href={p.postUrl} target="_blank" rel="noreferrer" className="text-brand-600 text-xs">Abrir</a>}
              </div>
            </div>
          ))}
          {posts.data?.length === 0 && <div className="p-5 text-slate-500">Sin datos aún.</div>}
        </div>
      )}
    </div>
  );
}
