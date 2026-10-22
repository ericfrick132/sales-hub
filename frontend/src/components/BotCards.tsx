// Mejoras del bot en tarjetas tipo Tinder (Dashboard → Bot). Las propuestas las sube la rutina /bot-review
// (Claude Code en la Mac de Eric, sin API) después de leer los chats del día anterior. Derecha / ✓ = aplicar el
// texto nuevo; izquierda / ✕ = descartar. Todo lo decidido se puede deshacer (rollback) desde el historial.
import { useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';

interface Example { leadId: string; leadName: string | null; context: string | null; botSaid: string | null; better: string | null }
interface Card {
  id: string; day: string; productKey: string; kind: string; target: string; title: string; rationale: string;
  oldValue: string | null; newValue: string | null; examples: Example[]; occurrences: number; status: string;
  createdAt: string; decidedAt: string | null; appliedAt: string | null; rolledBackAt: string | null;
}
interface Run { day: string; ranAt: string; conversations: number; proposals: number; error: string | null }
interface Data { pending: Card[]; history: Card[]; runs: Run[] }

const APP: Record<string, string> = { gymhero: 'GymHero', turnospro: 'TurnosPro', playcrew: 'PlayCrew' };
const STATUS: Record<string, { label: string; tone: string }> = {
  approved: { label: 'Aplicada', tone: 'bg-emerald-100 text-emerald-800' },
  rejected: { label: 'Descartada', tone: 'bg-slate-100 text-slate-600' },
  rolled_back: { label: 'Deshecha', tone: 'bg-amber-100 text-amber-800' },
  stale: { label: 'Vencida (el texto cambió)', tone: 'bg-rose-100 text-rose-700' },
};
const SWIPE = 120;
const fmtDay = (d: string) => { const [, m, dd] = d.split('-'); return `${dd}/${m}`; };

/** "[NUEVO_MENSAJE]" separa mensajes de WhatsApp: se muestran como burbujas. */
function Bubbles({ text, tone }: { text: string | null; tone: 'old' | 'new' | 'bot' | 'better' }) {
  if (!text) return null;
  const cls = {
    old: 'bg-rose-50 border-rose-200 text-rose-900 line-through decoration-rose-300',
    new: 'bg-emerald-50 border-emerald-200 text-emerald-900',
    bot: 'bg-slate-50 border-slate-200 text-slate-700',
    better: 'bg-sky-50 border-sky-200 text-sky-900',
  }[tone];
  return (
    <div className="space-y-1">
      {text.split('[NUEVO_MENSAJE]').map((t) => t.trim()).filter(Boolean).map((t, i) => (
        <div key={i} className={`text-sm border rounded-lg px-2.5 py-1.5 whitespace-pre-line ${cls}`}>{t}</div>
      ))}
    </div>
  );
}

export default function BotCards() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['bot-learning'], queryFn: async () => (await api.get<Data>('/bot-learning')).data });
  const [busy, setBusy] = useState(false);
  const [dx, setDx] = useState(0);
  const [leaving, setLeaving] = useState<0 | 1 | -1>(0);
  const start = useRef<number | null>(null);

  const cards = q.data?.pending ?? [];
  const top = cards[0];

  const decide = async (yes: boolean) => {
    if (!top || busy) return;
    setBusy(true);
    setLeaving(yes ? 1 : -1);
    try {
      await api.post(`/bot-learning/${top.id}/${yes ? 'approve' : 'reject'}`);
      toast.success(yes ? (top.kind === 'idea' ? 'Idea aprobada (queda anotada)' : 'Aplicado: el bot ya usa el texto nuevo') : 'Descartada');
    } catch (e: any) {
      toast.error(e?.response?.data?.error ?? 'No se pudo guardar');
    }
    await qc.invalidateQueries({ queryKey: ['bot-learning'] });
    setLeaving(0); setDx(0); setBusy(false);
  };

  const rollback = async (c: Card) => {
    try {
      await api.post(`/bot-learning/${c.id}/rollback`);
      toast.success(c.status === 'rejected' ? 'Volvió a las tarjetas' : 'Deshecho: el bot volvió al texto anterior');
    } catch (e: any) {
      toast.error(e?.response?.data?.error ?? 'No se pudo deshacer');
    }
    qc.invalidateQueries({ queryKey: ['bot-learning'] });
  };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.target as HTMLElement)?.closest('input,textarea,select')) return;
      if (e.key === 'ArrowRight') decide(true);
      if (e.key === 'ArrowLeft') decide(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  const offset = leaving ? leaving * 900 : dx;
  const hint = offset > 40 ? 'yes' : offset < -40 ? 'no' : null;
  const lastRun = q.data?.runs[0];

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <div>
          <h2 className="font-semibold">Mejoras del bot</h2>
          <p className="text-xs text-slate-500">
            Lo que habría respondido distinto en los chats de ayer. Derecha o ✓ aplica el texto nuevo; izquierda o ✕ la descarta. Flechas del teclado también.
          </p>
        </div>
        {lastRun && (
          <div className="text-xs text-slate-500">
            Último análisis: chats del {fmtDay(lastRun.day)} · {lastRun.conversations} chats · {lastRun.proposals} propuestas
          </div>
        )}
      </div>

      {q.isLoading && <div className="text-sm text-slate-400">Cargando…</div>}
      {q.data && !top && (
        <div className="card p-8 text-center text-sm text-slate-500">No hay tarjetas pendientes. La rutina sube las nuevas cada mañana.</div>
      )}

      {top && (
        <div className="relative max-w-2xl mx-auto" style={{ minHeight: 420 }}>
          {cards.slice(1, 3).reverse().map((c, i, arr) => (
            <div key={c.id} className="absolute inset-x-0 top-0 card h-[420px]"
              style={{ transform: `translateY(${(arr.length - i) * 10}px) scale(${1 - (arr.length - i) * 0.03})`, opacity: 0.6 }} />
          ))}
          <div
            className="relative card p-5 select-none touch-pan-y cursor-grab active:cursor-grabbing shadow-lg"
            style={{ transform: `translateX(${offset}px) rotate(${offset / 25}deg)`, transition: start.current === null ? 'transform .25s ease' : 'none' }}
            onPointerDown={(e) => { if ((e.target as HTMLElement).closest('button,a')) return; start.current = e.clientX; (e.target as HTMLElement).setPointerCapture?.(e.pointerId); }}
            onPointerMove={(e) => { if (start.current !== null) setDx(e.clientX - start.current); }}
            onPointerUp={() => {
              const d = dx; start.current = null;
              if (d > SWIPE) decide(true); else if (d < -SWIPE) decide(false); else setDx(0);
            }}
          >
            {hint && (
              <div className={`absolute top-4 ${hint === 'yes' ? 'left-4 border-emerald-500 text-emerald-600' : 'right-4 border-rose-500 text-rose-600'} border-2 rounded-lg px-3 py-1 font-bold text-lg rotate-[-8deg] bg-white/80`}>
                {hint === 'yes' ? 'SÍ' : 'NO'}
              </div>
            )}
            <div className="flex flex-wrap items-center gap-2 text-[11px] text-slate-500">
              <span className="bg-slate-100 border border-slate-200 rounded px-1.5 py-0.5 text-slate-700">{APP[top.productKey] ?? top.productKey}</span>
              <span>{top.kind === 'idea' ? 'Idea (no se aplica sola)' : top.kind === 'intent' ? `Respuesta "${top.target}"` : `Guion: ${top.target}`}</span>
              <span>· chats del {fmtDay(top.day)}</span>
              <span>· pasó en {top.occurrences} chat{top.occurrences === 1 ? '' : 's'}</span>
              <span className="ml-auto">{cards.length} pendiente{cards.length === 1 ? '' : 's'}</span>
            </div>
            <h3 className="text-lg font-bold mt-2">{top.title}</h3>
            <p className="text-sm text-slate-600 mt-1">{top.rationale}</p>

            {top.kind !== 'idea' && (
              <div className="grid sm:grid-cols-2 gap-3 mt-4">
                <div><div className="text-[11px] uppercase tracking-wide text-slate-400 mb-1">Ahora</div><Bubbles text={top.oldValue} tone="old" /></div>
                <div><div className="text-[11px] uppercase tracking-wide text-slate-400 mb-1">Propuesta</div><Bubbles text={top.newValue} tone="new" /></div>
              </div>
            )}

            {top.examples.length > 0 && (
              <div className="mt-4 space-y-3">
                <div className="text-[11px] uppercase tracking-wide text-slate-400">En los chats</div>
                {top.examples.map((ex, i) => (
                  <div key={i} className="border border-slate-100 rounded-lg p-3 space-y-2">
                    <div className="text-xs text-slate-500">
                      {ex.leadName && <b className="text-slate-700">{ex.leadName}</b>}
                      {ex.context && <div className="whitespace-pre-line mt-0.5">{ex.context}</div>}
                    </div>
                    <div className="grid sm:grid-cols-2 gap-2">
                      <div><div className="text-[11px] text-slate-400 mb-0.5">El bot dijo</div><Bubbles text={ex.botSaid} tone="bot" /></div>
                      <div><div className="text-[11px] text-slate-400 mb-0.5">Yo hubiera dicho</div><Bubbles text={ex.better} tone="better" /></div>
                    </div>
                  </div>
                ))}
              </div>
            )}

            <div className="flex justify-center gap-6 mt-5">
              <button type="button" disabled={busy} onClick={() => decide(false)} title="Descartar (←)"
                className="w-14 h-14 rounded-full border-2 border-rose-300 text-rose-600 text-2xl hover:bg-rose-50 disabled:opacity-50">✕</button>
              <button type="button" disabled={busy} onClick={() => decide(true)} title="Aplicar (→)"
                className="w-14 h-14 rounded-full border-2 border-emerald-300 text-emerald-600 text-2xl hover:bg-emerald-50 disabled:opacity-50">✓</button>
            </div>
          </div>
        </div>
      )}

      {(q.data?.history.length ?? 0) > 0 && (
        <section className="card p-4">
          <h3 className="font-semibold mb-2">Decididas (30 días)</h3>
          <div className="divide-y divide-slate-100">
            {q.data!.history.map((c) => {
              const st = STATUS[c.status] ?? { label: c.status, tone: 'bg-slate-100 text-slate-600' };
              return (
                <div key={c.id} className="py-2 flex flex-wrap items-center gap-2 text-sm">
                  <span className={`text-[11px] rounded px-1.5 py-0.5 ${st.tone}`}>{st.label}</span>
                  <span className="text-[11px] text-slate-500">{APP[c.productKey] ?? c.productKey} · {fmtDay(c.day)}</span>
                  <span className="flex-1 min-w-[200px]">{c.title}</span>
                  {(c.status === 'approved' || c.status === 'rejected') && (
                    <button type="button" className="btn-secondary text-xs" onClick={() => rollback(c)}>
                      {c.status === 'approved' ? (c.kind === 'idea' ? 'Deshacer' : 'Rollback') : 'Volver a decidir'}
                    </button>
                  )}
                </div>
              );
            })}
          </div>
        </section>
      )}
    </div>
  );
}
