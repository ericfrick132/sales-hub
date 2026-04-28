import { useState, useEffect } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import type { Product } from '../lib/types';

const EMPTY: Product = {
  id: '', productKey: '', displayName: '', active: true, country: 'AR', countryName: 'Argentina',
  regionCode: 'ar', language: 'es', phonePrefix: '54', categories: [], messageTemplate: '',
  checkoutUrl: '', priceDisplay: '', dailyLimit: 60, triggerHours: [10, 14, 18], requiresAssistedSale: false,
  googlePlacesDailyLeadCap: 60
};

export default function Products() {
  const qc = useQueryClient();
  const [selected, setSelected] = useState<Product | null>(null);
  const [draft, setDraft] = useState<Product>(EMPTY);

  const productsQ = useQuery({
    queryKey: ['products-admin'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  useEffect(() => { if (selected) setDraft(selected); }, [selected?.id]);

  async function save() {
    try {
      const body = draft;
      if (selected?.id) await api.put(`/products/${selected.id}`, body);
      else await api.post('/products', body);
      toast.success('Guardado');
      qc.invalidateQueries({ queryKey: ['products-admin'] });
      if (!selected?.id) { setDraft(EMPTY); setSelected(null); }
    } catch (err: any) { toast.error(err.response?.data?.error ?? 'Falló'); }
  }

  function onChange<K extends keyof Product>(k: K, v: Product[K]) {
    setDraft((d) => ({ ...d, [k]: v }));
  }

  return (
    <div className="grid grid-cols-12 gap-6">
      <div className="col-span-4">
        <div className="flex items-center justify-between mb-2">
          <h2 className="text-xl font-bold">Aplicaciones</h2>
          <button className="btn-primary text-xs" onClick={() => { setSelected(null); setDraft(EMPTY); }}>+ Nuevo</button>
        </div>
        <div className="card divide-y divide-slate-100">
          {(productsQ.data ?? []).map((p) => (
            <button key={p.id} onClick={() => setSelected(p)}
              className={`w-full text-left p-3 hover:bg-slate-50 ${selected?.id === p.id ? 'bg-brand-50' : ''}`}>
              <div className="font-medium">{p.displayName}</div>
              <div className="text-xs text-slate-500">{p.productKey} — {p.active ? 'activo' : 'pausado'}</div>
            </button>
          ))}
        </div>
      </div>

      <div className="col-span-8 card p-5 space-y-3">
        <h3 className="font-semibold">{selected?.id ? `Editar: ${selected.displayName}` : 'Nuevo producto'}</h3>
        <div className="grid grid-cols-2 gap-3">
          <Field label="product_key">
            <input className="input" value={draft.productKey} onChange={(e) => onChange('productKey', e.target.value)} disabled={!!selected?.id} />
          </Field>
          <Field label="Nombre"><input className="input" value={draft.displayName} onChange={(e) => onChange('displayName', e.target.value)} /></Field>
          <Field label="País (code)"><input className="input" value={draft.country} onChange={(e) => onChange('country', e.target.value)} /></Field>
          <Field label="País (nombre)"><input className="input" value={draft.countryName} onChange={(e) => onChange('countryName', e.target.value)} /></Field>
          <Field label="Region code (maps)"><input className="input" value={draft.regionCode} onChange={(e) => onChange('regionCode', e.target.value)} /></Field>
          <Field label="Idioma"><input className="input" value={draft.language} onChange={(e) => onChange('language', e.target.value)} /></Field>
          <Field label="Prefix teléfono"><input className="input" value={draft.phonePrefix} onChange={(e) => onChange('phonePrefix', e.target.value)} /></Field>
          <Field label="Checkout URL"><input className="input" value={draft.checkoutUrl} onChange={(e) => onChange('checkoutUrl', e.target.value)} /></Field>
          <Field label="Precio display"><input className="input" value={draft.priceDisplay} onChange={(e) => onChange('priceDisplay', e.target.value)} /></Field>
          <Field label="Daily limit"><input type="number" className="input" value={draft.dailyLimit} onChange={(e) => onChange('dailyLimit', +e.target.value)} /></Field>
          <Field label="Leads/día Google Places (0 = sin tope)">
            <input type="number" className="input" value={draft.googlePlacesDailyLeadCap}
              onChange={(e) => onChange('googlePlacesDailyLeadCap', +e.target.value)} />
          </Field>
        </div>
        <Field label="Categorías de búsqueda Google Maps (coma)">
          <input className="input" value={draft.categories.join(', ')}
            onChange={(e) => onChange('categories', e.target.value.split(',').map((s) => s.trim()).filter(Boolean))} />
        </Field>
        <Field label="Trigger hours (coma, 0-23)">
          <input className="input" value={draft.triggerHours.join(', ')}
            onChange={(e) => onChange('triggerHours', e.target.value.split(',').map((s) => parseInt(s.trim())).filter((n) => !isNaN(n)))} />
        </Field>
        <Field label="Mensaje template">
          <textarea className="input min-h-48 font-mono text-sm"
            value={draft.messageTemplate} onChange={(e) => onChange('messageTemplate', e.target.value)} />
          <div className="text-xs text-slate-400 mt-1">
            Placeholders: <code>&#123;name&#125;</code>, <code>&#123;city&#125;</code>, <code>&#123;price&#125;</code>, <code>&#123;checkout_url&#125;</code>, <code>&#123;seller&#125;</code>.
            Spin-text: <code>&#123;Hola!|Qué tal!|Buenas!&#125;</code>
          </div>
        </Field>
        <div className="flex items-center justify-between gap-4">
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={draft.active} onChange={(e) => onChange('active', e.target.checked)} />
            Activo
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={draft.requiresAssistedSale} onChange={(e) => onChange('requiresAssistedSale', e.target.checked)} />
            Requiere venta asistida (demo con admin)
          </label>
          <button className="btn-primary" onClick={save}>Guardar</button>
        </div>
      </div>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="block"><div className="text-xs text-slate-500 mb-1">{label}</div>{children}</label>;
}
