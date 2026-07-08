import { useState, type ReactNode } from 'react';

/**
 * Sección colapsable del dashboard. Header clickeable (título + resumen opcional que
 * se ve INCLUSO cerrado, para no perder el dato de un pantallazo) y contenido que se
 * despliega. No dibuja card propia: los hijos traen su `.card`, así no se anidan bordes.
 */
export default function Collapsible({
  title,
  subtitle,
  summary,
  defaultOpen = false,
  children,
}: {
  title: string;
  subtitle?: string;
  summary?: ReactNode;
  defaultOpen?: boolean;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <section className="card overflow-hidden">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="w-full flex items-center gap-3 px-4 md:px-5 py-3 text-left hover:bg-slate-50 transition-colors"
        aria-expanded={open}
      >
        <svg
          className={`w-4 h-4 shrink-0 text-slate-400 transition-transform ${open ? 'rotate-90' : ''}`}
          viewBox="0 0 20 20" fill="currentColor" aria-hidden
        >
          <path fillRule="evenodd" d="M7.21 5.23a.75.75 0 011.06.02l4 4.25a.75.75 0 010 1.02l-4 4.25a.75.75 0 11-1.08-1.04L10.63 10 7.19 6.29a.75.75 0 01.02-1.06z" clipRule="evenodd" />
        </svg>
        <div className="flex-1 min-w-0">
          <span className="font-semibold">{title}</span>
          {subtitle && <span className="text-xs text-slate-400 ml-2">{subtitle}</span>}
        </div>
        {summary && <div className="text-sm text-slate-500 shrink-0">{summary}</div>}
      </button>
      {open && (
        <div className="p-3 md:p-4 bg-slate-50/70 border-t border-slate-100 space-y-4">
          {children}
        </div>
      )}
    </section>
  );
}
