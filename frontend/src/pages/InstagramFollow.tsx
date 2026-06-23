import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';

interface IgAccount {
  id: string;
  username: string;
  sellerName?: string | null;
  isActive: boolean;
  isLoggedIn: boolean;
  isActionBlocked: boolean;
}

// Normaliza el source a handle limpio para mostrar (tolera URL completa, @, barras).
function cleanHandle(raw: string): string {
  let s = (raw || '').trim();
  const i = s.toLowerCase().indexOf('instagram.com/');
  if (i >= 0) s = s.slice(i + 'instagram.com/'.length);
  return s.replace(/^@/, '').replace(/^\/+|\/+$/g, '').split(/[/?]/)[0];
}

interface Campaign {
  id: string;
  instagramAccountId: string;
  instagramAccountUsername?: string | null;
  sourceHandle: string;
  sourceMode: string;
  dailyRate: number;
  maxTotalFollows: number;
  isActive: boolean;
  totalEnqueued: number;
  totalFollowed: number;
  totalFailed: number;
  totalSkipped: number;
  pending: number;
  followedToday: number;
  lastScrapeAt?: string | null;
  lastFollowAt?: string | null;
  createdAt: string;
}

interface CampaignDetail extends Campaign {
  scrapeBatchSize: number;
  minQueuedThreshold: number;
  recentActions: Array<{
    id: string;
    targetHandle: string;
    status: string;
    enqueuedAt: string;
    executedAt?: string | null;
    attempts: number;
    errorMessage?: string | null;
  }>;
}

const STATUS_COLOR: Record<string, string> = {
  Pending: 'text-slate-500',
  Done: 'text-emerald-600',
  Failed: 'text-rose-600',
  Skipped: 'text-amber-600'
};

