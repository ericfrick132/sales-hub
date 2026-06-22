// Switch ON/OFF reusable (estilo iOS). Frena la propagación para poder usarse
// dentro de filas/cards clickeables sin disparar el click del contenedor.
export default function Switch({ on, onClick, title, disabled }: {
  on: boolean;
  onClick: () => void;
  title?: string;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      title={title}
      disabled={disabled}
      aria-pressed={on}
      onClick={(e) => { e.stopPropagation(); if (!disabled) onClick(); }}
      className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors ${
        on ? 'bg-emerald-500' : 'bg-slate-300'
      } ${disabled ? 'opacity-40 cursor-not-allowed' : 'cursor-pointer'}`}>
      <span
        className={`inline-block h-5 w-5 transform rounded-full bg-white shadow transition-transform ${
          on ? 'translate-x-[22px]' : 'translate-x-0.5'
        }`} />
    </button>
  );
}
