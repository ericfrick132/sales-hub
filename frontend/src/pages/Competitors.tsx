import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';

type Competitor = {
  id: string;
  handle: string;
  platform: string;
  displayName?: string;
  vertical?: string;
  lastScrapedAt?: string;
  isActive: boolean;
};

type Post = { id: string; caption?: string; likes: number; commentsCount: number; postedAt?: string; postUrl?: string };
type NegativeComment = { id: string; authorHandle?: string; text?: string; postedAt?: string; postId: string; postUrl?: string };

export default function Competitors() {
  const qc = useQueryClient();
  const [selected, setSelected] = useState<Competitor | null>(null);
  const [newHandle, setNewHandle] = useState('');
  const [newVertical, setNewVertical] = useState('');

  const list = useQuery({
    queryKey: ['competitors'],
    queryFn: async () => (await api.get<Competitor[]>('/competitors')).data
  });

  const posts = useQuery({
    queryKey: ['competitor-posts', selected?.id],
    enabled: !!selected,
    queryFn: async () => (await api.get<Post[]>(`/competitors/${selected!.id}/posts`)).data
  });
  const negatives = useQuery({
    queryKey: ['competitor-neg', selected?.id],
    enabled: !!selected,
    queryFn: async () => (await api.get<NegativeComment[]>(`/competitors/${selected!.id}/negative-comments`)).data
  });

  async function add() {
    if (!newHandle) return;
    await api.post('/competitors', { handle: newHandle.replace(/^@/, ''), platform: 'instagram', vertical: newVertical || null });
    setNewHandle(''); setNewVertical('');
    qc.invalidateQueries({ queryKey: ['competitors'] });
  }

  async function scrape() {
    if (!selected) return;
    try {
      const { data } = await api.post(`/competitors/${selected.id}/scrape`);
      toast.success(`${data.posts} posts / ${data.comments} comentarios`);
      qc.invalidateQueries({ queryKey: ['competitor-posts', selected.id] });
      qc.invalidateQueries({ queryKey: ['competitor-neg', selected.id] });
    } catch { toast.error('Falló el scrape'); }
  }

  return (
    <div className="grid grid-cols-1 md:grid-cols-12 gap-4 md:gap-6">
      <div className="md:col-span-4">
        <h2 className="text-xl font-bold mb-2">Competencia (IG)</h2>
        <div className="card p-3 space-y-2 mb-3">
          <input className="input" placeholder="@handle" value={newHandle} onChange={(e) => setNewHandle(e.target.value)} />
          <input className="input" placeholder="Vertical (ej gymhero)" value={newVertical} onChange={(e) => setNewVertical(e.target.value)} />
          <button className="btn-primary w-full" onClick={add}>Agregar</button>
        </div>
        <div className="card divide-y divide-slate-100">
          {(list.data ?? [])
            .filter((c) => !c.handle.startsWith('__tiktok_'))
            .map((c) => (
              <button key={c.id} onClick={() => setSelected(c)}
                className={`w-full text-left p-3 hover:bg-slate-50 ${selected?.id === c.id ? 'bg-brand-50' : ''}`}>
                <div className="font-medium">@{c.handle}</div>
                <div className="text-xs text-slate-500">{c.vertical ?? '—'} · {c.lastScrapedAt ? `scraped ${new Date(c.lastScrapedAt).toLocaleDateString()}` : 'nunca scrapeado'}</div>
              </button>
            ))}
        </div>
      </div>

      <div className="md:col-span-8 space-y-4">
        {!selected ? (
          <div className="card p-8 text-center text-slate-500">Seleccioná un handle</div>
        ) : (
          <>
            <div className="flex items-center justify-between">
              <h3 className="text-lg font-semibold">@{selected.handle}</h3>
              <button className="btn-secondary" onClick={scrape}>Scrapear ahora</button>
            </div>
            <div className="card p-4">
              <h4 className="font-semibold mb-2">Últimos posts ({posts.data?.length ?? 0})</h4>
              <ul className="space-y-2 max-h-72 overflow-y-auto">
                {(posts.data ?? []).map((p) => (
                  <li key={p.id} className="text-sm border-b border-slate-100 pb-2">
                    <div className="text-slate-500 text-xs">
                      {p.postedAt ? new Date(p.postedAt).toLocaleDateString() : '?'} — ♥ {p.likes} · 💬 {p.commentsCount}
                    </div>
                    <div>{p.caption?.slice(0, 180) ?? ''}</div>
                    {p.postUrl && <a href={p.postUrl} target="_blank" rel="noreferrer" className="text-brand-600 text-xs">Ver</a>}
                  </li>
                ))}
              </ul>
            </div>
            <div className="card p-4">
              <h4 className="font-semibold mb-2 text-rose-700">Comentarios negativos ({negatives.data?.length ?? 0})</h4>
              <p className="text-xs text-slate-500 mb-2">Estos autores son leads calientes para la competencia — consideralos.</p>
              <ul className="space-y-2 max-h-72 overflow-y-auto">
                {(negatives.data ?? []).map((c) => (
                  <li key={c.id} className="text-sm border-b border-slate-100 pb-2">
                    <div className="text-slate-500 text-xs">@{c.authorHandle ?? '?'} — {c.postedAt ? new Date(c.postedAt).toLocaleDateString() : ''}</div>
                    <div>"{c.text}"</div>
                  </li>
                ))}
              </ul>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
