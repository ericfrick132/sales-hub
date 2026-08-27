import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import type { Seller } from '../lib/types';

interface IgAccount {
  id: string;
  username: string;
  sellerId: string;
  sellerName?: string | null;
  isActive: boolean;
  isLoggedIn: boolean;
  isActionBlocked: boolean;
  isAwaitingTwoFactor: boolean;
  proxyUrl?: string | null;
  dailyDmCap?: number | null;
  blockedUntil?: string | null;
  lastLoginAt?: string | null;
  lastUsedAt?: string | null;
  createdAt: string;
}

interface DashboardItem {
  id: string;
  username: string;
  sellerName?: string | null;
  isActive: boolean;
  isLoggedIn: boolean;
  isActionBlocked: boolean;
  blockedUntil?: string | null;
  lastUsedAt?: string | null;
  todayMetrics?: {
    profilesScraped: number;
    leadsCreated: number;
    dmSent: number;
    dmFailed: number;
    actionBlocks: number;
  } | null;
}

export default function InstagramAccounts() {
  const qc = useQueryClient();
  const [editing, setEditing] = useState<IgAccount | null>(null);
  const [showBulk, setShowBulk] = useState(false);
  const [showNew, setShowNew] = useState(false);
  const [twoFaFor, setTwoFaFor] = useState<IgAccount | null>(null);

  const accountsQ = useQuery({
    queryKey: ['ig-accounts'],
    queryFn: async () => (await api.get<IgAccount[]>('/instagram-accounts')).data,
    refetchInterval: 20000
  });

  const dashboardQ = useQuery({
    queryKey: ['ig-accounts-dashboard'],
    queryFn: async () => (await api.get<DashboardItem[]>('/instagram-accounts/dashboard')).data,
    refetchInterval: 20000
  });

  const sellersQ = useQuery({
    queryKey: ['sellers-for-ig'],
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data
  });

  const createMut = useMutation({
    mutationFn: async (body: { sellerId: string; username: string; password: string; twoFactorSecret?: string; proxyUrl?: string }) =>
      (await api.post('/instagram-accounts', body)).data,
    onSuccess: () => {
      toast.success('Cuenta creada');
      setShowNew(false);
      qc.invalidateQueries({ queryKey: ['ig-accounts'] });
      qc.invalidateQueries({ queryKey: ['ig-accounts-dashboard'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló crear')
  });

  const importMut = useMutation({
    mutationFn: async (body: { defaultSellerId?: string; rawData: string }) =>
      (await api.post('/instagram-accounts/import', body)).data,
    onSuccess: (data: any) => {
      toast.success(data.message ?? 'Import completo');
      setShowBulk(false);
      qc.invalidateQueries({ queryKey: ['ig-accounts'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló import')
  });

  const updateMut = useMutation({
    mutationFn: async ({ id, body }: { id: string; body: Record<string, unknown> }) =>
      (await api.put(`/instagram-accounts/${id}`, body)).data,
    onSuccess: () => {
      toast.success('Actualizada');
      setEditing(null);
      qc.invalidateQueries({ queryKey: ['ig-accounts'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló')
  });

  const deleteMut = useMutation({
    mutationFn: async (id: string) => (await api.delete(`/instagram-accounts/${id}`)).data,
    onSuccess: () => {
      toast.success('Eliminada');
      qc.invalidateQueries({ queryKey: ['ig-accounts'] });
      qc.invalidateQueries({ queryKey: ['ig-accounts-dashboard'] });
    }
  });

  const loginMut = useMutation({
    mutationFn: async (id: string) => (await api.post(`/instagram-accounts/${id}/login`)).data,
    onSuccess: (_d, id) => {
      toast.success('Login disparado');
      qc.invalidateQueries({ queryKey: ['ig-accounts'] });
      // Si quedó esperando 2FA el GET lo va a reflejar
      void id;
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló')
  });

  const loginAllMut = useMutation({
    mutationFn: async () => (await api.post('/instagram-accounts/login-all')).data,
    onSuccess: (data: any) => {
      toast.success(data.message ?? 'Login batch completado');
      qc.invalidateQueries({ queryKey: ['ig-accounts'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Falló')
  });

  const submit2faMut = useMutation({
    mutationFn: async ({ id, code }: { id: string; code: string }) =>
      (await api.post(`/instagram-accounts/${id}/submit-2fa`, { code })).data,
    onSuccess: () => {
      toast.success('2FA enviado');
      setTwoFaFor(null);
      qc.invalidateQueries({ queryKey: ['ig-accounts'] });
    },
    onError: (e: any) => toast.error(e.response?.data?.error ?? 'Código incorrecto')
  });

  const accounts = accountsQ.data ?? [];
  const dashboard = dashboardQ.data ?? [];
  const sellers = sellersQ.data ?? [];

  // Merge dashboard metrics into the account list
  const byId = new Map(dashboard.map((d) => [d.id, d]));

  return (
    <div className="space-y-6">
      <header className="flex items-start justify-between gap-3 flex-wrap">
        <div>
          <h1 className="text-2xl font-bold">Cuentas de Instagram</h1>
          <p className="text-sm text-slate-500 mt-1">
            Las cuentas que mandan DMs, hacen auto-follow y scrapean leads en Instagram.
          </p>
        </div>
        <div className="flex gap-2">
          <button className="btn-secondary text-xs" onClick={() => setShowNew((v) => !v)}>
            {showNew ? 'Cancelar' : '+ Nueva'}
          </button>
          <button className="btn-secondary text-xs" onClick={() => setShowBulk((v) => !v)}>
            {showBulk ? 'Cancelar' : 'Import bulk'}
          </button>
          <button
            className="btn-primary text-xs"
            disabled={loginAllMut.isPending}
            onClick={() => loginAllMut.mutate()}>
            {loginAllMut.isPending ? 'Conectando…' : 'Conectar todas'}
          </button>
        </div>
      </header>

      <div className="text-xs text-slate-600 bg-slate-50 rounded-lg px-3 py-2 leading-relaxed">
        Cómo dejar una cuenta lista: <b>1.</b> Agregala con usuario y contraseña ·{' '}
        <b>2.</b> tocá <b>Conectar</b> ·{' '}
        <b>3.</b> si Instagram pide código, ingresá el <b>2FA</b> → queda{' '}
        <span className="text-emerald-600 font-medium">Lista para usar</span>.{' '}
        Tip: cargá un <b>proxy</b> del país de la cuenta (botón Editar) para evitar verificaciones.
      </div>

      {showNew && (
        <CreateForm
          sellers={sellers}
          submitting={createMut.isPending}
          onSubmit={(b) => createMut.mutate(b)}
        />
      )}

      {showBulk && (
        <BulkForm
          sellers={sellers}
          submitting={importMut.isPending}
          onSubmit={(b) => importMut.mutate(b)}
        />
      )}

      <section className="card divide-y divide-slate-100">
        {accounts.length === 0 && (
          <div className="p-4 text-sm text-slate-500">
            No hay cuentas todavía. Creá una con "+ Nueva" o usá "Import bulk".
          </div>
        )}
        {accounts.map((a) => {
          const m = byId.get(a.id)?.todayMetrics;
          const st = statusOf(a);
          // Solo la cuenta que se está conectando muestra "Conectando…" (no todas).
          const connecting = loginMut.isPending && loginMut.variables === a.id;
          return (
            <div key={a.id} className="p-3 flex flex-wrap items-center gap-3">
              <div className="flex-1 min-w-[14rem]">
                <div className="font-medium">@{a.username}</div>
                <div className="text-xs text-slate-500">
                  {a.sellerName ?? '— sin vendedor —'} · creada {new Date(a.createdAt).toLocaleDateString()}
                </div>
                <div className={`text-xs mt-1 inline-flex items-center gap-1.5 font-medium ${st.text}`}>
                  <span className={`w-2 h-2 rounded-full ${st.dot}`} />
                  {st.label}
                </div>
                {a.isLoggedIn && !a.proxyUrl && (
                  <div className="text-[11px] text-amber-600 mt-0.5">
                    Sin proxy: Instagram puede pedir verificación. Recomendado cargar uno (Editar).
                  </div>
                )}
              </div>

              {m && (m.profilesScraped > 0 || m.dmSent > 0) && (
                <div className="text-xs text-slate-600">
                  hoy: scrape <b>{m.profilesScraped}</b> · DM <b>{m.dmSent}</b>{a.dailyDmCap ? <span className="text-slate-400">/{a.dailyDmCap}</span> : null}
                  {m.actionBlocks > 0 && <span className="text-rose-600"> · blocks {m.actionBlocks}</span>}
                </div>
              )}

              <div className="flex items-center gap-2">
                {st.primary === 'login' && (
                  <button className="btn-primary text-xs" disabled={connecting}
                    onClick={() => loginMut.mutate(a.id)}>
                    {connecting ? 'Conectando…' : 'Conectar'}
                  </button>
                )}
                {st.primary === '2fa' && (
                  <button className="btn-primary text-xs" onClick={() => setTwoFaFor(a)}>
                    Ingresar código 2FA
                  </button>
                )}
                {st.primary === 'ready' && (
                  <button className="text-xs px-2 py-1 rounded border border-slate-200 text-slate-500 hover:bg-slate-50"
                    disabled={connecting} onClick={() => loginMut.mutate(a.id)} title="Volver a conectar / refrescar sesión">
                    {connecting ? 'Conectando…' : 'Reconectar'}
                  </button>
                )}
                <button
                  className="text-xs px-2 py-1 rounded border border-slate-200 text-slate-500 hover:bg-slate-50"
                  onClick={() => setEditing(a)}>
                  Editar
                </button>
                <button
                  className="text-xs px-2 py-1 rounded text-rose-500 hover:bg-rose-50"
                  title="Eliminar cuenta"
                  onClick={() => {
                    if (confirm(`¿Eliminar @${a.username}? Se borran sus cookies y métricas.`))
                      deleteMut.mutate(a.id);
                  }}>
                  ✕
                </button>
              </div>
            </div>
          );
        })}
      </section>

      {editing && (
        <EditModal
          account={editing}
          sellers={sellers}
          submitting={updateMut.isPending}
          onCancel={() => setEditing(null)}
          onSubmit={(body) => updateMut.mutate({ id: editing.id, body })}
        />
      )}

      {twoFaFor && (
        <TwoFaModal
          account={twoFaFor}
          submitting={submit2faMut.isPending}
          onCancel={() => setTwoFaFor(null)}
          onSubmit={(code) => submit2faMut.mutate({ id: twoFaFor.id, code })}
        />
      )}
    </div>
  );
}

// Estado claro y accionable de una cuenta: una sola línea + cuál es la acción principal.
function statusOf(a: IgAccount): {
  label: string;
  dot: string;
  text: string;
  primary: 'login' | '2fa' | 'ready' | 'none';
} {
  if (a.isActionBlocked) {
    return {
      label: `Bloqueada por Instagram${a.blockedUntil ? ` hasta ${new Date(a.blockedUntil).toLocaleString()}` : ''}`,
      dot: 'bg-rose-500', text: 'text-rose-600', primary: 'none',
    };
  }
  if (a.isAwaitingTwoFactor) {
    return { label: 'Esperando código 2FA', dot: 'bg-amber-500', text: 'text-amber-600', primary: '2fa' };
  }
  if (!a.isLoggedIn) {
    return { label: 'Falta iniciar sesión', dot: 'bg-amber-500', text: 'text-amber-600', primary: 'login' };
  }
  if (!a.isActive) {
    return { label: 'Pausada', dot: 'bg-slate-400', text: 'text-slate-500', primary: 'none' };
  }
  return { label: 'Lista para usar', dot: 'bg-emerald-500', text: 'text-emerald-600', primary: 'ready' };
}

function CreateForm({
  sellers,
  submitting,
  onSubmit
}: {
  sellers: Seller[];
  submitting: boolean;
  onSubmit: (body: { sellerId: string; username: string; password: string; twoFactorSecret?: string; proxyUrl?: string }) => void;
}) {
  const [sellerId, setSellerId] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [twoFactorSecret, setTwoFactorSecret] = useState('');
  const [proxyUrl, setProxyUrl] = useState('');

  return (
    <div className="card p-4 space-y-3">
      <h2 className="font-semibold">Nueva cuenta</h2>
      <div className="grid grid-cols-1 sm:grid-cols-4 gap-3">
        <SellerSelect value={sellerId} onChange={setSellerId} sellers={sellers} />
        <Field label="Username (sin @)">
          <input className="input" value={username} onChange={(e) => setUsername(e.target.value)} />
        </Field>
        <Field label="Password">
          <input className="input" type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <Field label="2FA secret (opcional)">
          <input className="input" value={twoFactorSecret} onChange={(e) => setTwoFactorSecret(e.target.value)} placeholder="JBSWY3DPEHPK3PXP" />
        </Field>
      </div>
      <Field label="Proxy (recomendado: residencial del país de la cuenta)">
        <input
          className="input font-mono text-xs"
          value={proxyUrl}
          onChange={(e) => setProxyUrl(e.target.value)}
          placeholder="http://user:pass@host:port  ó  host:port:user:pass"
        />
      </Field>
      <button
        className="btn-primary"
        disabled={!sellerId || !username.trim() || !password || submitting}
        onClick={() =>
          onSubmit({
            sellerId,
            username: username.trim(),
            password,
            twoFactorSecret: twoFactorSecret.trim() || undefined,
            proxyUrl: proxyUrl.trim() || undefined
          })
        }>
        {submitting ? 'Creando…' : 'Crear'}
      </button>
    </div>
  );
}

function BulkForm({
  sellers,
  submitting,
  onSubmit
}: {
  sellers: Seller[];
  submitting: boolean;
  onSubmit: (body: { defaultSellerId?: string; rawData: string }) => void;
}) {
  const [sellerId, setSellerId] = useState('');
  const [rawData, setRawData] = useState('');

  return (
    <div className="card p-4 space-y-3">
      <h2 className="font-semibold">Import bulk</h2>
      <p className="text-xs text-slate-500">
        Formato: <code>login:password:2FA_secret</code> (una por línea). El 2FA es opcional.
      </p>
      <SellerSelect value={sellerId} onChange={setSellerId} sellers={sellers} label="Seller default (todas las cuentas se asignan)" />
      <textarea
        className="input font-mono text-xs"
        rows={8}
        value={rawData}
        onChange={(e) => setRawData(e.target.value)}
        placeholder={'user1:pass1:JBSWY3DPEHPK3PXP\nuser2:pass2:\nuser3:pass3:JBSWY3DPEHPK3PXR'}
      />
      <button
        className="btn-primary"
        disabled={!sellerId || !rawData.trim() || submitting}
        onClick={() => onSubmit({ defaultSellerId: sellerId, rawData })}>
        {submitting ? 'Importando…' : 'Importar'}
      </button>
    </div>
  );
}

function EditModal({
  account,
  sellers,
  submitting,
  onCancel,
  onSubmit
}: {
  account: IgAccount;
  sellers: Seller[];
  submitting: boolean;
  onCancel: () => void;
  onSubmit: (body: Record<string, unknown>) => void;
}) {
  const [sellerId, setSellerId] = useState(account.sellerId);
  const [password, setPassword] = useState('');
  const [twoFactorSecret, setTwoFactorSecret] = useState('');
  const [isActive, setIsActive] = useState(account.isActive);
  const [proxyUrl, setProxyUrl] = useState(account.proxyUrl ?? '');
  const [dailyDmCap, setDailyDmCap] = useState<number>(account.dailyDmCap ?? 0);

  return (
    <Modal title={`Editar @${account.username}`} onCancel={onCancel}>
      <div className="space-y-3">
        <SellerSelect value={sellerId} onChange={setSellerId} sellers={sellers} />
        <Field label="Nueva password (vacío = no cambiar)">
          <input className="input" type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <Field label="2FA secret (vacío = no cambiar)">
          <input className="input" value={twoFactorSecret} onChange={(e) => setTwoFactorSecret(e.target.value)} />
        </Field>
        <Field label="Proxy (vacío = sin proxy)">
          <input
            className="input font-mono text-xs"
            value={proxyUrl}
            onChange={(e) => setProxyUrl(e.target.value)}
            placeholder="http://user:pass@host:port  ó  host:port:user:pass"
          />
        </Field>
        <Field label="Tope de DMs por día de esta cuenta (0 = usar el global del hub, 40)">
          <input className="input w-32" type="number" min={0} value={dailyDmCap} onChange={(e) => setDailyDmCap(Number(e.target.value))} />
          <div className="text-[11px] text-slate-500 mt-0.5">La cola rota entre cuentas: cada DM sale por la cuenta que menos mandó hoy y todavía tiene cupo. Para cuentas de marca, 5-10 es un tope prudente.</div>
        </Field>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} />
          Activa
        </label>
      </div>
      <div className="flex justify-end gap-2 mt-4">
        <button className="text-xs px-3 py-1 rounded border border-slate-300" onClick={onCancel}>Cancelar</button>
        <button
          className="btn-primary"
          disabled={submitting}
          onClick={() => {
            const body: Record<string, unknown> = { sellerId, isActive };
            if (password) body.password = password;
            if (twoFactorSecret) body.twoFactorSecret = twoFactorSecret;
            // proxyUrl siempre se envía: permite setear y también limpiar (string vacío → null)
            body.proxyUrl = proxyUrl.trim();
            body.dailyDmCap = Number.isFinite(dailyDmCap) ? Math.max(0, dailyDmCap) : 0;
            onSubmit(body);
          }}>
          {submitting ? 'Guardando…' : 'Guardar'}
        </button>
      </div>
    </Modal>
  );
}

function TwoFaModal({
  account,
  submitting,
  onCancel,
  onSubmit
}: {
  account: IgAccount;
  submitting: boolean;
  onCancel: () => void;
  onSubmit: (code: string) => void;
}) {
  const [code, setCode] = useState('');
  return (
    <Modal title={`2FA para @${account.username}`} onCancel={onCancel}>
      <p className="text-xs text-slate-500 mb-2">
        Ingresá el código de 6 dígitos que te muestra tu app de autenticación.
        Solo funciona si la cuenta está esperando 2FA (ya disparaste Login y IG lo pidió).
      </p>
      <input
        className="input text-center text-xl tracking-widest font-mono"
        maxLength={6}
        value={code}
        onChange={(e) => setCode(e.target.value.replace(/[^0-9]/g, ''))}
        placeholder="000000"
      />
      <div className="flex justify-end gap-2 mt-4">
        <button className="text-xs px-3 py-1 rounded border border-slate-300" onClick={onCancel}>Cancelar</button>
        <button
          className="btn-primary"
          disabled={code.length !== 6 || submitting}
          onClick={() => onSubmit(code)}>
          {submitting ? 'Enviando…' : 'Enviar'}
        </button>
      </div>
    </Modal>
  );
}

function Modal({ title, children, onCancel }: { title: string; children: React.ReactNode; onCancel: () => void }) {
  return (
    <div className="fixed inset-0 z-50 bg-black/40 flex items-center justify-center p-4" onClick={onCancel}>
      <div className="bg-white rounded-lg p-5 max-w-md w-full" onClick={(e) => e.stopPropagation()}>
        <h3 className="font-semibold mb-3">{title}</h3>
        {children}
      </div>
    </div>
  );
}

function SellerSelect({
  value,
  onChange,
  sellers,
  label = 'Seller'
}: {
  value: string;
  onChange: (v: string) => void;
  sellers: Seller[];
  label?: string;
}) {
  return (
    <Field label={label}>
      <select className="input" value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">— elegir —</option>
        {sellers.map((s) => (
          <option key={s.id} value={s.id}>
            {s.displayName} ({s.sellerKey})
          </option>
        ))}
      </select>
    </Field>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="text-xs text-slate-500">{label}</label>
      {children}
    </div>
  );
}
