import { useEffect, useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import { isAdmin, useAuthStore } from '../lib/auth';
import {
  LEAD_SOURCE_LABEL,
  LEAD_STATUS_LABEL,
  type Lead,
  type LeadSource,
  type LeadStatus,
  type Product,
  type Seller
} from '../lib/types';
import LeadTable from '../components/LeadTable';

type TabKey = 'mine' | 'pool';

const STATUSES: LeadStatus[] = ['Assigned', 'Queued', 'Sent', 'Replied', 'Interested', 'DemoScheduled', 'Closed', 'Lost', 'NoWhatsApp'];

const SOURCE_FILTER_OPTIONS: { value: string; label: string }[] = [
  { value: '', label: 'Todos' },
  { value: 'ads', label: 'Anuncios (Meta + WhatsApp)' },
  { value: 'ApifyGoogleMaps', label: 'Google Maps (Apify)' },
  { value: 'GooglePlaces', label: 'Google Places' },
  { value: 'ApifyInstagram', label: 'Instagram (Apify)' },
  { value: 'MetaLeadAd', label: 'Meta Lead Ads (form)' },
  { value: 'WhatsAppAd', label: 'WhatsApp Ad (CTWA)' },
  { value: 'ProductOnboarding', label: 'Onboarding / OTP' },
  { value: 'ProductReengage', label: 'Re-enganche producto' },
  { value: 'ApifyMetaAdsLibrary', label: 'Meta Ads (biblioteca)' },
  { value: 'ApifyFacebookPages', label: 'Facebook' },
  { value: 'ManualMaps', label: 'Manual · Maps' },
  { value: 'ManualInstagram', label: 'Manual · Instagram' },
  { value: 'ManualWhatsApp', label: 'Manual · WhatsApp' },
  { value: 'ManualWeb', label: 'Manual · Web' },
  { value: 'Manual', label: 'Manual (otro)' }
];

const SOURCE_OPTIONS: { value: string; label: string }[] = [
  { value: 'ManualMaps', label: 'Google Maps' },
  { value: 'ManualInstagram', label: 'Instagram' },
  { value: 'ManualWhatsApp', label: 'WhatsApp' },
  { value: 'ManualWeb', label: 'Web' }
];

const STATUS_OPTIONS: { value: LeadStatus; label: string }[] = [
  { value: 'Sent', label: 'Contactado' },
  { value: 'Interested', label: 'Interesado' },
  { value: 'DemoScheduled', label: 'Demo agendada' },
  { value: 'Closed', label: 'Cerrado' },
  { value: 'Lost', label: 'Perdido' }
];

const STORAGE_KEY = 'saleshub.lead-modal.defaults';

interface ModalDefaults {
  productKey?: string;
  source?: string;
  status?: LeadStatus;
}

function loadDefaults(): ModalDefaults {
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}');
  } catch {
    return {};
  }
}

function saveDefaults(d: ModalDefaults) {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(d));
}

