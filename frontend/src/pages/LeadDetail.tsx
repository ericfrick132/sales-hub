import { useParams, useNavigate, Link } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import { LEAD_STATUS_LABEL, type Lead, type LeadStatus, type Seller } from '../lib/types';
import StatusBadge from '../components/StatusBadge';
import { isAdmin, useAuthStore } from '../lib/auth';

const STATUSES: LeadStatus[] = ['Assigned', 'Queued', 'Sent', 'Replied', 'Interested', 'DemoScheduled', 'Closed', 'Lost', 'Blocked'];

export default function LeadDetail() {
  const { id } = useParams();
  const nav = useNavigate();
  const qc = useQueryClient();
  const user = useAuthStore((s) => s.user);
  const admin = isAdmin(user);
  const { data: lead, isLoading } = useQuery({
    queryKey: ['lead', id],
    queryFn: async () => {
      const { data } = await api.get<Lead[]>('/leads/mine');
      return data.find((l) => l.id === id) ?? null;
    }
  });

  const sellersQ = useQuery({
    queryKey: ['sellers-for-assign'],
    enabled: admin,
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data
  });

  const [notes, setNotes] = useState('');
  const [enriching, setEnriching] = useState(false);
  const [editing, setEditing] = useState(false);
  const [editName, setEditName] = useState('');
  const [editPhone, setEditPhone] = useState('');
  const [savingInfo, setSavingInfo] = useState(false);
  const [assignSellerId, setAssignSellerId] = useState('');
  const [assignAutoQueue, setAssignAutoQueue] = useState(true);
  const [assigning, setAssigning] = useState(false);

  if (isLoading) return <div>Cargando…</div>;
  if (!lead) return <div className="card p-8 text-center">Lead no encontrado. <Link className="text-brand-600" to="/leads">Volver</Link></div>;

  async function update(newStatus: LeadStatus) {
    await api.patch(`/leads/${lead!.id}`, { status: newStatus, notes: notes || lead!.notes });
    toast.success('Actualizado');
    qc.invalidateQueries({ queryKey: ['lead', id] });
    qc.invalidateQueries({ queryKey: ['my-leads'] });
  }

  async function queueNow() {
    try {
      await api.post(`/leads/${lead!.id}/queue`, {});
      toast.success('Encolado');
      qc.invalidateQueries();
    } catch (err: any) {
      toast.error(err.response?.data?.error ?? 'Falló');
    }
  }

  async function assignTo() {
    if (!assignSellerId) return toast.error('Elegí un vendedor');
    setAssigning(true);
    try {
      await api.post(`/leads/${lead!.id}/assign`, { sellerId: assignSellerId, autoQueue: assignAutoQueue });
      toast.success('Asignado' + (assignAutoQueue ? ' + en cola' : ''));
      qc.invalidateQueries();
      setAssignSellerId('');
    } catch (err: any) {
      toast.error(err.response?.data?.error ?? 'Falló');
    } finally {
      setAssigning(false);
    }
  }

  function startEdit() {
    setEditName(lead!.name);
    setEditPhone(lead!.whatsappPhone ?? '');
    setEditing(true);
  }

  async function saveInfo() {
    setSavingInfo(true);
    try {
      await api.patch(`/leads/${lead!.id}/info`, {
        name: editName.trim(),
        whatsappPhone: editPhone.trim()
      });
      toast.success('Datos actualizados');
      setEditing(false);
      qc.invalidateQueries({ queryKey: ['lead', id] });
      qc.invalidateQueries({ queryKey: ['my-leads'] });
    } catch (err: any) {
      toast.error(err.response?.data?.error ?? 'Falló');
    } finally {
      setSavingInfo(false);
    }
  }

  async function enrich(kind: 'instagram' | 'website') {
    setEnriching(true);
    try {
      await api.post(`/leads/${lead!.id}/enrich/${kind}`);
      toast.success('Enriquecido');
      qc.invalidateQueries();
    } catch (err: any) {
      toast.error('Falló enrich');
    } finally { setEnriching(false); }
  }

  return (
    <div className="space-y-5 max-w-3xl">
      <div className="flex items-center gap-2 md:gap-3 flex-wrap">
        <button className="btn-secondary text-sm" onClick={() => nav(-1)}>← Volver</button>
        <h1 className="text-xl md:text-2xl font-bold break-words min-w-0">{lead.name}</h1>
        <StatusBadge status={lead.status} />
        {!editing && (
          <button className="btn-secondary text-sm ml-auto" onClick={startEdit}>Editar</button>
        )}
      </div>

      {editing && (
        <div className="card p-5 space-y-3">
          <h3 className="font-semibold">Editar datos del lead</h3>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <label className="text-sm">
              <div className="text-slate-500 mb-1">Nombre</div>
              <input className="input" value={editName} onChange={(e) => setEditName(e.target.value)} />
            </label>
            <label className="text-sm">
              <div className="text-slate-500 mb-1">WhatsApp (con código de país, sin +)</div>
              <input className="input" value={editPhone} onChange={(e) => setEditPhone(e.target.value)} placeholder="5491155555555" />
            </label>
          </div>
          <div className="flex gap-2">
            <button className="btn-primary" disabled={savingInfo || !editName.trim()} onClick={saveInfo}>
              {savingInfo ? 'Guardando…' : 'Guardar'}
            </button>
            <button className="btn-secondary" disabled={savingInfo} onClick={() => setEditing(false)}>Cancelar</button>
          </div>
        </div>
      )}

      <div className="card p-4 md:p-5 grid grid-cols-1 sm:grid-cols-2 gap-4 text-sm">
        <div>
          <dt className="text-slate-500">Producto</dt>
          <dd className="font-medium">{lead.productName ?? lead.productKey}</dd>
        </div>
        <div>
          <dt className="text-slate-500">Fuente</dt>
          <dd>{lead.source}</dd>
        </div>
        <div>
          <dt className="text-slate-500">Ciudad</dt>
          <dd>{lead.city ?? '—'}</dd>
        </div>
        <div>
          <dt className="text-slate-500">Rating</dt>
          <dd>{lead.rating ? `${lead.rating} (${lead.totalReviews ?? '?'} reviews)` : '—'}</dd>
        </div>
        <div>
          <dt className="text-slate-500">WhatsApp</dt>
          <dd>
            {lead.whatsappLink ? (
              <a href={lead.whatsappLink} target="_blank" rel="noreferrer" className="text-emerald-600 underline">
                Abrir chat ({lead.whatsappPhone})
              </a>
            ) : '—'}
          </dd>
        </div>
        <div>
          <dt className="text-slate-500">Website</dt>
          <dd>{lead.website ? <a href={lead.website} target="_blank" rel="noreferrer" className="text-brand-600">{lead.website}</a> : '—'}</dd>
        </div>
        <div>
          <dt className="text-slate-500">Instagram</dt>
          <dd>{lead.instagramHandle ? <a href={`https://instagram.com/${lead.instagramHandle}`} target="_blank" rel="noreferrer" className="text-brand-600">@{lead.instagramHandle}</a> : '—'}</dd>
        </div>
        <div>
          <dt className="text-slate-500">Asignado</dt>
          <dd>{lead.assignedAt ? new Date(lead.assignedAt).toLocaleString() : '—'}</dd>
        </div>
      </div>

      {lead.renderedMessage && (
        <div className="card p-5">
          <h3 className="font-semibold mb-2">Mensaje sugerido</h3>
          <pre className="whitespace-pre-wrap text-sm bg-slate-50 p-3 rounded border border-slate-200">{lead.renderedMessage}</pre>
        </div>
      )}

      <div className="card p-5 space-y-3">
        <h3 className="font-semibold">Actualizar estado</h3>
        <div className="flex flex-wrap gap-2">
          {STATUSES.map((s) => (
            <button key={s} className={`btn ${lead.status === s ? 'bg-brand-600 text-white' : 'bg-white border border-slate-300'}`}
              onClick={() => update(s)}>
              {LEAD_STATUS_LABEL[s]}
            </button>
          ))}
        </div>
        <textarea className="input min-h-24" placeholder="Notas (se guardan al cambiar status)"
          value={notes} onChange={(e) => setNotes(e.target.value)} />
        {lead.notes && <div className="text-xs text-slate-500">Previas: {lead.notes}</div>}
      </div>

      {admin && (
        <div className="card p-5 space-y-3">
          <h3 className="font-semibold">Asignar vendedor</h3>
          <div className="flex flex-wrap items-center gap-2">
            <select
              className="input"
              value={assignSellerId}
              onChange={(e) => setAssignSellerId(e.target.value)}>
              <option value="">— Elegir vendedor —</option>
              {(sellersQ.data ?? []).filter((s) => s.isActive).map((s) => {
                const ready = s.sendingEnabled && s.instanceStatus === 'Connected';
                return (
                  <option key={s.id} value={s.id}>
                    {s.displayName} {ready ? '✓' : ''} · {s.instanceStatus ?? 'sin instance'} · envío {s.sendingEnabled ? 'on' : 'off'}
                  </option>
                );
              })}
            </select>
            <label className="text-sm inline-flex items-center gap-1">
              <input type="checkbox" checked={assignAutoQueue} onChange={(e) => setAssignAutoQueue(e.target.checked)} />
              Encolar al asignar
            </label>
            <button className="btn-primary" disabled={!assignSellerId || assigning} onClick={assignTo}>
              {assigning ? 'Asignando…' : 'Asignar'}
            </button>
          </div>
          <div className="text-xs text-slate-500">
            Si "Encolar al asignar" está tildado y el vendedor tiene WhatsApp conectado + envío ON,
            se crea la fila en la cola y SalesHub lo manda en su próximo tick.
          </div>
        </div>
      )}

      <div className="card p-5 space-y-3">
        <h3 className="font-semibold">Acciones</h3>
        {(!lead.sellerId || !lead.whatsappPhone) && (
          <div className="text-xs text-amber-700 bg-amber-50 border border-amber-200 rounded px-3 py-2">
            No se puede encolar:
            <ul className="list-disc ml-5 mt-1">
              {!lead.sellerId && <li>el lead no está asignado a un vendedor.</li>}
              {!lead.whatsappPhone && <li>el lead no tiene número de WhatsApp.</li>}
            </ul>
          </div>
        )}
        <div className="flex flex-wrap gap-2">
          <button
            className="btn-primary"
            onClick={queueNow}
            disabled={!lead.sellerId || !lead.whatsappPhone}>
            Encolar envío automático
          </button>
          <button className="btn-secondary" disabled={enriching || !lead.instagramHandle} onClick={() => enrich('instagram')}>
            Enriquecer con Instagram
          </button>
          <button className="btn-secondary" disabled={enriching || !lead.website} onClick={() => enrich('website')}>
            Enriquecer desde website
          </button>
        </div>
      </div>
    </div>
  );
}
