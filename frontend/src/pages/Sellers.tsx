import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import type { Seller, Product } from '../lib/types';
import GaugeEditor from '../components/GaugeEditor';

export default function Sellers() {
  const qc = useQueryClient();
  const [selected, setSelected] = useState<Seller | null>(null);
  const [showCreate, setShowCreate] = useState(false);

  const sellersQ = useQuery({
    queryKey: ['sellers'],
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data
  });
  const products = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  async function save(patch: Partial<Seller>) {
    if (!selected) return;
    await api.put(`/sellers/${selected.id}`, patch);
    toast.success('Guardado');
    qc.invalidateQueries({ queryKey: ['sellers'] });
  }

  return (
    <div className="grid grid-cols-12 gap-6">
      <div className="col-span-4">
        <div className="flex items-center justify-between mb-2">
          <h2 className="text-xl font-bold">Vendedores</h2>
          <div className="flex gap-2">
            <Link to="/sellers/zones" className="btn-secondary text-xs">Mapa de zonas</Link>
            <button className="btn-primary text-xs" onClick={() => setShowCreate(true)}>+ Nuevo</button>
          </div>
        </div>
        <div className="card divide-y divide-slate-100">
          {(sellersQ.data ?? []).map((s) => (
            <button
              key={s.id}
              onClick={() => setSelected(s)}
              className={`w-full text-left p-3 hover:bg-slate-50 ${selected?.id === s.id ? 'bg-brand-50' : ''}`}>
              <div className="font-medium">{s.displayName}</div>
              <div className="text-xs text-slate-500">{s.email} — {s.role}</div>
              <div className="text-xs text-slate-400 mt-1">
                {s.instanceStatus ?? 'sin instance'} · {s.sendingEnabled ? 'envío ON' : 'envío OFF'}
              </div>
            </button>
          ))}
        </div>
      </div>

      <div className="col-span-8">
        {selected ? (
          <div className="space-y-4">
            <div className="flex items-start justify-between">
              <div>
                <h2 className="text-xl font-bold">{selected.displayName}</h2>
                <p className="text-sm text-slate-500">{selected.email}</p>
              </div>
              <div className="flex gap-2">
                <Link to={`/sellers/zones?seller=${selected.id}`} className="btn-secondary text-xs">
                  Editar zonas (mapa)
                </Link>
                <button className="btn-secondary text-xs"
                  onClick={async () => {
                    const pwd = prompt('Nueva contraseña:');
                    if (!pwd) return;
                    await save({ password: pwd } as Partial<Seller>);
                    toast.success('Password actualizada');
                  }}>
                  Reset password
                </button>
                <button className="btn-danger text-xs"
                  onClick={async () => {
                    if (!confirm('Desactivar vendedor?')) return;
                    await api.delete(`/sellers/${selected.id}`);
                    qc.invalidateQueries({ queryKey: ['sellers'] });
                    setSelected(null);
                  }}>
                  Desactivar
                </button>
              </div>
            </div>

            <AssignmentEditor
              key={selected.id}
              seller={selected}
              products={products.data ?? []}
              onSave={save} />


            <div className="card p-5">
              <h3 className="font-semibold mb-3">Gauges humanización</h3>
              <GaugeEditor seller={selected} onSave={save} />
            </div>
          </div>
        ) : (
          <div className="card p-8 text-center text-slate-500">Seleccioná un vendedor</div>
        )}
      </div>

      {showCreate && <CreateModal
        products={products.data ?? []}
        onClose={() => setShowCreate(false)}
        onDone={() => { qc.invalidateQueries({ queryKey: ['sellers'] }); setShowCreate(false); }} />}
    </div>
  );
}

