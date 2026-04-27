import { Link } from 'react-router-dom';
import type { Lead } from '../lib/types';
import StatusBadge from './StatusBadge';

type Props = {
  leads: Lead[];
  showSeller?: boolean;
  emptyText?: string;
  onClaim?: (leadId: string) => void;
};

export default function LeadTable({ leads, showSeller, emptyText, onClaim }: Props) {
  if (leads.length === 0) {
    return <div className="card p-8 text-center text-slate-500">{emptyText ?? 'No hay leads'}</div>;
  }
  return (
    <div className="card overflow-hidden">
      <table className="min-w-full text-sm">
        <thead className="bg-slate-50 text-slate-600 text-xs uppercase tracking-wide">
          <tr>
            <th className="px-3 py-2 text-left">Nombre</th>
            <th className="px-3 py-2 text-left">Ciudad</th>
            <th className="px-3 py-2 text-left">Producto</th>
            <th className="px-3 py-2 text-left">Fuente</th>
            <th className="px-3 py-2 text-left">WhatsApp</th>
            <th className="px-3 py-2 text-left">Status</th>
            {showSeller && <th className="px-3 py-2 text-left">Asignado</th>}
            <th className="px-3 py-2"></th>
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100">
          {leads.map((l) => (
            <tr key={l.id} className="hover:bg-slate-50">
              <td className="px-3 py-2 font-medium">
                <Link to={`/leads/${l.id}`} className="text-brand-700 hover:underline">
                  {l.name}
                </Link>
                {l.rating && <span className="ml-2 text-xs text-slate-400">★ {l.rating}</span>}
              </td>
              <td className="px-3 py-2">{l.city ?? '—'}</td>
              <td className="px-3 py-2">{l.productName ?? l.productKey}</td>
              <td className="px-3 py-2 text-xs text-slate-500">{l.source}</td>
              <td className="px-3 py-2">
                {l.whatsappLink ? (
                  <a href={l.whatsappLink} target="_blank" rel="noreferrer"
                     className="text-emerald-600 hover:underline">
                    {l.whatsappPhone}
                  </a>
                ) : l.whatsappPhone ?? '—'}
              </td>
              <td className="px-3 py-2"><StatusBadge status={l.status} /></td>
              {showSeller && <td className="px-3 py-2 text-slate-600">{l.sellerName ?? '—'}</td>}
              <td className="px-3 py-2 text-right">
                {onClaim && !l.sellerId && (
                  <button onClick={() => onClaim(l.id)} className="btn-secondary py-1 px-2 text-xs">
                    Tomar
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