export default function InstagramFollow() {
  const qc = useQueryClient();
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const [accountId, setAccountId] = useState('');
  const [sourceHandle, setSourceHandle] = useState('');
  const [sourceMode, setSourceMode] = useState<'Followers' | 'Following'>('Followers');
  const [dailyRate, setDailyRate] = useState(30);
  const [maxTotal, setMaxTotal] = useState(0);

  const accountsQ = useQuery({
    queryKey: ['ig-accounts'],
    queryFn: async () => (await api.get<IgAccount[]>('/instagram-accounts')).data
  });

  const campaignsQ = useQuery({
    queryKey: ['ig-follow-campaigns'],
    queryFn: async () => (await api.get<Campaign[]>('/instagram-follow-campaigns')).data,
    refetchInterval: 15000
  });

  const detailQ = useQuery({
    queryKey: ['ig-follow-campaign', selectedId],
    enabled: !!selectedId,
    queryFn: async () =>
      (await api.get<CampaignDetail>(`/instagram-follow-campaigns/${selectedId}`)).data,
    refetchInterval: 15000
  });

  const createMut = useMutation({
    mutationFn: async () =>
      (
        await api.post('/instagram-follow-campaigns', {
          instagramAccountId: accountId,
          sourceHandle: sourceHandle.trim().replace(/^@/, ''),
          sourceMode,
          dailyRate,
          maxTotalFollows: maxTotal
        })
      ).data,
    onSuccess: () => {
      toast.success('Campaña creada');
      setSourceHandle('');
      setMaxTotal(0);
      qc.invalidateQueries({ queryKey: ['ig-follow-campaigns'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló crear')
  });

  const toggleMut = useMutation({
    mutationFn: async ({ id, isActive }: { id: string; isActive: boolean }) =>
      (await api.patch(`/instagram-follow-campaigns/${id}`, { isActive })).data,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['ig-follow-campaigns'] });
      qc.invalidateQueries({ queryKey: ['ig-follow-campaign'] });
    }
  });

  const rateMut = useMutation({
    mutationFn: async ({ id, dailyRate }: { id: string; dailyRate: number }) =>
      (await api.patch(`/instagram-follow-campaigns/${id}`, { dailyRate })).data,
    onSuccess: () => {
      toast.success('Rate actualizado');
      qc.invalidateQueries({ queryKey: ['ig-follow-campaigns'] });
      qc.invalidateQueries({ queryKey: ['ig-follow-campaign'] });
    }
  });

  const deleteMut = useMutation({
    mutationFn: async (id: string) =>
      (await api.delete(`/instagram-follow-campaigns/${id}`)).data,
    onSuccess: () => {
      toast.success('Campaña eliminada');
      setSelectedId(null);
      qc.invalidateQueries({ queryKey: ['ig-follow-campaigns'] });
    }
  });

  const runMut = useMutation({
    mutationFn: async (id: string) =>
      (await api.post(`/instagram-follow-campaigns/${id}/run`)).data,
    onSuccess: () => {
      toast.success('Run disparado');
      qc.invalidateQueries({ queryKey: ['ig-follow-campaigns'] });
      qc.invalidateQueries({ queryKey: ['ig-follow-campaign'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló')
  });

  const accounts = accountsQ.data ?? [];
  const campaigns = campaignsQ.data ?? [];

  return (
    <div className="space-y-6">
      <header>
        <h1 className="text-2xl font-bold">Auto-follow Instagram</h1>
        <p className="text-sm text-slate-500 mt-1">
          Encolá los seguidores de un perfil —o las cuentas que ese perfil sigue— para seguirlos
          desde tu cuenta de IG a un ritmo configurable por día. Mantenelo bajo (&lt;50/día) para no
          gatillar action blocks.
        </p>
      </header>

      <section className="card p-4 space-y-3">
        <h2 className="font-semibold">Nueva campaña</h2>

        <div className="grid grid-cols-1 sm:grid-cols-4 gap-3">
          <div className="sm:col-span-2">
            <label className="text-xs text-slate-500">Cuenta IG (ejecuta los follows)</label>
            <select
              className="input"
              value={accountId}
              onChange={(e) => setAccountId(e.target.value)}>
              <option value="">— elegir —</option>
              {accounts.map((a) => (
                <option key={a.id} value={a.id}>
                  @{a.username}
                  {a.sellerName ? ` — ${a.sellerName}` : ''}
                  {a.isActionBlocked ? ' (bloqueada)' : ''}
                  {!a.isLoggedIn ? ' (no logueada)' : ''}
                </option>
              ))}
            </select>
          </div>

          <div className="sm:col-span-2">
            <label className="text-xs text-slate-500">Perfil source (de quién tomar las cuentas)</label>
            <input
              className="input"
              placeholder="ej: gimnasiocompetidor"
              value={sourceHandle}
              onChange={(e) => setSourceHandle(e.target.value)}
            />
          </div>

          <div className="sm:col-span-2">
            <label className="text-xs text-slate-500">A quién seguir</label>
            <select
              className="input"
              value={sourceMode}
              onChange={(e) => setSourceMode(e.target.value as 'Followers' | 'Following')}>
              <option value="Followers">Seguidores del perfil (followers)</option>
              <option value="Following">Cuentas que el perfil sigue (following)</option>
            </select>
          </div>

          <div>
            <label className="text-xs text-slate-500">Follows / día</label>
            <input
              className="input"
              type="number"
              min={1}
              max={150}
              value={dailyRate}
              onChange={(e) => setDailyRate(Number(e.target.value))}
            />
          </div>

          <div>
            <label className="text-xs text-slate-500">Tope total (0 = sin tope)</label>
            <input
              className="input"
              type="number"
              min={0}
              value={maxTotal}
              onChange={(e) => setMaxTotal(Number(e.target.value))}
            />
          </div>

          <div className="sm:col-span-2 flex items-end">
            <button
              className="btn-primary"
              disabled={!accountId || !sourceHandle.trim() || createMut.isPending}
              onClick={() => createMut.mutate()}>
              {createMut.isPending ? 'Creando…' : 'Crear campaña'}
            </button>
          </div>
        </div>
      </section>

      <section className="grid grid-cols-1 lg:grid-cols-12 gap-4">
        <div className="lg:col-span-5 card divide-y divide-slate-100">
          {campaigns.length === 0 && (
            <div className="p-4 text-sm text-slate-500">No hay campañas todavía.</div>
          )}
          {campaigns.map((c) => (
            <button
              key={c.id}
              onClick={() => setSelectedId(c.id)}
              className={`w-full text-left p-3 hover:bg-slate-50 ${
                selectedId === c.id ? 'bg-brand-50' : ''
              }`}>
              <div className="flex items-center justify-between gap-2">
                <div className="font-medium">@{cleanHandle(c.sourceHandle)}</div>
                <span
                  className={`text-[10px] px-2 py-0.5 rounded ${
                    c.isActive
                      ? 'bg-emerald-100 text-emerald-700'
                      : 'bg-slate-200 text-slate-600'
                  }`}>
                  {c.isActive ? 'activa' : 'pausada'}
                </span>
              </div>
              <div className="text-xs text-slate-500 mt-0.5">
                desde @{c.instagramAccountUsername} · {c.dailyRate}/día ·{' '}
                {c.sourceMode === 'Following' ? 'sus seguidos' : 'sus seguidores'}
              </div>
              <div className="text-xs text-slate-600 mt-1">
                hoy: <b>{c.followedToday}</b>/{c.dailyRate} · seguidos:{' '}
                <b>{c.totalFollowed}</b> · cola: {c.pending}
              </div>
              {c.lastScrapeAt && c.totalEnqueued === 0 && (
                <div className="text-[11px] text-amber-600 mt-1 leading-snug">
                  ⚠ El último scrape no encontró seguidores. Suele pasar si el perfil es privado
                  o si Instagram limita la lista de seguidores para esta cuenta.
                </div>
              )}
            </button>
          ))}
        </div>

        <div className="lg:col-span-7">
          {!selectedId && (
            <div className="card p-6 text-sm text-slate-500">Elegí una campaña para ver detalle.</div>
          )}

          {selectedId && detailQ.data && (
            <div className="card p-4 space-y-4">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <h3 className="font-semibold">@{detailQ.data.sourceHandle}</h3>
                  <div className="text-xs text-slate-500">
                    cuenta: @{detailQ.data.instagramAccountUsername} ·{' '}
                    siguiendo {detailQ.data.sourceMode === 'Following' ? 'a sus seguidos' : 'a sus seguidores'}
                  </div>
                </div>
                <div className="flex gap-2">
                  <button
                    className="text-xs px-2 py-1 rounded border border-slate-300 hover:bg-slate-50"
                    onClick={() => runMut.mutate(detailQ.data!.id)}
                    disabled={runMut.isPending}>
                    Run ahora
                  </button>
                  <button
                    className="text-xs px-2 py-1 rounded border border-slate-300 hover:bg-slate-50"
                    onClick={() =>
                      toggleMut.mutate({ id: detailQ.data!.id, isActive: !detailQ.data!.isActive })
                    }>
                    {detailQ.data.isActive ? 'Pausar' : 'Reanudar'}
                  </button>
                  <button
                    className="text-xs px-2 py-1 rounded border border-rose-300 text-rose-700 hover:bg-rose-50"
                    onClick={() => {
                      if (confirm('¿Eliminar campaña? Se borran sus acciones encoladas.'))
                        deleteMut.mutate(detailQ.data!.id);
                    }}>
                    Eliminar
                  </button>
                </div>
              </div>

              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 text-sm">
                <Stat label="Hoy" value={`${detailQ.data.followedToday} / ${detailQ.data.dailyRate}`} />
                <Stat label="Seguidos total" value={detailQ.data.totalFollowed} />
                <Stat label="En cola" value={detailQ.data.pending} />
                <Stat label="Skipped / Failed" value={`${detailQ.data.totalSkipped} / ${detailQ.data.totalFailed}`} />
              </div>

              <div className="flex items-end gap-2">
                <div>
                  <label className="text-xs text-slate-500">Rate (follows/día)</label>
                  <input
                    className="input w-28"
                    type="number"
                    min={1}
                    max={150}
                    defaultValue={detailQ.data.dailyRate}
                    onBlur={(e) => {
                      const v = Number(e.target.value);
                      if (v > 0 && v !== detailQ.data!.dailyRate)
                        rateMut.mutate({ id: detailQ.data!.id, dailyRate: v });
                    }}
                  />
                </div>
                <div className="text-xs text-slate-500">
                  Último scrape: {detailQ.data.lastScrapeAt ? new Date(detailQ.data.lastScrapeAt).toLocaleString() : '—'}<br />
                  Último follow: {detailQ.data.lastFollowAt ? new Date(detailQ.data.lastFollowAt).toLocaleString() : '—'}
                </div>
              </div>

              <div>
                <h4 className="font-semibold text-sm mb-2">Últimas 100 acciones</h4>
                <div className="max-h-96 overflow-y-auto border border-slate-200 rounded divide-y divide-slate-100">
                  {detailQ.data.recentActions.length === 0 && (
                    <div className="p-3 text-xs text-slate-400">Sin acciones todavía.</div>
                  )}
                  {detailQ.data.recentActions.map((a) => (
                    <div key={a.id} className="p-2 text-xs flex items-center gap-2">
                      <span className={`w-16 font-medium ${STATUS_COLOR[a.status] ?? ''}`}>
                        {a.status}
                      </span>
                      <span className="flex-1 font-mono">@{a.targetHandle}</span>
                      <span className="text-slate-400">
                        {a.executedAt
                          ? new Date(a.executedAt).toLocaleTimeString()
                          : new Date(a.enqueuedAt).toLocaleTimeString()}
                      </span>
                      {a.errorMessage && (
                        <span className="text-rose-500 truncate max-w-[12rem]" title={a.errorMessage}>
                          {a.errorMessage}
                        </span>
                      )}
                    </div>
                  ))}
                </div>
              </div>
            </div>
          )}
        </div>
      </section>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="border border-slate-200 rounded p-2">
      <div className="text-[10px] uppercase tracking-wide text-slate-500">{label}</div>
      <div className="font-semibold">{value}</div>
    </div>
  );
}
