import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';

/**
 * Salud del auto-follow de Instagram por cuenta, en un pantallazo para el panel:
 * si el worker está prendido, login vivo, bloqueos/2FA, follows de hoy vs. ritmo, y
 * si alguna cuenta se trabó (típico: túnel de proxy caído → 0 follows). Clic → /instagram/follow.
 */

interface IgAccountStatus {
  accountId: string;
  username: string;
  isLoggedIn: boolean;
  isActionBlocked: boolean;
  isAwaitingTwoFactor: boolean;
  lastLoginAt: string | null;
  lastFollowAt: string | null;
  activeCampaigns: number;
  dailyRate: number;
  followedToday: number;
  pending: number;
  lastError: string | null;
  status: string;
}
interface IgFollowSummary {
  enabled: boolean;
  accounts: number;
  ok: number;
  attention: number;
  followedToday: number;
  rows: IgAccountStatus[];
}

const STATUS: Record<string, { label: string; cls: string }> = {
  ok: { label: 'Andando', cls: 'bg-emerald-50 text-emerald-700 border-emerald-200' },
  stale: { label: 'Trabada', cls: 'bg-amber-50 text-amber-700 border-amber-200' },
  logged_out: { label: 'Sin login', cls: 'bg-red-50 text-red-700 border-red-200' },
  blocked: { label: 'Bloqueada', cls: 'bg-red-50 text-red-700 border-red-200' },
  '2fa': { label: 'Pide 2FA', cls: 'bg-red-50 text-red-700 border-red-200' },
  idle: { label: 'Sin campaña', cls: 'bg-slate-50 text-slate-500 border-slate-200' },
  off: { label: 'Apagado', cls: 'bg-slate-50 text-slate-500 border-slate-200' },
};

function ago(iso: string | null): string {
  if (!iso) return 'nunca';
  const mins = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  if (mins < 1) return 'recién';
  if (mins < 60) return `hace ${mins}m`;
  const h = Math.round(mins / 60);
  if (h < 48) return `hace ${h}h`;
  return `hace ${Math.round(h / 24)}d`;
}

export default function InstagramFollowCard() {
  const { data, isLoading } = useQuery({
    queryKey: ['instagram-follow-status'],
    queryFn: async () => (await api.get<IgFollowSummary>('/dashboard/instagram-follow')).data,
    refetchInterval: 60000,
  });

  const rows = data?.rows ?? [];

  return (
    <div className="card p-5">
      <div className="flex items-center justify-between mb-3 gap-2 flex-wrap">
        <h2 className="text-lg font-semibold">Auto-follow Instagram</h2>
        <div className="flex items-center gap-2 text-xs">
          {data && !data.enabled && (
            <span className="px-2 py-0.5 rounded border border-slate-200 bg-slate-50 text-slate-500">Worker apagado</span>
          )}
          {data && (
            <span className="text-slate-400">
              {data.ok} andando · {data.attention > 0 ? <span className="text-amber-600 font-medium">{data.attention} a revisar</span> : '0 a revisar'} · {data.followedToday} follows hoy
            </span>
          )}
          <Link to="/instagram/follow" className="text-brand-600 hover:underline">detalle →</Link>
        </div>
      </div>

      {isLoading ? (
        <p className="text-slate-400 text-sm">Cargando…</p>
      ) : rows.length === 0 ? (
        <p className="text-slate-400 text-sm">
          No hay cuentas de Instagram activas. <Link to="/instagram/accounts" className="text-brand-600 hover:underline">Conectar una →</Link>
        </p>
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
          {rows.map((r) => {
            const s = STATUS[r.status] ?? STATUS.idle;
            return (
              <div key={r.accountId} className="rounded-lg border border-slate-200 p-3">
                <div className="flex items-center justify-between gap-2">
                  <span className="font-medium text-slate-800 truncate" title={r.username}>@{r.username}</span>
                  <span className={`text-[11px] px-2 py-0.5 rounded border whitespace-nowrap ${s.cls}`}>{s.label}</span>
                </div>
                <div className="mt-2 flex items-baseline gap-1">
                  <span className="text-2xl font-bold tabular-nums text-slate-900">{r.followedToday}</span>
                  <span className="text-xs text-slate-400">/ {r.dailyRate} hoy</span>
                </div>
                <div className="mt-1 text-xs text-slate-500">
                  Último follow: {ago(r.lastFollowAt)} · {r.pending} en cola
                </div>
                {r.lastError && (r.status === 'stale' || r.status === 'blocked') && (
                  <div className="mt-1 text-[11px] text-red-600 truncate" title={r.lastError}>⚠ {r.lastError}</div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
