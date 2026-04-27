type Card = { label: string; value: string | number; hint?: string };

export default function MetricCards({ cards }: { cards: Card[] }) {
  return (
    <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
      {cards.map((c) => (
        <div key={c.label} className="card p-4">
          <div className="text-xs uppercase tracking-wide text-slate-500">{c.label}</div>
          <div className="mt-1 text-2xl font-bold">{c.value}</div>
          {c.hint && <div className="text-xs text-slate-400 mt-1">{c.hint}</div>}
        </div>
      ))}
    </div>
  );
}
