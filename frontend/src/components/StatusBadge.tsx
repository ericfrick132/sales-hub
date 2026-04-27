import clsx from 'clsx';
import type { LeadStatus } from '../lib/types';

const styles: Record<LeadStatus, string> = {
  New: 'bg-slate-100 text-slate-700',
  Assigned: 'bg-blue-100 text-blue-700',
  Queued: 'bg-indigo-100 text-indigo-700',
  Sent: 'bg-purple-100 text-purple-700',
  Replied: 'bg-amber-100 text-amber-800',
  Interested: 'bg-emerald-100 text-emerald-700',
  DemoScheduled: 'bg-teal-100 text-teal-700',
  Closed: 'bg-green-100 text-green-800',
  Lost: 'bg-rose-100 text-rose-700',
  Blocked: 'bg-zinc-200 text-zinc-700'
};

export default function StatusBadge({ status }: { status: LeadStatus }) {
  return <span className={clsx('badge', styles[status])}>{status}</span>;
}