export default function MyLeads() {
  const user = useAuthStore((s) => s.user);
  const admin = isAdmin(user);
  const [searchParams, setSearchParams] = useSearchParams();
  const initialTab: TabKey = (searchParams.get('tab') as TabKey) === 'pool' ? 'pool' : 'mine';
  const [tab, setTab] = useState<TabKey>(initialTab);
  // Filtros deep-linkables: se inicializan desde la URL (drill-down desde el dashboard) y se
  // reflejan de vuelta en la URL. Ej: /leads?product=gymhero&source=MetaLeadAd&source=WhatsAppAd
  const [status, setStatus] = useState<LeadStatus | ''>((searchParams.get('status') as LeadStatus) || '');
  const [productKey, setProductKey] = useState(searchParams.get('product') || '');
  const [sources, setSources] = useState<LeadSource[]>(() => searchParams.getAll('source') as LeadSource[]);
  const [showAdd, setShowAdd] = useState(false);
  const [showBulkReassign, setShowBulkReassign] = useState(false);
  const qc = useQueryClient();

  useEffect(() => {
    const sp = new URLSearchParams();
    if (tab === 'pool') sp.set('tab', 'pool');
    if (productKey) sp.set('product', productKey);
    if (status) sp.set('status', status);
    for (const s of sources) sp.append('source', s);
    setSearchParams(sp, { replace: true });
  }, [tab, productKey, status, sources, setSearchParams]);

  const products = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  const leadsQ = useQuery({
    queryKey: ['leads', tab, status, productKey, sources.join(',')],
    queryFn: async () => {
      const params = new URLSearchParams();
      if (productKey) params.set('productKey', productKey);
      if (tab === 'pool') {
        const { data } = await api.get<Lead[]>('/leads/pool', { params });
        return data;
      }
      if (status) params.set('status', status);
      // Filtro por fuente(s) en el backend (soporta varias, ej. anuncios = Meta + WhatsApp),
      // así no queda capado a las primeras 500 filas antes de filtrar.
      for (const s of sources) params.append('source', s);
      const { data } = await api.get<Lead[]>('/leads/mine', { params });
      return data;
    }
  });

  // El pool no filtra por fuente en el backend → lo hacemos acá; para 'mine' ya viene filtrado
  // (este filtro queda como no-op consistente).
  const leads = useMemo(() => {
    const all = leadsQ.data ?? [];
    if (sources.length === 0) return all;
    return all.filter((l) => sources.includes(l.source));
  }, [leadsQ.data, sources]);

  async function claim(leadId: string) {
    try {
      await api.post(`/leads/${leadId}/claim`);
      toast.success('Lead tomado');
      qc.invalidateQueries({ queryKey: ['leads'] });
    } catch (err) {
      const e = err as { response?: { data?: { error?: string } } };
      toast.error(e?.response?.data?.error ?? 'No se pudo tomar');
    }
  }

  const title = admin ? 'Leads del equipo' : 'Mis leads';
  const showTabs = admin;
  const showStatusFilter = tab === 'mine';
  const showClaim = tab === 'pool';

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">{title}</h1>
        <div className="flex gap-2">
          {admin && (
            <button
              className="btn-secondary"
              onClick={async () => {
                if (!confirm(
                  'Reasignar TODOS los leads sin contactar al vendedor DUEÑO de cada app (su whitelist), esté conectado o no.\n\n' +
                  '• Si el lead está pegado a un vendedor que no tiene esa app → lo mueve al que sí la tiene.\n' +
                  '• Si el dueño está conectado, sale a la cola; si está desconectado, queda asignado y sale solo cuando conecte.\n' +
                  '• Si ninguna app tiene dueño, o el lead no tiene app, lo deja en el pool y te avisa.\n\n' +
                  'No toca leads que ya arrancaron conversación. ¿Seguir?'
                )) return;
                try {
                  const { data } = await api.post<{
                    scanned: number; reassigned: number; queued: number; waitingSellerOffline: number;
                    alreadyOk: number; pooledNoOwner: number; noProduct: number; noOwnerByProduct: Record<string, number>
                  }>('/leads/reassign-by-owner');
                  const noOwner = Object.entries(data.noOwnerByProduct);
                  toast.success(
                    `Reasignados: ${data.reassigned} (${data.queued} a la cola · ${data.waitingSellerOffline} esperan que el vendedor conecte) · Ya ok: ${data.alreadyOk}` +
                    (data.noProduct > 0 ? ` · Sin app: ${data.noProduct}` : '') +
                    (noOwner.length > 0 ? ` · Sin vendedor para: ${noOwner.map(([k, v]) => `${k}(${v})`).join(', ')}` : ''),
                    { duration: 8000 }
                  );
                  qc.invalidateQueries({ queryKey: ['leads'] });
                } catch (err) {
                  const e = err as { response?: { data?: { error?: string } } };
                  toast.error(e?.response?.data?.error ?? 'Falló la reasignación');
                }
              }}>
              Reasignar todo
            </button>
          )}
          <Link to="/leads/import" className="btn-secondary">Importar de Maps</Link>
          <button className="btn-primary" onClick={() => setShowAdd(true)}>+ Cargar lead</button>
        </div>
      </div>

      {showTabs && (
        <div className="flex gap-1 border-b border-slate-200 overflow-x-auto">
          {(['mine', 'pool'] as TabKey[]).map((t) => (
            <button
              key={t}
              onClick={() => { setTab(t); setStatus(''); }}
              className={`px-4 py-2 text-sm border-b-2 ${
                tab === t
                  ? 'border-brand-600 text-brand-700 font-medium'
                  : 'border-transparent text-slate-500 hover:text-slate-700'
              }`}>
              {t === 'mine' ? 'Todos' : 'Sin asignar'}
            </button>
          ))}
        </div>
      )}

      <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-3 items-end">
        {showStatusFilter && (
          <div>
            <label className="text-xs text-slate-500">Estado</label>
            <select className="input" value={status} onChange={(e) => setStatus(e.target.value as LeadStatus)}>
              <option value="">Todos</option>
              {STATUSES.map((s) => <option key={s} value={s}>{LEAD_STATUS_LABEL[s]}</option>)}
            </select>
          </div>
        )}
        <div>
          <label className="text-xs text-slate-500">Producto</label>
          <select className="input" value={productKey} onChange={(e) => setProductKey(e.target.value)}>
            <option value="">Todos</option>
            {(products.data ?? []).map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
          </select>
        </div>
        <div>
          <label className="text-xs text-slate-500">Origen</label>
          <select
            className="input"
            value={
              sources.length === 0
                ? ''
                : sources.length === 2 && sources.includes('MetaLeadAd') && sources.includes('WhatsAppAd')
                  ? 'ads'
                  : sources[0]
            }
            onChange={(e) => {
              const v = e.target.value;
              setSources(v === '' ? [] : v === 'ads' ? (['MetaLeadAd', 'WhatsAppAd'] as LeadSource[]) : [v as LeadSource]);
            }}
          >
            {SOURCE_FILTER_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </div>
      </div>
      <div className="flex items-center gap-3 flex-wrap">
        <button className="btn-secondary text-sm" onClick={() => qc.invalidateQueries({ queryKey: ['leads'] })}>
          Refrescar
        </button>
        <div className="text-xs text-slate-500">
          {leadsQ.isLoading ? '' : `${leads.length} lead${leads.length === 1 ? '' : 's'}`}
        </div>
        {admin && tab === 'mine' && (sources.length > 0 || productKey || status) && (
          <button
            className="btn-secondary text-sm"
            onClick={() => setShowBulkReassign(true)}
            title="Reasigna TODOS los leads que matchean los filtros actuales a un vendedor elegido">
            Reasignar filtrados a un vendedor…
          </button>
        )}
      </div>

      {leadsQ.isLoading ? (
        <div>Cargando…</div>
      ) : (
        <LeadTable
          leads={leads}
          showSeller={admin}
          onClaim={showClaim ? claim : undefined}
          emptyText={tab === 'pool' ? 'El pool está vacío.' : 'No hay leads.'}
        />
      )}

      {showAdd && (
        <AddLeadModal
          products={products.data ?? []}
          onClose={() => setShowAdd(false)}
          onSaved={() => qc.invalidateQueries({ queryKey: ['leads'] })}
        />
      )}

      {showBulkReassign && (
        <BulkReassignModal
          sources={sources}
          productKey={productKey}
          status={status}
          products={products.data ?? []}
          onClose={() => setShowBulkReassign(false)}
          onDone={() => {
            setShowBulkReassign(false);
            qc.invalidateQueries({ queryKey: ['leads'] });
          }}
        />
      )}
    </div>
  );
}

interface BulkReassignResult {
  matched: number;
  alreadyOnTarget: number;
  movedUncontacted: number;
  movedContacted: number;
  skippedContacted: number;
  queued: number;
  waitingNoInstance: number;
  outboxCancelled: number;
  dryRun: boolean;
}

interface BulkReassignModalProps {
  sources: LeadSource[];
  productKey: string;
  status: LeadStatus | '';
  products: Product[];
  onClose: () => void;
  onDone: () => void;
}

function BulkReassignModal({ sources, productKey, status, products, onClose, onDone }: BulkReassignModalProps) {
  const [sellerId, setSellerId] = useState('');
  const [includeContacted, setIncludeContacted] = useState(true);
  const [autoQueue, setAutoQueue] = useState(true);
  const [running, setRunning] = useState(false);

  const sellersQ = useQuery({
    queryKey: ['sellers-for-assign'],
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data
  });

  const body = {
    sellerId,
    source: sources.length > 0 ? sources : null,
    productKey: productKey || null,
    status: status || null,
    includeContacted,
    autoQueue
  };

  // Preview real (dry-run en el backend): el listado de la página está capado a 200/500 filas,
  // acá contamos TODOS los que se moverían de verdad.
  const previewQ = useQuery({
    queryKey: ['bulk-reassign-preview', sellerId, sources.join(','), productKey, status, includeContacted, autoQueue],
    enabled: !!sellerId,
    queryFn: async () =>
      (await api.post<BulkReassignResult>('/leads/bulk-reassign', { ...body, dryRun: true })).data
  });

  const filterChips: string[] = [
    ...sources.map((s) => `Origen: ${LEAD_SOURCE_LABEL[s] ?? s}`),
    ...(productKey ? [`App: ${products.find((p) => p.productKey === productKey)?.displayName ?? productKey}`] : []),
    ...(status ? [`Estado: ${LEAD_STATUS_LABEL[status] ?? status}`] : [])
  ];

  const preview = previewQ.data;
  const toMove = preview ? preview.movedUncontacted + preview.movedContacted : 0;

  const run = async () => {
    if (!sellerId) return toast.error('Elegí un vendedor');
    setRunning(true);
    try {
      const { data } = await api.post<BulkReassignResult>('/leads/bulk-reassign', { ...body, dryRun: false });
      toast.success(
        `Reasignados ${data.movedUncontacted + data.movedContacted} leads` +
        ` (${data.queued} a la cola · ${data.movedContacted} con conversación solo cambian de dueño)` +
        (data.skippedContacted > 0 ? ` · ${data.skippedContacted} contactados sin tocar` : '') +
        (data.alreadyOnTarget > 0 ? ` · ${data.alreadyOnTarget} ya eran de ese vendedor` : ''),
        { duration: 8000 }
      );
      onDone();
    } catch (err) {
      const e = err as { response?: { data?: { error?: string } } };
      toast.error(e?.response?.data?.error ?? 'Falló la reasignación masiva');
    } finally {
      setRunning(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 bg-black/40 flex items-center justify-center p-4" onClick={onClose}>
      <div className="bg-white rounded-xl shadow-xl max-w-lg w-full p-6 max-h-[90vh] overflow-y-auto" onClick={(e) => e.stopPropagation()}>
        <h2 className="text-xl font-bold mb-1">Reasignar filtrados a un vendedor</h2>
        <p className="text-sm text-slate-500 mb-4">
          Mueve TODOS los leads que matchean los filtros actuales (no solo los que se ven en la tabla).
        </p>

        <div className="flex flex-wrap gap-1 mb-4">
          {filterChips.map((c) => (
            <span key={c} className="text-xs bg-slate-100 border border-slate-200 rounded px-2 py-0.5">{c}</span>
          ))}
        </div>

        <div className="space-y-3">
          <div>
            <label className="text-xs text-slate-500">Vendedor destino *</label>
            <select className="input w-full" value={sellerId} onChange={(e) => setSellerId(e.target.value)}>
              <option value="">— Elegir vendedor —</option>
              {(sellersQ.data ?? []).filter((s) => s.isActive).map((s) => {
                const ready = s.sendingEnabled && s.instanceStatus === 'Connected';
                return (
                  <option key={s.id} value={s.id}>
                    {s.displayName} {ready ? '✓' : ''} · {s.instanceStatus ?? 'sin instancia'} · envío {s.sendingEnabled ? 'on' : 'off'}
                  </option>
                );
              })}
            </select>
          </div>

          <label className="flex items-start gap-2 text-sm cursor-pointer">
            <input type="checkbox" className="mt-0.5" checked={includeContacted} onChange={(e) => setIncludeContacted(e.target.checked)} />
            <span>
              <span className="font-medium">Incluir leads con conversación</span>
              <span className="block text-xs text-slate-600">
                Contactados/respondieron: solo cambian de dueño (se cancelan los envíos pendientes
                de la línea anterior; la charla la sigue el vendedor nuevo a mano).
              </span>
            </span>
          </label>

          <label className="flex items-start gap-2 text-sm cursor-pointer">
            <input type="checkbox" className="mt-0.5" checked={autoQueue} onChange={(e) => setAutoQueue(e.target.checked)} />
            <span>
              <span className="font-medium">Encolar cadencia a los sin contactar</span>
              <span className="block text-xs text-slate-600">
                Los que nunca recibieron mensaje salen por la línea del vendedor nuevo en su próximo tick.
              </span>
            </span>
          </label>

          {sellerId && (
            <div className="text-sm bg-slate-50 border border-slate-200 rounded p-3">
              {previewQ.isLoading ? (
                'Contando…'
              ) : preview ? (
                <>
                  <div className="font-semibold">Se van a mover {toMove} leads</div>
                  <div className="text-xs text-slate-600 mt-1">
                    {preview.movedUncontacted} sin contactar ({preview.queued} irían a la cola) ·{' '}
                    {includeContacted
                      ? `${preview.movedContacted} con conversación`
                      : `${preview.skippedContacted} contactados quedan como están`} ·{' '}
                    {preview.alreadyOnTarget} ya son de ese vendedor
                    {preview.outboxCancelled > 0 ? ` · ${preview.outboxCancelled} envíos pendientes se cancelan` : ''}
                  </div>
                </>
              ) : (
                'No se pudo calcular el preview'
              )}
            </div>
          )}
        </div>

        <div className="flex gap-2 justify-end pt-4">
          <button type="button" className="btn-secondary" onClick={onClose} disabled={running}>Cancelar</button>
          <button
            type="button"
            className="btn-primary"
            disabled={!sellerId || running || previewQ.isLoading || toMove === 0}
            onClick={run}>
            {running ? 'Reasignando…' : `Reasignar${preview ? ` ${toMove} leads` : ''}`}
          </button>
        </div>
      </div>
    </div>
  );
}

interface SimilarLead {
  id: string;
  name: string;
  productKey: string;
  productName?: string;
  status: LeadStatus;
  sellerId?: string;
  sellerName?: string;
  createdAt: string;
}

interface AddLeadModalProps {
  products: Product[];
  onClose: () => void;
  onSaved: () => void;
}

function AddLeadModal({ products, onClose, onSaved }: AddLeadModalProps) {
  const activeProducts = products.filter((p) => p.active);
  const defaults = loadDefaults();
  const [name, setName] = useState('');
  const [productKey, setProductKey] = useState(defaults.productKey ?? activeProducts[0]?.productKey ?? '');
  const [source, setSource] = useState<string>(defaults.source ?? 'ManualMaps');
  const [leadStatus, setLeadStatus] = useState<LeadStatus>(defaults.status ?? 'Sent');
  const [city, setCity] = useState('');
  const [whatsappPhone, setWhatsappPhone] = useState('');
  const [instagramHandle, setInstagramHandle] = useState('');
  const [website, setWebsite] = useState('');
  const [notes, setNotes] = useState('');
  const [saving, setSaving] = useState(false);
  const [suggestions, setSuggestions] = useState<SimilarLead[]>([]);
  const [ignoredDup, setIgnoredDup] = useState(false);
  const [autoQueue, setAutoQueue] = useState(false);

  useEffect(() => {
    saveDefaults({ productKey, source, status: leadStatus });
  }, [productKey, source, leadStatus]);

  useEffect(() => {
    const trimmed = name.trim();
    if (trimmed.length < 3) {
      setSuggestions([]);
      return;
    }
    const handle = setTimeout(async () => {
      try {
        const { data } = await api.get<SimilarLead[]>('/leads/search', { params: { q: trimmed } });
        setSuggestions(data);
        setIgnoredDup(false);
      } catch {
        setSuggestions([]);
      }
    }, 300);
    return () => clearTimeout(handle);
  }, [name]);

  const reset = (keepDefaults = true) => {
    setName('');
    setCity('');
    setWhatsappPhone('');
    setInstagramHandle('');
    setWebsite('');
    setNotes('');
    setSuggestions([]);
    setIgnoredDup(false);
    if (!keepDefaults) {
      setProductKey(activeProducts[0]?.productKey ?? '');
      setSource('ManualMaps');
      setLeadStatus('Sent');
    }
  };

  const doSave = async (closeAfter: boolean) => {
    if (!name.trim()) return toast.error('Falta el nombre');
    if (!productKey) return toast.error('Falta el producto');
    if (suggestions.length > 0 && !ignoredDup) {
      return toast.error('Hay leads parecidos — revisalos o tildá "Cargar igual"');
    }
    setSaving(true);
    try {
      await api.post('/leads', {
        name: name.trim(),
        productKey,
        source,
        status: leadStatus,
        city: city || null,
        whatsappPhone: whatsappPhone || null,
        instagramHandle: instagramHandle || null,
        website: website || null,
        notes: notes || null,
        autoQueue
      });
      toast.success('Lead cargado');
      onSaved();
      if (closeAfter) {
        onClose();
      } else {
        reset();
      }
    } catch (err: unknown) {
      const e = err as { response?: { data?: { error?: string } } };
      toast.error(e?.response?.data?.error ?? 'No se pudo guardar');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 bg-black/40 flex items-center justify-center p-4" onClick={onClose}>
      <div className="bg-white rounded-xl shadow-xl max-w-lg w-full p-6 max-h-[90vh] overflow-y-auto" onClick={(e) => e.stopPropagation()}>
        <h2 className="text-xl font-bold mb-4">Cargar lead</h2>
        <form onSubmit={(e) => { e.preventDefault(); doSave(true); }} className="space-y-3">
          <div>
            <label className="text-xs text-slate-500">Nombre del negocio *</label>
            <input
              className="input w-full"
              value={name}
              onChange={(e) => setName(e.target.value)}
              autoFocus
              required
              placeholder="Ej. Fitness King" />
            {suggestions.length > 0 && (
              <div className="mt-2 border border-amber-300 bg-amber-50 rounded p-2 text-xs space-y-1">
                <div className="font-semibold text-amber-800">
                  Ya hay {suggestions.length} lead{suggestions.length === 1 ? '' : 's'} parecido{suggestions.length === 1 ? '' : 's'}:
                </div>
                <ul className="space-y-1">
                  {suggestions.map((s) => (
                    <li key={s.id} className="flex justify-between gap-2">
                      <span className="font-medium">{s.name}</span>
                      <span className="text-slate-500">
                        {s.sellerName ?? 'Sin vendedor'} · {s.productName ?? s.productKey} · {LEAD_STATUS_LABEL[s.status]}
                      </span>
                    </li>
                  ))}
                </ul>
                <label className="flex items-center gap-2 pt-1 cursor-pointer">
                  <input type="checkbox" checked={ignoredDup} onChange={(e) => setIgnoredDup(e.target.checked)} />
                  <span>Cargar igual (no es duplicado)</span>
                </label>
              </div>
            )}
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="text-xs text-slate-500">Aplicación *</label>
              <select className="input w-full" value={productKey} onChange={(e) => setProductKey(e.target.value)} required>
                {activeProducts.map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
              </select>
            </div>
            <div>
              <label className="text-xs text-slate-500">Origen *</label>
              <select className="input w-full" value={source} onChange={(e) => setSource(e.target.value)}>
                {SOURCE_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
              </select>
            </div>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="text-xs text-slate-500">Estado *</label>
              <select
                className="input w-full"
                value={autoQueue ? 'Assigned' : leadStatus}
                onChange={(e) => setLeadStatus(e.target.value as LeadStatus)}
                disabled={autoQueue}
                title={autoQueue ? 'Con auto-encolar el estado se fuerza a Assigned' : undefined}>
                {STATUS_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
              </select>
            </div>
            <div>
              <label className="text-xs text-slate-500">Ciudad</label>
              <input className="input w-full" value={city} onChange={(e) => setCity(e.target.value)} />
            </div>
          </div>
          <div>
            <label className="text-xs text-slate-500">WhatsApp</label>
            <input className="input w-full" value={whatsappPhone} onChange={(e) => setWhatsappPhone(e.target.value)} placeholder="+54911..." />
          </div>
          <div>
            <label className="text-xs text-slate-500">Instagram</label>
            <input className="input w-full" value={instagramHandle} onChange={(e) => setInstagramHandle(e.target.value)} placeholder="@handle" />
          </div>
          <div>
            <label className="text-xs text-slate-500">Web</label>
            <input className="input w-full" value={website} onChange={(e) => setWebsite(e.target.value)} placeholder="https://..." />
          </div>
          <div>
            <label className="text-xs text-slate-500">Notas</label>
            <textarea className="input w-full" rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} />
          </div>
          <label className="flex items-start gap-2 text-sm bg-amber-50 border border-amber-200 rounded p-2 cursor-pointer">
            <input
              type="checkbox"
              className="mt-0.5"
              checked={autoQueue}
              onChange={(e) => setAutoQueue(e.target.checked)} />
            <span>
              <span className="font-medium">Encolar cadencia automática</span>
              <span className="block text-xs text-slate-600">
                Manda los pasos del producto (mensajes + audios) al WhatsApp del lead. Requiere WhatsApp y vendedor con instancia conectada. Fuerza el estado a "Asignado".
              </span>
            </span>
          </label>
          <div className="flex flex-wrap gap-2 justify-end pt-2">
            <button type="button" className="btn-secondary" onClick={onClose} disabled={saving}>Cerrar</button>
            <button
              type="button"
              className="btn-secondary"
              onClick={() => doSave(false)}
              disabled={saving}
              title="Guarda y mantiene el modal abierto para cargar otro lead">
              {saving ? 'Guardando…' : 'Guardar y cargar otro'}
            </button>
            <button type="submit" className="btn-primary" disabled={saving}>
              {saving ? 'Guardando…' : 'Guardar'}
            </button>
          </div>
          <div className="text-xs text-slate-400">
            La aplicación, el origen y el estado quedan recordados para el próximo lead.
          </div>
        </form>
      </div>
    </div>
  );
}
