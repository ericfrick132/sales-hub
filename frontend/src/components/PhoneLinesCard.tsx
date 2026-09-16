import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import QrConnectModal from './QrConnectModal';

/** Teléfono vinculado por QR, sin vendedor (instancia de Evolution). */
interface PhoneLine {
  id: string;
  instanceName: string;
  label: string;
  phone?: string | null;
  status: string;
  /** Candado: entra todo, no sale nada por esta línea. */
  listenOnly: boolean;
  productKey?: string | null;
  extraProductKeys: string[];
  connectedAt?: string | null;
  disconnectedAt?: string | null;
  createdAt: string;
  leadsToday: number;
  leads7d: number;
}
interface Product { productKey: string; displayName: string }

type Draft = { label: string; productKey: string; extraProductKeys: string[]; listenOnly: boolean };
const EMPTY: Draft = { label: '', productKey: '', extraProductKeys: [], listenOnly: true };

const appName = (products: Product[], key?: string | null) =>
  products.find(p => p.productKey === key)?.displayName?.trim() || key || '';

/**
 * CRUD de los teléfonos que se vinculan escaneando un QR. Agregar uno crea la línea y abre el
 * QR en el acto; queda registrado con su nombre, las apps que atiende y el candado de solo
 * escucha. De acá salen los leads del gráfico de /entradas y del reporte diario.
 */