function AssignmentEditor({ seller, products, onSave }: {
  seller: Seller;
  products: Product[];
  onSave: (patch: Partial<Seller>) => Promise<void>;
}) {
  const [whitelist, setWhitelist] = useState<string[]>(seller.verticalsWhitelist);
  const [regionsRaw, setRegionsRaw] = useState((seller.regionsAssigned ?? []).join(', '));
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setWhitelist(seller.verticalsWhitelist);
    setRegionsRaw((seller.regionsAssigned ?? []).join(', '));
  }, [seller.id]);

  const initialRegions = (seller.regionsAssigned ?? []).join(', ');
  const dirty =
    whitelist.length !== seller.verticalsWhitelist.length ||
    whitelist.some((v) => !seller.verticalsWhitelist.includes(v)) ||
    seller.verticalsWhitelist.some((v) => !whitelist.includes(v)) ||
    regionsRaw.trim() !== initialRegions.trim();

  function toggle(productKey: string) {
    setWhitelist((prev) =>
      prev.includes(productKey)
        ? prev.filter((v) => v !== productKey)
        : [...prev, productKey]
    );
  }

  async function handleSave() {
    setSaving(true);
    try {
      const regions = regionsRaw
        .split(',')
        .map((r) => r.trim())
        .filter(Boolean);
      await onSave({ verticalsWhitelist: whitelist, regionsAssigned: regions });
    } finally {
      setSaving(false);
    }
  }

  function handleReset() {
    setWhitelist(seller.verticalsWhitelist);
    setRegionsRaw(initialRegions);
  }

  return (
    <div className="card p-5 space-y-4">
      <div>
        <div className="text-xs text-slate-500 mb-2">Verticals whitelist (productos que puede atender — vacío = ninguno para admins, todos para sellers)</div>
        <div className="flex flex-wrap gap-2">
          {products.map((p) => {
            const active = whitelist.includes(p.productKey);
            return (
              <button
                key={p.productKey}
                type="button"
                onClick={() => toggle(p.productKey)}
                className={`text-sm px-3 py-1 rounded-full border transition ${
                  active
                    ? 'bg-brand-600 text-white border-brand-600'
                    : 'bg-white text-slate-700 border-slate-300 hover:bg-slate-50'
                }`}>
                {p.displayName}
              </button>
            );
          })}
        </div>
      </div>

      <div>
        <div className="text-xs text-slate-500 mb-1">
          Regiones asignadas (ciudades o provincias, separadas por coma — vacío = catch-all)
        </div>
        <input
          className="input w-full"
          value={regionsRaw}
          onChange={(e) => setRegionsRaw(e.target.value)}
          placeholder="ej. Rosario, Santa Fe, CABA, Buenos Aires" />
        <div className="text-[11px] text-slate-400 mt-1">
          Match case-insensitive contra la ciudad o provincia del lead. City-level (ej. Rosario) gana sobre province-level si ambos matchean.
        </div>
      </div>

      <div className="flex items-center gap-2 pt-2 border-t border-slate-100">
        <button
          type="button"
          className="btn-primary"
          disabled={!dirty || saving}
          onClick={handleSave}>
          {saving ? 'Guardando…' : 'Guardar cambios'}
        </button>
        {dirty && !saving && (
          <button type="button" className="btn-secondary" onClick={handleReset}>
            Descartar
          </button>
        )}
        {dirty && <span className="text-xs text-amber-600">Hay cambios sin guardar</span>}
      </div>
    </div>
  );
}

function CreateModal({ onClose, onDone, products }: { onClose: () => void; onDone: () => void; products: Product[] }) {
  const [form, setForm] = useState({ sellerKey: '', displayName: '', email: '', password: '', whatsappPhone: '', verticals: [] as string[] });
  async function submit() {
    try {
      await api.post('/sellers', {
        sellerKey: form.sellerKey, displayName: form.displayName, email: form.email,
        password: form.password, whatsappPhone: form.whatsappPhone, verticalsWhitelist: form.verticals
      });
      toast.success('Creado');
      onDone();
    } catch (err: any) { toast.error(err.response?.data?.error ?? 'Falló'); }
  }
  return (
    <div className="fixed inset-0 bg-black/40 grid place-items-center z-50">
      <div className="card p-6 w-full max-w-md space-y-3">
        <h3 className="font-semibold">Nuevo vendedor</h3>
        <input className="input" placeholder="seller_key (ej juan)" value={form.sellerKey} onChange={(e) => setForm({ ...form, sellerKey: e.target.value })} />
        <input className="input" placeholder="Nombre" value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} />
        <input className="input" type="email" placeholder="Email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
        <input className="input" type="password" placeholder="Password inicial" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} />
        <input className="input" placeholder="WhatsApp personal (opc)" value={form.whatsappPhone} onChange={(e) => setForm({ ...form, whatsappPhone: e.target.value })} />
        <div className="text-xs text-slate-500">Productos que puede atender:</div>
        <div className="flex flex-wrap gap-2">
          {products.map((p) => (
            <label key={p.productKey} className="text-sm">
              <input type="checkbox" className="mr-1" checked={form.verticals.includes(p.productKey)}
                onChange={(e) => setForm({ ...form, verticals: e.target.checked
                  ? [...form.verticals, p.productKey]
                  : form.verticals.filter((v) => v !== p.productKey) })} />
              {p.displayName}
            </label>
          ))}
        </div>
        <div className="flex justify-end gap-2 pt-3">
          <button className="btn-secondary" onClick={onClose}>Cancelar</button>
          <button className="btn-primary" onClick={submit}>Crear</button>
        </div>
      </div>
    </div>
  );
}
