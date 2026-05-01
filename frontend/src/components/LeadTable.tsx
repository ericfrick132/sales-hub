import { Link } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import { LEAD_SOURCE_LABEL, LEAD_STATUS_LABEL, type Lead, type LeadStatus } from '../lib/types';

type Props = {
  leads: Lead[];
  showSeller?: boolean;
  emptyText?: string;
  onClaim?: (leadId: string) => void;
};

const QUICK_STATUSES: LeadStatus[] = ['Sent', 'Interested', 'DemoScheduled', 'Closed', 'Lost'];

const fmtDateTime = (s?: string) => {
  if (!s) return '—';
  const d = new Date(s);
  const dd = String(d.getDate()).padStart(2, '0');
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const hh = String(d.getHours()).padStart(2, '0');
  const mi = String(d.getMinutes()).padStart(2, '0');
  return `${dd}/${mm} ${hh}:${mi}`;
};

const daysSince = (iso?: string) => {
  if (!iso) return 0;
  const ms = Date.now() - new Date(iso).getTime();
  return Math.max(0, Math.floor(ms / 86_400_000));
};

export default function LeadTable({ leads, showSeller, emptyText, onClaim }: Props) {
  const qc = useQueryClient();

  if (leads.length === 0) {
    return <div className="card p-8 text-center text-slate-500">{emptyText ?? 'No hay leads'}</div>;
  }

  const updateStatus = async (id: string, status: LeadStatus) => {
    try {
      await api.patch(`/leads/${id}`, { status, notes: null });
      qc.invalidateQueries({ queryKey: ['my-leads'] });
      qc.invalidateQueries({ queryKey: ['admin-dashboard'] });
      qc.invalidateQueries({ queryKey: ['my-dashboard'] });
    } catch {
      toast.error('No se pudo actualizar el estado');
    }
  };

  const StatusSelect = ({ l }: { l: Lead }) => (
    <select
      value={l.status}
      onChange={(e) => updateStatus(l.id, e.target.value as LeadStatus)}
      className="text-xs rounded border border-slate-300 bg-white px-2 py-1 focus:outline-none focus:ring-1 focus:ring-brand-500">
      {QUICK_STATUSES.map((s) => (
        <option key={s} value={s}>{LEAD_STATUS_LABEL[s]}</option>
      ))}
      {!QUICK_STATUSES.includes(l.status) && (
        <option value={l.status}>{LEAD_STATUS_LABEL[l.status]}</option>
      )}
    </select>
  );

  return (
    <>
      {/* Mobile: card list */}
      <div className="md:hidden space-y-2">
        {leads.map((l) => (
          <div key={l.id} className="card p-3 space-y-2">
            <div className="flex items-start justify-between gap-2">
              <div className="min-w-0">
                <Link to={`/leads/${l.id}`} className="font-medium text-brand-700 hover:underline break-words">
                  {l.name}
                </Link>
                {l.city && <div className="text-xs text-slate-400">{l.city}</div>}
              </div>
              <StatusSelect l={l} />
            </div>
            <div className="text-xs text-slate-500 flex flex-wrap gap-x-3 gap-y-1">
              <span>{fmtDateTime(l.sentAt ?? l.assignedAt ?? l.createdAt)}</span>
              <span>{l.productName ?? l.productKey}</span>
              <span>{LEAD_SOURCE_LABEL[l.source] ?? l.source}</span>
            </div>
            {showSeller && (
              <div className="text-xs">
                {l.sellerName ? (
                  <span className="text-slate-600">Vendedor: {l.sellerName}</span>
                ) : (
                  <span className="inline-flex items-center gap-1 text-amber-700 bg-amber-50 border border-amber-200 rounded px-2 py-0.5">
                    Sin asignar
                    {daysSince(l.createdAt) > 0 && <span className="text-amber-500">· {daysSince(l.createdAt)}d</span>}
                  </span>
                )}
              </div>
            )}
            <div className="flex flex-wrap items-center gap-2 pt-1 border-t border-slate-100">
              {l.whatsappLink ? (
                <a
                  href={l.whatsappLink}
                  target="_blank"
                  rel="noreferrer"
                  className="text-xs text-emerald-600 hover:underline truncate">
                  {l.whatsappPhone ?? 'WhatsApp'}
                </a>
              ) : (
                <span className="text-xs text-slate-500 truncate">{l.whatsappPhone ?? '—'}</span>
              )}
              {onClaim && !l.sellerId && (
                <button onClick={() => onClaim(l.id)} className="btn-secondary py-1 px-2 text-xs ml-auto">
                  Tomar
                </button>
              )}
            </div>
          </div>
        ))}
      </div>

      {/* Desktop: table */}
      <div className="card overflow-hidden hidden md:block">
        <div className="overflow-x-auto">
          <table className="min-w-full text-sm">
            <thead className="bg-slate-50 text-slate-600 text-xs uppercase tracking-wide">
              <tr>
                <th className="px-3 py-2 text-left">Fecha/hora</th>
                {showSeller && <th className="px-3 py-2 text-left">Vendedor</th>}
                <th className="px-3 py-2 text-left">Negocio</th>
                <th className="px-3 py-2 text-left">App</th>
                <th className="px-3 py-2 text-left">Origen</th>
                <th className="px-3 py-2 text-left">WhatsApp</th>
                <th className="px-3 py-2 text-left">Estado</th>
                {onClaim && <th className="px-3 py-2"></th>}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {leads.map((l) => (
                <tr key={l.id} className="hover:bg-slate-50">
                  <td className="px-3 py-2 text-slate-600 whitespace-nowrap">
                    {fmtDateTime(l.sentAt ?? l.assignedAt ?? l.createdAt)}
                  </td>
                  {showSeller && (
                    <td className="px-3 py-2 text-slate-700">
                      {l.sellerName ?? (
                        <span className="inline-flex items-center gap-1 text-amber-700 bg-amber-50 border border-amber-200 rounded px-2 py-0.5 text-xs">
                          Sin asignar
                          {daysSince(l.createdAt) > 0 && <span className="text-amber-500">· {daysSince(l.createdAt)}d</span>}
                        </span>
                      )}
                    </td>
                  )}
                  <td className="px-3 py-2 font-medium">
                    <Link to={`/leads/${l.id}`} className="text-brand-700 hover:underline">
                      {l.name}
                    </Link>
                    {l.city && <span className="ml-2 text-xs text-slate-400">{l.city}</span>}
                  </td>
                  <td className="px-3 py-2 text-slate-600">{l.productName ?? l.productKey}</td>
                  <td className="px-3 py-2 text-slate-600">{LEAD_SOURCE_LABEL[l.source] ?? l.source}</td>
                  <td className="px-3 py-2">
                    {l.whatsappLink ? (
                      <a href={l.whatsappLink} target="_blank" rel="noreferrer"
                         className="text-emerald-600 hover:underline">
                        {l.whatsappPhone}
                      </a>
                    ) : l.whatsappPhone ?? '—'}
                  </td>
                  <td className="px-3 py-2">
                    <StatusSelect l={l} />
                  </td>
                  {onClaim && (
                    <td className="px-3 py-2 text-right">
                      {!l.sellerId && (
                        <button onClick={() => onClaim(l.id)} className="btn-secondary py-1 px-2 text-xs">
                          Tomar
                        </button>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </>
  );
}