export default function PhoneLinesCard() {
  const qc = useQueryClient();
  const [adding, setAdding] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft>(EMPTY);
  const [saving, setSaving] = useState(false);
  const [qrFor, setQrFor] = useState<PhoneLine | null>(null);

  const { data: lines, isLoading } = useQuery({
    queryKey: ['phone-lines'],
    queryFn: async () => (await api.get<PhoneLine[]>('/phone-lines')).data,
    refetchInterval: 15_000
  });
  const { data: products = [] } = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data,
    staleTime: 5 * 60_000,
    // Hay una fila de producto con key y nombre vacíos: no es una app real.
    select: (rows) => rows.filter(p => p.productKey?.trim())
  });

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['phone-lines'] });
    qc.invalidateQueries({ queryKey: ['app-wa-lines'] });
  };

  function startAdd() {
    setEditingId(null);
    setDraft({ ...EMPTY, productKey: products[0]?.productKey ?? '' });
    setAdding(true);
  }

  function startEdit(l: PhoneLine) {
    setAdding(false);
    setDraft({
      label: l.label,
      productKey: l.productKey ?? products[0]?.productKey ?? '',
      extraProductKeys: l.extraProductKeys,
      listenOnly: l.listenOnly
    });
    setEditingId(l.id);
  }

  async function save() {
    if (!draft.label.trim()) return toast.error('Poné un nombre para reconocer el teléfono');
    if (!draft.productKey) return toast.error('Elegí qué app atiende');
    setSaving(true);
    try {
      if (editingId) {
        await api.put(`/phone-lines/${editingId}`, draft);
        toast.success('Teléfono actualizado');
        setEditingId(null);
      } else {
        const { data } = await api.post<PhoneLine>('/phone-lines', draft);
        toast.success('Teléfono agregado: escaneá el QR');
        setAdding(false);
        // El QR se abre solo: agregar un teléfono ES escanearlo.
        setQrFor(data);
      }
      refresh();
    } catch (e: any) {
      toast.error(e.response?.data?.error ?? 'No se pudo guardar');
    } finally {
      setSaving(false);
    }
  }

  async function unlink(l: PhoneLine) {
    if (!confirm(`Desvincular "${l.label}"? Deja de recibir chats hasta que lo escanees de nuevo. Queda registrado.`)) return;
    try {
      await api.post(`/phone-lines/${l.id}/logout`);
      toast.success('Teléfono desvinculado');
      refresh();
    } catch {
      toast.error('No se pudo desvincular');
    }
  }

  async function remove(l: PhoneLine) {
    if (!confirm(`Eliminar "${l.label}"? Se cierra la sesión y se borra el registro. Los chats y leads que entraron por él quedan.`)) return;
    try {
      await api.delete(`/phone-lines/${l.id}`);
      toast.success('Teléfono eliminado');
      refresh();
    } catch {
      toast.error('No se pudo eliminar');
    }
  }

  const form = (
    <div className="mt-2 p-3 bg-slate-50 rounded space-y-2">
      <input
        className="input"
        placeholder="Nombre (ej: Celu Rosario)"
        value={draft.label}
        autoFocus
        onChange={e => setDraft({ ...draft, label: e.target.value })}
      />
      <label className="block text-xs text-slate-600">
        App principal (con esa se crea el lead cuando escribe un número nuevo)
        <select
          className="input mt-1"
          value={draft.productKey}
          onChange={e => setDraft({
            ...draft,
            productKey: e.target.value,
            extraProductKeys: draft.extraProductKeys.filter(k => k !== e.target.value)
          })}>
          {products.map(p => (
            <option key={p.productKey} value={p.productKey}>{p.displayName?.trim() || p.productKey}</option>
          ))}
        </select>
      </label>
      <div>
        <div className="text-xs text-slate-600">También atiende:</div>
        <div className="flex flex-wrap gap-x-3 gap-y-1 mt-0.5">
          {products.filter(p => p.productKey !== draft.productKey).map(p => (
            <label key={p.productKey} className="text-xs text-slate-600 flex items-center gap-1 cursor-pointer">
              <input
                type="checkbox"
                checked={draft.extraProductKeys.includes(p.productKey)}
                onChange={e => setDraft({
                  ...draft,
                  extraProductKeys: e.target.checked
                    ? [...draft.extraProductKeys, p.productKey]
                    : draft.extraProductKeys.filter(k => k !== p.productKey)
                })}
              />
              {p.displayName?.trim() || p.productKey}
            </label>
          ))}
        </div>
      </div>
      <label className="text-xs text-slate-600 flex items-center gap-1.5 cursor-pointer">
        <input
          type="checkbox"
          checked={draft.listenOnly}
          onChange={e => setDraft({ ...draft, listenOnly: e.target.checked })}
        />
        <span className={draft.listenOnly ? 'text-emerald-700 font-medium' : ''}>
          Solo escuchar (no sale ningún mensaje a leads por este número)
        </span>
      </label>
      <div className="flex gap-2">
        <button className="btn-primary text-xs" disabled={saving} onClick={save}>
          {editingId ? 'Guardar' : 'Agregar y escanear QR'}
        </button>
        <button className="btn-secondary text-xs" onClick={() => { setAdding(false); setEditingId(null); }}>
          Cancelar
        </button>
      </div>
    </div>
  );

  return (
    <div className="card p-4 mb-5">
      <div className="flex items-start justify-between gap-3 mb-3">
        <div>
          <h3 className="font-semibold">Teléfonos escaneados por QR</h3>
          <p className="text-xs text-slate-500">
            Cada teléfono se vincula como un dispositivo más de WhatsApp: sus chats entran a Conversaciones y al
            CRM, y cuentan en <Link to="/entradas" className="underline">Entradas por teléfono</Link> y en el
            reporte diario. No se le asigna vendedor.
          </p>
        </div>
        <button className="btn-primary text-xs shrink-0" onClick={startAdd}>+ Agregar teléfono</button>
      </div>

      {adding && form}

      <div className="divide-y divide-slate-100">
        {(lines ?? []).map(l => {
          const connected = l.status === 'Connected';
          return (
            <div key={l.id} className="py-2">
              <div className="flex items-center justify-between gap-3">
                <div className="min-w-0">
                  <div className="font-medium truncate">{l.label}</div>
                  <div className="text-xs text-slate-500 truncate">
                    <span className={connected ? 'text-emerald-600' : 'text-slate-400'}>
                      {connected ? '● Conectado' : l.status === 'Connecting' ? '○ Esperando el escaneo' : '○ Desconectado'}
                    </span>
                    {l.phone && ` · +${l.phone}`}
                    {' · '}
                    <span className="tabular-nums">{l.leadsToday} leads hoy · {l.leads7d} en 7 días</span>
                  </div>
                  <div className="text-[11px] text-slate-500 truncate">
                    {[l.productKey, ...l.extraProductKeys].filter(Boolean).map(k => appName(products, k)).join(', ') || 'sin app'}
                    {l.listenOnly && <span className="text-emerald-700"> · solo escucha</span>}
                  </div>
                </div>
                <div className="flex gap-1 shrink-0 flex-wrap justify-end">
                  {!connected && (
                    <button className="btn-primary text-xs" onClick={() => setQrFor(l)}>Escanear QR</button>
                  )}
                  <button className="btn-secondary text-xs"
                    onClick={() => editingId === l.id ? setEditingId(null) : startEdit(l)}>
                    Editar
                  </button>
                  {connected && (
                    <button className="btn-secondary text-xs" onClick={() => unlink(l)}>Desvincular</button>
                  )}
                  <button className="btn-danger text-xs" onClick={() => remove(l)}>Eliminar</button>
                </div>
              </div>
              {editingId === l.id && form}
            </div>
          );
        })}
        {!isLoading && (lines ?? []).length === 0 && !adding && (
          <div className="py-3 text-slate-400 text-sm text-center">No hay teléfonos cargados</div>
        )}
      </div>

      {qrFor && (
        <QrConnectModal
          title={`Vincular ${qrFor.label}`}
          fetchUrl={`/phone-lines/${qrFor.id}/qr`}
          onClose={() => setQrFor(null)}
          onConnected={refresh}
        />
      )}
    </div>
  );
}
