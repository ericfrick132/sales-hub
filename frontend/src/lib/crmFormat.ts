/** Formatos compartidos del CRM (tablero, ficha y modo llamadas). */

/** El teléfono se guarda sólo con dígitos (5491112345678); se muestra con el +. */
export const fmtPhone = (phone: string) => `+${phone.replace(/\D/g, '')}`;

/**
 * tel: lo abre la app de llamadas del sistema: FaceTime en la Mac y Enlace Móvil en
 * Windows, que llaman por el celular vinculado.
 */
export const telHref = (phone: string) => `tel:${fmtPhone(phone)}`;

export const hace = (iso?: string) => {
  if (!iso) return 'sin actividad';
  const min = Math.floor((Date.now() - new Date(iso).getTime()) / 60000);
  if (min < 1) return 'recién';
  if (min < 60) return `hace ${min} min`;
  if (min < 1440) return `hace ${Math.floor(min / 60)} h`;
  return `hace ${Math.floor(min / 1440)} d`;
};

export const fmtDate = (iso?: string) =>
  iso ? new Date(iso).toLocaleDateString('es-AR', { day: '2-digit', month: '2-digit' }) : '—';

export const fmtDateTime = (iso?: string) =>
  iso ? new Date(iso).toLocaleString('es-AR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : '—';

/** ISO → valor para <input type="datetime-local"> en hora local. */
export const toLocalInput = (iso?: string) => {
  if (!iso) return '';
  const d = new Date(iso);
  const off = d.getTimezoneOffset();
  return new Date(d.getTime() - off * 60000).toISOString().slice(0, 16);
};
