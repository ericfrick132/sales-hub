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

// Lista pegada → handles limpios y únicos (el backend vuelve a validar y deduplicar).
function parseHandles(raw: string): string[] {
  const out = new Set<string>();
  for (const part of raw.split(/[\n,;\s]+/)) {
    const h = cleanHandle(part).toLowerCase();
    if (/^[a-z0-9._]{1,30}$/.test(h)) out.add(h);
  }
  return [...out];
}

// El workflow de deploy del front zipea tools/ig-follow-extension y lo publica en la raíz.
const EXTENSION_ZIP_URL = '/ig-follow-extension.zip';

const MODE_LABEL: Record<string, string> = {
  Followers: 'sus seguidores',
  Following: 'sus seguidos',
  List: 'una lista importada'
};

interface Campaign {
  id: string;
  instagramAccountId: string;
  instagramAccountUsername?: string | null;
  sourceHandle: string;
  sourceMode: string;
  dailyRate: number;
  activeHourStart?: number | null;
  activeHourEnd?: number | null;
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
  const [sourceMode, setSourceMode] = useState<'Followers' | 'Following' | 'List'>('Followers');
  const [handlesText, setHandlesText] = useState('');
  const [addHandlesText, setAddHandlesText] = useState('');
  const [dailyRate, setDailyRate] = useState(30);
  const [maxTotal, setMaxTotal] = useState(0);
  const [hourStart, setHourStart] = useState(10);
  const [hourEnd, setHourEnd] = useState(22);

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
          sourceHandle: sourceMode === 'List' ? sourceHandle.trim() : sourceHandle.trim().replace(/^@/, ''),
          sourceMode,
          handles: sourceMode === 'List' ? parseHandles(handlesText) : undefined,
          dailyRate,
          activeHourStart: hourStart,
          activeHourEnd: hourEnd,
          maxTotalFollows: maxTotal
        })
      ).data,
    onSuccess: (data: { enqueued?: number; duplicates?: number; invalid?: number }) => {
      toast.success(
        data.enqueued != null
          ? `Campaña creada · ${data.enqueued} encolados (${data.duplicates} repetidos, ${data.invalid} inválidos)`
          : 'Campaña creada'
      );
      setSourceHandle('');
      setHandlesText('');
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

  const hoursMut = useMutation({
    mutationFn: async ({ id, start, end }: { id: string; start: number; end: number }) =>
      (await api.patch(`/instagram-follow-campaigns/${id}`, {
        setActiveHours: true, activeHourStart: start, activeHourEnd: end
      })).data,
    onSuccess: () => {
      toast.success('Horario actualizado');
      qc.invalidateQueries({ queryKey: ['ig-follow-campaigns'] });
      qc.invalidateQueries({ queryKey: ['ig-follow-campaign'] });
    }
  });

  const sourceMut = useMutation({
    mutationFn: async ({ id, sourceHandle }: { id: string; sourceHandle: string }) =>
      (await api.patch(`/instagram-follow-campaigns/${id}`, { sourceHandle })).data,
    onSuccess: () => {
      toast.success('Source actualizado');
      qc.invalidateQueries({ queryKey: ['ig-follow-campaigns'] });
      qc.invalidateQueries({ queryKey: ['ig-follow-campaign'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló')
  });

  const addHandlesMut = useMutation({
    mutationFn: async ({ id, handles }: { id: string; handles: string[] }) =>
      (await api.post(`/instagram-follow-campaigns/${id}/handles`, { handles })).data as {
        enqueued: number; duplicates: number; invalid: number;
      },
    onSuccess: (r) => {
      toast.success(`${r.enqueued} encolados (${r.duplicates} repetidos, ${r.invalid} inválidos)`);
      setAddHandlesText('');
      qc.invalidateQueries({ queryKey: ['ig-follow-campaigns'] });
      qc.invalidateQueries({ queryKey: ['ig-follow-campaign'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló')
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

      <details className="card p-4 border-amber-200 bg-amber-50/30">
        <summary className="font-semibold cursor-pointer">
          Extensión de Chrome — seguir una lista desde tu compu (sin proxy)
        </summary>
        <div className="mt-3 space-y-3 text-sm text-slate-700">
          <p>
            Sigue de a una las cuentas de una lista desde <b>tu Chrome</b>, con tu sesión de Instagram y
            la IP de tu casa. No pasa por el server. Por defecto: 15 por día, pausas al azar de 8 a 30 min,
            de 10 a 22 h. Si Instagram muestra "Intentá de nuevo más tarde", frena 24 h sola.
          </p>
          <a className="btn-primary inline-block" href={EXTENSION_ZIP_URL} download>
            Descargar extensión (.zip)
          </a>
          <ol className="space-y-2 list-decimal list-inside">
            <li><b>Descomprimí el .zip</b> en una carpeta que no vayas a borrar (ej. Documentos/ig-follow-extension).</li>
            <li>
              En Chrome abrí <code className="px-1 rounded bg-slate-100">chrome://extensions</code> y prendé{' '}
              <b>Modo de desarrollador</b> (arriba a la derecha).
            </li>
            <li><b>Cargar descomprimida</b> → elegí la carpeta del paso 1.</li>
            <li>Fijá el ícono "IG Follow Lista" en la barra (el ícono de la pieza de rompecabezas → el pin).</li>
            <li>
              <b>Logueate en instagram.com</b> en esa ventana de Chrome con la cuenta que va a seguir. Si usás
              varias cuentas, armá un perfil de Chrome por cuenta e instalá la extensión en cada uno.
            </li>
            <li>
              Abrí la extensión, cargá el <b>.txt con los handles</b> (uno por línea; acepta @usuario o links
              de instagram.com) o pegalos, y tocá <b>Arrancar</b>.
            </li>
            <li>
              Dejá Chrome abierto y la compu sin dormirse. El popup muestra cuándo va el próximo follow, lo
              que lleva hoy y la cola; con <b>Exportar log</b> bajás un CSV con lo que hizo.
            </li>
          </ol>
          <p className="text-xs text-slate-500">
            Para actualizarla: bajá el .zip de nuevo, reemplazá la carpeta y tocá el ícono de recargar de la
            extensión en chrome://extensions. La cola y el log se mantienen.
          </p>
        </div>
      </details>

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
            <label className="text-xs text-slate-500">
              {sourceMode === 'List' ? 'Nombre de la lista' : 'Perfil source (de quién tomar las cuentas)'}
            </label>
            <input
              className="input"
              placeholder={sourceMode === 'List' ? 'ej: leads crossfy' : 'ej: gimnasiocompetidor'}
              value={sourceHandle}
              onChange={(e) => setSourceHandle(e.target.value)}
            />
          </div>

          <div className="sm:col-span-2">
            <label className="text-xs text-slate-500">A quién seguir</label>
            <select
              className="input"
              value={sourceMode}
              onChange={(e) => setSourceMode(e.target.value as 'Followers' | 'Following' | 'List')}>
              <option value="Followers">Seguidores del perfil (followers)</option>
              <option value="Following">Cuentas que el perfil sigue (following)</option>
              <option value="List">Una lista de cuentas (pegar handles)</option>
            </select>
          </div>

          {sourceMode === 'List' && (
            <div className="sm:col-span-4">
              <label className="text-xs text-slate-500">
                Handles o links de Instagram (uno por línea, o separados por coma) ·{' '}
                {parseHandles(handlesText).length} detectados
              </label>
              <textarea
                className="input h-32 font-mono text-xs"
                placeholder={'clubpadelnorte\n@crossfit_sur\nhttps://instagram.com/estudio.pilates'}
                value={handlesText}
                onChange={(e) => setHandlesText(e.target.value)}
              />
            </div>
          )}

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

          <div>
            <label className="text-xs text-slate-500">Horario (hora AR, desde)</label>
            <input
              className="input"
              type="number"
              min={0}
              max={23}
              value={hourStart}
              onChange={(e) => setHourStart(Number(e.target.value))}
            />
          </div>

          <div>
            <label className="text-xs text-slate-500">Horario (hasta, exclusive)</label>
            <input
              className="input"
              type="number"
              min={0}
              max={23}
              value={hourEnd}
              onChange={(e) => setHourEnd(Number(e.target.value))}
            />
          </div>

          <div className="sm:col-span-2 flex items-end">
            <button
              className="btn-primary"
              disabled={
                !accountId ||
                !sourceHandle.trim() ||
                (sourceMode === 'List' && parseHandles(handlesText).length === 0) ||
                createMut.isPending
              }
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
                <div className="font-medium">
                  {c.sourceMode === 'List' ? c.sourceHandle : `@${cleanHandle(c.sourceHandle)}`}
                </div>
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
                {MODE_LABEL[c.sourceMode] ?? 'sus seguidores'}
                {c.activeHourStart != null && c.activeHourEnd != null
                  ? ` · ${c.activeHourStart}-${c.activeHourEnd}h`
                  : ' · 24h'}
              </div>
              <div className="text-xs text-slate-600 mt-1">
                hoy: <b>{c.followedToday}</b>/{c.dailyRate} · seguidos:{' '}
                <b>{c.totalFollowed}</b> · cola: {c.pending}
              </div>
              {c.sourceMode !== 'List' && c.lastScrapeAt && c.totalEnqueued === 0 && (
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
                  <h3 className="font-semibold">
                    {detailQ.data.sourceMode === 'List' ? detailQ.data.sourceHandle : `@${detailQ.data.sourceHandle}`}
                  </h3>
                  <div className="text-xs text-slate-500">
                    cuenta: @{detailQ.data.instagramAccountUsername} · siguiendo a{' '}
                    {MODE_LABEL[detailQ.data.sourceMode] ?? 'sus seguidores'}
                  </div>
                </div>
                <div className="flex gap-2">
                  <button
                    className="text-xs px-2 py-1 rounded border border-slate-300 hover:bg-slate-50 disabled:opacity-50"
                    onClick={() => runMut.mutate(detailQ.data!.id)}
                    disabled={runMut.isPending}>
                    {runMut.isPending ? 'Corriendo…' : 'Run ahora'}
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

              <div className="flex items-end gap-2 flex-wrap">
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
                <div>
                  <label className="text-xs text-slate-500">Horario (desde-hasta, AR)</label>
                  <div className="flex gap-1">
                    <input
                      className="input w-16"
                      type="number" min={0} max={23}
                      key={`hs-${detailQ.data.id}-${detailQ.data.activeHourStart}`}
                      defaultValue={detailQ.data.activeHourStart ?? 10}
                      onBlur={(e) => {
                        const v = Number(e.target.value);
                        if (v !== detailQ.data!.activeHourStart)
                          hoursMut.mutate({ id: detailQ.data!.id, start: v, end: detailQ.data!.activeHourEnd ?? 22 });
                      }}
                    />
                    <input
                      className="input w-16"
                      type="number" min={0} max={23}
                      key={`he-${detailQ.data.id}-${detailQ.data.activeHourEnd}`}
                      defaultValue={detailQ.data.activeHourEnd ?? 22}
                      onBlur={(e) => {
                        const v = Number(e.target.value);
                        if (v !== detailQ.data!.activeHourEnd)
                          hoursMut.mutate({ id: detailQ.data!.id, start: detailQ.data!.activeHourStart ?? 10, end: v });
                      }}
                    />
                  </div>
                </div>
                {detailQ.data.sourceMode !== 'List' && (
                <div>
                  <label className="text-xs text-slate-500">Perfil source (corregir)</label>
                  <input
                    className="input w-44"
                    key={`sh-${detailQ.data.id}`}
                    defaultValue={cleanHandle(detailQ.data.sourceHandle)}
                    onBlur={(e) => {
                      const v = cleanHandle(e.target.value);
                      if (v && v !== cleanHandle(detailQ.data!.sourceHandle))
                        sourceMut.mutate({ id: detailQ.data!.id, sourceHandle: v });
                    }}
                  />
                </div>
                )}
                <div className="text-xs text-slate-500">
                  Último scrape: {detailQ.data.lastScrapeAt ? new Date(detailQ.data.lastScrapeAt).toLocaleString() : '—'}<br />
                  Último follow: {detailQ.data.lastFollowAt ? new Date(detailQ.data.lastFollowAt).toLocaleString() : '—'}
                </div>
              </div>

              {detailQ.data.sourceMode === 'List' && (
                <div className="space-y-1">
                  <label className="text-xs text-slate-500">
                    Agregar handles a la lista · {parseHandles(addHandlesText).length} detectados
                  </label>
                  <textarea
                    className="input h-24 font-mono text-xs"
                    value={addHandlesText}
                    onChange={(e) => setAddHandlesText(e.target.value)}
                  />
                  <button
                    className="text-xs px-2 py-1 rounded border border-slate-300 hover:bg-slate-50 disabled:opacity-50"
                    disabled={parseHandles(addHandlesText).length === 0 || addHandlesMut.isPending}
                    onClick={() =>
                      addHandlesMut.mutate({ id: detailQ.data!.id, handles: parseHandles(addHandlesText) })
                    }>
                    {addHandlesMut.isPending ? 'Agregando…' : 'Agregar a la cola'}
                  </button>
                </div>
              )}

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
