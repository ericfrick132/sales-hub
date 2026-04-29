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
  type Product
} from '../lib/types';
import LeadTable from '../components/LeadTable';

type TabKey = 'mine' | 'pool';

const STATUSES: LeadStatus[] = ['Assigned', 'Queued', 'Sent', 'Replied', 'Interested', 'DemoScheduled', 'Closed', 'Lost'];

const SOURCE_FILTER_OPTIONS: { value: LeadSource | ''; label: string }[] = [
  { value: '', label: 'Todos' },
  { value: 'ApifyGoogleMaps', label: 'Google Maps (Apify)' },
  { value: 'GooglePlaces', label: 'Google Places' },
  { value: 'ApifyInstagram', label: 'Instagram (Apify)' },
  { value: 'ApifyMetaAdsLibrary', label: 'Meta Ads' },
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
  const [status, setStatus] = useState<LeadStatus | ''>('');
  const [productKey, setProductKey] = useState('');
  const [sourceFilter, setSourceFilter] = useState<LeadSource | ''>('');
  const [showAdd, setShowAdd] = useState(false);
  const qc = useQueryClient();

  useEffect(() => {
    if (tab === 'pool') setSearchParams({ tab: 'pool' }, { replace: true });
    else setSearchParams({}, { replace: true });
  }, [tab, setSearchParams]);

  const products = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  const leadsQ = useQuery({
    queryKey: ['leads', tab, status, productKey],
    queryFn: async () => {
      const params: Record<string, string> = {};
      if (productKey) params.productKey = productKey;
      if (tab === 'pool') {
        const { data } = await api.get<Lead[]>('/leads/pool', { params });
        return data;
      }
      if (status) params.status = status;
      const { data } = await api.get<Lead[]>('/leads/mine', { params });
      return data;
    }
  });

  // Filter by source on the client — small lists, avoids backend changes.
  const leads = useMemo(() => {
    const all = leadsQ.data ?? [];
    if (!sourceFilter) return all;
    return all.filter((l) => l.source === sourceFilter);
  }, [leadsQ.data, sourceFilter]);

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
          {admin && tab === 'pool' && (
            <button
              className="btn-secondary"
              onClick={async () => {
                if (!confirm('Reasignar todos los leads "Sin asignar" usando whitelist + regiones de los vendedores conectados?')) return;
                try {
                  const { data } = await api.post<{
                    scanned: number; assigned: number; queued: number; stillOrphanByProduct: Record<string, number>
                  }>('/leads/reassign-orphans?autoQueue=true');
                  const stuck = Object.entries(data.stillOrphanByProduct);
                  toast.success(
                    `Asignados: ${data.assigned} de ${data.scanned} · En cola: ${data.queued}` +
                    (stuck.length > 0 ? ` · Sin destino: ${stuck.map(([k, v]) => `${k}(${v})`).join(', ')}` : '')
                  );
                  qc.invalidateQueries({ queryKey: ['leads'] });
                } catch (err) {
                  const e = err as { response?: { data?: { error?: string } } };
                  toast.error(e?.response?.data?.error ?? 'Falló la reasignación');
                }
              }}>
              Reasignar todos
            </button>
          )}
          <Link to="/leads/import" className="btn-secondary">Importar de Maps</Link>
          <button className="btn-primary" onClick={() => setShowAdd(true)}>+ Cargar lead</button>
        </div>
      </div>

      {showTabs && (
        <div className="flex gap-1 border-b border-slate-200">
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

      <div className="flex gap-3 items-end flex-wrap">
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
          <select className="input" value={sourceFilter} onChange={(e) => setSourceFilter(e.target.value as LeadSource | '')}>
            {SOURCE_FILTER_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </div>
        <button className="btn-secondary" onClick={() => qc.invalidateQueries({ queryKey: ['leads'] })}>
          Refrescar
        </button>
        <div className="ml-auto text-xs text-slate-500 self-center">
          {leadsQ.isLoading ? '' : `${leads.length} lead${leads.length === 1 ? '' : 's'}`}
        </div>
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
        notes: notes || null
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
          <div className="grid grid-cols-2 gap-3">
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
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="text-xs text-slate-500">Estado *</label>
              <select className="input w-full" value={leadStatus} onChange={(e) => setLeadStatus(e.target.value as LeadStatus)}>
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
