import { useParams, useNavigate, Link } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import { LEAD_STATUS_LABEL, type Lead, type LeadStatus } from '../lib/types';
import StatusBadge from '../components/StatusBadge';

const STATUSES: LeadStatus[] = ['Assigned', 'Queued', 'Sent', 'Replied', 'Interested', 'DemoScheduled', 'Closed', 'Lost', 'Blocked'];

export default function LeadDetail() {
  const { id } = useParams();
  const nav = useNavigate();
  const qc = useQueryClient();
  const { data: lead, isLoading } = useQuery({
    queryKey: ['lead', id],
    queryFn: async () => {
      const { data } = await api.get<Lead[]>('/leads/mine');
      return data.find((l) => l.id === id) ?? null;
    }
  });

  const [notes, setNotes] = useState('');
  const [enriching, setEnriching] = useState(false);
  const [editing, setEditing] = useState(false);
  const [editName, setEditName] = useState('');
  const [editPhone, setEditPhone] = useState('');
  const [savingInfo, setSavingInfo] = useState(false);

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
      <div className="flex items-center gap-3">
        <button className="btn-secondary" onClick={() => nav(-1)}>← Volver</button>
        <h1 className="text-2xl font-bold">{lead.name}</h1>
        <StatusBadge status={lead.status} />
        {!editing && (
          <button className="btn-secondary ml-auto" onClick={startEdit}>Editar</button>
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

      <div className="card p-5 grid grid-cols-2 gap-4 text-sm">
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

      <div className="card p-5 space-y-3">
        <h3 className="font-semibold">Acciones</h3>
        <div className="flex flex-wrap gap-2">
          <button className="btn-primary" onClick={queueNow}>Encolar envío automático</button>
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
