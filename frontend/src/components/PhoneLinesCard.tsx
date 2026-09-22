import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import { useAuthStore } from '../lib/auth';
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
  /** Cargar también los chats de antes de escanear. */
  importHistory: boolean;
  historyImportPasses: number;
  historyImportStartedAt?: string | null;
  historyImportedAt?: string | null;
  historyImportedMessages: number;
  /** Vendedores que se reparten los contactos del historial como prospectos para llamar. */
  prospectSellerIds: string[];
  /** Palabras que dejan un chat afuera (delivery, bancos, familia...). */
  prospectSkipWords: string[];
  /** Prospectos de remarketing que ya cargó este teléfono. */
  prospects: number;
  /** Avance de la pasada en curso: chats a recorrer y chats recorridos. */
  historyTotalChats: number;
  historyDoneChats: number;
}
interface Product { productKey: string; displayName: string }
interface Seller { id: string; displayName: string; isActive?: boolean }

type Draft = {
  label: string; productKey: string; extraProductKeys: string[]; listenOnly: boolean; importHistory: boolean;
  prospectSellerIds: string[]; prospectSkipWords: string;
};
const EMPTY: Draft = {
  label: '', productKey: '', extraProductKeys: [], listenOnly: true, importHistory: true,
  prospectSellerIds: [], prospectSkipWords: ''
};

/** ¿La carga del historial está corriendo AHORA? */
const isImporting = (l: PhoneLine) =>
  l.importHistory && !!l.historyImportStartedAt
  && (!l.historyImportedAt || l.historyImportStartedAt > l.historyImportedAt);

/** En qué va la carga del historial de un teléfono (null si no se pidió). */
function historyStatus(l: PhoneLine): string | null {
  if (!l.importHistory) return null;
  const n = l.historyImportedMessages.toLocaleString('es-AR');
  const p = l.prospects > 0 ? ` · ${l.prospects.toLocaleString('es-AR')} prospectos en el CRM` : '';
  if (isImporting(l)) {
    const chats = l.historyTotalChats > 0
      ? `${l.historyDoneChats.toLocaleString('es-AR')} de ${l.historyTotalChats.toLocaleString('es-AR')} chats`
      : 'buscando los chats…';
    return l.historyImportPasses === 0
      ? `Cargando el historial: ${chats}${p}`
      : `Repasando por si llegaron más chats: ${chats}${p}`;
  }
  if (l.historyImportPasses >= 3) return `Historial cargado (${n} mensajes)${p}`;
  if (l.historyImportPasses > 0)
    return `Historial cargado (${n} mensajes)${p} · repasa de nuevo más tarde por si el celu manda más chats`;
  return l.status === 'Connected'
    ? 'El historial se empieza a cargar a los 3 minutos de conectar'
    : 'El historial se carga cuando escanees el QR';
}

/** Barra de avance de la carga (chats recorridos sobre el total del teléfono). */
function HistoryProgress({ line }: { line: PhoneLine }) {
  if (!isImporting(line)) return null;
  const pct = line.historyTotalChats > 0
    ? Math.min(100, Math.round((line.historyDoneChats / line.historyTotalChats) * 100))
    : null;
  return (
    <div className="mt-1 flex items-center gap-2">
      <div className="h-1.5 flex-1 rounded-full bg-slate-200 overflow-hidden max-w-xs">
        <div
          className={`h-full bg-emerald-500 transition-all duration-500 ${pct === null ? 'animate-pulse w-1/4' : ''}`}
          style={pct === null ? undefined : { width: `${pct}%` }}
        />
      </div>
      <span className="text-[11px] text-slate-500 tabular-nums">{pct === null ? '…' : `${pct}%`}</span>
    </div>
  );
}

const appName = (products: Product[], key?: string | null) =>
  products.find(p => p.productKey === key)?.displayName?.trim() || key || '';

/**
 * CRUD de los teléfonos que se vinculan escaneando un QR. Agregar uno crea la línea y abre el
 * QR en el acto; queda registrado con su nombre, las apps que atiende y el candado de solo
 * escucha. De acá salen los leads del gráfico de /entradas y del reporte diario.
 */
