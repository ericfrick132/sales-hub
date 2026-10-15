import clsx from 'clsx';

/** De dónde salió el teléfono principal: rosa = del Instagram del negocio (suele ser el celu con WhatsApp), gris = el normal. */
export default function PhoneOrigin({ fromInstagram, className }: { fromInstagram?: boolean; className?: string }) {
  return (
    <span
      title={fromInstagram ? 'Número sacado de su Instagram (suele ser el celu con WhatsApp)' : 'Número normal (Maps, anuncio o carga manual)'}
      className={clsx('inline-block text-[10px] font-semibold leading-none rounded px-1 py-0.5 border shrink-0 not-italic',
        fromInstagram ? 'bg-pink-100 text-pink-700 border-pink-200' : 'bg-slate-100 text-slate-500 border-slate-200', className)}>
      {fromInstagram ? 'IG' : 'Tel'}
    </span>
  );
}
