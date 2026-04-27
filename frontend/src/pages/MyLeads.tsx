import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import { LEAD_STATUS_LABEL, type Lead, type LeadStatus, type Product } from '../lib/types';
import LeadTable from '../components/LeadTable';

const STATUSES: LeadStatus[] = ['Assigned', 'Queued', 'Sent', 'Replied', 'Interested', 'DemoScheduled', 'Closed', 'Lost'];

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

export default function MyLeads() {
  const [status, setStatus] = useState<LeadStatus | ''>('');
  const [productKey, setProductKey] = useState('');
  const [showAdd, setShowAdd] = useState(false);
  const qc = useQueryClient();

  const products = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  const leadsQ = useQuery({
    queryKey: ['my-leads', status, productKey],
    queryFn: async () => {
      const params: Record<string, string> = {};
      if (status) params.status = status;
      if (productKey) params.productKey = productKey;
      const { data } = await api.get<Lead[]>('/leads/mine', { params });
      return data;
    }
  });

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Mis leads</h1>
        <button className="btn-primary" onClick={() => setShowAdd(true)}>+ Cargar lead</button>
      </div>
      <div className="flex gap-3 items-end">
        <div>
          <label className="text-xs text-slate-500">Estado</label>
          <select className="input" value={status} onChange={(e) => setStatus(e.target.value as LeadStatus)}>
            <option value="">Todos</option>
            {STATUSES.map((s) => <option key={s} value={s}>{LEAD_STATUS_LABEL[s]}</option>)}
          </select>
        </div>
        <div>
          <label className="text-xs text-slate-500">Producto</label>
          <select className="input" value={productKey} onChange={(e) => setProductKey(e.target.value)}>
            <option value="">Todos</option>
            {(products.data ?? []).map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
          </select>
        </div>
        <button className="btn-secondary" onClick={() => qc.invalidateQueries({ queryKey: ['my-leads'] })}>
          Refrescar
        </button>
      </div>
      {leadsQ.isLoading ? <div>Cargando…</div> : <LeadTable leads={leadsQ.data ?? []} />}

      {showAdd && (
        <AddLeadModal
          products={products.data ?? []}
          onClose={() => setShowAdd(false)}
          onSaved={() => {
            toast.success('Lead cargado');
            qc.invalidateQueries({ queryKey: ['my-leads'] });
            setShowAdd(false);
          }}
        />
      )}
    </div>
  );
}

interface AddLeadModalProps {
  products: Product[];
  onClose: () => void;
  onSaved: () => void;
}

function AddLeadModal({ products, onClose, onSaved }: AddLeadModalProps) {
  const activeProducts = products.filter((p) => p.active);
  const [name, setName] = useState('');
  const [productKey, setProductKey] = useState(activeProducts[0]?.productKey ?? '');
  const [source, setSource] = useState<string>('ManualMaps');
  const [leadStatus, setLeadStatus] = useState<LeadStatus>('Sent');
  const [city, setCity] = useState('');
  const [whatsappPhone, setWhatsappPhone] = useState('');
  const [instagramHandle, setInstagramHandle] = useState('');
  const [website, setWebsite] = useState('');
  const [notes, setNotes] = useState('');
  const [saving, setSaving] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return toast.error('Falta el nombre');
    if (!productKey) return toast.error('Falta el producto');
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
      onSaved();
    } catch (err: unknown) {
      const e = err as { response?: { data?: { error?: string } } };
      toast.error(e?.response?.data?.error ?? 'No se pudo guardar');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 bg-black/40 flex items-center justify-center p-4" onClick={onClose}>
      <div className="bg-white rounded-xl shadow-xl max-w-lg w-full p-6" onClick={(e) => e.stopPropagation()}>
        <h2 className="text-xl font-bold mb-4">Cargar lead</h2>
        <form onSubmit={submit} className="space-y-3">
          <div>
            <label className="text-xs text-slate-500">Nombre del negocio *</label>
            <input className="input w-full" value={name} onChange={(e) => setName(e.target.value)} autoFocus required />
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="text-xs text-slate-500">Producto *</label>
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
          <div className="flex gap-2 justify-end pt-2">
            <button type="button" className="btn-secondary" onClick={onClose} disabled={saving}>Cancelar</button>
            <button type="submit" className="btn-primary" disabled={saving}>
              {saving ? 'Guardando…' : 'Guardar'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