export default function PhoneLinesCard() {
  const qc = useQueryClient();
  const me = useAuthStore(s => s.user?.sellerId) ?? null;
  const [adding, setAdding] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft>(EMPTY);
  const [saving, setSaving] = useState(false);
  const [qrFor, setQrFor] = useState<PhoneLine | null>(null);

  const { data: lines, isLoading } = useQuery({
    queryKey: ['phone-lines'],
    queryFn: async () => (await api.get<PhoneLine[]>('/phone-lines')).data,
    // Mientras una carga corre, la barra tiene que moverse: se pregunta más seguido.
    refetchInterval: (q) => (q.state.data ?? []).some(isImporting) ? 4_000 : 15_000
  });
  const { data: sellers = [] } = useQuery({
    queryKey: ['sellers-min'],
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data,
    staleTime: 5 * 60_000,
    select: (rows) => rows.filter(s => s.isActive !== false)
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
    setDraft({
      ...EMPTY,
      productKey: products[0]?.productKey ?? '',
      // Quien escanea el teléfono se lleva sus prospectos salvo que elija otra cosa.
      prospectSellerIds: me ? [me] : []
    });
    setAdding(true);
  }

  function startEdit(l: PhoneLine) {
    setAdding(false);
    setDraft({
      label: l.label,
      productKey: l.productKey ?? products[0]?.productKey ?? '',
      extraProductKeys: l.extraProductKeys,
      listenOnly: l.listenOnly,
      importHistory: l.importHistory,
      prospectSellerIds: l.prospectSellerIds,
      prospectSkipWords: l.prospectSkipWords.join(', ')
    });
    setEditingId(l.id);
  }

  // Si el form se abrió antes de que cargaran las apps, el select muestra la primera pero el
  // borrador no la tiene: se toma la que se ve.
  const mainKey = draft.productKey || products[0]?.productKey || '';

  async function save() {
    if (!draft.label.trim()) return toast.error('Poné un nombre para reconocer el teléfono');
    if (!mainKey) return toast.error('Elegí qué app atiende');
    const body = {
      ...draft,
      productKey: mainKey,
      extraProductKeys: draft.extraProductKeys.filter(k => k !== mainKey),
      prospectSkipWords: draft.prospectSkipWords.split(',').map(w => w.trim()).filter(Boolean)
    };
    setSaving(true);
    try {
      if (editingId) {
        await api.put(`/phone-lines/${editingId}`, body);
        toast.success('Teléfono actualizado');
        setEditingId(null);
      } else {
        const { data } = await api.post<PhoneLine>('/phone-lines', body);
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

  async function importNow(l: PhoneLine) {
    if (l.prospectSellerIds.length === 0
      && !confirm('Este teléfono no tiene a nadie tildado para tomar los prospectos: se van a cargar los chats pero NO se van a crear leads para llamar. ¿Seguir igual?')) return;
    try {
      await api.post(`/phone-lines/${l.id}/import-now`);
      toast.success('Cargando: en un minuto arranca y vas a ver la barra');
      refresh();
    } catch (e: any) {
      toast.error(e.response?.data?.error ?? 'No se pudo arrancar la carga');
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
          value={mainKey}
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
          {products.filter(p => p.productKey !== mainKey).map(p => (
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
      <label className="text-xs text-slate-600 flex items-start gap-1.5 cursor-pointer">
        <input
          type="checkbox"
          className="mt-0.5"
          checked={draft.importHistory}
          onChange={e => setDraft({ ...draft, importHistory: e.target.checked })}
        />
        <span>
          <span className={draft.importHistory ? 'text-emerald-700 font-medium' : ''}>
            Cargar también los chats de antes (para atrás)
          </span>
          <span className="block text-slate-400">
            Trae TODOS los números que tenga el celu (aunque WhatsApp todavía no haya bajado esa charla) con sus
            últimos 10 mensajes de contexto. El bot no les escribe por esas charlas viejas.
          </span>
        </span>
      </label>
      <div className={draft.importHistory ? '' : 'opacity-50 pointer-events-none'}>
        <div className="text-xs text-slate-600">
          Los contactos del historial entran al CRM como prospectos de <b>remarketing</b> para llamar. Los toman:
        </div>
        <div className="flex flex-wrap gap-x-3 gap-y-1 mt-0.5">
          {sellers.map(v => (
            <label key={v.id} className="text-xs text-slate-600 flex items-center gap-1 cursor-pointer">
              <input
                type="checkbox"
                checked={draft.prospectSellerIds.includes(v.id)}
                onChange={e => setDraft({
                  ...draft,
                  prospectSellerIds: e.target.checked
                    ? [...draft.prospectSellerIds, v.id]
                    : draft.prospectSellerIds.filter(id => id !== v.id)
                })}
              />
              {v.displayName}
            </label>
          ))}
        </div>
        <div className="text-[11px] text-slate-400 mt-0.5">
          {draft.prospectSellerIds.length === 0
            ? 'Sin nadie tildado no se crean prospectos: los chats entran a Conversaciones y nada más.'
            : 'Se reparten parejo entre los tildados y quedan listos en el modo llamadas del CRM. Por WhatsApp no les sale nada: estos leads no reciben cadencia.'}
        </div>
        <input
          className="input mt-1.5 text-xs"
          placeholder="Dejar afuera los chats que digan: delivery, banco, mamá…"
          value={draft.prospectSkipWords}
          onChange={e => setDraft({ ...draft, prospectSkipWords: e.target.value })}
        />
        <div className="text-[11px] text-slate-400 mt-0.5">
          Separadas por coma. Si el nombre del contacto o alguno de sus mensajes tiene una de esas palabras, ese
          chat no entra (ni prospecto ni conversación).
        </div>
      </div>
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
                    {l.prospectSellerIds.length > 0 && (
                      <span> · prospectos para {l.prospectSellerIds
                        .map(id => sellers.find(v => v.id === id)?.displayName ?? '?')
                        .join(' y ')}</span>
                    )}
                  </div>
                  {historyStatus(l) && (
                    <div className="text-[11px] text-slate-500 truncate">{historyStatus(l)}</div>
                  )}
                  <HistoryProgress line={l} />
                </div>
                <div className="flex gap-1 shrink-0 flex-wrap justify-end">
                  {!connected && (
                    <button className="btn-primary text-xs" onClick={() => setQrFor(l)}>Escanear QR</button>
                  )}
                  {l.connectedAt && !isImporting(l) && (
                    <button className="btn-secondary text-xs" onClick={() => importNow(l)}>
                      Cargar contactos ahora
                    </button>
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
